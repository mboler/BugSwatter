namespace BugSwatter.Git.Tests;

public sealed class GitRunnerTests
{
    [Fact]
    public async Task CapturesOutputAndZeroExitCode()
    {
        var git = new GitRunner(TestGit.ExecutablePath);
        GitResult result = await git.RunAsync("version");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("git version", result.StandardOutput);
    }

    [Fact]
    public async Task RunCheckedThrowsGitOperationExceptionOnNonZeroExit()
    {
        var git = new GitRunner(TestGit.ExecutablePath);
        GitOperationException ex = await Assert.ThrowsAsync<GitOperationException>(() => git.RunCheckedAsync("definitely-not-a-git-subcommand"));
        Assert.Contains("failed with exit code", ex.Message);
    }
}
