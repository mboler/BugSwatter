namespace SlimShady.Tests;

public sealed class ReviewStateStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void MissingFileYieldsNoBaseline()
    {
        var store = new ReviewStateStore(StatePath());
        Assert.Null(store.GetBaseline("https://example.test/repo.git", "main"));
    }

    [Fact]
    public void BaselineRoundTripsThroughDisk()
    {
        string path = StatePath();
        new ReviewStateStore(path).SetBaseline("https://example.test/repo.git", "main", "abc123");
        Assert.Equal("abc123", new ReviewStateStore(path).GetBaseline("https://example.test/repo.git", "main"));
    }

    [Fact]
    public void KeysAreIndependentPerRepositoryAndBranch()
    {
        string path = StatePath();
        var store = new ReviewStateStore(path);
        store.SetBaseline("https://example.test/repo.git", "main", "sha-main");
        store.SetBaseline("https://example.test/repo.git", "develop", "sha-develop");
        store.SetBaseline("https://example.test/other.git", "main", "sha-other");
        var reloaded = new ReviewStateStore(path);
        Assert.Equal("sha-main", reloaded.GetBaseline("https://example.test/repo.git", "main"));
        Assert.Equal("sha-develop", reloaded.GetBaseline("https://example.test/repo.git", "develop"));
        Assert.Equal("sha-other", reloaded.GetBaseline("https://example.test/other.git", "main"));
    }

    [Fact]
    public void CorruptStateFileThrowsFatal()
    {
        string path = StatePath();
        File.WriteAllText(path, "{{ definitely not json");
        SlimShadyFatalException ex = Assert.Throws<SlimShadyFatalException>(() => new ReviewStateStore(path));
        Assert.Contains("corrupt", ex.Message);
    }

    [Fact]
    public void CreatesMissingStateDirectoryOnSave()
    {
        string path = Path.Combine(_directory.Path, "nested", "state.json");
        new ReviewStateStore(path).SetBaseline("repo", "main", "abc");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AtomicSaveLeavesNoTempFileBehind()
    {
        string path = StatePath();
        var store = new ReviewStateStore(path);
        store.SetBaseline("repo", "main", "abc");
        store.SetBaseline("repo", "main", "def");

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("def", new ReviewStateStore(path).GetBaseline("repo", "main"));
    }

    private string StatePath() => Path.Combine(_directory.Path, "state.json");
}
