using BugSwatter.Common;

namespace BugSwatter.Git.Tests;

/// <summary>Tests bounded reads from immutable Git blob objects</summary>
public sealed class GitBlobReaderTests
{
    /// <summary>Verifies deleted text remains readable from its baseline revision</summary>
    [Fact]
    public async Task ReadsDeletedTextFromTheRequestedRevision()
    {
        using TestRepository repository = await TestRepository.CreateAsync();
        string baseline = await repository.CommitFileAsync("gone.cs", "line one\nline two\n", "add file");
        await repository.DeleteFileAsync("gone.cs", "delete file");
        var git = new GitRunner(TestGit.ExecutablePath);

        string[] lines = await GitBlobReader.ReadLinesAsync(git, repository.SeedPath, baseline, "gone.cs", 1024);

        Assert.Equal(["line one", "line two"], lines);
    }

    /// <summary>Verifies the byte limit is enforced before blob content is loaded</summary>
    [Fact]
    public async Task RejectsBlobThatExceedsTheConfiguredLimit()
    {
        using TestRepository repository = await TestRepository.CreateAsync();
        string baseline = await repository.CommitFileAsync("large.txt", "too large", "add large file");
        var git = new GitRunner(TestGit.ExecutablePath);

        RepositoryFileException exception = await Assert.ThrowsAsync<RepositoryFileException>(() => GitBlobReader.ReadLinesAsync(git, repository.SeedPath, baseline, "large.txt", 4));

        Assert.Equal(RepositoryFileError.TooLarge, exception.Error);
    }
}
