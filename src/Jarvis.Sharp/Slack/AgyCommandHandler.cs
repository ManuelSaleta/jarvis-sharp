namespace Jarvis.Sharp.Slack;

using System.Net.Http.Json;
using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.Interaction;
using SlackNet.WebApi;

public class AgyCommandHandler : ISlashCommandHandler
{
    private readonly ILogger<AgyCommandHandler> _logger;
    private readonly AuthorizationGuard _authGuard;
    private readonly RateLimiter _rateLimiter;
    private readonly IAgentBridge _agentBridge;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _httpClient;

    [ActivatorUtilitiesConstructor]
    public AgyCommandHandler(
        ILogger<AgyCommandHandler> logger,
        AuthorizationGuard authGuard,
        RateLimiter rateLimiter,
        IAgentBridge agentBridge,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _authGuard = authGuard;
        _rateLimiter = rateLimiter;
        _agentBridge = agentBridge;
        _httpClientFactory = httpClientFactory;
    }

    public AgyCommandHandler(
        ILogger<AgyCommandHandler> logger,
        AuthorizationGuard authGuard,
        RateLimiter rateLimiter,
        IAgentBridge agentBridge,
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _authGuard = authGuard;
        _rateLimiter = rateLimiter;
        _agentBridge = agentBridge;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<SlashCommandResponse> Handle(SlashCommand command)
    {
        var userId = command.UserId;
        try
        {
            var verifiedUser = _authGuard.RequireAuthorized(userId);
            _rateLimiter.Check(verifiedUser);

            var prompt = command.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new SlashCommandResponse
                {
                    ResponseType = ResponseType.Ephemeral,
                    Message = new Message { Text = "⚠️ Please provide a prompt for Antigravity (e.g. `/agy check cluster status`)." }
                };
            }

            var context = new Dictionary<string, string>
            {
                ["slack_user"] = verifiedUser,
                ["channel_id"] = command.ChannelId ?? "direct",
                ["command"] = "/agy"
            };

            if (!string.IsNullOrWhiteSpace(command.ResponseUrl))
            {
                var responseUrl = command.ResponseUrl;
                _ = Task.Run(async () => await DispatchAsyncReply(responseUrl, prompt, context));

                return new SlashCommandResponse
                {
                    ResponseType = ResponseType.Ephemeral,
                    Message = new Message { Text = "⏳ Asking Antigravity..." }
                };
            }

            var reply = await _agentBridge.AskAsync(prompt, context);

            return new SlashCommandResponse
            {
                ResponseType = ResponseType.InChannel,
                Message = new Message { Text = reply }
            };
        }
        catch (UnauthorizedAccessException uex)
        {
            _logger.LogWarning("Unauthorized /agy command attempt from user '{UserId}': {Message}", userId, uex.Message);
            return new SlashCommandResponse
            {
                ResponseType = ResponseType.Ephemeral,
                Message = new Message { Text = $"⛔ {uex.Message}" }
            };
        }
        catch (RateLimitExceededException rex)
        {
            _logger.LogWarning("Rate limit exceeded for user '{UserId}': {Message}", userId, rex.Message);
            return new SlashCommandResponse
            {
                ResponseType = ResponseType.Ephemeral,
                Message = new Message { Text = $"⛔ {rex.Message}" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing /agy command from user '{UserId}'", userId);
            return new SlashCommandResponse
            {
                ResponseType = ResponseType.Ephemeral,
                Message = new Message { Text = $"⚠️ Error communicating with Antigravity: {ex.Message}" }
            };
        }
    }

    private async Task DispatchAsyncReply(string responseUrl, string prompt, Dictionary<string, string> context)
    {
        var client = _httpClientFactory?.CreateClient() ?? _httpClient ?? new HttpClient();
        try
        {
            var reply = await _agentBridge.AskAsync(prompt, context);
            var payload = new
            {
                text = reply,
                response_type = "in_channel"
            };
            using var response = await client.PostAsJsonAsync(responseUrl, payload);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Slack ResponseUrl returned HTTP {StatusCode} for /agy reply.", (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch async /agy reply to ResponseUrl");
            try
            {
                await client.PostAsJsonAsync(responseUrl, new
                {
                    text = $"⚠️ Error communicating with Antigravity: {ex.Message}",
                    response_type = "ephemeral"
                });
            }
            catch
            {
                // Best effort error notification
            }
        }
    }
}
