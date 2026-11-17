namespace Jarvis.Sharp;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

public sealed class HealthProbeServer : IAsyncDisposable
{
    private readonly int _port;
    private readonly Func<bool> _isReady;
    private readonly ILogger _logger;
    private readonly string _prefix;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public HealthProbeServer(int port, Func<bool> isReady, ILogger logger, string host = "+")
    {
        _port = port;
        _isReady = isReady;
        _logger = logger;
        _prefix = $"http://{host}:{port}/";
    }

    public bool IsListening => _listener?.IsListening ?? false;

    public void Start(CancellationToken stoppingToken)
    {
        if (_port <= 0)
        {
            _logger.LogInformation("ℹ️ [Jarvis.Sharp] Health probe server disabled (HEALTH_PORT={Port}).", _port);
            return;
        }

        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(_prefix);
            listener.Start();
            _listener = listener;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _listenTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token), CancellationToken.None);
            _logger.LogInformation("🩺 [Jarvis.Sharp] Health probe server listening on {Prefix} (/healthz, /readyz).", _prefix);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ [Jarvis.Sharp] Failed to start health probe server on {Prefix}. Continuing without HTTP health endpoint.", _prefix);
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (context != null)
            {
                await HandleRequestAsync(context);
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            int statusCode;
            string body;

            if (string.Equals(path, "/healthz", StringComparison.OrdinalIgnoreCase))
            {
                statusCode = 200;
                body = "ok\n";
            }
            else if (string.Equals(path, "/readyz", StringComparison.OrdinalIgnoreCase))
            {
                if (_isReady())
                {
                    statusCode = 200;
                    body = "ready\n";
                }
                else
                {
                    statusCode = 503;
                    body = "not ready\n";
                }
            }
            else
            {
                statusCode = 404;
                body = "not found\n";
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error writing health probe HTTP response.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_listener is { IsListening: true })
            {
                _listener.Stop();
            }
            _listener?.Close();
        }
        catch
        {
            // Ignore errors during shutdown
        }

        if (_listenTask != null)
        {
            try
            {
                await _listenTask;
            }
            catch
            {
                // Ignore errors during shutdown
            }
        }

        _cts?.Dispose();
    }
}
