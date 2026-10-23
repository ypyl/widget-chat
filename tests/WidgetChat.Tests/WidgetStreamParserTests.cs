using WidgetChat.Chat;
using WidgetChat.Llm;
using Xunit;

namespace WidgetChat.Tests;

public class WidgetStreamParserTests
{
    private const string Sample = """
        Two short sentences of prose.
        @@HEIGHT:420@@
        @@CSS@@
        .a { color: red; }
        @@HTML@@
        <div class="a">hello</div>
        @@JS@@
        var x = 1;
        @@END@@
        """;

    private static (string Text, int? Height, string Css, string Html, string Js) Collect(
        IEnumerable<string> chunks,
        WidgetStreamParser? parser = null)
    {
        parser ??= new WidgetStreamParser();
        var text = new System.Text.StringBuilder();
        var css = new System.Text.StringBuilder();
        var html = new System.Text.StringBuilder();
        var js = new System.Text.StringBuilder();
        int? height = null;

        void Apply(WidgetChunk chunk)
        {
            switch (chunk.Channel)
            {
                case WidgetChannel.Text: text.Append(chunk.Text); break;
                case WidgetChannel.Css: css.Append(chunk.Text); break;
                case WidgetChannel.Html: html.Append(chunk.Text); break;
                case WidgetChannel.Js: js.Append(chunk.Text); break;
                case WidgetChannel.Height: height = int.Parse(chunk.Text); break;
            }
        }

        foreach (var chunk in chunks)
        {
            foreach (var parsed in parser.Push(chunk))
            {
                Apply(parsed);
            }
        }

        foreach (var parsed in parser.Complete())
        {
            Apply(parsed);
        }

        return (text.ToString(), height, css.ToString(), html.ToString(), js.ToString());
    }

    [Fact]
    public void SplitsProseHeightAndChannels()
    {
        var result = Collect([Sample]);

        Assert.Equal("Two short sentences of prose.\n", result.Text);
        Assert.Equal(420, result.Height);
        Assert.Equal(".a { color: red; }\n", result.Css);
        Assert.Equal("<div class=\"a\">hello</div>\n", result.Html);
        Assert.Equal("var x = 1;\n", result.Js);
    }

    /// <summary>
    /// The property that matters: a marker split across any two chunks must parse identically to
    /// the unsplit text. Real token streams cut markers anywhere.
    /// </summary>
    [Fact]
    public void SplittingAnywhereYieldsTheSameResult()
    {
        var expected = Collect([Sample]);

        for (var split = 1; split < Sample.Length; split++)
        {
            var actual = Collect([Sample[..split], Sample[split..]]);
            Assert.True(expected == actual, $"Mismatch when split at index {split}");
        }
    }

    [Fact]
    public void SplittingIntoManySmallChunksYieldsTheSameResult()
    {
        var expected = Collect([Sample]);

        foreach (var size in new[] { 1, 2, 3, 5, 8, 13 })
        {
            var chunks = new List<string>();
            for (var i = 0; i < Sample.Length; i += size)
            {
                chunks.Add(Sample.Substring(i, Math.Min(size, Sample.Length - i)));
            }

            var actual = Collect(chunks);
            Assert.True(expected == actual, $"Mismatch with chunk size {size}");
        }
    }

    [Fact]
    public void HoldsBackAMarkerThatIsSplitAcrossChunks()
    {
        var parser = new WidgetStreamParser();

        // Prose before the marker is released immediately; the partial marker is held back.
        var beforeMarker = parser.Push("intro @@C");
        Assert.Single(beforeMarker);
        Assert.Equal(WidgetChannel.Text, beforeMarker[0].Channel);
        Assert.Equal("intro ", beforeMarker[0].Text);

        // Still nothing: the channel content is under the fence probe until a newline arrives.
        Assert.Empty(parser.Push("SS@@body {"));

        var afterCss = parser.Push(" }\nmore");
        Assert.Single(afterCss);
        Assert.Equal(WidgetChannel.Css, afterCss[0].Channel);
        Assert.Equal("body { }\nmore", afterCss[0].Text);
    }

    [Fact]
    public void DropsTheNewlineThatFollowsAMarker()
    {
        var result = Collect(["@@CSS@@\n.a{}@@HTML@@\n<div></div>@@JS@@\nvar a = 1;"]);

        Assert.Equal(".a{}", result.Css);
        Assert.Equal("<div></div>", result.Html);
        Assert.Equal("var a = 1;", result.Js);
    }

    [Fact]
    public void KeepsContentThatStartsOnTheMarkerLine()
    {
        var result = Collect(["@@CSS@@.inline{}@@END@@"]);

        Assert.Equal(".inline{}", result.Css);
    }

    [Fact]
    public void WhitespaceBetweenMarkersIsNotProse()
    {
        var result = Collect(["@@HEIGHT:300@@\n\n@@CSS@@\n.a{}@@END@@"]);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(300, result.Height);
    }

    [Fact]
    public void ProseOnlyProducesOneTextChunk()
    {
        var result = Collect(["Just prose, no widget."]);

        Assert.Equal("Just prose, no widget.", result.Text);
        Assert.Null(result.Height);
        Assert.Equal(string.Empty, result.Css);
        Assert.Equal(string.Empty, result.Js);
    }

    [Fact]
    public void StripsAMarkdownFenceTheModelAddedAnyway()
    {
        var result = Collect(["""
            @@CSS@@
            ```css
            body { margin: 0; }
            ```
            @@HTML@@
            <p>x</p>
            @@END@@
            """]);

        Assert.StartsWith("body { margin: 0; }", result.Css);
        Assert.DoesNotContain("```css", result.Css);
    }

    [Fact]
    public void IgnoresEverythingAfterTheEndMarker()
    {
        var result = Collect(["@@CSS@@a{}\n@@HTML@@b\n@@JS@@c\n@@END@@\nignored tail"]);

        Assert.Equal("a{}\n", result.Css);
        Assert.Equal("b\n", result.Html);
        Assert.Equal("c\n", result.Js);
        Assert.Equal(string.Empty, result.Text);
    }

    /// <summary>
    /// A height marker that lost its closing '@@' must end at the newline. Reading past it closes the
    /// marker on the next marker's opening '@@', which loses the whole channel.
    /// </summary>
    [Fact]
    public void EndsAnUnclosedHeightMarkerAtTheNewline()
    {
        const string Sample = "@@HEIGHT:420\n@@CSS@@\n.a{}@@HTML@@\n<i>x</i>@@END@@";
        var expected = Collect([Sample]);

        Assert.Equal(string.Empty, expected.Text);
        Assert.Equal(420, expected.Height);
        Assert.Equal(".a{}", expected.Css);
        Assert.Equal("<i>x</i>", expected.Html);

        for (var split = 1; split < Sample.Length; split++)
        {
            Assert.True(
                expected == Collect([Sample[..split], Sample[split..]]),
                $"Mismatch when split at index {split}");
        }
    }

    [Fact]
    public void KeepsProseAfterAnUnclosedHeightMarker()
    {
        var result = Collect(["@@HEIGHT:420\nno closing marker here"]);

        Assert.Equal(420, result.Height);
        Assert.Equal("\nno closing marker here", result.Text);
    }

    [Fact]
    public void TreatsAHeightMarkerWithJunkAsProse()
    {
        var result = Collect(["@@HEIGHT:not a number\n@@CSS@@\n.a{}"]);

        Assert.Null(result.Height);
        Assert.Equal(".a{}", result.Css);
        Assert.Contains("@@HEIGHT:not a number", result.Text);
    }

    [Fact]
    public void CompleteIsIdempotent()
    {
        var parser = new WidgetStreamParser();
        parser.Push("@@CSS@@a{}@@END@@");

        Assert.Empty(parser.Complete());
        Assert.Empty(parser.Complete());
        Assert.Empty(parser.Push("late"));
    }

    /// <summary>
    /// The Samples are the stub's replies and the worked examples in the article. If one drifts out
    /// of the wire format, the offline run renders nothing and nobody notices.
    /// </summary>
    [Fact]
    public void EverySampleStaysInTheWireFormatAndSurvivesAnyChunkSize()
    {
        var samples = new WidgetSamples();
        Assert.NotEmpty(samples.Names);

        foreach (var name in samples.Names)
        {
            var sample = samples.Get(name)!;
            var expected = Collect([sample]);

            Assert.True(expected.Height is > 0, $"{name}: no height marker");
            Assert.NotEmpty(expected.Css);
            Assert.NotEmpty(expected.Html);
            Assert.NotEmpty(expected.Js);

            foreach (var size in new[] { 1, 2, 3, 5, 8, 13, 21, 34 })
            {
                var chunks = new List<string>();
                for (var i = 0; i < sample.Length; i += size)
                {
                    chunks.Add(sample.Substring(i, Math.Min(size, sample.Length - i)));
                }

                Assert.True(expected == Collect(chunks), $"{name}: mismatch at chunk size {size}");
            }
        }
    }
}
