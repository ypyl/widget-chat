using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WidgetChat.Agents;
using WidgetChat.Chat;
using WidgetChat.Llm;
using Xunit;

namespace WidgetChat.Tests;

public class ChatRetryTests
{
    private static readonly LlmSettings Settings =
        new("stub", "stub-widget", "http://localhost", IsStub: true, MaxOutputTokens: 0, Temperature: null);

    private static ConversationSessions CreateSessions(AIAgent agent) =>
        new(
            agent,
            NullLogger<ConversationSessions>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sessions:Capacity"] = "10",
                    ["Sessions:MaxTurns"] = "20",
                })
                .Build());

    private static HttpContext Request(string message)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { sessionId = "c", message }, JsonSerializerOptions.Web)));
        http.Request.ContentType = "application/json";
        return http;
    }

    /// <summary>Records the messages of every call and answers from a queue. An empty string means no text.</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<string> _replies;
        public List<List<ChatMessage>> Calls { get; } = [];

        public ScriptedChatClient(params string[] replies) => _replies = new Queue<string>(replies);

        public ChatClientMetadata Metadata { get; } = new("scripted", defaultModelId: "scripted");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToList());
            var text = _replies.Count > 0 ? _replies.Dequeue() : "fallback";
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// An empty reply is retried. The retry must see the earlier turns; a clean session would erase
    /// them, and the user would get an answer that ignores the conversation so far.
    /// </summary>
    [Fact]
    public async Task RetryAfterAnEmptyReplyKeepsTheEarlierTurns()
    {
        var client = new ScriptedChatClient("First answer.", string.Empty, "Second answer.");
        var agent = WidgetAgent.Create(client, Settings);
        var sessions = CreateSessions(agent);

        await ChatEndpoints.ChatAsync(Request("first question"), agent, sessions, Settings, NullLoggerFactory.Instance);
        await ChatEndpoints.ChatAsync(Request("second question"), agent, sessions, Settings, NullLoggerFactory.Instance);

        Assert.Equal(3, client.Calls.Count);
        Assert.Contains(client.Calls[2], m => m.Role == ChatRole.User && m.Text == "first question");
    }
}
