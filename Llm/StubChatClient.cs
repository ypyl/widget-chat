using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;

namespace WidgetChat.Llm;

/// <summary>
/// Offline stand-in for a chat model. It streams one of the reference widgets so the whole
/// pipeline (parser, NDJSON transport, iframe host, Three.js) can be exercised without
/// credentials. Every chunk boundary is chosen to include a deliberately awkward split.
/// </summary>
public sealed class StubChatClient : IChatClient
{
    private readonly WidgetSamples _samples;
    private readonly TimeSpan _delayPerChunk;

    public StubChatClient(WidgetSamples samples, TimeSpan? delayPerChunk = null)
    {
        _samples = samples;
        _delayPerChunk = delayPerChunk ?? TimeSpan.FromMilliseconds(8);
    }

    public ChatClientMetadata Metadata { get; } = new("stub", defaultModelId: "stub-widget");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            text.Append(update.Text);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text.ToString()))
        {
            ModelId = "stub-widget",
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        var prompt = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var reply = _samples.PickStubReply(prompt);

        foreach (var slice in Slice(reply))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_delayPerChunk > TimeSpan.Zero)
            {
                await Task.Delay(_delayPerChunk, cancellationToken);
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, slice)
            {
                ModelId = "stub-widget",
            };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    /// <summary>Chops text at uneven boundaries so marker splits happen for real.</summary>
    private static IEnumerable<string> Slice(string text)
    {
        var index = 0;
        var step = 3;
        while (index < text.Length)
        {
            var length = Math.Min(step % 37 + 3, text.Length - index);
            yield return text.Substring(index, length);
            index += length;
            step += 7;
        }
    }
}
