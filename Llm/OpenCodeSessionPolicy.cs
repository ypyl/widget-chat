using System.ClientModel.Primitives;

namespace WidgetChat.Llm;

/// <summary>
/// Adds the headers OpenCode Go asks clients to send: a stable per-conversation session id, which
/// it uses for routing and prompt caching. The value travels in an AsyncLocal so one shared client
/// can serve concurrent conversations without leaking ids between them.
/// </summary>
public sealed class OpenCodeSessionPolicy : PipelinePolicy
{
    public const string DefaultHeaderName = "x-opencode-session";

    private static readonly AsyncLocal<string?> CurrentSession = new();
    private readonly string _headerName;

    public OpenCodeSessionPolicy(string headerName) => _headerName = headerName;

    /// <summary>Sets the session id for the current request flow. Clear it when the run completes.</summary>
    public static IDisposable Use(string? sessionId)
    {
        var previous = CurrentSession.Value;
        CurrentSession.Value = sessionId;
        return new Restore(previous);
    }

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        Apply(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    private void Apply(PipelineMessage message)
    {
        var sessionId = CurrentSession.Value;
        if (!string.IsNullOrEmpty(sessionId))
        {
            message.Request.Headers.Set(_headerName, sessionId);
        }
    }

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;

        public Restore(string? previous) => _previous = previous;

        public void Dispose() => CurrentSession.Value = _previous;
    }
}
