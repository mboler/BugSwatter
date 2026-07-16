using System.Net;

namespace Informant.Tests;

/// <summary>Verifies deployment validation for loaded-model wildcard targets</summary>
public sealed class LoadedModelValidationTests
{
    /// <summary>Verifies validation reports the resolved model and its capacity</summary>
    [Fact]
    public async Task ValidationReportsResolvedModelAndItsCapacity()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        handler.Enqueue(HttpStatusCode.OK, LoadedModelJson("loaded-review-model", 32768));
        handler.Enqueue(HttpStatusCode.OK, LoadedModelJson("loaded-review-model", 32768));
        using var http = new HttpClient(handler);

        IReadOnlyList<ValidationCheck> checks = await ValidateCommand.GatherChecksAsync(Config(), http);

        ValidationCheck selection = Assert.Single(checks, check => check.Label == "loaded model resolved");
        Assert.True(selection.Passed);
        Assert.Contains("loaded-review-model", selection.Detail);
        ValidationCheck capacity = Assert.Single(checks, check => check.Label == "model context capacity");
        Assert.True(capacity.Passed);
        Assert.Contains("32,768 loaded tokens", capacity.Detail);
    }

    /// <summary>Verifies validation rejects ambiguous selection and skips model-specific capacity checks</summary>
    [Fact]
    public async Task ValidationRejectsAmbiguousLoadedModelsAndSkipsCapacityCheck()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, "{}");
        handler.Enqueue(HttpStatusCode.OK, """
            {
              "models": [
                {
                  "type": "llm",
                  "loaded_instances": [
                    { "id": "model-one" },
                    { "id": "model-two" }
                  ]
                }
              ]
            }
            """);
        using var http = new HttpClient(handler);

        IReadOnlyList<ValidationCheck> checks = await ValidateCommand.GatherChecksAsync(Config(), http);

        ValidationCheck selection = Assert.Single(checks, check => check.Label == "loaded model resolved");
        Assert.False(selection.Passed);
        Assert.Contains("multiple loaded", selection.Detail);
        Assert.DoesNotContain(checks, check => check.Label == "model context capacity");
    }

    private static InformantConfig Config() => new()
    {
        RepositoryUrl = "https://example.test/repo.git",
        Branch = "main",
        WorkingTreePath = @"C:\informant\tree",
        GitExecutablePath = TestGit.ExecutablePath,
        ModelEndpoint = "http://localhost:1234/v1",
        ModelName = PrimaryModelTargetResolver.LoadedModelWildcard
    };

    private static string LoadedModelJson(string modelName, int contextLength) => $$"""
        {
          "models": [
            {
              "type": "llm",
              "key": "{{modelName}}",
              "max_context_length": 262144,
              "loaded_instances": [
                {
                  "id": "{{modelName}}",
                  "config": { "context_length": {{contextLength}} }
                }
              ]
            }
          ]
        }
        """;
}
