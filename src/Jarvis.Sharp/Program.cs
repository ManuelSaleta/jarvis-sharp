namespace Jarvis.Sharp;

using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Jarvis.Sharp.Slack;
using Jarvis.Sharp.State;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Plex.Core.Http.Extensions;
using SlackNet;
using SlackNet.Blocks;
using SlackNet.Events;
using SlackNet.Extensions.DependencyInjection;

public static class Program
{
    public static readonly string[] RequiredEnvironmentVariables =
    {
        "SLACK_APP_TOKEN",
        "SLACK_BOT_TOKEN",
        "USER_WHITELIST",
        "WEBHOOK_SECRET_TOKEN",
        "MEDIA_SYNC_WEBHOOK_URL"
    };

    public static IReadOnlyList<string> ValidateRequiredEnvironment(Func<string, string?>? getEnvironmentVariable = null)
    {
        var getter = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var missing = new List<string>();
        foreach (var name in RequiredEnvironmentVariables)
        {
            if (string.IsNullOrWhiteSpace(getter(name)))
            {
                missing.Add(name);
            }
        }
        return missing.AsReadOnly();
    }

    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        // Security & State
        builder.Services.AddSingleton(new AuthorizationGuard());
        builder.Services.AddSingleton(new RateLimiter(maxRequests: 10, windowSeconds: 60.0));
        builder.Services.AddSingleton(sp => new CandidateStateStore(
            stateFilePath: Environment.GetEnvironmentVariable("CANDIDATE_STATE_PATH"),
            logger: sp.GetService<ILogger<CandidateStateStore>>()));

        // HTTP Client & Services
        builder.Services.AddHttpClient();
        builder.Services.AddPlexClient(options =>
        {
            options.ServerUrl = Environment.GetEnvironmentVariable("PLEX_URL")?.Trim() ?? string.Empty;
            options.Token = Environment.GetEnvironmentVariable("PLEX_TOKEN")?.Trim() ?? string.Empty;
            options.Product = "Jarvis.Sharp";
        });
        builder.Services.AddTransient<IPlexService, PlexService>();
        builder.Services.AddHttpClient<IMediaSyncDispatcher, MediaSyncDispatcher>();
        builder.Services.AddHttpClient<IGrafanaLokiService, GrafanaLokiService>();

        // Kubernetes Client with graceful fallback
        builder.Services.AddSingleton<IKubernetes>(_ =>
        {
            try
            {
                var config = KubernetesClientConfiguration.IsInCluster()
                    ? KubernetesClientConfiguration.InClusterConfig()
                    : KubernetesClientConfiguration.BuildConfigFromConfigFile();
                return new Kubernetes(config);
            }
            catch
            {
                return new Kubernetes(new KubernetesClientConfiguration { Host = "http://localhost:8080" });
            }
        });
        builder.Services.AddSingleton<IKubernetesDiagnostics, KubernetesDiagnostics>();
        builder.Services.AddSingleton<IAgentBridge, AgentBridge>();

        // SlackNet Handlers & Socket Mode Configuration
        var botToken = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN")?.Trim();
        var appToken = Environment.GetEnvironmentVariable("SLACK_APP_TOKEN")?.Trim();

        builder.Services.AddSlackNet(c =>
        {
            if (!string.IsNullOrWhiteSpace(botToken))
            {
                c.UseApiToken(botToken);
            }
            if (!string.IsNullOrWhiteSpace(appToken))
            {
                c.UseAppLevelToken(appToken);
            }

            // Slash Commands
            c.RegisterSlashCommandHandler<DownloadCommandHandler>("/download");
            c.RegisterSlashCommandHandler<AgyCommandHandler>("/agy");

            // Interactive Block Actions
            c.RegisterBlockActionHandler<ButtonAction, RemediationActionHandler>("remediate_restart_deployment");
            c.RegisterBlockActionHandler<ButtonAction, RemediationActionHandler>("remediate_view_logs");
            c.RegisterBlockActionHandler<ButtonAction, RemediationActionHandler>("remediate_dismiss");
            c.RegisterBlockActionHandler<ButtonAction, CandidateActionHandler>();

            // Event Subscriptions
            c.RegisterEventHandler<MessageEvent, AlertMessageEventHandler>();
            c.RegisterEventHandler<AppMention, AppMentionEventHandler>();
        });

        // Background Worker Daemon
        builder.Services.AddHostedService<Worker>();

        using var host = builder.Build();

        var missingEnvVars = ValidateRequiredEnvironment();
        if (missingEnvVars.Count > 0)
        {
            var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Jarvis.Sharp.Startup");
            startupLogger.LogCritical(
                "❌ [Jarvis.Sharp] Missing required environment variable(s): {MissingVariables}. Failing fast.",
                string.Join(", ", missingEnvVars));
            Environment.ExitCode = 1;
            return;
        }

        host.Run();
    }
}
