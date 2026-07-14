using System.Text.Json;
using BugSwatter.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Serilog;

namespace Marshal;

/// <summary>Captures when the process started, for the status view</summary>
public sealed class MarshalStatus
{
    /// <summary>When Marshal started, used to report uptime</summary>
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
}

/// <summary>Maps health, status, history, dashboard and provider webhook routes onto the Kestrel host
/// Webhook routes authenticate requests, accept repository push events and deduplicate provider delivery IDs</summary>
public static class WebEndpoints
{
    private const int MaxBodyBytes = 1024 * 1024;

    /// <summary>Maps every route; webhook routes are added only when webhooks are enabled</summary>
    public static void Map(WebApplication app, MarshalConfig config)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(config);

        app.MapGet("/health", (ReviewQueue queue, MarshalStatus status) => Results.Json(new
        {
            status = "ok",
            uptimeSeconds = Math.Round((DateTimeOffset.UtcNow - status.StartedUtc).TotalSeconds, 0),
            running = queue.RunningJobName,
            queueDepth = queue.WaitingCount
        }));

        app.MapGet("/api/status", (ReviewQueue queue, MarshalStatus status, CurrentReviewStatusStore current) => Results.Json(new
        {
            startedUtc = status.StartedUtc.ToString("O"),
            uptimeSeconds = Math.Round((DateTimeOffset.UtcNow - status.StartedUtc).TotalSeconds, 0),
            running = queue.RunningJobName,
            queueDepth = queue.WaitingCount,
            jobCount = config.Jobs.Count,
            activity = current.Snapshot()
        }));

        app.MapGet("/api/history", (RunHistoryStore history) => Results.Json(history.ReadRecent(100)));

        // Configured jobs, so a UI can enumerate them and offer a Run Now control per job
        app.MapGet("/api/jobs", () => Results.Json(config.Jobs.Select(job => new
        {
            name = job.Name,
            schedule = job.Schedule ?? [],
            pollSchedule = job.Poll?.Schedule,
            watchPath = job.WatchPath,
            webhook = job.Webhook is null ? null : (object)new { provider = job.Webhook.Provider.ToString(), repository = job.Webhook.Repository }
        })));

        // Manually enqueue a configured job; the same path a webhook or schedule takes, so coalescing and the serial executor still apply
        app.MapPost("/api/jobs/{name}/run", (string name, ReviewQueue queue) =>
        {
            ReviewJobConfig? job = config.Jobs.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (job is null)
            {
                return Results.NotFound(new { error = $"no configured job named '{name}'" });
            }

            EnqueueResult result = queue.Enqueue(job, "manual trigger via API");
            return Results.Json(new { job = job.Name, result = result.ToString() });
        });

        // The live queue: what is running and what is waiting
        app.MapGet("/api/queue", (ReviewQueue queue) => Results.Json(new
        {
            running = queue.RunningJobName,
            waiting = queue.SnapshotWaiting().Select(request => new { job = request.Job.Name, reason = request.Reason })
        }));

        // Cancel a waiting review by job name; the review currently running is never touched
        app.MapDelete("/api/queue/{name}", (string name, ReviewQueue queue) =>
            queue.RemoveWaiting(name) ? Results.Json(new { removed = name }) : Results.NotFound(new { error = $"'{name}' is not waiting in the queue" }));

        app.MapGet("/", () => Results.Content(DashboardPage.Html, "text/html"));
        app.MapGet("/dashboard", () => Results.Content(DashboardPage.Html, "text/html"));

        if (config.Webhook is { Enabled: true })
        {
            app.MapPost("/webhook/github", (HttpContext context, ReviewQueue queue, WebhookDeliveryTracker deliveries) => HandleWebhookAsync(context, queue, deliveries, config, WebhookProvider.GitHub));
            app.MapPost("/webhook/azuredevops", (HttpContext context, ReviewQueue queue, WebhookDeliveryTracker deliveries) => HandleWebhookAsync(context, queue, deliveries, config, WebhookProvider.AzureDevOps));
            Log.Information("Webhook routes enabled: /webhook/github (HMAC-SHA256), /webhook/azuredevops (basic auth)");
        }
    }

    private static async Task<IResult> HandleWebhookAsync(HttpContext context, ReviewQueue queue, WebhookDeliveryTracker deliveries, MarshalConfig config, WebhookProvider provider)
    {
        byte[]? body = await ReadBodyBoundedAsync(context.Request.Body, MaxBodyBytes, context.RequestAborted);
        if (body is null)
        {
            Log.Warning("Rejected {Provider} webhook from {Remote}: body exceeded {Max} bytes", provider, context.Connection.RemoteIpAddress, MaxBodyBytes);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!Validate(context, config, provider, body))
        {
            Log.Warning("Rejected {Provider} webhook from {Remote}: signature validation failed", provider, context.Connection.RemoteIpAddress);
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        JsonDocument payload;
        try
        {
            payload = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return Results.BadRequest("payload is not valid JSON");
        }

        using (payload)
        {
            string? eventType = WebhookRouter.ExtractEventType(provider, payload.RootElement, context.Request.Headers["X-GitHub-Event"]);
            if (eventType is null)
            {
                return Results.BadRequest("payload carries no recognized event type");
            }

            if (WebhookRouter.IsHandshakeEvent(provider, eventType))
            {
                Log.Information("Accepted {Provider} webhook handshake", provider);
                return Results.Ok();
            }

            if (!WebhookRouter.IsRepositoryChangeEvent(provider, eventType))
            {
                Log.Information("Ignored authenticated {Provider} webhook event {EventType}: only repository push events trigger reviews", provider, eventType);
                return Results.Accepted();
            }

            string? deliveryId = WebhookRouter.ExtractDeliveryId(provider, payload.RootElement, context.Request.Headers["X-GitHub-Delivery"]);
            if (deliveryId is null)
            {
                return Results.BadRequest("repository-change webhook carries no valid delivery ID");
            }

            string? repository = WebhookRouter.ExtractRepository(provider, payload.RootElement);
            if (repository is null)
            {
                return Results.BadRequest("payload carries no repository identifier");
            }

            ReviewJobConfig? job = WebhookRouter.MatchJob(config.Jobs, provider, repository);
            if (job is null)
            {
                Log.Warning("Valid {Provider} webhook for {Repository} matches no configured job", provider, repository);
                return Results.NotFound("no job configured for this repository");
            }

            if (!deliveries.TryClaim(provider, deliveryId))
            {
                Log.Information("Ignored duplicate {Provider} webhook delivery {DeliveryId} for {Repository}", provider, deliveryId, repository);
                return Results.Accepted();
            }

            EnqueueResult result = queue.Enqueue(job, $"{provider} {eventType} webhook for {repository}");
            if (result == EnqueueResult.DroppedQueueFull)
            {
                deliveries.Forget(provider, deliveryId);
                Log.Warning("Could not accept {Provider} webhook delivery {DeliveryId}: review queue is full; returning 503 so the provider can retry", provider, deliveryId);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Accepted();
        }
    }

    private static bool Validate(HttpContext context, MarshalConfig config, WebhookProvider provider, byte[] body)
    {
        if (provider == WebhookProvider.GitHub)
        {
            string? secret = config.ResolveConfiguredSecret(config.Webhook!.GitHubSecret);
            if (secret is null)
            {
                Log.Error("A GitHub webhook arrived but no gitHubSecret is configured; rejecting");
                return false;
            }

            return WebhookValidator.ValidateGitHubSignature(body, context.Request.Headers["X-Hub-Signature-256"], secret);
        }

        string? adoSecret = config.ResolveConfiguredSecret(config.Webhook!.AzureDevOpsSecret);
        if (adoSecret is null)
        {
            Log.Error("An Azure DevOps webhook arrived but no azureDevOpsSecret is configured; rejecting");
            return false;
        }

        return WebhookValidator.ValidateBasicAuthorization(context.Request.Headers.Authorization, adoSecret);
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> from the stream; the Content-Length header cannot be trusted for this because chunked requests carry none</summary>
    /// <returns>The body bytes, or null when the stream held more than the limit</returns>
    public static Task<byte[]?> ReadBodyBoundedAsync(Stream input, int maxBytes, CancellationToken cancellationToken = default) => BoundedStreamReader.ReadAsync(input, maxBytes, cancellationToken);
}
