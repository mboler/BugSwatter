namespace Informant.Tests;

// Opt-in live integration tests. They drive a real OpenAI-compatible endpoint (a local model server such as
// LM Studio or Ollama, or a cloud gateway) and are SKIPPED unless the environment opts in, so an ordinary
// `dotnet test` run and CI stay green and offline. To run them, set:
//   INFORMANT_IT=1
//   INFORMANT_IT_ENDPOINT=http://host:port/v1
//   INFORMANT_IT_MODEL=your-review-model
// and, to also cover the second-opinion endpoint:
//   INFORMANT_IT_SO_ENDPOINT=http://host:port/v1
//   INFORMANT_IT_SO_MODEL=your-validator-model
//
// Example (PowerShell), pointing at the review model on one host and the validator on another:
//   $env:INFORMANT_IT='1'
//   $env:INFORMANT_IT_ENDPOINT='http://model-host.example:1234/v1'; $env:INFORMANT_IT_MODEL='your-review-model'
//   dotnet test tests/Informant.Tests
public sealed class LiveIntegrationTests
{
    [Fact]
    public async Task ModelEndpointCompletesASimplePrompt()
    {
        Assert.SkipUnless(Live.Enabled, Live.DisabledReason);

        var client = new ModelClient(new HttpClient(), Live.Endpoint, Live.Model, TimeSpan.FromSeconds(120));
        ChatMessage reply = await client.CompleteAsync(
            [new ChatMessage { Role = "system", Content = "You are a terse assistant." }, new ChatMessage { Role = "user", Content = "Reply with the single word: pong" }],
            []);

        Assert.False(string.IsNullOrWhiteSpace(reply.Content), "the model returned no content");
    }

    [Fact]
    public async Task ToolCallingVerificationPassesLive()
    {
        Assert.SkipUnless(Live.Enabled, Live.DisabledReason);

        var client = new ModelClient(new HttpClient(), Live.Endpoint, Live.Model, TimeSpan.FromSeconds(120));
        VerificationResult result = await ToolCallingVerifier.VerifyAsync(client, 24000);

        // On failure the detail explains whether the model made no tool call or echoed the wrong token
        Assert.True(result.Success, result.Detail);
    }

    [Fact]
    public async Task ReviewOfABuggyFileProducesFindings()
    {
        Assert.SkipUnless(Live.Enabled, Live.DisabledReason);

        using var tree = new TempDirectory();
        const string fileName = "ConfigLoader.cs";
        await File.WriteAllTextAsync(Path.Combine(tree.Path, fileName), BuggySource);

        var client = new ModelClient(new HttpClient(), Live.Endpoint, Live.Model, TimeSpan.FromSeconds(300));
        var loop = new ToolCallLoop(client, new ReadFileLinesTool(tree.Path), 24000);
        var reviewer = new FileReviewer(loop, tree.Path, DefaultReviewPrompt.Text, 800, 24000, 1);

        FileReviewResult result = await reviewer.ReviewAsync(new ChangedFile(fileName, ChangeKind.Added, []));

        Assert.True(result.FullyReviewed, result.SkipReason ?? "review did not complete");
        Assert.False(string.IsNullOrWhiteSpace(result.Findings), "the review produced no findings text");
    }

    [Fact]
    public async Task SecondOpinionEndpointCompletesASimplePrompt()
    {
        Assert.SkipUnless(Live.Enabled, Live.DisabledReason);
        Assert.SkipUnless(Live.SecondOpinionConfigured, "set INFORMANT_IT_SO_ENDPOINT and INFORMANT_IT_SO_MODEL to cover the second-opinion endpoint");

        var client = new ModelClient(new HttpClient(), Live.SecondOpinionEndpoint, Live.SecondOpinionModel, TimeSpan.FromSeconds(120));
        ChatMessage reply = await client.CompleteAsync([new ChatMessage { Role = "user", Content = "Reply with the single word: ok" }], []);

        Assert.False(string.IsNullOrWhiteSpace(reply.Content), "the second-opinion model returned no content");
    }

    /// <summary>Asks the configured validator to judge a real primary finding against the referenced source file</summary>
    [Fact]
    public async Task SecondOpinionValidatesFindingsAgainstActualCode()
    {
        Assert.SkipUnless(Live.Enabled, Live.DisabledReason);
        Assert.SkipUnless(Live.SecondOpinionConfigured, "set INFORMANT_IT_SO_ENDPOINT and INFORMANT_IT_SO_MODEL to cover the second-opinion endpoint");

        using var tree = new TempDirectory();
        const string fileName = "Calculator.cs";
        await File.WriteAllTextAsync(Path.Combine(tree.Path, fileName), """
            namespace Demo;

            public static class Calculator
            {
                public static int Divide(int dividend, int divisor) => dividend / divisor;
            }
            """);

        var client = new ModelClient(new HttpClient(), Live.SecondOpinionEndpoint, Live.SecondOpinionModel, TimeSpan.FromMinutes(15));
        var reviewer = new SecondOpinionReviewer(client, tree.Path, DefaultSecondOpinionPrompt.Text, 24000, 30, false, 0);
        var primaryResult = new FileReviewResult(new ChangedFile(fileName, ChangeKind.Modified, [new LineRange(5, 5)]), FileReviewStatus.Reviewed,
            "Divide does not guard against a zero divisor, so ordinary input can throw DivideByZeroException.", 1, 1, null);

        string? validation = await reviewer.ValidateAsync(primaryResult);

        Assert.False(string.IsNullOrWhiteSpace(validation), "the second-opinion review returned no validation");
        Assert.Contains("zero", validation, StringComparison.OrdinalIgnoreCase);
    }

    // A small file with unmistakable defects (undisposed StreamReader, and IndexOf('=') returning -1 feeding
    // Substring) so any competent model has something to report
    private const string BuggySource = """
        namespace Demo;

        public static class ConfigLoader
        {
            public static Dictionary<string, string> Load(string path)
            {
                var result = new Dictionary<string, string>();
                var reader = new StreamReader(path);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    int separator = line.IndexOf('=');
                    result[line.Substring(0, separator)] = line.Substring(separator + 1);
                }

                return result;
            }
        }
        """;
}

// Reads the opt-in configuration once from the environment; file-scoped so it stays private to these tests
file static class Live
{
    static Live()
    {
        Endpoint = Environment.GetEnvironmentVariable("INFORMANT_IT_ENDPOINT") ?? "";
        Model = Environment.GetEnvironmentVariable("INFORMANT_IT_MODEL") ?? "";
        SecondOpinionEndpoint = Environment.GetEnvironmentVariable("INFORMANT_IT_SO_ENDPOINT") ?? "";
        SecondOpinionModel = Environment.GetEnvironmentVariable("INFORMANT_IT_SO_MODEL") ?? "";

        string masterSwitch = Environment.GetEnvironmentVariable("INFORMANT_IT") ?? "";
        _switchedOn = masterSwitch is "1" || masterSwitch.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly bool _switchedOn;

    public static string Endpoint { get; }

    public static string Model { get; }

    public static string SecondOpinionEndpoint { get; }

    public static string SecondOpinionModel { get; }

    /// <summary>True only when the master switch is on and the review endpoint and model are both set</summary>
    public static bool Enabled => _switchedOn && Endpoint.Length > 0 && Model.Length > 0;

    /// <summary>True when the optional second-opinion endpoint and model are both set</summary>
    public static bool SecondOpinionConfigured => SecondOpinionEndpoint.Length > 0 && SecondOpinionModel.Length > 0;

    public static string DisabledReason => "live integration tests are opt-in: set INFORMANT_IT=1 with INFORMANT_IT_ENDPOINT and INFORMANT_IT_MODEL to run them";
}
