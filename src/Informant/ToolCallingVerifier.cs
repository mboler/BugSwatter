using Serilog;

namespace Informant;

/// <summary>Outcome of the tool-calling verification gate</summary>
public sealed record VerificationResult(bool Success, string Detail);

/// <summary>The stage gate: proves the configured model actually performs tool-calling through the configured endpoint before any review is attempted. Tool-calling is a hard requirement; there is no text-only fallback</summary>
public static class ToolCallingVerifier
{
    private const string ProbeFileName = "probe.txt";
    private const string ProbeToken = "MELON-COVENANT-7291";

    /// <summary>Runs the minimal round trip: the model is offered read_file_lines and must call it, receive the result, and echo the token that exists only inside the probe file</summary>
    public static async Task<VerificationResult> VerifyAsync(ModelClient client, int maxContextCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        string probeDirectory = Path.Combine(Path.GetTempPath(), "informant-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDirectory);

        try
        {
            File.WriteAllLines(Path.Combine(probeDirectory, ProbeFileName), ["This file verifies Informant tool-calling.", $"The verification token is {ProbeToken}", "End of probe file."]);
            var loop = new ToolCallLoop(client, new ReadFileLinesTool(probeDirectory), maxContextCharacters);

            LoopResult result = await loop.RunAsync(
                "You are a tool-calling verification probe. You must use the read_file_lines tool to answer; do not answer from memory.",
                $"Call the read_file_lines tool to read lines 1 to 3 of the file '{ProbeFileName}'. Then reply with the verification token that appears on line 2, exactly as written.",
                cancellationToken);

            if (result.ToolCallCount == 0)
            {
                return new VerificationResult(false, "The model replied without making any tool call; either the model does not support tool-calling or the endpoint does not pass tools through");
            }

            return !result.FinalContent.Contains(ProbeToken, StringComparison.Ordinal)
                ? new VerificationResult(false,
                    $"The model made {result.ToolCallCount} tool call(s) but its answer did not contain the token read from the probe file: {result.FinalContent.Trim()}")
                : new VerificationResult(true, $"Tool-calling verified: {result.ToolCallCount} tool call(s) round-tripped and the probe token was echoed correctly");
        }
        catch (ModelCallException ex)
        {
            return new VerificationResult(false, ex.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(probeDirectory, true);
            }
            catch (IOException)
            {
                // best effort cleanup of the temp probe directory; leaking it must not fail verification
            }
            catch (UnauthorizedAccessException)
            {
                // best effort cleanup of the temp probe directory; leaking it must not fail verification
            }
        }
    }

    /// <summary>Runs the gate and converts a failure into the fatal abort the spec requires</summary>
    public static async Task RequireToolCallingAsync(ModelClient client, int maxContextCharacters, CancellationToken cancellationToken = default)
    {
        Log.Information("Verifying that the configured model performs tool-calling through the configured endpoint");

        var result = await VerifyAsync(client, maxContextCharacters, cancellationToken);
        if (!result.Success)
        {
            throw new InformantFatalException($"Tool-calling verification failed: {result.Detail}. Tool-calling is a hard requirement and there is no text-only fallback. Check that the endpoint is running, the model name is correct, and the model supports function calling");
        }

        Log.Information("Tool-calling verification passed: {Detail}", result.Detail);
    }
}
