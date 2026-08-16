using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using BugSwatter.Common;

namespace Informant;

/// <summary>Result of one controller-owned GitHub Check Run publication</summary>
public sealed record GitHubCheckRunResult(int AnnotationCount, int EligibleFindingCount);

/// <summary>Failure returned while publishing an optional GitHub Check Run</summary>
public sealed class GitHubCheckRunException : Exception
{
    /// <summary>Creates a publication failure with a safe bounded explanation</summary>
    public GitHubCheckRunException(string message) : base(message)
    {
    }

    /// <summary>Creates a publication failure that preserves its network cause</summary>
    public GitHubCheckRunException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>Publishes bounded informational annotations from application-controlled finding artifacts</summary>
public sealed class GitHubCheckRunPublisher
{
    /// <summary>Maximum annotations GitHub accepts in one Check Run creation request</summary>
    public const int MaxAnnotations = 50;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    /// <summary>Creates a publisher over the supplied reusable HTTP client</summary>
    public GitHubCheckRunPublisher(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>Creates one completed neutral check for the reviewed commit without granting either model API access</summary>
    public async Task<GitHubCheckRunResult> PublishAsync(GitHubCheckRunConfig config, string tipSha, FindingRunArtifact artifact, IReadOnlyList<FileReviewResult> results, string artifactFileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(tipSha);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactFileName);

        string token = config.ResolveToken() ?? throw new GitHubCheckRunException("Unresolved GitHub Check Run token reference");
        GitHubCheckAnnotation[] eligible = CreateEligibleAnnotations(artifact, results);
        GitHubCheckAnnotation[] annotations = [.. eligible.Take(MaxAnnotations)];
        int newCount = artifact.Findings.Count(finding => finding.Disposition == FindingDisposition.New);
        int knownCount = artifact.Findings.Count(finding => finding.Disposition == FindingDisposition.Known);
        int suppressedCount = artifact.Findings.Count(finding => finding.Disposition == FindingDisposition.Suppressed);
        string summary = $"BugSwatter recorded {newCount} new, {knownCount} known, and {suppressedCount} suppressed findings. "
            + $"This informational check includes {annotations.Length} of {eligible.Length} eligible changed-line annotations. See the local {Path.GetFileName(artifactFileName)} artifact for complete disposition details.";
        var output = new GitHubCheckOutput($"BugSwatter review: {newCount} new finding{(newCount == 1 ? "" : "s")}", summary, annotations);
        var payload = new GitHubCheckRequest(config.Name, tipSha, "completed", "neutral", output);
        string[] repositoryParts = config.Repository.Split('/', StringSplitOptions.TrimEntries);
        var uri = new Uri($"https://api.github.com/repos/{Uri.EscapeDataString(repositoryParts[0])}/{Uri.EscapeDataString(repositoryParts[1])}/check-runs");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(payload) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("BugSwatter", "1.2"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(RequestTimeout);

        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
                string detail = SanitizeText(body, 1000);
                throw new GitHubCheckRunException($"GitHub Check Run publication failure: HTTP {(int)response.StatusCode} ({response.StatusCode}){(detail.Length == 0 ? "" : $": {detail}")}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            throw new GitHubCheckRunException($"Unable to publish the GitHub Check Run: {ex.Message}", ex);
        }

        return new GitHubCheckRunResult(annotations.Length, eligible.Length);
    }

    private static GitHubCheckAnnotation[] CreateEligibleAnnotations(FindingRunArtifact artifact, IReadOnlyList<FileReviewResult> results)
    {
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        Dictionary<string, ChangedFile> changedFiles = results
            .Select(result => result.File)
            .GroupBy(file => file.Path, comparer)
            .ToDictionary(group => group.Key, group => group.Last(), comparer);

        return
        [
            .. artifact.Findings
                .Where(finding => finding.Disposition == FindingDisposition.New && finding.ValidatorStatus != FindingValidatorStatus.Discarded && finding.AnchorLine > 0)
                .Where(finding => changedFiles.TryGetValue(finding.Path, out ChangedFile? file)
                    && file.Kind != ChangeKind.Deleted
                    && file.ChangedRanges.Any(range => finding.AnchorLine >= range.Start && finding.AnchorLine <= range.End))
                .OrderByDescending(finding => SecondOpinionParser.ParseSeverity(finding.Severity))
                .ThenBy(finding => finding.Path, StringComparer.Ordinal)
                .ThenBy(finding => finding.AnchorLine)
                .ThenBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                .Select(CreateAnnotation)
        ];
    }

    private static GitHubCheckAnnotation CreateAnnotation(FindingArtifactEntry finding)
    {
        Severity severity = SecondOpinionParser.ParseSeverity(finding.Severity);
        string level = severity >= Severity.High ? "warning" : "notice";
        string message = SanitizeText(finding.Summary, 4000);
        if (message.Length == 0)
        {
            message = "BugSwatter reported a finding at this location.";
        }

        string title = $"BugSwatter {finding.Severity.Trim().ToLowerInvariant()} {finding.Category} finding";
        return new GitHubCheckAnnotation(finding.Path, finding.AnchorLine, finding.AnchorLine, level, SanitizeText(title, 255), message);
    }

    private static string SanitizeText(string value, int maxCharacters)
    {
        var builder = new StringBuilder(Math.Min(value.Length, maxCharacters));
        foreach (char character in value)
        {
            if (builder.Length >= maxCharacters)
            {
                break;
            }

            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private sealed record GitHubCheckRequest([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("head_sha")] string HeadSha,
        [property: JsonPropertyName("status")] string Status, [property: JsonPropertyName("conclusion")] string Conclusion, [property: JsonPropertyName("output")] GitHubCheckOutput Output);

    private sealed record GitHubCheckOutput([property: JsonPropertyName("title")] string Title, [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("annotations")] IReadOnlyList<GitHubCheckAnnotation> Annotations);

    private sealed record GitHubCheckAnnotation([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("start_line")] int StartLine,
        [property: JsonPropertyName("end_line")] int EndLine, [property: JsonPropertyName("annotation_level")] string AnnotationLevel,
        [property: JsonPropertyName("title")] string Title, [property: JsonPropertyName("message")] string Message);
}
