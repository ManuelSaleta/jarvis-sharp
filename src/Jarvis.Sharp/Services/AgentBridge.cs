namespace Jarvis.Sharp.Services;

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public class AgentBridge : IAgentBridge
{
    private const string DefaultGeminiModel = "gemini-3.8-flash";
    private const string SystemInstructionText =
        "You are Jarvis, an autonomous homelab ChatOps & Kubernetes SRE assistant. " +
        "Be concise, technical, and actionable. Format responses cleanly for Slack mrkdwn.";

    private readonly ILogger<AgentBridge> _logger;
    private readonly IKubernetesDiagnostics _k8sDiagnostics;
    private readonly HttpClient _httpClient;
    private readonly string _agyBinary;
    private readonly string? _agyWorkspace;
    private readonly string? _geminiApiKey;
    private readonly string _geminiModel;
    private readonly TimeSpan _timeout;

    [ActivatorUtilitiesConstructor]
    public AgentBridge(
        HttpClient httpClient,
        ILogger<AgentBridge> logger,
        IKubernetesDiagnostics k8sDiagnostics)
        : this(logger, k8sDiagnostics, httpClient)
    {
    }

    public AgentBridge(
        ILogger<AgentBridge> logger,
        IKubernetesDiagnostics k8sDiagnostics,
        HttpClient? httpClient = null,
        string? agyBinary = null,
        string? agyWorkspace = null,
        string? geminiApiKey = null,
        string? geminiModel = null)
    {
        _logger = logger;
        _k8sDiagnostics = k8sDiagnostics;
        _httpClient = httpClient ?? new HttpClient();
        _agyBinary = (agyBinary ?? Environment.GetEnvironmentVariable("AGY_BINARY") ?? "agy").Trim();
        _agyWorkspace = (agyWorkspace ?? Environment.GetEnvironmentVariable("AGY_WORKSPACE"))?.Trim();
        _geminiApiKey = (geminiApiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY"))?.Trim();
        _geminiModel = (geminiModel ?? Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? DefaultGeminiModel).Trim();
        if (string.IsNullOrWhiteSpace(_geminiModel))
        {
            _geminiModel = DefaultGeminiModel;
        }
        _timeout = TimeSpan.FromSeconds(90);
    }

    public async Task<string> AskAsync(
        string prompt,
        IDictionary<string, string>? context = null,
        CancellationToken cancellationToken = default)
    {
        var cleanedPrompt = prompt?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cleanedPrompt))
        {
            return "Please provide a non-empty prompt for Antigravity.";
        }

        var fullPrompt = BuildPrompt(cleanedPrompt, context);

        if (!string.IsNullOrWhiteSpace(_geminiApiKey))
        {
            return await AskGeminiApiAsync(fullPrompt, cancellationToken);
        }

        return await AskAgyCliAsync(fullPrompt, cancellationToken);
    }

    private static string BuildPrompt(string cleanedPrompt, IDictionary<string, string>? context)
    {
        var fullPrompt = new StringBuilder();
        if (context is { Count: > 0 })
        {
            fullPrompt.AppendLine("Homelab ChatOps Context:");
            foreach (var kvp in context.OrderBy(k => k.Key))
            {
                fullPrompt.AppendLine($"- {kvp.Key}: {kvp.Value}");
            }
            fullPrompt.AppendLine();
            fullPrompt.AppendLine("User Request:");
        }
        fullPrompt.Append(cleanedPrompt);
        return fullPrompt.ToString();
    }

    private async Task<string> AskGeminiApiAsync(string fullPrompt, CancellationToken cancellationToken)
    {
        var endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(_geminiModel)}:generateContent";
        var payload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = SystemInstructionText } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = fullPrompt } }
                }
            }
        };

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Add("x-goog-api-key", _geminiApiKey);
            request.Content = JsonContent.Create(payload);

            using var response = await _httpClient.SendAsync(request, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Gemini API returned HTTP {StatusCode}: {Body}", (int)response.StatusCode, body);
                return $"Gemini API error (HTTP {(int)response.StatusCode}). Verify `GEMINI_API_KEY` and `GEMINI_MODEL` (`{_geminiModel}`).";
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0 &&
                candidates[0].TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var textElem))
                    {
                        var text = textElem.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (sb.Length > 0) sb.AppendLine();
                            sb.Append(text.Trim());
                        }
                    }
                }

                if (sb.Length > 0)
                {
                    return sb.ToString();
                }
            }

            return "Gemini API returned an empty response.";
        }
        catch (OperationCanceledException)
        {
            return $"Gemini API timed out after {(int)_timeout.TotalSeconds}s.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invoke Gemini API model '{Model}'", _geminiModel);
            return $"Gemini API invocation notice: {ex.Message}";
        }
    }

    private async Task<string> AskAgyCliAsync(string fullPrompt, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _agyBinary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!string.IsNullOrWhiteSpace(_agyWorkspace) && Directory.Exists(_agyWorkspace))
            {
                psi.WorkingDirectory = _agyWorkspace;
                psi.ArgumentList.Add("--add-dir");
                psi.ArgumentList.Add(_agyWorkspace);
            }
            psi.ArgumentList.Add("--prompt");
            psi.ArgumentList.Add(fullPrompt);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return "Failed to start `agy` process.";
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                return stdout;
            }

            return !string.IsNullOrWhiteSpace(stdout)
                ? stdout
                : !string.IsNullOrWhiteSpace(stderr)
                    ? stderr
                    : $"`agy` exited with status {process.ExitCode}";
        }
        catch (OperationCanceledException)
        {
            return $"Antigravity CLI timed out after {(int)_timeout.TotalSeconds}s.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invoke Antigravity CLI binary '{Binary}'", _agyBinary);
            return $"Antigravity CLI (`{_agyBinary}`) invocation notice: {ex.Message}. " +
                   "Ensure `agy` is installed or set `GEMINI_API_KEY` for direct API mode.";
        }
    }

    public async Task<string> TriageAlertAsync(
        string alertText,
        string @namespace,
        string deployment,
        string pod,
        CancellationToken cancellationToken = default)
    {
        string diagSummary;
        try
        {
            var diag = await _k8sDiagnostics.GetPodDiagnosticsAsync(@namespace, pod, 40, cancellationToken);
            var logsExcerpt = diag.Logs.Length > 1200 ? diag.Logs[^1200..] : diag.Logs;
            diagSummary = $"Pod `{@namespace}/{pod}` status: {diag.StatusPhase}\nLogs excerpt:\n```{logsExcerpt}```";
        }
        catch (Exception ex)
        {
            diagSummary = $"Could not collect live pod diagnostics for `{@namespace}/{pod}`: {ex.Message}";
        }

        var prompt = $"Analyze the following Grafana homelab alert and Kubernetes pod diagnostics. " +
                     $"Provide a concise root-cause summary and recommended remediation.\n\n" +
                     $"Alert:\n{alertText}\n\n{diagSummary}";

        var context = new Dictionary<string, string>
        {
            ["namespace"] = @namespace,
            ["deployment"] = deployment,
            ["pod"] = pod,
        };

        return await AskAsync(prompt, context, cancellationToken);
    }
}
