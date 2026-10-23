using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WidgetChat.Agents;
using WidgetChat.Llm;
using Xunit;

namespace WidgetChat.Tests;

public class ConversationSessionsTests
{
    private static readonly LlmSettings Settings =
        new("stub", "stub-widget", "http://localhost", IsStub: true, MaxOutputTokens: 0, Temperature: null);

    private static ConversationSessions Create(int capacity = 200, int maxTurns = 20) =>
        new(
            WidgetAgent.Create(new StubChatClient(new WidgetSamples(), TimeSpan.Zero), Settings),
            NullLogger<ConversationSessions>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sessions:Capacity"] = capacity.ToString(),
                    ["Sessions:MaxTurns"] = maxTurns.ToString(),
                })
                .Build());

    [Fact]
    public async Task GivesOneLeaseAtATimeAndKeepsTheSession()
    {
        var sessions = Create();

        var first = Assert.IsType<SessionLease>(await sessions.TryAcquireAsync("c", default));
        var session = first.Session;

        Assert.Null(await sessions.TryAcquireAsync("c", default));

        first.Dispose();

        var second = Assert.IsType<SessionLease>(await sessions.TryAcquireAsync("c", default));
        Assert.Same(session, second.Session);
        second.Dispose();
    }

    /// <summary>
    /// Trimming must never remove a conversation that is streaming: the next message would start a
    /// new session and the history would be gone without an error.
    /// </summary>
    [Fact]
    public async Task NeverEvictsAConversationThatIsStreaming()
    {
        var sessions = Create(capacity: 2);

        using var busy = Assert.IsType<SessionLease>(await sessions.TryAcquireAsync("busy", default));

        for (var i = 0; i < 10; i++)
        {
            using var other = await sessions.TryAcquireAsync("other" + i, default);
            Assert.NotNull(other);
        }

        Assert.Null(await sessions.TryAcquireAsync("busy", default));
    }

    [Fact]
    public async Task StartsAFreshSessionAfterTheTurnLimit()
    {
        var sessions = Create(maxTurns: 2);
        var seen = new List<AgentSession>();

        for (var i = 0; i < 4; i++)
        {
            using var lease = Assert.IsType<SessionLease>(await sessions.TryAcquireAsync("c", default));
            seen.Add(lease.Session);
        }

        Assert.Same(seen[0], seen[1]);
        Assert.NotSame(seen[1], seen[2]);
        Assert.Same(seen[2], seen[3]);
    }
}
