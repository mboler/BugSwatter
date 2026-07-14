namespace Informant.Tests;

/// <summary>Tests deterministic whole-block repository context packing</summary>
public sealed class RepositoryContextPackerTests
{
    /// <summary>Verifies priority and identifier ordering produce the same bounded result for shuffled inputs</summary>
    [Fact]
    public void PacksDeterministicallyWithinExactCharacterBudget()
    {
        RepositoryContextItem required = new("required", 1, "AAAA", true);
        RepositoryContextItem second = new("second", 2, "BB");
        RepositoryContextItem omitted = new("omitted", 3, "CCCC");

        RepositoryContextPack first = RepositoryContextPacker.Pack([omitted, second, required], 8);
        RepositoryContextPack secondPack = RepositoryContextPacker.Pack([required, omitted, second], 8);

        Assert.Equal("AAAA\n\nBB", first.Text);
        Assert.Equal(8, first.UsedCharacters);
        Assert.Equal(["required", "second"], first.Selected.Select(item => item.Id));
        Assert.Equal(["omitted"], first.Omitted.Select(item => item.Id));
        Assert.Equal(first.Text, secondPack.Text);
        Assert.Equal(first.UsedCharacters, secondPack.UsedCharacters);
        Assert.Equal(first.Selected.Select(item => item.Id), secondPack.Selected.Select(item => item.Id));
        Assert.Equal(first.Omitted.Select(item => item.Id), secondPack.Omitted.Select(item => item.Id));
    }

    /// <summary>Verifies required content that cannot fit is reported explicitly and never partially included</summary>
    [Fact]
    public void ReportsOversizedRequiredBlockAsOmitted()
    {
        var required = new RepositoryContextItem("required", 1, "too large", true);

        RepositoryContextPack result = RepositoryContextPacker.Pack([required], 4);

        Assert.Empty(result.Text);
        Assert.Equal(required, Assert.Single(result.OmittedRequired));
        Assert.Equal(0, result.UsedCharacters);
    }

    /// <summary>Verifies duplicate identifiers are rejected because they make deterministic coverage accounting ambiguous</summary>
    [Fact]
    public void RejectsDuplicateIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => RepositoryContextPacker.Pack([new RepositoryContextItem("same", 1, "a"), new RepositoryContextItem("same", 2, "b")], 10));
    }
}
