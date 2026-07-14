using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BugSwatter.Common;
using Serilog;

namespace BugSwatter.AI;

/// <summary>Minimal client for an OpenAI-compatible chat-completions endpoint with native tool-calling. No streaming, one choice, nothing speculative</summary>
public sealed class ModelClient
{
    /// <summary>Default model-response limit of 4 MiB</summary>
    public const int DefaultMaxResponseBytes = 4 * 1024 * 1024;

    private const int MaxRateLimitRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private readonly Uri _chatCompletionsUri;
    private readonly string _modelName;
    private readonly TimeSpan _requestTimeout;
    private readonly string? _apiKey;
    private readonly int _maxResponseBytes;
    private readonly ModelAuthentication _authentication;
    private readonly Action<ModelCallProgress>? _progressObserver;

    /// <summary>Creates a client over an injected HttpClient; an optional API key is sent per request as a bearer token
    /// or api-key header so authenticated cloud endpoints work through the same client as local endpoints</summary>
    public ModelClient(HttpClient httpClient, string endpoint, string modelName, TimeSpan requestTimeout, string? apiKey = null,
        int maxResponseBytes = DefaultMaxResponseBytes, ModelAuthentication authentication = ModelAuthentication.Bearer, Action<ModelCallProgress>? progressObserver = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBytes);
        if (!Enum.IsDefined(authentication))
        {
            throw new ArgumentOutOfRangeException(nameof(authentication), authentication, "The model authentication mode is not supported");
        }

        _http = httpClient;
        try
        {
            // The per-request timeout is enforced via a linked token below; HttpClient's own 100 second default must not
            // preempt slow local models. A second client over the same shared HttpClient skips the redundant set, because
            // setting Timeout after the first request throws even for an unchanged value
            if (_http.Timeout != Timeout.InfiniteTimeSpan)
            {
                _http.Timeout = Timeout.InfiniteTimeSpan;
            }
        }
        catch (InvalidOperationException)
        {
            // the injected client has already sent a request, so its configured timeout stays in force alongside the per-request token
            Log.Warning("HttpClient was already in use; its existing timeout remains active");
        }

        _chatCompletionsUri = new Uri(endpoint.TrimEnd('/') + "/chat/completions");
        _modelName = modelName;
        _requestTimeout = requestTimeout;
        _apiKey = apiKey;
        _maxResponseBytes = maxResponseBytes;
        _authentication = authentication;
        _progressObserver = progressObserver;
    }

    /// <summary>Sends the conversation with the offered tools and returns the assistant message of the first choice</summary>
    public async Task<ChatMessage> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default)
    {
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        // Endpoints reject empty tool arrays, so a tool-free conversation omits both fields entirely
        var request = new ChatRequest { Model = _modelName, Messages = messages, Tools = tools.Count > 0 ? tools : null, ToolChoice = tools.Count > 0 ? "auto" : null };
        string requestBody = JsonSerializer.Serialize(request, JsonOptions);
        ReportProgress(new ModelCallProgress(ModelCallState.Started, _modelName, startedUtc, TimeSpan.Zero, null, requestBody.Length));

        try
        {
            (ChatMessage message, ModelTokenUsage? usage) = await CompleteCoreAsync(requestBody, cancellationToken);
            ReportProgress(new ModelCallProgress(ModelCallState.Completed, _modelName, startedUtc, stopwatch.Elapsed, usage, requestBody.Length));
            return message;
        }
        catch
        {
            // catch-all: every failed request reports a terminal telemetry event before the original exception continues unchanged
            ReportProgress(new ModelCallProgress(ModelCallState.Failed, _modelName, startedUtc, stopwatch.Elapsed, null, requestBody.Length));
            throw;
        }
    }

    private async Task<(ChatMessage Message, ModelTokenUsage? Usage)> CompleteCoreAsync(string requestBody, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_requestTimeout);

        string body = "";
        try
        {
            for (int retry = 0; ; retry++)
            {
                using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri) { Content = content };
                if (_apiKey is not null)
                {
                    if (_authentication == ModelAuthentication.ApiKey)
                    {
                        httpRequest.Headers.Add("api-key", _apiKey);
                    }
                    else
                    {
                        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                    }
                }

                using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);

                body = await ReadResponseBodyAsync(response.Content, timeoutSource.Token);
                if (response.IsSuccessStatusCode)
                {
                    break;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && retry < MaxRateLimitRetries)
                {
                    TimeSpan delay = GetRateLimitDelay(response, retry);
                    Log.Warning("Model endpoint rate limited the request; retrying in {DelaySeconds:0.###} seconds ({Attempt}/{Maximum})", delay.TotalSeconds, retry + 1, MaxRateLimitRetries);
                    await Task.Delay(delay, timeoutSource.Token);
                    continue;
                }

                throw new ModelCallException($"Model endpoint returned {(int)response.StatusCode} {response.StatusCode}: {TextSummary.Create(body, 500)}");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ModelCallException($"Model request timed out after {_requestTimeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex)
        {
            throw new ModelCallException($"Model endpoint unreachable at {_chatCompletionsUri}: {ex.Message}", ex);
        }

        ChatResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ChatResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ModelCallException($"Model endpoint returned unparseable JSON: {ex.Message}", ex);
        }

        var message = parsed?.Choices is { Count: > 0 } choices ? choices[0].Message : null;
        if (message is null)
        {
            throw new ModelCallException($"Model response contained no message: {TextSummary.Create(body, 500)}");
        }

        Log.Debug("Model reply: {ToolCallCount} tool calls, {ContentLength} content characters", message.ToolCalls?.Count ?? 0, message.Content?.Length ?? 0);
        ModelTokenUsage? usage = parsed?.Usage is null ? null : new ModelTokenUsage(parsed.Usage.PromptTokens, parsed.Usage.CompletionTokens, parsed.Usage.TotalTokens);
        return (message, usage);
    }

    private static TimeSpan GetRateLimitDelay(HttpResponseMessage response, int retry)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (response.Headers.RetryAfter?.Date is { } retryAt)
        {
            TimeSpan delay = retryAt - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return TimeSpan.FromSeconds(1 << retry);
    }

    private void ReportProgress(ModelCallProgress progress)
    {
        try
        {
            _progressObserver?.Invoke(progress);
        }
        catch (Exception ex)
        {
            // catch-all: optional telemetry must never change whether a model request succeeds or fails
            Log.Warning("Model progress observer failed: {Reason}", ex.Message);
        }
    }

    private async Task<string> ReadResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _maxResponseBytes)
        {
            throw new ModelCallException($"Model response exceeds maxModelResponseBytes limit of {_maxResponseBytes} bytes");
        }

        await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
        byte[]? body = await BoundedStreamReader.ReadAsync(source, _maxResponseBytes, cancellationToken);
        if (body is null)
        {
            throw new ModelCallException($"Model response exceeds maxModelResponseBytes limit of {_maxResponseBytes} bytes");
        }

        return Encoding.UTF8.GetString(body);
    }
}
