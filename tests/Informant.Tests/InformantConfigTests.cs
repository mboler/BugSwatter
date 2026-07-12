using System.Text.Json;

namespace Informant.Tests;

public sealed class InformantConfigTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void LoadsValidConfigWithDefaults()
    {
        WriteConfig();
        var config = InformantConfig.Load(_directory.Path);
        Assert.Equal("https://example.test/repo.git", config.RepositoryUrl);
        Assert.Equal("main", config.Branch);
        Assert.Equal(ReviewMode.Changed, config.ReviewMode);
        Assert.Equal("reports", config.ReportDirectory);
        Assert.Equal("informant.state.json", config.StateFilePath);
        Assert.Equal(24000, config.MaxContextCharacters);
        Assert.Equal(800, config.MaxFileLines);
        Assert.Equal(2, config.PerFileRetryCount);
        Assert.Equal(1800, config.RequestTimeoutSeconds);
        Assert.Null(config.ConsoleLogging);
        Assert.Equal(config.WorkingTreePath, config.ResolvedAllowedReadRoot);
    }

    [Fact]
    public void LoadFileAcceptsAnExplicitPathWithAnyName()
    {
        WriteConfig();
        string customPath = Path.Combine(_directory.Path, "job-alpha.json");
        File.Move(Path.Combine(_directory.Path, InformantConfig.FileName), customPath);

        InformantConfig config = InformantConfig.LoadFile(customPath);
        Assert.Equal("https://example.test/repo.git", config.RepositoryUrl);
    }

    [Fact]
    public void ParsesFullModeCaseInsensitively()
    {
        WriteConfig(values => values["reviewMode"] = "Full");
        Assert.Equal(ReviewMode.Full, InformantConfig.Load(_directory.Path).ReviewMode);
    }

    [Fact]
    public void MissingFileThrowsFatal()
    {
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("init", ex.Message);
    }

    [Fact]
    public void InvalidJsonThrowsFatal()
    {
        File.WriteAllText(Path.Combine(_directory.Path, InformantConfig.FileName), "{ not json");
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("could not be read", ex.Message);
    }

    [Fact]
    public void MissingRequiredFieldThrowsFatal()
    {
        WriteConfig(values => values["repositoryUrl"] = "");
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("repositoryUrl", ex.Message);
    }

    [Fact]
    public void RelativeWorkingTreePathThrowsFatal()
    {
        WriteConfig(values => values["workingTreePath"] = "relative/tree");
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("absolute", ex.Message);
    }

    [Fact]
    public void MissingGitExecutableThrowsFatal()
    {
        WriteConfig(values => values["gitExecutablePath"] = Path.Combine(_directory.Path, "no-git-here.exe"));
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("gitExecutablePath", ex.Message);
    }

    [Fact]
    public void InvalidEndpointThrowsFatal()
    {
        WriteConfig(values => values["modelEndpoint"] = "not a url");
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("modelEndpoint", ex.Message);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        WriteConfig();
        string path = Path.Combine(_directory.Path, InformantConfig.FileName);
        string json = File.ReadAllText(path);
        File.WriteAllText(path, "// leading comment\n" + json.TrimEnd().TrimEnd('}') + ",\n// inline comment\n\"logLevel\": \"Debug\",\n}\n");
        Assert.Equal("Debug", InformantConfig.Load(_directory.Path).LogLevel);
    }

    [Fact]
    public void InlinePromptWinsOverFileAndDefault()
    {
        WriteConfig(values =>
        {
            values["reviewPrompt"] = "inline prompt";
            values["reviewPromptFile"] = "does-not-exist.txt";
        });
        Assert.Equal("inline prompt", InformantConfig.Load(_directory.Path).ResolveReviewPrompt(EmptyTree()));
    }

    [Fact]
    public void PromptFileIsReadWhenNoInlinePrompt()
    {
        string promptPath = Path.Combine(_directory.Path, "prompt.txt");
        File.WriteAllText(promptPath, "prompt from file");
        WriteConfig(values => values["reviewPromptFile"] = promptPath);
        Assert.Equal("prompt from file", InformantConfig.Load(_directory.Path).ResolveReviewPrompt(EmptyTree()));
    }

    [Fact]
    public void MissingPromptFileThrowsFatal()
    {
        WriteConfig(values => values["reviewPromptFile"] = Path.Combine(_directory.Path, "gone.txt"));
        var config = InformantConfig.Load(_directory.Path);
        Assert.Throws<InformantFatalException>(() => config.ResolveReviewPrompt(EmptyTree()));
    }

    [Fact]
    public void DefaultPromptUsedWhenNothingConfigured()
    {
        WriteConfig();
        Assert.Equal(DefaultReviewPrompt.Text, InformantConfig.Load(_directory.Path).ResolveReviewPrompt(EmptyTree()));
    }

    [Fact]
    public void AgentsFileInTreeIsAppendedWhenListed()
    {
        string tree = EmptyTree();
        File.WriteAllText(Path.Combine(tree, "AGENTS.md"), "Always use tabs. Just kidding.");
        WriteConfig(values => values["promptIncludeFiles"] = new[] { "AGENTS.md" });

        string prompt = InformantConfig.Load(_directory.Path).ResolveReviewPrompt(tree);

        Assert.StartsWith(DefaultReviewPrompt.Text, prompt);
        Assert.Contains("Additional project guidance from AGENTS.md", prompt);
        Assert.Contains("Always use tabs. Just kidding.", prompt);
    }

    [Fact]
    public void WildcardPatternsMatchMultipleFilesOnceEach()
    {
        string tree = EmptyTree();
        File.WriteAllText(Path.Combine(tree, "AGENTS.md"), "agents content");
        File.WriteAllText(Path.Combine(tree, "AGENTX.md"), "agentx content");
        WriteConfig(values => values["promptIncludeFiles"] = new[] { "AGENT*.md", "AGENTS.md" });

        string prompt = InformantConfig.Load(_directory.Path).ResolveReviewPrompt(tree);

        Assert.Contains("agents content", prompt);
        Assert.Contains("agentx content", prompt);
        Assert.Equal(1, CountOccurrences(prompt, "agents content"));
    }

    [Fact]
    public void AbsolutePathIncludeIsAppended()
    {
        string tree = EmptyTree();
        string standards = Path.Combine(_directory.Path, "standards.md");
        File.WriteAllText(standards, "absolute include content");
        WriteConfig(values => values["promptIncludeFiles"] = new[] { standards });

        Assert.Contains("absolute include content", InformantConfig.Load(_directory.Path).ResolveReviewPrompt(tree));
    }

    [Fact]
    public void EmptyIncludeListDisablesTheMechanism()
    {
        string tree = EmptyTree();
        File.WriteAllText(Path.Combine(tree, "AGENTS.md"), "should not appear");
        WriteConfig(values => values["promptIncludeFiles"] = Array.Empty<string>());

        Assert.Equal(DefaultReviewPrompt.Text, InformantConfig.Load(_directory.Path).ResolveReviewPrompt(tree));
    }

    [Fact]
    public void MissingIncludeFilesAreSilentlySkipped()
    {
        WriteConfig(values => values["promptIncludeFiles"] = new[] { "AGENTS.md", "NOPE*.md" });
        Assert.Equal(DefaultReviewPrompt.Text, InformantConfig.Load(_directory.Path).ResolveReviewPrompt(EmptyTree()));
    }

    [Fact]
    public void EnvironmentVariableOverridesAConfigValue()
    {
        WriteConfig();
        Environment.SetEnvironmentVariable("INFORMANT_ModelName", "overridden-model");
        try
        {
            Assert.Equal("overridden-model", InformantConfig.Load(_directory.Path).ModelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_ModelName", null);
        }
    }

    [Fact]
    public void EnvironmentVariableOverridesANestedConfigValue()
    {
        WriteConfig(values => values["secondOpinion"] = new Dictionary<string, object?> { ["endpoint"] = "http://localhost:1234/v1", ["modelName"] = "validator" });
        Environment.SetEnvironmentVariable("INFORMANT_SecondOpinion__ModelName", "env-validator");
        try
        {
            Assert.Equal("env-validator", InformantConfig.Load(_directory.Path).SecondOpinion!.ModelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_SecondOpinion__ModelName", null);
        }
    }

    private string EmptyTree()
    {
        string tree = Path.Combine(_directory.Path, "tree");
        Directory.CreateDirectory(tree);
        return tree;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private void WriteConfig(Action<Dictionary<string, object?>>? mutate = null)
    {
        var values = new Dictionary<string, object?>
        {
            ["repositoryUrl"] = "https://example.test/repo.git",
            ["branch"] = "main",
            ["workingTreePath"] = Path.Combine(_directory.Path, "tree"),
            ["gitExecutablePath"] = TestGit.ExecutablePath,
            ["modelEndpoint"] = "http://localhost:1234/v1",
            ["modelName"] = "test-model"
        };
        mutate?.Invoke(values);
        File.WriteAllText(Path.Combine(_directory.Path, InformantConfig.FileName), JsonSerializer.Serialize(values));
    }
}
