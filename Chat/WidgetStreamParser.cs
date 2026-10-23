namespace WidgetChat.Chat;

public enum WidgetChannel
{
    Text,
    Height,
    Css,
    Html,
    Js,
}

/// <summary>One piece of a streamed model reply, already attributed to a channel.</summary>
public readonly record struct WidgetChunk(WidgetChannel Channel, string Text);

/// <summary>
/// Splits the model's plain-text reply into chat prose and the three widget channels.
///
/// The wire format is deliberately not tool calling: the widget must arrive in the order
/// css -> html -> js so the browser can apply each part as soon as it is complete. A single
/// text stream gives that ordering for free, and no partial-JSON reconstruction is needed.
///
/// Every marker starts with '@@', so a marker can never be split across chunks without the
/// trailing '@' being retained in the buffer. That single invariant is what makes the
/// incremental parser safe.
/// </summary>
public sealed class WidgetStreamParser
{
    public const string HeightMarker = "@@HEIGHT:";
    public const string CssMarker = "@@CSS@@";
    public const string HtmlMarker = "@@HTML@@";
    public const string JsMarker = "@@JS@@";
    public const string EndMarker = "@@END@@";

    private static readonly string[] Markers = [HeightMarker, CssMarker, HtmlMarker, JsMarker, EndMarker];

    /// <summary>Enough characters to recognise an opening code fence such as ```javascript.</summary>
    private const int HeadProbeLength = 16;

    private readonly System.Text.StringBuilder _buffer = new();

    // Held back until we can tell whether a channel starts with a markdown code fence.
    private System.Text.StringBuilder? _head;
    private WidgetChannel _channel = WidgetChannel.Text;
    private bool _finished;
    private bool _sawAnyMarker;

    /// <summary>False when the reply stopped before @@END@@, which usually means truncation.</summary>
    public bool SawEndMarker => _finished;

    public List<WidgetChunk> Push(string? delta)
    {
        var output = new List<WidgetChunk>();
        if (_finished || string.IsNullOrEmpty(delta))
        {
            return output;
        }

        _buffer.Append(delta);
        Drain(output, final: false);
        return output;
    }

    public List<WidgetChunk> Complete()
    {
        var output = new List<WidgetChunk>();
        if (_finished)
        {
            return output;
        }

        Drain(output, final: true);
        FlushHead(output);
        _finished = true;
        return output;
    }

    private void Drain(List<WidgetChunk> output, bool final)
    {
        while (true)
        {
            var text = _buffer.ToString();
            if (text.Length == 0)
            {
                return;
            }

            if (Scan(text, final, out var position, out var marker, out var length, out var payload, out var partial))
            {
                if (position > 0)
                {
                    Emit(output, text[..position]);
                }

                _buffer.Remove(0, position + length);

                if (!Activate(marker, payload, output))
                {
                    return;
                }

                continue;
            }

            if (final)
            {
                // On the final pass Scan never reports a partial marker, so anything left here is
                // content: an unterminated marker, stray '@@', or plain prose. Emit all of it.
                Emit(output, text);
                _buffer.Clear();
                return;
            }

            if (partial >= 0)
            {
                if (partial > 0)
                {
                    Emit(output, text[..partial]);
                }

                _buffer.Remove(0, partial);
                return;
            }

            // No '@' suffix can begin a marker, so nothing spans the chunk boundary.
            Emit(output, text);
            _buffer.Clear();
            return;
        }
    }

    /// <summary>
    /// Finds the first complete marker. When none is complete, <paramref name="partial"/> receives
    /// the index of a possible marker whose remainder has not arrived yet, or -1.
    /// </summary>
    private static bool Scan(
        string text,
        bool final,
        out int position,
        out string marker,
        out int length,
        out string payload,
        out int partial)
    {
        position = -1;
        marker = string.Empty;
        length = 0;
        payload = string.Empty;
        partial = -1;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '@')
            {
                continue;
            }

            var rest = text.AsSpan(i);

            if (rest.StartsWith(HeightMarker, StringComparison.Ordinal))
            {
                // The value ends at the closing '@@' or, when the model forgot it, at the end of the
                // line. Searching past the newline would close the marker on the next marker's
                // opening '@@', which loses that channel and leaks its name into the prose.
                var valueStart = i + HeightMarker.Length;
                var newline = text.IndexOf('\n', valueStart);
                var lineEnd = newline < 0 ? text.Length : newline;
                var close = text.IndexOf("@@", valueStart, lineEnd - valueStart, StringComparison.Ordinal);

                if (close >= 0)
                {
                    position = i;
                    marker = HeightMarker;
                    length = close + 2 - i;
                    payload = text[valueStart..close].Trim();
                    return true;
                }

                if (newline < 0)
                {
                    if (!final)
                    {
                        partial = i;
                        return false;
                    }

                    continue;
                }

                var value = text[valueStart..newline].Trim();
                if (int.TryParse(value, out _))
                {
                    position = i;
                    marker = HeightMarker;
                    length = newline - i;
                    payload = value;
                    return true;
                }

                // Not a height marker after all, so it is prose.
                continue;
            }

            for (var m = 1; m < Markers.Length; m++)
            {
                if (rest.StartsWith(Markers[m], StringComparison.Ordinal))
                {
                    position = i;
                    marker = Markers[m];
                    length = Markers[m].Length;
                    return true;
                }
            }

            if (!final && IsMarkerPrefix(rest))
            {
                partial = i;
                return false;
            }
        }

        return false;
    }

    private static bool IsMarkerPrefix(ReadOnlySpan<char> rest)
    {
        foreach (var candidate in Markers)
        {
            if (candidate.AsSpan().StartsWith(rest, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Applies a marker. Returns false once the reply is finished.</summary>
    private bool Activate(string marker, string payload, List<WidgetChunk> output)
    {
        switch (marker)
        {
            case HeightMarker:
                _sawAnyMarker = true;
                output.Add(new WidgetChunk(WidgetChannel.Height, payload));
                return true;

            case CssMarker:
                FlushHead(output);
                _channel = WidgetChannel.Css;
                _head = new System.Text.StringBuilder();
                _sawAnyMarker = true;
                return true;

            case HtmlMarker:
                FlushHead(output);
                _channel = WidgetChannel.Html;
                _head = new System.Text.StringBuilder();
                _sawAnyMarker = true;
                return true;

            case JsMarker:
                FlushHead(output);
                _channel = WidgetChannel.Js;
                _head = new System.Text.StringBuilder();
                _sawAnyMarker = true;
                return true;

            case EndMarker:
                FlushHead(output);
                _buffer.Clear();
                _finished = true;
                return false;

            default:
                return true;
        }
    }

    private void Emit(List<WidgetChunk> output, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (_channel == WidgetChannel.Text)
        {
            // Whitespace between the height marker and the first content marker is layout, not
            // prose. Without this the chat bubble gains a stray blank line before the widget.
            if (_sawAnyMarker && text.Trim().Length == 0)
            {
                return;
            }

            output.Add(new WidgetChunk(WidgetChannel.Text, text));
            return;
        }

        if (_head is null)
        {
            output.Add(new WidgetChunk(_channel, text));
            return;
        }

        _head.Append(text);
        var probe = _head.ToString();

        // Release as soon as the fence question is settled.
        if (probe.Contains('\n') || probe.Length >= HeadProbeLength)
        {
            _head = null;
            var cleaned = CleanChannelHead(probe);
            if (cleaned.Length > 0)
            {
                output.Add(new WidgetChunk(_channel, cleaned));
            }
        }
    }

    private void FlushHead(List<WidgetChunk> output)
    {
        if (_head is null)
        {
            return;
        }

        var cleaned = CleanChannelHead(_head.ToString());
        _head = null;
        if (cleaned.Length > 0)
        {
            output.Add(new WidgetChunk(_channel, cleaned));
        }
    }

    /// <summary>
    /// Drops the two artifacts a model reliably leaves at the start of a channel: an opening
    /// markdown fence it was told not to add, and the newline that follows the marker itself.
    /// </summary>
    internal static string CleanChannelHead(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var fenceEnd = trimmed.IndexOf('\n');
            if (fenceEnd < 0)
            {
                return string.Empty;
            }

            return DropLeadingBlankLines(trimmed[(fenceEnd + 1)..]);
        }

        return DropLeadingBlankLines(text);
    }

    private static string DropLeadingBlankLines(string text)
    {
        var index = 0;
        while (index < text.Length && (text[index] == ' ' || text[index] == '\t' || text[index] == '\r'))
        {
            index++;
        }

        if (index < text.Length && text[index] == '\n')
        {
            index++;
            while (index < text.Length && (text[index] == '\n' || text[index] == '\r'))
            {
                index++;
            }

            return text[index..];
        }

        // No newline yet: the content started on the same line as the marker, so keep it as is.
        return text;
    }
}
