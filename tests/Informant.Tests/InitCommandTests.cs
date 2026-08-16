namespace Informant.Tests;

public sealed class InitCommandTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void StarterConfigIncludesDefaultReportRetention()
    {
        Assert.Equal(0, InitCommand.Run(_directory.Path));

        string config = File.ReadAllText(Path.Combine(_directory.Path, InformantConfig.FileName));
        Assert.Contains("\"reportRetentionDays\": 31", config);
        Assert.Contains("\"fallbackModels\": []", config);
        Assert.Contains("\"inputCostPerMillion\": null", config);
        Assert.Contains("\"outputCostPerMillion\": null", config);
        Assert.Contains("\"reviewStrategy\": \"exhaustive\"", config);
        Assert.Contains("\"coverageStateFilePath\": \"informant.coverage-state.json\"", config);
        Assert.Contains("\"primaryReviewBudgetMinutes\": 180", config);
        Assert.Contains("\"adaptiveCarryoverCount\": 25", config);
        Assert.Contains("\"scope\": \"candidatePlusSample\"", config);
        Assert.Contains("\"maxCleanFiles\": 10", config);
        Assert.Contains("\"reviewBudgetMinutes\": 60", config);
        Assert.Contains("Informant never loads models", config);
    }
}
