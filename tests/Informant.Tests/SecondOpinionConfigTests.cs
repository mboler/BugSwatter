using System.Text.Json;

namespace Informant.Tests;

[Collection("Informant configuration environment")]
public sealed class SecondOpinionConfigTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void AbsentBlockLeavesSecondOpinionNull()
    {
        WriteConfig(secondOpinion: null);
        Assert.Null(InformantConfig.Load(_directory.Path).SecondOpinion);
    }

    [Fact]
    public void ValidBlockLoadsWithDefaults()
    {
        WriteConfig(secondOpinion: new Dictionary<string, object?> { ["endpoint"] = "https://api.example.test/v1", ["modelName"] = "frontier-1", ["apiKey"] = "env:SO_TEST_KEY" });

        SecondOpinionConfig secondOpinion = InformantConfig.Load(_directory.Path).SecondOpinion!;
        Assert.Equal("https://api.example.test/v1", secondOpinion.Endpoint);
        Assert.Equal("frontier-1", secondOpinion.ModelName);
        Assert.Equal(1800, secondOpinion.RequestTimeoutSeconds);
        Assert.Equal(DefaultSecondOpinionPrompt.Text, secondOpinion.ResolvePrompt());
    }

    [Fact]
    public void KeylessLocalEndpointIsAccepted()
    {
        WriteConfig(secondOpinion: new Dictionary<string, object?> { ["endpoint"] = "http://validator.example.test:1234/v1", ["modelName"] = "local-validator" });

        SecondOpinionConfig secondOpinion = InformantConfig.Load(_directory.Path).SecondOpinion!;
        Assert.False(secondOpinion.RequiresApiKey);
        Assert.Null(secondOpinion.ResolveApiKey());
        Assert.Equal(30, secondOpinion.ContextLines);
    }

    [Fact]
    public void PositiveRatesEnableSecondOpinionCostEstimates()
    {
        WriteConfig(secondOpinion: new Dictionary<string, object?>
        {
            ["endpoint"] = "https://api.example.test/v1",
            ["modelName"] = "frontier-1",
            ["inputCostPerMillion"] = 2.5m,
            ["outputCostPerMillion"] = 15m
        });

        SecondOpinionConfig secondOpinion = InformantConfig.Load(_directory.Path).SecondOpinion!;
        SecondOpinionModelSelection selection = secondOpinion.SelectModel(new PrimaryReviewClassification(Severity.Medium, true));

        Assert.Equal(2.5m, secondOpinion.InputCostPerMillion);
        Assert.Equal(15m, secondOpinion.OutputCostPerMillion);
        Assert.Equal(2.5m, selection.Model.Pricing.InputCostPerMillion);
        Assert.Equal(15m, selection.Model.Pricing.OutputCostPerMillion);
    }

    [Fact]
    public void ZeroSecondOpinionRatesAreAccepted()
    {
        WriteConfig(secondOpinion: new Dictionary<string, object?>
        {
            ["endpoint"] = "https://api.example.test/v1",
            ["modelName"] = "frontier-1",
            ["inputCostPerMillion"] = 0m,
            ["outputCostPerMillion"] = 0m
        });

        Assert.NotNull(InformantConfig.Load(_directory.Path).SecondOpinion);
    }

    [Fact]
    public void UnpairedOrNegativeSecondOpinionRatesAreRejected()
    {
        WriteConfig(secondOpinion: new Dictionary<string, object?> { ["endpoint"] = "https://api.example.test/v1", ["modelName"] = "frontier-1", ["inputCostPerMillion"] = 2.5m });
        Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        WriteConfig(secondOpinion: new Dictionary<string, object?> { ["endpoint"] = "https://api.example.test/v1", ["modelName"] = "frontier-1", ["inputCostPerMillion"] = -1m,
            ["outputCostPerMillion"] = 15m });
        Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
    }

    [Fact]
    public void ContextLinesIsConfigurableAndValidated()
    {
        WriteConfig(secondOpinion: new Dictionary<string, object?> { ["endpoint"] = "http://validator.example.test:1234/v1", ["modelName"] = "local-validator", ["contextLines"] = 12 });
        Assert.Equal(12, InformantConfig.Load(_directory.Path).SecondOpinion!.ContextLines);

        WriteConfig(secondOpinion: new Dictionary<string, object?> { ["endpoint"] = "http://validator.example.test:1234/v1", ["modelName"] = "local-validator", ["contextLines"] = 0 });
        Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
    }

    [Fact]
    public void LiteralApiKeyIsRejectedAtLoad()
    {
        WriteConfig(secondOpinion: new Dictionary<string, object?> { ["endpoint"] = "https://api.example.test/v1", ["modelName"] = "frontier-1", ["apiKey"] = "sk-notallowed123" });

        InformantFatalException ex = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
        Assert.Contains("never stored in the config file", ex.Message);
    }

    [Fact]
    public void EmptyEnvironmentVariableNameIsRejectedAtLoad()
    {
        WriteConfig(secondOpinion: new Dictionary<string, object?> { ["endpoint"] = "https://api.example.test/v1", ["modelName"] = "frontier-1", ["apiKey"] = "env:" });
        Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
    }

    [Fact]
    public void MissingModelNameIsRejectedAtLoad()
    {
        WriteConfig(secondOpinion: new Dictionary<string, object?> { ["endpoint"] = "https://api.example.test/v1", ["apiKey"] = "env:SO_TEST_KEY" });
        Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));
    }

    [Fact]
    public void ResolveApiKeyReadsTheEnvironment()
    {
        var secondOpinion = new SecondOpinionConfig { Endpoint = "https://api.example.test/v1", ModelName = "frontier-1", ApiKey = "env:INFORMANT_SO_CONFIG_TEST" };

        string? originalApiKey = Environment.GetEnvironmentVariable("INFORMANT_SO_CONFIG_TEST");
        Environment.SetEnvironmentVariable("INFORMANT_SO_CONFIG_TEST", "resolved-value");
        try
        {
            Assert.Equal("resolved-value", secondOpinion.ResolveApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_SO_CONFIG_TEST", originalApiKey);
        }

        Assert.Null(secondOpinion.ResolveApiKey());
    }

    [Fact]
    public void InlinePromptWinsOverFileAndDefault()
    {
        var secondOpinion = new SecondOpinionConfig { Prompt = "inline validation prompt", PromptFile = "missing.txt" };
        Assert.Equal("inline validation prompt", secondOpinion.ResolvePrompt());
    }

    [Fact]
    public void PromptFileIsReadWhenNoInlinePrompt()
    {
        string promptPath = Path.Combine(_directory.Path, "so-prompt.txt");
        File.WriteAllText(promptPath, "prompt from file");
        var secondOpinion = new SecondOpinionConfig { PromptFile = promptPath };
        Assert.Equal("prompt from file", secondOpinion.ResolvePrompt());
    }

    private void WriteConfig(Dictionary<string, object?>? secondOpinion)
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

        if (secondOpinion is not null)
        {
            values["secondOpinion"] = secondOpinion;
        }

        File.WriteAllText(Path.Combine(_directory.Path, InformantConfig.FileName), JsonSerializer.Serialize(values));
    }
}
