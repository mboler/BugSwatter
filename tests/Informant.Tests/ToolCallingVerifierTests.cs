using System.Net;

namespace Informant.Tests;

public sealed class ToolCallingVerifierTests
{
    private const string ProbeToken = "MELON-COVENANT-7291";

    [Fact]
    public async Task PassesWhenModelCallsToolAndEchoesToken()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", ReadFileLinesTool.ToolName, StubHttpMessageHandler.ReadArguments("probe.txt", 1, 3))));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse($"The verification token is {ProbeToken}"));

        VerificationResult result = await ToolCallingVerifier.VerifyAsync(CreateClient(handler), 24000);

        Assert.True(result.Success, result.Detail);
        Assert.Contains("1 tool call", result.Detail);
    }

    [Fact]
    public async Task FailsWhenModelAnswersWithoutAnyToolCall()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse($"I am sure the token is {ProbeToken}"));

        VerificationResult result = await ToolCallingVerifier.VerifyAsync(CreateClient(handler), 24000);

        Assert.False(result.Success);
        Assert.Contains("without making any tool call", result.Detail);
    }

    [Fact]
    public async Task FailsWhenAnswerLacksTheTokenFromTheFile()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.ToolCallResponse(("call_1", ReadFileLinesTool.ToolName, StubHttpMessageHandler.ReadArguments("probe.txt", 1, 3))));
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("I read the file but will not say what it contained"));

        VerificationResult result = await ToolCallingVerifier.VerifyAsync(CreateClient(handler), 24000);

        Assert.False(result.Success);
        Assert.Contains("did not contain the token", result.Detail);
    }

    [Fact]
    public async Task FailsCleanlyWhenEndpointIsUnreachable()
    {
        var handler = new StubHttpMessageHandler();
        handler.EnqueueException(new HttpRequestException("connection refused"));

        VerificationResult result = await ToolCallingVerifier.VerifyAsync(CreateClient(handler), 24000);

        Assert.False(result.Success);
        Assert.Contains("unreachable", result.Detail);
    }

    [Fact]
    public async Task RequireThrowsFatalOnFailure()
    {
        var handler = new StubHttpMessageHandler();
        handler.Enqueue(HttpStatusCode.OK, StubHttpMessageHandler.FinalResponse("no tools used"));
        InformantFatalException ex = await Assert.ThrowsAsync<InformantFatalException>(() => ToolCallingVerifier.RequireToolCallingAsync(CreateClient(handler), 24000));
        Assert.Contains("hard requirement", ex.Message);
    }

    private static ModelClient CreateClient(StubHttpMessageHandler handler) => new(new HttpClient(handler), "http://localhost:9999/v1", "test-model", TimeSpan.FromSeconds(5));
}
