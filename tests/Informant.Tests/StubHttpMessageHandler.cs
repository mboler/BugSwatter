using System.Net;
using System.Text;
using System.Text.Json;

namespace Informant.Tests;

/// <summary>Scripted HTTP handler standing in for an OpenAI-compatible endpoint: responses are dequeued in order and every request body is captured for assertions</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responders = new();

    /// <summary>Bodies of every request received, in order</summary>
    public List<string> RequestBodies { get; } = [];

    /// <summary>URIs of every request received, in order</summary>
    public List<Uri?> RequestUris { get; } = [];

    /// <summary>Authorization header of every request received, in order; null when absent</summary>
    public List<string?> AuthorizationHeaders { get; } = [];

    /// <summary>Queues a JSON response with the given status; a delay simulates a slow model</summary>
    public void Enqueue(HttpStatusCode status, string body, TimeSpan delay = default) => _responders.Enqueue(async cancellationToken =>
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    });

    /// <summary>Queues a connection-level failure</summary>
    public void EnqueueException(Exception exception) => _responders.Enqueue(_ => Task.FromException<HttpResponseMessage>(exception));

    /// <summary>Builds a chat response carrying a plain final answer</summary>
    public static string FinalResponse(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } } });

    /// <summary>Builds a chat response carrying tool calls; each entry is (id, name, arguments serialized to JSON)</summary>
    public static string ToolCallResponse(params (string Id, string Name, string ArgumentsJson)[] calls)
    {
        var toolCalls = calls.Select(call => new { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.ArgumentsJson } }).ToArray();
        return JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = (string?)null, tool_calls = toolCalls }, finish_reason = "tool_calls" } } });
    }

    /// <summary>Serializes read_file_lines arguments</summary>
    public static string ReadArguments(string path, int startLine, int endLine) => JsonSerializer.Serialize(new { path, start_line = startLine, end_line = endLine });

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        RequestUris.Add(request.RequestUri);
        AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());

        if (_responders.Count == 0)
        {
            throw new InvalidOperationException("StubHttpMessageHandler received more requests than were scripted");
        }

        return await _responders.Dequeue()(cancellationToken);
    }
}
