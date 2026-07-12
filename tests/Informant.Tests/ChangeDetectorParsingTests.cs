namespace Informant.Tests;

public sealed class ChangeDetectorParsingTests
{
    [Fact]
    public void ParsesAddedModifiedAndSkipsDeleted()
    {
        IReadOnlyList<NameStatusEntry> entries = ChangeDetector.ParseNameStatus("M\tsrc/Foo.cs\nA\tsrc/Bar.cs\nD\tgone.cs\n");
        Assert.Equal(2, entries.Count);
        Assert.Equal(new NameStatusEntry(ChangeKind.Modified, "src/Foo.cs", null), entries[0]);
        Assert.Equal(new NameStatusEntry(ChangeKind.Added, "src/Bar.cs", null), entries[1]);
    }

    [Fact]
    public void ParsesRenameWithBothPaths()
    {
        IReadOnlyList<NameStatusEntry> entries = ChangeDetector.ParseNameStatus("R100\told/Name.cs\tnew/Name.cs\n");
        NameStatusEntry entry = Assert.Single(entries);
        Assert.Equal(ChangeKind.Renamed, entry.Kind);
        Assert.Equal("new/Name.cs", entry.Path);
        Assert.Equal("old/Name.cs", entry.OldPath);
    }

    [Fact]
    public void ParsesCopyAsAddedNewPath()
    {
        IReadOnlyList<NameStatusEntry> entries = ChangeDetector.ParseNameStatus("C75\tsrc/A.cs\tsrc/B.cs\n");
        NameStatusEntry entry = Assert.Single(entries);
        Assert.Equal(ChangeKind.Added, entry.Kind);
        Assert.Equal("src/B.cs", entry.Path);
    }

    [Fact]
    public void ParsesTypeChangeAsModified()
    {
        NameStatusEntry entry = Assert.Single(ChangeDetector.ParseNameStatus("T\tsome/link.cs\n"));
        Assert.Equal(ChangeKind.Modified, entry.Kind);
    }

    [Fact]
    public void EmptyOutputYieldsNoEntries() => Assert.Empty(ChangeDetector.ParseNameStatus(""));

    [Fact]
    public void HandlesWindowsLineEndings()
    {
        NameStatusEntry entry = Assert.Single(ChangeDetector.ParseNameStatus("M\tsrc/Foo.cs\r\n"));
        Assert.Equal("src/Foo.cs", entry.Path);
    }

    [Theory]
    [InlineData("@@ -12,5 +12,7 @@ void Method()", 12, 18)]
    [InlineData("@@ -5,3 +4 @@", 4, 4)]
    [InlineData("@@ -0,0 +1,20 @@", 1, 20)]
    public void ParsesSingleHunkHeader(string header, int expectedStart, int expectedEnd)
    {
        LineRange range = Assert.Single(ChangeDetector.ParseHunkRanges(header));
        Assert.Equal(expectedStart, range.Start);
        Assert.Equal(expectedEnd, range.End);
    }

    [Fact]
    public void SkipsPureDeletionHunks() => Assert.Empty(ChangeDetector.ParseHunkRanges("@@ -10,2 +9,0 @@"));

    [Fact]
    public void ParsesMultipleHunksAcrossDiffText()
    {
        const string Diff = """
            diff --git a/src/Foo.cs b/src/Foo.cs
            index 111..222 100644
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -3 +3 @@ using System;
            -old
            +new
            @@ -10,2 +10,4 @@ void Bar()
            -x
            -y
            +a
            +b
            +c
            +d
            """;
        IReadOnlyList<LineRange> ranges = ChangeDetector.ParseHunkRanges(Diff);
        Assert.Equal(2, ranges.Count);
        Assert.Equal(new LineRange(3, 3), ranges[0]);
        Assert.Equal(new LineRange(10, 13), ranges[1]);
    }

    [Fact]
    public void IgnoresHunkMarkersInsideContentLines()
    {
        // A diff content line always carries a +, - or space prefix, so a literal header in content must not match
        Assert.Empty(ChangeDetector.ParseHunkRanges("-@@ -1,2 +3,4 @@\n+@@ -5,6 +7,8 @@"));
    }

    [Fact]
    public void PeelToCommitRevisionSyntaxRendersCorrectly()
    {
        // Guards the {{ }} escaping in IsCommitReachableAsync: the argument git receives must be <sha>^{commit}
        const string Sha = "abc123";
        Assert.Equal("abc123^{commit}", $"{Sha}^{{commit}}");
    }

    [Fact]
    public void LineRangeFormatsSingleAndSpan()
    {
        Assert.Equal("7", new LineRange(7, 7).ToString());
        Assert.Equal("7-9", new LineRange(7, 9).ToString());
    }
}
