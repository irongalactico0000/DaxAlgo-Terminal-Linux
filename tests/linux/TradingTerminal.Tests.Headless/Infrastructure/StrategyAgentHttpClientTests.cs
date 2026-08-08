using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Infrastructure.StrategyAgent;
using Xunit;

namespace TradingTerminal.Tests.Infrastructure;

public sealed class StrategyAgentHttpClientTests
{
    [Fact]
    public async Task Client_uses_existing_lifecycle_routes_and_snake_case_contracts()
    {
        var handler = new RecordingHandler(RouteResponse);
        var host = new AvailableHost();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8766/"),
        };
        var client = new StrategyAgentHttpClient(http, host);
        var context = JsonSerializer.SerializeToElement(new { symbol = "FDAX" });
        var intent = JsonSerializer.SerializeToElement(new { family = "directional_long_short" });
        var manifest = new StrategyAgentRunManifest(
            RunId: "run-1",
            ConfirmedIntentSha256: new string('a', 64),
            ResearchContextSha256: new string('b', 64),
            SelectedStartUtc: DateTimeOffset.Parse("2026-08-08T08:00:00Z"),
            SelectedEndUtc: DateTimeOffset.Parse("2026-08-08T10:00:00Z"),
            AsOfUtc: DateTimeOffset.Parse("2026-08-08T10:05:00Z"),
            TimezoneName: "UTC",
            DataFiles:
            [
                new StrategyAgentDataFile(
                    "primary",
                    "FDAX",
                    "EUREX",
                    "fixture",
                    "5m",
                    "inputs/fdax.csv",
                    new string('c', 64)),
            ],
            Components:
            [
                new StrategyAgentComponentPin("query_engine", "source", new string('d', 40)),
                new StrategyAgentComponentPin("vibequant", "0.1.0", new string('e', 40)),
                new StrategyAgentComponentPin("akquant", "0.3.36"),
                new StrategyAgentComponentPin("csp", "0.18.0"),
            ]);

        (await client.CreateSessionAsync(context)).SessionId.Should().Be("session-1");
        await client.GetSessionAsync("session-1");
        await client.SubmitMessageAsync("session-1", "Check the jump");
        (await client.ConfirmAsync("session-1", manifest, "/tmp/frozen", intent))
            .RunId.Should().Be("run-1");
        await client.StartAsync("run-1");
        await client.GetRunAsync("run-1");
        var sessionEvents = await client.GetSessionEventsAsync("session-1", 4, 25);
        sessionEvents.NextAfterSeq.Should().Be(5);
        sessionEvents.HasMore.Should().BeTrue();
        (await client.GetRunEventsAsync("run-1", 5, 50)).Terminal.Should().BeTrue();
        var artifact = await client.GetArtifactAsync("run-1", "native/csp/strategy.py");
        artifact.Encoding.Should().Be("utf-8");
        artifact.Content.Should().Be("# genuine csp fixture\n");
        await client.CancelAsync("run-1");

        handler.Requests.Select(static request => $"{request.Method} {request.Path}")
            .Should()
            .Equal(
                "POST /api/v1/strategy-sessions",
                "GET /api/v1/strategy-sessions/session-1",
                "POST /api/v1/strategy-sessions/session-1/messages",
                "POST /api/v1/strategy-sessions/session-1/confirm",
                "POST /api/v1/strategy-runs/run-1/start",
                "GET /api/v1/strategy-runs/run-1",
                "GET /api/v1/strategy-sessions/session-1/events?after_seq=4&limit=25",
                "GET /api/v1/strategy-runs/run-1/events?after_seq=5&limit=50",
                "GET /api/v1/strategy-runs/run-1/artifacts?path=native%2Fcsp%2Fstrategy.py",
                "POST /api/v1/strategy-runs/run-1/cancel");
        host.CallCount.Should().Be(10);

        using var confirmBody = JsonDocument.Parse(handler.Requests[3].Body!);
        var root = confirmBody.RootElement;
        root.GetProperty("input_workspace").GetString().Should().Be("/tmp/frozen");
        root.GetProperty("confirmed_intent").GetProperty("family").GetString()
            .Should().Be("directional_long_short");
        root.GetProperty("manifest").GetProperty("schema_version").GetString()
            .Should().Be("daxalgo-native-run-manifest/v1");
        root.GetProperty("manifest").GetProperty("data_files")[0]
            .GetProperty("relative_path").GetString().Should().Be("inputs/fdax.csv");
    }

    [Fact]
    public async Task Client_preserves_actionable_service_error_code_and_message()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.Conflict,
            """{"detail":{"code":"research_context_hash_mismatch","message":"manifest does not bind this session"}}"""));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8766/"),
        };
        var client = new StrategyAgentHttpClient(http, new AvailableHost());

        var action = () => client.GetRunAsync("run-1");

        var error = await action.Should().ThrowAsync<StrategyAgentApiException>();
        error.Which.Code.Should().Be("research_context_hash_mismatch");
        error.Which.Message.Should().Be("manifest does not bind this session");
        error.Which.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Client_does_not_issue_request_when_exact_service_is_unavailable()
    {
        var handler = new RecordingHandler(_ => RouteResponse(new RecordedRequest("GET", "/", null)));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8766/"),
        };
        var client = new StrategyAgentHttpClient(http, new UnavailableHost());

        var action = () => client.GetRunAsync("run-1");

        var error = await action.Should().ThrowAsync<StrategyAgentApiException>();
        error.Which.Code.Should().Be("strategy_agent_unavailable");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Client_preserves_cancellation_while_waiting_for_host_startup()
    {
        var handler = new RecordingHandler(_ => RouteResponse(new RecordedRequest("GET", "/", null)));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8766/"),
        };
        using var cancellation = new CancellationTokenSource();
        var client = new StrategyAgentHttpClient(http, new CancellingHost(cancellation));

        var action = () => client.GetRunAsync("run-1", cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        handler.Requests.Should().BeEmpty();
    }

    private static HttpResponseMessage RouteResponse(RecordedRequest request)
    {
        if (request.Path.Contains("/artifacts", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, ArtifactJson);
        if (request.Path.Contains("/events", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, EventPageJson);
        if (request.Path.Contains("/strategy-runs/", StringComparison.Ordinal) ||
            request.Path.EndsWith("/confirm", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, RunStatusJson);
        return Json(HttpStatusCode.OK, SessionStatusJson);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private const string SessionStatusJson = """
        {
          "session_id":"session-1","status":"researching",
          "created_at_utc":"2026-08-08T00:00:00Z","confirmed_run_id":null,
          "message_count":1,"last_event_sequence":3,"context":{"symbol":"FDAX"}
        }
        """;

    private const string RunStatusJson = """
        {
          "run_id":"run-1","session_id":"session-1","manifest_sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "status":"completed","cancel_requested":false,"fixed_lanes":["vibequant","csp"],
          "lane_states":{"vibequant":"passed","csp":"passed"},"results":{},
          "comparison":null,"evidence_status":"partially_proven","last_event_sequence":8
        }
        """;

    private const string EventPageJson = """
        {
          "events":[{
            "sequence":5,"session_id":null,"run_id":"run-1","lane":"comparison",
            "stage":"workflow_completion","status":"passed",
            "occurred_at_utc":"2026-08-08T00:00:00Z","message":"complete","details":{}
          }],
          "next_after_seq":5,"has_more":true,"terminal":true
        }
        """;

    private const string ArtifactJson = """
        {
          "run_id":"run-1","relative_path":"native/csp/strategy.py",
          "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
          "size_bytes":22,"encoding":"utf-8","content":"# genuine csp fixture\n"
        }
        """;

    private sealed class RecordingHandler(
        Func<RecordedRequest, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(recorded);
            return responseFactory(recorded);
        }
    }

    private sealed class AvailableHost : IStrategyAgentHost
    {
        public bool IsRunning => true;
        public int CallCount { get; private set; }

        public Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(true);
        }
    }

    private sealed class UnavailableHost : IStrategyAgentHost
    {
        public bool IsRunning => false;

        public Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class CancellingHost(CancellationTokenSource cancellation) : IStrategyAgentHost
    {
        public bool IsRunning => false;

        public Task<bool> EnsureRunningAsync(CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.FromResult(false);
        }
    }

    private sealed record RecordedRequest(string Method, string Path, string? Body);
}
