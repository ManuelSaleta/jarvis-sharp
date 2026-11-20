namespace Jarvis.Sharp.Tests;

using System.Text;
using Jarvis.Sharp.Security;
using Jarvis.Sharp.Services;
using Jarvis.Sharp.State;
using Microsoft.Extensions.DependencyInjection;
using SlackNet.Extensions.DependencyInjection;
using Xunit;

public class SecurityAndSanitizerTests
{
    [Fact]
    public void AuthorizationGuard_ShouldFailClosed_WhenWhitelistEmpty()
    {
        var guard = new AuthorizationGuard(string.Empty);
        Assert.False(guard.IsAuthorized("U12345"));
        Assert.Throws<UnauthorizedAccessException>(() => guard.RequireAuthorized("U12345"));
    }

    [Fact]
    public void AuthorizationGuard_ShouldAllowWhitelistedUser()
    {
        var guard = new AuthorizationGuard("U_MANNY, U_ADMIN");
        Assert.True(guard.IsAuthorized("U_MANNY"));
        Assert.True(guard.IsAuthorized("U_ADMIN"));
        Assert.False(guard.IsAuthorized("U_INTRUDER"));
        Assert.Equal("U_MANNY", guard.RequireAuthorized("U_MANNY"));
    }

    [Fact]
    public void HmacSigner_ShouldComputeAndVerifySignature()
    {
        var secret = "super-secret-key-12345";
        var payload = Encoding.UTF8.GetBytes("{\"batch_id\":\"batch-1\",\"items\":[]}");

        var signature = HmacSigner.ComputeSignature(secret, payload);
        Assert.StartsWith("sha256=", signature);
        Assert.True(HmacSigner.VerifySignature(secret, payload, signature));
        Assert.False(HmacSigner.VerifySignature("wrong-secret", payload, signature));
        Assert.False(HmacSigner.VerifySignature(secret, payload, "sha256=invalid"));
    }

    [Fact]
    public void RateLimiter_ShouldBlockExcessiveRequests()
    {
        var limiter = new RateLimiter(maxRequests: 3, windowSeconds: 60.0);
        var now = DateTimeOffset.UtcNow;

        limiter.Check("U_TEST", now);
        limiter.Check("U_TEST", now.AddSeconds(1));
        limiter.Check("U_TEST", now.AddSeconds(2));

        Assert.Throws<RateLimitExceededException>(() => limiter.Check("U_TEST", now.AddSeconds(3)));

        // After window expires, should allow again
        limiter.Check("U_TEST", now.AddSeconds(65));
    }

    [Fact]
    public void InputSanitizer_ShouldValidateK8sIdentifiersAndNamespaces()
    {
        Assert.Equal("valid-pod-1", InputSanitizer.ValidateK8sIdentifier("valid-pod-1"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateK8sIdentifier("invalid_pod!"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateK8sIdentifier("pod; rm -rf /"));

        Assert.Equal("monitoring", InputSanitizer.ValidateNamespace("monitoring"));
        Assert.Equal("media", InputSanitizer.ValidateNamespace("media"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateNamespace("unauthorized-ns"));
    }

    [Fact]
    public void InputSanitizer_ShouldValidateHomelabNodeIps()
    {
        Assert.Equal("192.168.1.200", InputSanitizer.ValidateNodeIp("192.168.1.200"));
        Assert.Equal("192.168.1.210", InputSanitizer.ValidateNodeIp("192.168.1.210"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateNodeIp("10.0.0.1"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateNodeIp("192.168.1.999"));
    }

    [Fact]
    public void InputSanitizer_ShouldValidateLogQlQueries()
    {
        var valid = "{namespace=\"monitoring\"} |= \"error\"";
        Assert.Equal(valid, InputSanitizer.ValidateLogQlQuery(valid));

        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateLogQlQuery(""));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateLogQlQuery("no stream selector here"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateLogQlQuery("{app=\"test\"}\nrm -rf /"));
        Assert.Throws<ArgumentException>(() => InputSanitizer.ValidateLogQlQuery("{app=\"test\"}", maxLength: 5));
    }
}

public class StateAndCommandTests
{
    [Fact]
    public void CommandParser_ShouldExtractIndices()
    {
        var indices = CommandParser.ParseDownloadCommand("/download 1 3 6 10");
        Assert.Equal(new[] { 1, 3, 6, 10 }, indices);

        var commaIndices = CommandParser.ParseDownloadCommand("/download 2, 4, 8");
        Assert.Equal(new[] { 2, 4, 8 }, commaIndices);

        Assert.Empty(CommandParser.ParseDownloadCommand("no numbers here"));
        Assert.Empty(CommandParser.ParseDownloadCommand(null));
    }

    [Fact]
    public void CandidateStateStore_ShouldStoreAndResolveBatchWithinTtl()
    {
        var store = new CandidateStateStore(ttl: TimeSpan.FromMinutes(10));
        var items = new List<CandidateItem>
        {
            new(1, "Severance", 2025, "TV"),
            new(2, "Gladiator II", 2024, "Movie"),
            new(3, "Evil Dead Burn", 2026, "Movie")
        };

        var now = DateTimeOffset.UtcNow;
        store.SaveBatch("batch-alpha", items, now);

        var batch = store.GetBatch("batch-alpha", now);
        Assert.NotNull(batch);
        Assert.Equal(3, batch.Items.Count);

        var resolved = store.ResolveIndices("batch-alpha", new[] { 1, 3 }, now);
        Assert.Equal(2, resolved.Count);
        Assert.Equal("Severance", resolved[0].Title);
        Assert.Equal("Evil Dead Burn", resolved[1].Title);

        // After TTL expired
        var expiredBatch = store.GetBatch("batch-alpha", now.AddMinutes(15));
        Assert.Null(expiredBatch);
    }

    [Fact]
    public void SlackNet_DIConfigurationCheck()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSlackNet(c =>
        {
            c.UseApiToken("xoxb-fake");
            c.UseAppLevelToken("xapp-fake");

            c.RegisterSlashCommandHandler<Jarvis.Sharp.Slack.DownloadCommandHandler>("/download");
            c.RegisterSlashCommandHandler<Jarvis.Sharp.Slack.AgyCommandHandler>("/agy");

            c.RegisterBlockActionHandler<SlackNet.Blocks.ButtonAction, Jarvis.Sharp.Slack.RemediationActionHandler>("remediate_restart_deployment");
            c.RegisterBlockActionHandler<SlackNet.Blocks.ButtonAction, Jarvis.Sharp.Slack.RemediationActionHandler>("remediate_view_logs");
            c.RegisterBlockActionHandler<SlackNet.Blocks.ButtonAction, Jarvis.Sharp.Slack.RemediationActionHandler>("remediate_dismiss");
            c.RegisterBlockActionHandler<SlackNet.Blocks.ButtonAction, Jarvis.Sharp.Slack.CandidateActionHandler>();

            c.RegisterEventHandler<SlackNet.Events.MessageEvent, Jarvis.Sharp.Slack.AlertMessageEventHandler>();
            c.RegisterEventHandler<SlackNet.Events.AppMention, Jarvis.Sharp.Slack.AppMentionEventHandler>();
        });

        services.AddSingleton(new AuthorizationGuard("U_TEST"));
        services.AddSingleton(new RateLimiter());
        services.AddSingleton<IKubernetesDiagnostics>(new StubKubernetesDiagnostics());
        services.AddHttpClient<IAgentBridge, AgentBridge>();
        services.AddTransient<Jarvis.Sharp.Slack.AgyCommandHandler>();

        var provider = services.BuildServiceProvider();
        var socketClient = provider.GetService<SlackNet.ISlackSocketModeClient>();
        Assert.NotNull(socketClient);
        Assert.NotNull(provider.GetRequiredService<IAgentBridge>());
        Assert.NotNull(provider.GetRequiredService<Jarvis.Sharp.Slack.AgyCommandHandler>());
    }

    [Fact]
    public void AlertMessageEventHandler_ExtractAlertTargets_ShouldParseAccurately()
    {
        var alert1 = "[FIRING:1] K3s Pod CrashLooping (namespace=media deployment=vpn-media-sync pod=vpn-media-sync-6d7c8f9b-abc12)";
        var (ns1, deploy1, pod1) = Jarvis.Sharp.Slack.AlertMessageEventHandler.ExtractAlertTargets(alert1);
        Assert.Equal("media", ns1);
        Assert.Equal("vpn-media-sync", deploy1);
        Assert.Equal("vpn-media-sync-6d7c8f9b-abc12", pod1);

        var alert2 = "[FIRING:1] Container OOMKilled pod: jellyfin-server-5d6e7f8a-xyz09";
        var (ns2, deploy2, pod2) = Jarvis.Sharp.Slack.AlertMessageEventHandler.ExtractAlertTargets(alert2);
        Assert.Equal("media", ns2);
        Assert.Equal("jellyfin-server", deploy2);
        Assert.Equal("jellyfin-server-5d6e7f8a-xyz09", pod2);

        var alert3 = "Generic alert without metadata";
        var (ns3, deploy3, pod3) = Jarvis.Sharp.Slack.AlertMessageEventHandler.ExtractAlertTargets(alert3);
        Assert.Equal("media", ns3);
        Assert.Equal("vpn-media-sync-0", pod3);
    }

    [Fact]
    public async Task DownloadCommandHandler_ShouldRejectUnauthorizedUser()
    {
        var guard = new AuthorizationGuard("U_AUTHORIZED");
        var limiter = new RateLimiter();
        var store = new CandidateStateStore();
        var dispatcher = new MockDispatcher();
        var handler = new Jarvis.Sharp.Slack.DownloadCommandHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jarvis.Sharp.Slack.DownloadCommandHandler>.Instance,
            guard,
            limiter,
            store,
            dispatcher);

        var command = new SlackNet.Interaction.SlashCommand
        {
            UserId = "U_ATTACKER",
            Text = "1 2"
        };

        var response = await handler.Handle(command);
        Assert.Equal(SlackNet.Interaction.ResponseType.Ephemeral, response.ResponseType);
        Assert.Contains("⛔", response.Message.Text);
    }

    [Fact]
    public async Task DownloadCommandHandler_ShouldPromptOnEmptyInput()
    {
        var guard = new AuthorizationGuard("U_AUTHORIZED");
        var limiter = new RateLimiter();
        var store = new CandidateStateStore();
        var dispatcher = new MockDispatcher();
        var handler = new Jarvis.Sharp.Slack.DownloadCommandHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jarvis.Sharp.Slack.DownloadCommandHandler>.Instance,
            guard,
            limiter,
            store,
            dispatcher);

        var command = new SlackNet.Interaction.SlashCommand
        {
            UserId = "U_AUTHORIZED",
            Text = "   "
        };

        var response = await handler.Handle(command);
        Assert.Equal(SlackNet.Interaction.ResponseType.Ephemeral, response.ResponseType);
        Assert.Contains("Please specify candidate item numbers", response.Message.Text);
    }

    [Fact]
    public async Task AgyCommandHandler_ShouldRejectUnauthorizedUser()
    {
        var guard = new AuthorizationGuard("U_AUTHORIZED");
        var limiter = new RateLimiter();
        var k8sMock = new KubernetesDiagnostics(new k8s.Kubernetes(new k8s.KubernetesClientConfiguration { Host = "http://localhost:8080" }));
        var bridge = new Jarvis.Sharp.Services.AgentBridge(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jarvis.Sharp.Services.AgentBridge>.Instance,
            k8sMock);
        var handler = new Jarvis.Sharp.Slack.AgyCommandHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jarvis.Sharp.Slack.AgyCommandHandler>.Instance,
            guard,
            limiter,
            bridge);

        var command = new SlackNet.Interaction.SlashCommand
        {
            UserId = "U_ATTACKER",
            Text = "what is the cluster status?"
        };

        var response = await handler.Handle(command);
        Assert.Equal(SlackNet.Interaction.ResponseType.Ephemeral, response.ResponseType);
        Assert.Contains("⛔", response.Message.Text);
    }

    [Fact]
    public async Task PlexService_ShouldMatchNormalizedTitleAndYearViaPlexClient()
    {
        var handler = new MockPlexHttpHandler("""
            {"MediaContainer":{"Metadata":[{"title":"The Matrix: Reloaded","year":2003}]}}
            """);
        using var httpClient = new HttpClient(handler);
        using var plexClient = new Plex.Core.Http.PlexClient(
            httpClient,
            new Plex.Core.Http.PlexClientOptions
            {
                ServerUrl = "http://plex.local:32400",
                Token = "test-token"
            },
            disposeClient: false);

        var service = new PlexService(plexClient);
        var result = await service.QueryPlexLibraryAsync(
            "the matrix reloaded",
            2003,
            plexUrl: "http://plex.local:32400",
            plexToken: "test-token",
            existingInventoryPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));

        Assert.True(result.Exists);
        Assert.Equal("The Matrix: Reloaded", result.Title);
        Assert.Equal(2003, result.Year);
        Assert.Equal("Plex REST API", result.Source);
        Assert.Equal("http://plex.local:32400/search?query=the%20matrix%20reloaded", handler.LastRequestUri?.AbsoluteUri);
        Assert.Equal("test-token", handler.LastToken);
    }

    [Fact]
    public async Task PlexService_ShouldFallbackToLocalInventoryOnPlexError()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"plex-inv-{Guid.NewGuid()}.json");
        await File.WriteAllTextAsync(tempFile, """[{"title":"Dune","year":2021}]""");

        try
        {
            var handler = new MockPlexHttpHandler("error", System.Net.HttpStatusCode.InternalServerError);
            using var httpClient = new HttpClient(handler);
            using var plexClient = new Plex.Core.Http.PlexClient(
                httpClient,
                new Plex.Core.Http.PlexClientOptions
                {
                    ServerUrl = "http://plex.local:32400",
                    Token = "test-token"
                },
                disposeClient: false);

            var service = new PlexService(plexClient);
            var hit = await service.QueryPlexLibraryAsync(
                "Dune",
                2021,
                plexUrl: "http://plex.local:32400",
                plexToken: "test-token",
                existingInventoryPath: tempFile);

            Assert.True(hit.Exists);
            Assert.Equal("Dune", hit.Title);
            Assert.Equal(2021, hit.Year);
            Assert.Equal("Local Inventory File", hit.Source);

            var miss = await service.QueryPlexLibraryAsync(
                "Dune",
                1984,
                plexUrl: "http://plex.local:32400",
                plexToken: "test-token",
                existingInventoryPath: tempFile);

            Assert.False(miss.Exists);
            Assert.Equal("None", miss.Source);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    private sealed class MockPlexHttpHandler : HttpMessageHandler
    {
        private readonly string _payload;
        private readonly System.Net.HttpStatusCode _statusCode;

        public Uri? LastRequestUri { get; private set; }
        public string? LastToken { get; private set; }

        public MockPlexHttpHandler(string payload, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _payload = payload;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastToken = request.Headers.TryGetValues("X-Plex-Token", out var vals) ? vals.FirstOrDefault() : null;
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_payload, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task MediaSyncDispatcher_ShouldSendHmacSignature_AndOmitRawWebhookSecret()
    {
        var store = new CandidateStateStore();
        store.SaveBatch("batch-hmac", new[] { new CandidateItem(1, "Dune: Part Two", 2024, "Movie") });

        var httpHandler = new CaptureRequestHttpHandler();
        using var httpClient = new HttpClient(httpHandler);
        var dispatcher = new MediaSyncDispatcher(httpClient, store, new AlwaysMissingPlexService());

        var secret = "test-webhook-secret-32-bytes-long";
        var result = await dispatcher.DispatchApprovedCandidatesAsync(
            "batch-hmac",
            new[] { 1 },
            "U_ADMIN",
            webhookUrl: "http://media-sync.local/api/v1/sync/trigger",
            webhookSecret: secret);

        Assert.True(result.Dispatched);
        Assert.NotNull(httpHandler.LastRequest);
        Assert.False(httpHandler.LastRequest!.Headers.Contains("X-Webhook-Secret"));
        Assert.True(httpHandler.LastRequest.Headers.TryGetValues("X-Signature-SHA256", out var sigValues));
        var signature = Assert.Single(sigValues);
        Assert.True(HmacSigner.VerifySignature(secret, httpHandler.LastBody!, signature));
    }

    [Fact]
    public void CandidateStateStore_ShouldLogWarning_WhenPersistFails()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), $"jarvis-block-{Guid.NewGuid()}");
        File.WriteAllText(blockingFile, "not-a-directory");
        var invalidStatePath = Path.Combine(blockingFile, "state.json");

        try
        {
            var logger = new RecordingLogger<CandidateStateStore>();
            var store = new CandidateStateStore(stateFilePath: invalidStatePath, logger: logger);
            store.SaveBatch("batch-err", new[] { new CandidateItem(1, "Test", 2025, "Movie") });

            Assert.Contains(logger.Entries, e =>
                e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                e.Message.Contains("Failed to persist candidate state", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(blockingFile)) File.Delete(blockingFile);
        }
    }

    [Fact]
    public void Program_ValidateRequiredEnvironment_ShouldReportMissingVariables()
    {
        var allMissing = Program.ValidateRequiredEnvironment(_ => null);
        Assert.Equal(5, allMissing.Count);
        Assert.Contains("SLACK_APP_TOKEN", allMissing);
        Assert.Contains("SLACK_BOT_TOKEN", allMissing);
        Assert.Contains("USER_WHITELIST", allMissing);
        Assert.Contains("WEBHOOK_SECRET_TOKEN", allMissing);
        Assert.Contains("MEDIA_SYNC_WEBHOOK_URL", allMissing);

        var env = new Dictionary<string, string>
        {
            ["SLACK_APP_TOKEN"] = "xapp-test",
            ["SLACK_BOT_TOKEN"] = "xoxb-test",
            ["USER_WHITELIST"] = "U123",
            ["WEBHOOK_SECRET_TOKEN"] = "secret",
            ["MEDIA_SYNC_WEBHOOK_URL"] = "http://localhost:5050/trigger"
        };
        var noneMissing = Program.ValidateRequiredEnvironment(k => env.GetValueOrDefault(k));
        Assert.Empty(noneMissing);
    }

    [Fact]
    public async Task HealthProbeServer_ShouldServeHealthzAndReadyz()
    {
        using var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        var ready = false;
        await using var server = new HealthProbeServer(
            port,
            () => ready,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            host: "localhost");

        using var cts = new CancellationTokenSource();
        server.Start(cts.Token);

        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };

        var healthResp = await client.GetAsync("/healthz");
        Assert.Equal(System.Net.HttpStatusCode.OK, healthResp.StatusCode);

        var notReadyResp = await client.GetAsync("/readyz");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, notReadyResp.StatusCode);

        ready = true;
        var readyResp = await client.GetAsync("/readyz");
        Assert.Equal(System.Net.HttpStatusCode.OK, readyResp.StatusCode);

        var notFoundResp = await client.GetAsync("/unknown");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, notFoundResp.StatusCode);
    }

    [Fact]
    public async Task Worker_ShouldRetrySocketModeConnectWithExponentialBackoff()
    {
        var origApp = Environment.GetEnvironmentVariable("SLACK_APP_TOKEN");
        var origBot = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("SLACK_APP_TOKEN", "xapp-test-token");
            Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", "xoxb-test-token");

            var fakeSocket = new FakeSocketModeClient(failuresBeforeSuccess: 2);
            var worker = new Worker(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Worker>.Instance,
                new AuthorizationGuard("U_TEST"),
                new CandidateStateStore(),
                fakeSocket,
                initialRetryDelay: TimeSpan.FromMilliseconds(10),
                maxRetryDelay: TimeSpan.FromMilliseconds(40),
                healthPort: 0);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StartAsync(cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!worker.IsReady && DateTime.UtcNow < deadline)
            {
                await Task.Delay(15);
            }

            Assert.True(worker.IsReady);
            Assert.Equal(3, fakeSocket.ConnectAttempts);

            await worker.StopAsync(CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SLACK_APP_TOKEN", origApp);
            Environment.SetEnvironmentVariable("SLACK_BOT_TOKEN", origBot);
        }
    }

    private sealed class CaptureRequestHttpHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content != null ? await request.Content.ReadAsByteArrayAsync(cancellationToken) : null;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\"}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class AlwaysMissingPlexService : IPlexService
    {
        public Task<PlexQueryResult> QueryPlexLibraryAsync(
            string title,
            int? year = null,
            string? plexUrl = null,
            string? plexToken = null,
            string? existingInventoryPath = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PlexQueryResult(false, title, year, "None"));
        }
    }

    private sealed class RecordingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class FakeSocketModeClient : SlackNet.ISlackSocketModeClient
    {
        private readonly int _failuresBeforeSuccess;
        public int ConnectAttempts { get; private set; }
        public bool Connected { get; private set; }

        public FakeSocketModeClient(int failuresBeforeSuccess)
        {
            _failuresBeforeSuccess = failuresBeforeSuccess;
        }

        public Task Connect(SlackNet.SocketMode.SocketModeConnectionOptions? connectionOptions = null, CancellationToken cancellationToken = default)
        {
            ConnectAttempts++;
            if (ConnectAttempts <= _failuresBeforeSuccess)
            {
                throw new HttpRequestException("Transient DNS failure");
            }
            Connected = true;
            return Task.CompletedTask;
        }

        public void Disconnect()
        {
            Connected = false;
        }

        public Task DisconnectAsync()
        {
            Connected = false;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Connected = false;
            return Task.CompletedTask;
        }

        public void Dispose() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private class MockDispatcher : IMediaSyncDispatcher
    {
        public Task<DispatchResult> DispatchApprovedCandidatesAsync(
            string? batchId,
            IEnumerable<int> indices,
            string requestedBy,
            string? webhookUrl = null,
            string? webhookSecret = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DispatchResult(
                true,
                batchId ?? "mock-batch",
                Array.Empty<CandidateItem>(),
                Array.Empty<CandidateItem>(),
                "✅ Dispatched mock items"));
        }

        public Task<DispatchResult> DispatchDirectQueryAsync(
            string query,
            string requestedBy,
            string? webhookUrl = null,
            string? webhookSecret = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DispatchResult(
                true,
                "direct-mock",
                Array.Empty<CandidateItem>(),
                Array.Empty<CandidateItem>(),
                $"✅ Dispatched {query}"));
        }
    }

    [Fact]
    public async Task MediaSyncDispatcher_DispatchDirectQuery_ParsesTitleYearAndMediaType()
    {
        var handler = new CaptureRequestHttpHandler();
        var dispatcher = new MediaSyncDispatcher(
            new HttpClient(handler),
            new CandidateStateStore(),
            new AlwaysMissingPlexService());

        var result = await dispatcher.DispatchDirectQueryAsync(
            "tv: Severance 2022",
            "slack:U0CHW3DMRM2",
            webhookUrl: "http://media-sync.local:5050/api/v1/sync/trigger",
            webhookSecret: "test-secret");

        Assert.True(result.Dispatched);
        Assert.NotNull(handler.LastBody);
        var capturedBody = Encoding.UTF8.GetString(handler.LastBody);
        Assert.Contains("\"title\":\"Severance\"", capturedBody);
        Assert.Contains("\"year\":2022", capturedBody);
        Assert.Contains("\"media_type\":\"TV\"", capturedBody);
    }

    [Fact]
    public async Task DownloadCommandHandler_ShouldDispatchDirectTitle_WhenTitleContainsNumbers()
    {
        var guard = new AuthorizationGuard("U_AUTHORIZED");
        var limiter = new RateLimiter();
        var store = new CandidateStateStore();
        var dispatcher = new MockDispatcher();
        var handler = new Jarvis.Sharp.Slack.DownloadCommandHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jarvis.Sharp.Slack.DownloadCommandHandler>.Instance,
            guard,
            limiter,
            store,
            dispatcher);

        var command = new SlackNet.Interaction.SlashCommand
        {
            UserId = "U_AUTHORIZED",
            Text = "Batman Knightfall Part 2: Knightquest"
        };

        var response = await handler.Handle(command);
        Assert.Equal(SlackNet.Interaction.ResponseType.InChannel, response.ResponseType);
        Assert.Contains("Batman Knightfall Part 2: Knightquest", response.Message.Text);
    }

    [Fact]
    public async Task AgentBridge_AskAsync_ShouldReturnGuidance_WhenPromptIsEmpty()
    {
        var bridge = new AgentBridge(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentBridge>.Instance,
            new StubKubernetesDiagnostics(),
            agyBinary: "/nonexistent/agy");

        var reply = await bridge.AskAsync("   ");
        Assert.Equal("Please provide a non-empty prompt for Antigravity.", reply);
    }

    [Fact]
    public async Task AgentBridge_AskAsync_ShouldUseGeminiApi_WhenApiKeyConfigured()
    {
        var handler = new StubGeminiHttpHandler(
            System.Net.HttpStatusCode.OK,
            """{"candidates":[{"content":{"parts":[{"text":"Hi from Gemini!"}]}}]}""");
        var bridge = new AgentBridge(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentBridge>.Instance,
            new StubKubernetesDiagnostics(),
            httpClient: new HttpClient(handler),
            geminiApiKey: "test-gemini-key",
            geminiModel: "gemini-3.8-flash");

        var reply = await bridge.AskAsync("say hi", new Dictionary<string, string> { ["slack_user"] = "U123" });

        Assert.Equal("Hi from Gemini!", reply);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("models/gemini-3.8-flash:generateContent", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("test-gemini-key", handler.LastRequest.Headers.GetValues("x-goog-api-key").Single());
    }

    [Fact]
    public async Task AgentBridge_AskAsync_ShouldReturnErrorNotice_WhenGeminiApiFails()
    {
        var handler = new StubGeminiHttpHandler(System.Net.HttpStatusCode.Unauthorized, """{"error":"bad key"}""");
        var bridge = new AgentBridge(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentBridge>.Instance,
            new StubKubernetesDiagnostics(),
            httpClient: new HttpClient(handler),
            geminiApiKey: "bad-key");

        var reply = await bridge.AskAsync("say hi");
        Assert.Contains("HTTP 401", reply);
    }

    [Fact]
    public async Task AgentBridge_AskAsync_ShouldFallbackToNextModel_WhenPrimaryModelReturns503()
    {
        var requestedUris = new List<string>();
        var seqHandler = new SequenceGeminiHttpHandler(req =>
        {
            requestedUris.Add(req.RequestUri!.ToString());
            if (requestedUris.Count == 1)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{"error":{"code":503}}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"candidates":[{"content":{"parts":[{"text":"Fallback ok!"}]}}]}""", Encoding.UTF8, "application/json")
            };
        });

        var bridge = new AgentBridge(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentBridge>.Instance,
            new StubKubernetesDiagnostics(),
            httpClient: new HttpClient(seqHandler),
            geminiApiKey: "test-gemini-key",
            geminiModel: "gemini-3.8-flash");

        var reply = await bridge.AskAsync("say hi");

        Assert.Equal("Fallback ok!", reply);
        Assert.Equal(2, requestedUris.Count);
        Assert.Contains("models/gemini-3.8-flash:generateContent", requestedUris[0]);
        Assert.Contains("models/gemini-3.5-flash-lite:generateContent", requestedUris[1]);
    }

    [Fact]
    public async Task AgyCommandHandler_ShouldAckImmediatelyAndPostToResponseUrl_WhenResponseUrlPresent()
    {
        var tcs = new TaskCompletionSource<string>();
        var responseHandler = new CallbackHttpHandler(body => tcs.TrySetResult(body));
        var bridge = new StubAgentBridge("Hello from Jarvis!");
        var handler = new Jarvis.Sharp.Slack.AgyCommandHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Jarvis.Sharp.Slack.AgyCommandHandler>.Instance,
            new AuthorizationGuard("U_AUTH"),
            new RateLimiter(),
            bridge,
            httpClient: new HttpClient(responseHandler));

        var command = new SlackNet.Interaction.SlashCommand
        {
            UserId = "U_AUTH",
            ChannelId = "C123",
            Text = "say hi",
            ResponseUrl = "https://hooks.slack.com/commands/test/123"
        };

        var ack = await handler.Handle(command);
        Assert.Equal(SlackNet.Interaction.ResponseType.Ephemeral, ack.ResponseType);
        Assert.Contains("Asking Antigravity", ack.Message.Text);

        var postedBody = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Hello from Jarvis!", postedBody);
        Assert.Contains("\"response_type\":\"in_channel\"", postedBody);
    }

    private sealed class StubAgentBridge : IAgentBridge
    {
        private readonly string _reply;
        public StubAgentBridge(string reply) => _reply = reply;
        public Task<string> AskAsync(string prompt, IDictionary<string, string>? context = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_reply);
        public Task<string> TriageAlertAsync(string alertText, string @namespace, string deployment, string pod, CancellationToken cancellationToken = default)
            => Task.FromResult(_reply);
    }

    private sealed class SequenceGeminiHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public SequenceGeminiHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class StubGeminiHttpHandler : HttpMessageHandler
    {
        private readonly System.Net.HttpStatusCode _statusCode;
        private readonly string _body;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubGeminiHttpHandler(System.Net.HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CallbackHttpHandler : HttpMessageHandler
    {
        private readonly Action<string> _onBody;
        public CallbackHttpHandler(Action<string> onBody) => _onBody = onBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
            _onBody(body);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task AgentBridge_AskAsync_ShouldReturnFriendlyNotice_WhenBinaryIsMissing()
    {
        var bridge = new AgentBridge(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentBridge>.Instance,
            new StubKubernetesDiagnostics(),
            agyBinary: "/nonexistent/agy-binary-path");

        var reply = await bridge.AskAsync("check cluster status");
        Assert.Contains("Antigravity CLI (`/nonexistent/agy-binary-path`) invocation notice:", reply);
    }

    [Fact]
    public async Task AgentBridge_AskAsync_ShouldPassWorkspaceAddDir_WhenWorkspaceExists()
    {
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"jarvis-agy-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWorkspace);
        try
        {
            var bridge = new AgentBridge(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentBridge>.Instance,
                new StubKubernetesDiagnostics(),
                agyBinary: "/bin/echo",
                agyWorkspace: tempWorkspace);

            var reply = await bridge.AskAsync("status", new Dictionary<string, string> { ["channel_id"] = "C123" });
            Assert.Contains($"--add-dir {tempWorkspace}", reply);
            Assert.Contains("--prompt", reply);
            Assert.Contains("channel_id: C123", reply);
        }
        finally
        {
            Directory.Delete(tempWorkspace, recursive: true);
        }
    }

    private sealed class StubKubernetesDiagnostics : IKubernetesDiagnostics
    {
        public Task<PodDiagnosticsResult> GetPodDiagnosticsAsync(string ns, string pod, int tailLines = 80, CancellationToken cancellationToken = default)
            => Task.FromResult(new PodDiagnosticsResult(ns, pod, true, "Running", "ok"));

        public Task<RestartDeploymentResult> RestartDeploymentAsync(string ns, string deployment, CancellationToken cancellationToken = default)
            => Task.FromResult(new RestartDeploymentResult(ns, deployment, true, "restarted"));
    }
}
