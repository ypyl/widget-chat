using System.Reflection;
using System.Text;

namespace WidgetChat.Llm;

/// <summary>
/// Loads the reference widgets compiled into the assembly. They are the canned replies for the
/// offline stub provider, and worked examples of the channel format for anyone reading the repo.
/// </summary>
public sealed class WidgetSamples
{
    private readonly Dictionary<string, string> _samples = new(StringComparer.OrdinalIgnoreCase);

    public WidgetSamples()
    {
        foreach (var resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
        {
            if (!resource.EndsWith(".widget.txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = resource[..^".widget.txt".Length];
            var dot = name.LastIndexOf('.');
            if (dot >= 0)
            {
                name = name[(dot + 1)..];
            }

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            _samples[name] = reader.ReadToEnd();
        }
    }

    public IReadOnlyCollection<string> Names => _samples.Keys;

    public string? Get(string? name) =>
        name is not null && _samples.TryGetValue(name.Trim(), out var sample) ? sample : null;

    /// <summary>Picks a canned reply for the stub provider based on the prompt.</summary>
    public string PickStubReply(string prompt)
    {
        var wantsChart = prompt.Contains("chart", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("svg", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("graph", StringComparison.OrdinalIgnoreCase);

        var name = wantsChart ? "svg-chart" : "three-cube";
        return Get(name) ?? throw new InvalidOperationException($"Missing sample '{name}'.");
    }
}
