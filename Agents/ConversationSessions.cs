using System.Collections.Concurrent;
using Microsoft.Agents.AI;

namespace WidgetChat.Agents;

/// <summary>
/// Hands out one <see cref="AgentSession"/> per browser conversation, with an exclusive lease so
/// two overlapping requests can never use the same session. Named to avoid colliding with
/// Microsoft.Agents.AI.AgentSessionStore, which is the persistence abstraction for hosted agents.
/// </summary>
public sealed class ConversationSessions
{
    private readonly AIAgent _agent;
    private readonly ILogger<ConversationSessions> _logger;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly int _maxTurns;

    public ConversationSessions(AIAgent agent, ILogger<ConversationSessions> logger, IConfiguration configuration)
    {
        _agent = agent;
        _logger = logger;
        _capacity = Math.Max(1, configuration.GetValue("Sessions:Capacity", 200));
        _maxTurns = Math.Max(1, configuration.GetValue("Sessions:MaxTurns", 20));
    }

    /// <summary>
    /// Takes exclusive use of a conversation. Returns null when another request is already
    /// streaming on the same session, because an AgentSession must not be used concurrently.
    /// </summary>
    public async Task<SessionLease?> TryAcquireAsync(string conversationId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(conversationId, _ => new Entry());

            if (!await entry.Gate.WaitAsync(TimeSpan.Zero, cancellationToken))
            {
                return null;
            }

            // Trim can remove an idle entry between the lookup above and the gate. Running on the
            // detached entry would silently start a new session on the next message, so take the
            // entry the dictionary holds now.
            if (!_entries.TryGetValue(conversationId, out var current) || !ReferenceEquals(current, entry))
            {
                entry.Gate.Release();
                continue;
            }

            try
            {
                // Every turn is appended to the session and resent with the next request, so a long
                // conversation grows more expensive each turn until it overflows the model's window.
                // Starting a fresh session bounds that.
                // shortcut: the model then forgets the earlier turns. Trim the history instead once
                // Agent Framework exposes the message list of a session.
                if (entry.Session is null || entry.Turns >= _maxTurns)
                {
                    entry.Session = await _agent.CreateSessionAsync(cancellationToken);
                    entry.Turns = 0;
                }

                entry.Turns++;
                Trim();
                return new SessionLease(entry);
            }
            catch
            {
                entry.Gate.Release();
                throw;
            }
        }
    }

    private void Trim()
    {
        if (_entries.Count <= _capacity)
        {
            return;
        }

        // Snapshot first: the loop removes from the dictionary these entries came from.
        var stale = _entries.ToArray()
            .Where(pair => pair.Value.Gate.CurrentCount == 1)
            .OrderBy(pair => pair.Value.LastUsed)
            .Take(_entries.Count - _capacity);

        foreach (var pair in stale)
        {
            // Claim the gate before removing. A conversation that started streaming after the filter
            // above must keep its entry, or its next message silently starts a new session.
            if (!pair.Value.Gate.Wait(0))
            {
                continue;
            }

            _entries.TryRemove(pair.Key, out _);
            pair.Value.Gate.Release();
            _logger.LogDebug("Evicted idle conversation {ConversationId}", pair.Key);
        }
    }

    internal sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AgentSession? Session { get; set; }
        public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
        public int Turns { get; set; }
    }
}

public sealed class SessionLease : IDisposable
{
    private bool _released;
    private readonly ConversationSessions.Entry _entry;

    internal SessionLease(ConversationSessions.Entry entry)
    {
        _entry = entry;
    }

    public AgentSession Session => _entry.Session
        ?? throw new InvalidOperationException("The lease has no session yet.");

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        // Age the entry by when the turn ended, not when it started, or a long turn looks stale.
        _entry.LastUsed = DateTimeOffset.UtcNow;
        _entry.Gate.Release();
    }
}
