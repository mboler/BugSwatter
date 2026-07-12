namespace BugSwatter.AI.Tests;

public sealed class SourceChunkerTests
{
    [Fact]
    public void EmptyInputProducesNoChunks()
    {
        Assert.Empty(SourceChunker.Split([], 100, 1000));
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(100, 0)]
    public void NonPositiveLimitsAreRejected(int maxLines, int maxCharacters)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceChunker.Split(["line"], maxLines, maxCharacters));
    }

    [Fact]
    public void SmallFileComesBackAsOneChunk()
    {
        string[] lines = [.. Enumerable.Range(1, 50).Select(i => $"line {i}")];
        SourceChunk chunk = Assert.Single(SourceChunker.Split(lines, 800, 100000));
        Assert.Equal(new SourceChunk(1, 50, false), chunk);
    }

    [Fact]
    public void SplitsAtMethodClosingBraces()
    {
        string[] lines = BuildCSharpClass(methodCount: 10, bodyLines: 20);
        IReadOnlyList<SourceChunk> chunks = SourceChunker.Split(lines, 60, 100000);
        Assert.True(chunks.Count > 1);
        AssertContiguousCoverage(chunks, lines.Length);
        foreach (SourceChunk chunk in chunks.Take(chunks.Count - 1))
        {
            Assert.False(chunk.HardCut);
            string cutLine = lines[chunk.EndLine - 1].Trim();
            Assert.True(cutLine is "}" or "" or "};", $"chunk ended mid-block on line {chunk.EndLine}: '{cutLine}'");
        }
        Assert.All(chunks, chunk => Assert.True(chunk.EndLine - chunk.StartLine + 1 <= 60));
    }

    [Fact]
    public void NeverCutsInsideAMethodWhenBoundariesExist()
    {
        string[] lines = BuildCSharpClass(methodCount: 8, bodyLines: 25);
        var methodSpans = new List<(int Start, int End)>();
        for (int index = 0; index < lines.Length; index++)
        {
            if (lines[index].Contains("public void Method"))
            {
                int end = index;
                while (lines[end].Trim() != "}")
                {
                    end++;
                }
                methodSpans.Add((index + 1, end + 1));
            }
        }
        foreach (SourceChunk chunk in SourceChunker.Split(lines, 70, 100000))
        {
            foreach ((int start, int end) in methodSpans)
            {
                Assert.False(chunk.EndLine >= start && chunk.EndLine < end, $"chunk boundary {chunk.EndLine} falls inside method spanning {start}-{end}");
            }
        }
    }

    [Fact]
    public void HardCutsWhenNoBoundaryExists()
    {
        string[] lines = [.. Enumerable.Range(1, 200).Select(i => $"data row {i} with no blank lines or braces")];
        IReadOnlyList<SourceChunk> chunks = SourceChunker.Split(lines, 80, 1000000);
        Assert.Equal(3, chunks.Count);
        Assert.True(chunks[0].HardCut);
        Assert.Equal(80, chunks[0].EndLine);
        AssertContiguousCoverage(chunks, 200);
        Assert.False(chunks[2].HardCut);
    }

    [Fact]
    public void BlankLinesActAsBoundariesInNonBraceText()
    {
        var lines = new List<string>();
        for (int paragraph = 0; paragraph < 10; paragraph++)
        {
            lines.AddRange(Enumerable.Range(1, 9).Select(i => $"paragraph {paragraph} line {i}"));
            lines.Add("");
        }
        IReadOnlyList<SourceChunk> chunks = SourceChunker.Split([.. lines], 25, 100000);
        Assert.True(chunks.Count > 1);
        AssertContiguousCoverage(chunks, lines.Count);
        foreach (SourceChunk chunk in chunks.Take(chunks.Count - 1))
        {
            Assert.False(chunk.HardCut);
            Assert.Equal("", lines[chunk.EndLine - 1]);
        }
    }

    [Fact]
    public void CharacterBudgetForcesSplitEvenUnderLineLimit()
    {
        string[] lines = [.. Enumerable.Range(1, 40).Select(i => new string('x', 500))];
        IReadOnlyList<SourceChunk> chunks = SourceChunker.Split(lines, 800, 5000);
        Assert.True(chunks.Count > 1);
        AssertContiguousCoverage(chunks, 40);
    }

    private static string[] BuildCSharpClass(int methodCount, int bodyLines)
    {
        var lines = new List<string> { "namespace Sample;", "", "public class Widget", "{" };
        for (int method = 0; method < methodCount; method++)
        {
            lines.Add($"    public void Method{method}()");
            lines.Add("    {");
            lines.AddRange(Enumerable.Range(1, bodyLines).Select(i => $"        DoWork({method}, {i});"));
            lines.Add("    }");
            lines.Add("");
        }
        lines.Add("}");
        return [.. lines];
    }

    private static void AssertContiguousCoverage(IReadOnlyList<SourceChunk> chunks, int totalLines)
    {
        Assert.Equal(1, chunks[0].StartLine);
        Assert.Equal(totalLines, chunks[^1].EndLine);
        for (int index = 1; index < chunks.Count; index++)
        {
            Assert.Equal(chunks[index - 1].EndLine + 1, chunks[index].StartLine);
        }
    }
}
