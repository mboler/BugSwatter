using System.Net;
using System.Text;
using System.Text.Json;

namespace BugSwatter.AI.Tests;

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responders = new();

    public List<string> RequestBodies { get; } = [];

    public List<Uri?> RequestUris { get; } = [];

    public List<string?> AuthorizationHeaders { get; } = [];

    public void Enqueue(HttpStatusCode status, string body, TimeSpan delay = default) => _responders.Enqueue(async cancellationToken =>
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    });

    public void EnqueueContent(HttpStatusCode status, HttpContent content) => _responders.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status) { Content = content }));

    public void EnqueueException(Exception exception) => _responders.Enqueue(_ => Task.FromException<HttpResponseMessage>(exception));

    public static string FinalResponse(string content) => JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } } });

    public static HttpContent UnknownLengthContent(string body) => new UnknownLengthStringContent(body);

    public static string ToolCallResponse(params (string Id, string Name, string ArgumentsJson)[] calls)
    {
        var toolCalls = calls.Select(call => new { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.ArgumentsJson } }).ToArray();
        return JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = (string?)null, tool_calls = toolCalls }, finish_reason = "tool_calls" } } });
    }

    public static string ReadArguments(string path, int startLine, int endLine) => JsonSerializer.Serialize(new { path, start_line = startLine, end_line = endLine });

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

    private sealed class UnknownLengthStringContent(string body) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(body);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(_bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(_bytes, writable: false));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
    }
}
