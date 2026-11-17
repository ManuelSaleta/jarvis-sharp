namespace Jarvis.Sharp;

using Jarvis.Sharp.Security;
using Jarvis.Sharp.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SlackNet;
using SlackNet.SocketMode;

public class Worker : BackgroundService
{
    private static readonly TimeSpan DefaultInitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly ILogger<Worker> _logger;
    private readonly AuthorizationGuard _authGuard;
    private readonly CandidateStateStore _stateStore;
    private readonly ISlackSocketModeClient? _socketModeClient;
    private readonly TimeSpan _initialRetryDelay;
    private readonly TimeSpan _maxRetryDelay;
    private readonly int _healthPort;

    private volatile bool _isSocketConnected;
    private volatile bool _standaloneReady;

    [ActivatorUtilitiesConstructor]
    public Worker(
        ILogger<Worker> logger,
        AuthorizationGuard authGuard,
        CandidateStateStore stateStore,
        ISlackSocketModeClient? socketModeClient = null)
        : this(
            logger,
            authGuard,
            stateStore,
            socketModeClient,
            DefaultInitialRetryDelay,
            DefaultMaxRetryDelay,
            ResolveHealthPort())
    {
    }

    public Worker(
        ILogger<Worker> logger,
        AuthorizationGuard authGuard,
        CandidateStateStore stateStore,
        ISlackSocketModeClient? socketModeClient,
        TimeSpan initialRetryDelay,
        TimeSpan maxRetryDelay,
        int healthPort = 0)
    {
        _logger = logger;
        _authGuard = authGuard;
        _stateStore = stateStore;
        _socketModeClient = socketModeClient;
        _initialRetryDelay = initialRetryDelay > TimeSpan.Zero ? initialRetryDelay : DefaultInitialRetryDelay;
        _maxRetryDelay = maxRetryDelay >= _initialRetryDelay ? maxRetryDelay : _initialRetryDelay;
        _healthPort = healthPort;
    }

    public bool IsReady => _socketModeClient == null
        ? _standaloneReady
        : (_standaloneReady || (_isSocketConnected && _socketModeClient.Connected));

    public static int ResolveHealthPort(string? rawValue = null)
    {
        var value = rawValue ?? Environment.GetEnvironmentVariable("HEALTH_PORT");
        if (string.IsNullOrWhiteSpace(value))
        {
            return 8080;
        }

        return int.TryParse(value.Trim(), out var port) ? port : 8080;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🤖 [Jarvis.Sharp] Daemon initialized successfully.");
        _logger.LogInformation("🛡️ [Jarvis.Sharp] USER_WHITELIST loaded with {Count} authorized user(s).", _authGuard.AllowedUsers.Count);

        await using var healthServer = new HealthProbeServer(_healthPort, () => IsReady, _logger);
        healthServer.Start(stoppingToken);

        var appToken = Environment.GetEnvironmentVariable("SLACK_APP_TOKEN")?.Trim();
        var botToken = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN")?.Trim();

        if (!string.IsNullOrWhiteSpace(appToken) && appToken.StartsWith("xapp-") &&
            !string.IsNullOrWhiteSpace(botToken) && _socketModeClient != null)
        {
            await ConnectWithRetryAsync(stoppingToken);
        }
        else
        {
            _standaloneReady = true;
            _logger.LogInformation("ℹ️ [Jarvis.Sharp] Slack Socket Mode tokens (SLACK_APP_TOKEN / SLACK_BOT_TOKEN) not configured or in mock mode. Standalone daemon running.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var evicted = _stateStore.EvictExpired();
            if (evicted > 0)
            {
                _logger.LogInformation("🧹 [Jarvis.Sharp] Evicted {Count} expired candidate batch(es).", evicted);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_socketModeClient != null && _socketModeClient.Connected)
        {
            try
            {
                await _socketModeClient.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during Slack Socket Mode disconnection.");
            }
        }

        _logger.LogInformation("🛑 [Jarvis.Sharp] Daemon shutting down gracefully.");
    }

    private async Task ConnectWithRetryAsync(CancellationToken stoppingToken)
    {
        var delay = _initialRetryDelay;
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                _logger.LogInformation("🔌 [Jarvis.Sharp] Connecting to Slack via Socket Mode WebSocket (attempt {Attempt})...", attempt);
                await _socketModeClient!.Connect(new SocketModeConnectionOptions(), stoppingToken);
                _isSocketConnected = true;
                _logger.LogInformation("⚡ [Jarvis.Sharp] Slack Socket Mode listener connected successfully.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "❌ [Jarvis.Sharp] Slack Socket Mode connection attempt {Attempt} failed. Retrying in {DelaySeconds:F1}s...",
                    attempt,
                    delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var nextMs = Math.Min(delay.TotalMilliseconds * 2, _maxRetryDelay.TotalMilliseconds);
                delay = TimeSpan.FromMilliseconds(nextMs);
            }
        }
    }
}
