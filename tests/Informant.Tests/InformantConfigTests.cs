using System.Text.Json;

namespace Informant.Tests;

[Collection("Informant configuration environment")]
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
        Assert.Equal(ReviewStrategy.Exhaustive, config.ReviewStrategy);
        Assert.Equal(Path.Combine(_directory.Path, "reports"), config.ReportDirectory);
        Assert.Equal(31, config.ReportRetentionDays);
        Assert.Equal(Path.Combine(_directory.Path, "informant.state.json"), config.StateFilePath);
        Assert.Equal(24000, config.MaxContextCharacters);
        Assert.Equal(800, config.MaxFileLines);
        Assert.Equal(10 * 1024 * 1024, config.MaxFileBytes);
        Assert.Equal(4 * 1024 * 1024, config.MaxModelResponseBytes);
        Assert.Equal(2, config.PerFileRetryCount);
        Assert.Equal(1800, config.RequestTimeoutSeconds);
        Assert.Empty(config.FallbackModels);
        Assert.Empty(config.SeedPaths);
        Assert.Single(config.GetPrimaryModelTargets());
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

    /// <summary>Verifies adaptive review strategy is explicitly configurable and case-insensitive</summary>
    [Fact]
    public void ParsesAdaptiveStrategyCaseInsensitively()
    {
        WriteConfig(values => values["reviewStrategy"] = "Adaptive");

        Assert.Equal(ReviewStrategy.Adaptive, InformantConfig.Load(_directory.Path).ReviewStrategy);
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
    public void PrimaryPricingRatesAreOptionalPairedAndNonNegative()
    {
        WriteConfig(values =>
        {
            values["inputCostPerMillion"] = 2.5m;
            values["outputCostPerMillion"] = 15m;
        });

        PrimaryModelTarget target = Assert.Single(InformantConfig.Load(_directory.Path).GetPrimaryModelTargets());
        Assert.False(target.Pricing.IsLocal);
        Assert.True(target.Pricing.CanEstimate);

        WriteConfig(values => values["inputCostPerMillion"] = 2.5m);
        InformantFatalException missingRate = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("outputCostPerMillion", missingRate.Message);

        WriteConfig(values =>
        {
            values["inputCostPerMillion"] = -1m;
            values["outputCostPerMillion"] = 15m;
        });
        InformantFatalException negativeRate = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("cannot be negative", negativeRate.Message);
    }

    [Fact]
    public void LoadsOrderedFallbackModels()
    {
        WriteConfig(values => values["fallbackModels"] = new[]
        {
            new Dictionary<string, object?> { ["name"] = "backup-one", ["endpoint"] = "http://backup-one.example/v1", ["modelName"] = "model-one" },
            new Dictionary<string, object?> { ["name"] = "backup-two", ["endpoint"] = "http://backup-two.example/v1", ["modelName"] = "model-two" }
        });

        InformantConfig config = InformantConfig.Load(_directory.Path);
        IReadOnlyList<PrimaryModelTarget> targets = config.GetPrimaryModelTargets();

        Assert.Equal(3, targets.Count);
        Assert.Equal(["primary", "backup-one", "backup-two"], targets.Select(target => target.Name));
        Assert.False(targets[0].IsFallback);
        Assert.True(targets[1].IsFallback);
    }

    [Fact]
    public void ZeroFallbackRatesMarkFrontierUsageWithoutEnablingCostEstimates()
    {
        WriteConfig(values => values["fallbackModels"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["name"] = "backup",
                ["endpoint"] = "http://backup.example/v1",
                ["modelName"] = "backup-model",
                ["inputCostPerMillion"] = 0m,
                ["outputCostPerMillion"] = 0m
            }
        });

        PrimaryModelTarget fallback = InformantConfig.Load(_directory.Path).GetPrimaryModelTargets()[1];

        Assert.False(fallback.Pricing.IsLocal);
        Assert.False(fallback.Pricing.CanEstimate);
    }

    [Fact]
    public void UnpairedFallbackRatesAreRejected()
    {
        WriteConfig(values => values["fallbackModels"] = new[]
        {
            new Dictionary<string, object?> { ["name"] = "backup", ["endpoint"] = "http://backup.example/v1", ["modelName"] = "backup-model", ["inputCostPerMillion"] = 1m }
        });

        Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("endpoint", "not-a-url")]
    [InlineData("modelName", "")]
    public void InvalidFallbackFieldThrowsFatal(string field, string value)
    {
        var fallback = new Dictionary<string, object?> { ["name"] = "backup", ["endpoint"] = "http://backup.example/v1", ["modelName"] = "backup-model" };
        fallback[field] = value;
        WriteConfig(values => values["fallbackModels"] = new[]
        {
            fallback
        });

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        Assert.Contains($"fallbackModels[0].{field}", exception.Message);
    }

    [Fact]
    public void DuplicateFallbackTargetThrowsFatal()
    {
        WriteConfig(values => values["fallbackModels"] = new[]
        {
            new Dictionary<string, object?> { ["name"] = "duplicate", ["endpoint"] = "http://localhost:1234/v1/", ["modelName"] = "test-model" }
        });

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        Assert.Contains("duplicates", exception.Message);
    }

    [Theory]
    [InlineData("maxFileBytes")]
    [InlineData("maxModelResponseBytes")]
    public void NonPositiveByteLimitThrowsFatal(string field)
    {
        WriteConfig(values => values[field] = 0);
        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains(field, ex.Message);
    }

    /// <summary>Verifies a context budget too small for one useful bounded tool response is rejected</summary>
    [Fact]
    public void ContextBudgetTooSmallForBoundedToolResultsThrowsFatal()
    {
        WriteConfig(values => values["maxContextCharacters"] = ReadFileLinesTool.MinimumMaxResultCharacters * 4 - 1);

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        Assert.Contains("maxContextCharacters", exception.Message);
    }

    /// <summary>Verifies repository-relative files, directories, and glob patterns load in configured order</summary>
    [Fact]
    public void LoadsSeedPaths()
    {
        WriteConfig(values => values["seedPaths"] = new[] { "src", "tools/*.ps1", "docs/**/*.md" });

        Assert.Equal(["src", "tools/*.ps1", "docs/**/*.md"], InformantConfig.Load(_directory.Path).SeedPaths);
    }

    /// <summary>Verifies seeds cannot escape or replace the repository-root planning boundary</summary>
    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("/outside")]
    public void RejectsUnsafeSeedPath(string seedPath)
    {
        WriteConfig(values => values["seedPaths"] = new[] { seedPath });

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        Assert.Contains("seedPaths[0]", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void InvalidReportRetentionThrowsFatal(int days)
    {
        WriteConfig(values => values["reportRetentionDays"] = days);

        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        Assert.Contains("reportRetentionDays", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(365)]
    public void ValidReportRetentionLoads(int days)
    {
        WriteConfig(values => values["reportRetentionDays"] = days);

        Assert.Equal(days, InformantConfig.Load(_directory.Path).ReportRetentionDays);
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
        string prompt = InformantConfig.Load(_directory.Path).ResolveReviewPrompt(EmptyTree());
        Assert.StartsWith("inline prompt", prompt);
        Assert.Contains(DefaultReviewPrompt.StructuredFindingsMarker, prompt);
    }

    [Fact]
    public void PromptFileIsReadWhenNoInlinePrompt()
    {
        string promptPath = Path.Combine(_directory.Path, "prompt.txt");
        File.WriteAllText(promptPath, "prompt from file");
        WriteConfig(values => values["reviewPromptFile"] = promptPath);
        string prompt = InformantConfig.Load(_directory.Path).ResolveReviewPrompt(EmptyTree());
        Assert.StartsWith("prompt from file", prompt);
        Assert.Contains(DefaultReviewPrompt.StructuredFindingsMarker, prompt);
    }

    [Fact]
    public void RelativePathsResolveAgainstExplicitConfigWithoutChangingCurrentDirectory()
    {
        File.WriteAllText(Path.Combine(_directory.Path, "prompt.txt"), "prompt from relative file");
        WriteConfig(values =>
        {
            values["allowedReadRoot"] = "allowed";
            values["reportDirectory"] = "artifacts/reports";
            values["stateFilePath"] = "state/review.json";
            values["reviewPromptFile"] = "prompt.txt";
            values["logFilePath"] = "logs/informant-.log";
        });
        string originalDirectory = Directory.GetCurrentDirectory();

        InformantConfig config = InformantConfig.LoadFile(Path.Combine(_directory.Path, InformantConfig.FileName));

        Assert.Equal(originalDirectory, Directory.GetCurrentDirectory());
        Assert.Equal(Path.Combine(_directory.Path, "allowed"), config.ResolvedAllowedReadRoot);
        Assert.Equal(Path.Combine(_directory.Path, "artifacts", "reports"), config.ReportDirectory);
        Assert.Equal(Path.Combine(_directory.Path, "state", "review.json"), config.StateFilePath);
        Assert.Equal(Path.Combine(_directory.Path, "logs", "informant-.log"), config.LogFilePath);
        string prompt = config.ResolveReviewPrompt(EmptyTree());
        Assert.StartsWith("prompt from relative file", prompt);
        Assert.Contains(DefaultReviewPrompt.StructuredFindingsMarker, prompt);
    }

    [Fact]
    public void RelativeNestedPromptAndSecretFilesResolveAgainstConfig()
    {
        File.WriteAllText(Path.Combine(_directory.Path, "second-opinion.txt"), "validate carefully");
        File.WriteAllText(Path.Combine(_directory.Path, "api-key.txt"), "test-key\n");
        WriteConfig(values => values["secondOpinion"] = new Dictionary<string, object?>
        {
            ["endpoint"] = "http://localhost:1234/v1",
            ["modelName"] = "validator",
            ["promptFile"] = "second-opinion.txt",
            ["apiKey"] = "file:api-key.txt"
        });

        SecondOpinionConfig config = InformantConfig.Load(_directory.Path).SecondOpinion!;

        Assert.Equal("validate carefully", config.ResolvePrompt());
        Assert.Equal("test-key", config.ResolveApiKey());
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
    public void ExistingStructuredContractIsNotAppendedTwice()
    {
        WriteConfig(values => values["reviewPrompt"] = $"custom guidance\n\n{DefaultReviewPrompt.StructuredFindingsContract}");

        string prompt = InformantConfig.Load(_directory.Path).ResolveReviewPrompt(EmptyTree());

        Assert.Equal(1, CountOccurrences(prompt, DefaultReviewPrompt.StructuredFindingsMarker));
    }

    [Fact]
    public void AgentsFileInTreeIsAppendedWhenListed()
    {
        string tree = EmptyTree();
        File.WriteAllText(Path.Combine(tree, "AGENTS.md"), "Always use tabs. Just kidding.");
        WriteConfig(values => values["promptIncludeFiles"] = new[] { "AGENTS.md" });

        string prompt = InformantConfig.Load(_directory.Path).ResolveReviewPrompt(tree);

        Assert.StartsWith("You are a senior code reviewer", prompt);
        Assert.Contains("Additional project guidance from AGENTS.md", prompt);
        Assert.Contains("Always use tabs. Just kidding.", prompt);
        Assert.EndsWith(DefaultReviewPrompt.StructuredFindingsContract, prompt);
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
    public void RepositoryPromptIncludeSymbolicLinkIsRejected()
    {
        using var outside = new TempDirectory();
        string tree = EmptyTree();
        string link = Path.Combine(tree, "AGENTS.md");
        File.WriteAllText(Path.Combine(outside.Path, "secret.md"), "outside secret guidance");
        bool created = TryCreateFileSymbolicLink(link, Path.Combine(outside.Path, "secret.md"));
        Assert.SkipUnless(created, "symbolic links are not available on this test host");
        try
        {
            WriteConfig(values => values["promptIncludeFiles"] = new[] { "AGENTS.md" });

            string prompt = InformantConfig.Load(_directory.Path).ResolveReviewPrompt(tree);

            Assert.Equal(DefaultReviewPrompt.Text, prompt);
            Assert.DoesNotContain("outside secret guidance", prompt);
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void NullPromptIncludeFilesIsRejectedAtPromptResolution()
    {
        var config = new InformantConfig { PromptIncludeFiles = null! };

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => config.ResolveReviewPrompt(EmptyTree()));

        Assert.Contains("promptIncludeFiles", exception.Message);
    }

    [Fact]
    public void JsonNullPromptIncludeFilesBindsToTheSafeDefault()
    {
        WriteConfig(values => values["promptIncludeFiles"] = null);

        InformantConfig config = InformantConfig.Load(_directory.Path);

        Assert.Empty(config.PromptIncludeFiles);
    }

    [Fact]
    public void EnvironmentVariableOverridesAConfigValue()
    {
        WriteConfig();
        string? originalModelName = Environment.GetEnvironmentVariable("INFORMANT_ModelName");
        Environment.SetEnvironmentVariable("INFORMANT_ModelName", "overridden-model");
        try
        {
            Assert.Equal("overridden-model", InformantConfig.Load(_directory.Path).ModelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_ModelName", originalModelName);
        }
    }

    [Fact]
    public void EnvironmentVariableOverridesReportRetention()
    {
        WriteConfig();
        string? originalReportRetentionDays = Environment.GetEnvironmentVariable("INFORMANT_ReportRetentionDays");
        Environment.SetEnvironmentVariable("INFORMANT_ReportRetentionDays", "45");
        try
        {
            Assert.Equal(45, InformantConfig.Load(_directory.Path).ReportRetentionDays);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_ReportRetentionDays", originalReportRetentionDays);
        }
    }

    [Fact]
    public void EnvironmentVariableOverridesANestedConfigValue()
    {
        WriteConfig(values => values["secondOpinion"] = new Dictionary<string, object?> { ["endpoint"] = "http://localhost:1234/v1", ["modelName"] = "validator" });
        string? originalSecondOpinionModelName = Environment.GetEnvironmentVariable("INFORMANT_SecondOpinion__ModelName");
        Environment.SetEnvironmentVariable("INFORMANT_SecondOpinion__ModelName", "env-validator");
        try
        {
            Assert.Equal("env-validator", InformantConfig.Load(_directory.Path).SecondOpinion!.ModelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_SecondOpinion__ModelName", originalSecondOpinionModelName);
        }
    }

    [Fact]
    public void EnvironmentVariableOverridesAnArrayValue()
    {
        WriteConfig(values => values["promptIncludeFiles"] = new[] { "AGENTS.md" });
        string? originalPromptIncludeFile = Environment.GetEnvironmentVariable("INFORMANT_PromptIncludeFiles__0");
        Environment.SetEnvironmentVariable("INFORMANT_PromptIncludeFiles__0", "GUIDANCE.md");
        try
        {
            Assert.Equal("GUIDANCE.md", Assert.Single(InformantConfig.Load(_directory.Path).PromptIncludeFiles));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_PromptIncludeFiles__0", originalPromptIncludeFile);
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

    private static bool TryCreateFileSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
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
