using System.Text.Json;

namespace Informant.Tests;

[Collection("Informant configuration environment")]
public sealed class SecondOpinionRoutingTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void SimpleConfigurationAlwaysSelectsItsSingleModel()
    {
        var config = new SecondOpinionConfig { Endpoint = "http://localhost:1234/v1", ModelName = "validator" };

        SecondOpinionModelSelection selection = config.SelectModel(new PrimaryReviewClassification(Severity.Critical, true));

        Assert.False(selection.UsesSeverityRouting);
        Assert.Equal("single", selection.ProfileName);
        Assert.Equal("validator", selection.Model.ModelName);
    }

    [Theory]
    [InlineData(Severity.None, true, "economy")]
    [InlineData(Severity.Low, true, "economy")]
    [InlineData(Severity.Medium, true, "balanced")]
    [InlineData(Severity.High, true, "premium")]
    [InlineData(Severity.Critical, true, "premium")]
    [InlineData(Severity.None, false, "premium")]
    public void AdvancedConfigurationSelectsOneProfileForTheRun(Severity severity, bool determined, string expectedProfile)
    {
        SecondOpinionModelSelection selection = CreateAdvancedConfig().SelectModel(new PrimaryReviewClassification(severity, determined));

        Assert.True(selection.UsesSeverityRouting);
        Assert.Equal(expectedProfile, selection.ProfileName);
        Assert.Equal(expectedProfile, selection.Model.ModelName);
    }

    [Fact]
    public void AdvancedConfigurationLoadsProfilesAuthenticationAndRelativeSecrets()
    {
        File.WriteAllText(Path.Combine(_directory.Path, "cloud-key.txt"), "test-key\n");
        WriteConfig(CreateAdvancedValues());

        SecondOpinionConfig config = InformantConfig.Load(_directory.Path).SecondOpinion!;
        SecondOpinionModelSelection selection = config.SelectModel(new PrimaryReviewClassification(Severity.Medium, true));

        Assert.Equal("balanced", selection.ProfileName);
        Assert.Equal(ModelAuthentication.ApiKey, selection.Model.Authentication);
        Assert.Equal("test-key", selection.Model.ResolveApiKey());
    }

    [Fact]
    public void EnvironmentCanOverrideAnAdvancedProfile()
    {
        WriteConfig(CreateAdvancedValues());
        string? originalPremiumModelName = Environment.GetEnvironmentVariable("INFORMANT_SecondOpinion__Profiles__premium__ModelName");
        Environment.SetEnvironmentVariable("INFORMANT_SecondOpinion__Profiles__premium__ModelName", "overridden-premium");
        try
        {
            SecondOpinionConfig config = InformantConfig.Load(_directory.Path).SecondOpinion!;

            SecondOpinionModelSelection selection = config.SelectModel(new PrimaryReviewClassification(Severity.High, true));

            Assert.Equal("overridden-premium", selection.Model.ModelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("INFORMANT_SecondOpinion__Profiles__premium__ModelName", originalPremiumModelName);
        }
    }

    [Fact]
    public void AdvancedConfigurationRejectsMoreThanThreeProfiles()
    {
        Dictionary<string, object?> secondOpinion = CreateAdvancedValues();
        var profiles = (Dictionary<string, object?>)secondOpinion["profiles"]!;
        profiles["fourth"] = Profile("fourth");
        WriteConfig(secondOpinion);

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        Assert.Contains("between one and three", exception.Message);
    }

    [Fact]
    public void AdvancedConfigurationRequiresEveryRoute()
    {
        Dictionary<string, object?> secondOpinion = CreateAdvancedValues();
        var routes = (Dictionary<string, object?>)secondOpinion["routeBySeverity"]!;
        routes.Remove("undetermined");
        WriteConfig(secondOpinion);

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        Assert.Contains("routeBySeverity.undetermined", exception.Message);
    }

    [Fact]
    public void AdvancedConfigurationRejectsUnknownProfileRoute()
    {
        Dictionary<string, object?> secondOpinion = CreateAdvancedValues();
        var routes = (Dictionary<string, object?>)secondOpinion["routeBySeverity"]!;
        routes["high"] = "missing";
        WriteConfig(secondOpinion);

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        Assert.Contains("unknown profile 'missing'", exception.Message);
    }

    [Fact]
    public void AdvancedConfigurationRejectsMixedSimpleFields()
    {
        Dictionary<string, object?> secondOpinion = CreateAdvancedValues();
        secondOpinion["endpoint"] = "http://localhost:9999/v1";
        WriteConfig(secondOpinion);

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        Assert.Contains("cannot mix", exception.Message);
    }

    [Fact]
    public void SimpleConfigurationRejectsRoutingWithoutProfiles()
    {
        WriteConfig(new Dictionary<string, object?>
        {
            ["endpoint"] = "http://localhost:9999/v1",
            ["modelName"] = "validator",
            ["routeBySeverity"] = new Dictionary<string, object?> { ["high"] = "premium" }
        });

        InformantFatalException exception = Assert.Throws<InformantFatalException>(() => InformantConfig.Load(_directory.Path));

        Assert.Contains("requires advanced profiles", exception.Message);
    }

    private static SecondOpinionConfig CreateAdvancedConfig() => new()
    {
        Profiles = new Dictionary<string, SecondOpinionModelProfile>
        {
            ["economy"] = new() { Endpoint = "https://economy.example/v1", ModelName = "economy" },
            ["balanced"] = new() { Endpoint = "https://balanced.example/v1", ModelName = "balanced" },
            ["premium"] = new() { Endpoint = "https://premium.example/v1", ModelName = "premium" }
        },
        RouteBySeverity = new Dictionary<string, string>
        {
            ["none"] = "economy",
            ["low"] = "economy",
            ["medium"] = "balanced",
            ["high"] = "premium",
            ["critical"] = "premium",
            ["undetermined"] = "premium"
        }
    };

    private static Dictionary<string, object?> CreateAdvancedValues() => new()
    {
        ["profiles"] = new Dictionary<string, object?>
        {
            ["economy"] = Profile("economy"),
            ["balanced"] = new Dictionary<string, object?>
            {
                ["endpoint"] = "https://balanced.example/v1",
                ["modelName"] = "balanced",
                ["apiKey"] = "file:cloud-key.txt",
                ["authentication"] = "apiKey"
            },
            ["premium"] = Profile("premium")
        },
        ["routeBySeverity"] = new Dictionary<string, object?>
        {
            ["none"] = "economy",
            ["low"] = "economy",
            ["medium"] = "balanced",
            ["high"] = "premium",
            ["critical"] = "premium",
            ["undetermined"] = "premium"
        }
    };

    private static Dictionary<string, object?> Profile(string name) => new()
    {
        ["endpoint"] = $"https://{name}.example/v1",
        ["modelName"] = name
    };

    private void WriteConfig(Dictionary<string, object?> secondOpinion)
    {
        var values = new Dictionary<string, object?>
        {
            ["repositoryUrl"] = "https://example.test/repo.git",
            ["branch"] = "main",
            ["workingTreePath"] = Path.Combine(_directory.Path, "tree"),
            ["gitExecutablePath"] = TestGit.ExecutablePath,
            ["modelEndpoint"] = "http://localhost:1234/v1",
            ["modelName"] = "test-model",
            ["secondOpinion"] = secondOpinion
        };
        File.WriteAllText(Path.Combine(_directory.Path, InformantConfig.FileName), JsonSerializer.Serialize(values));
    }
}
