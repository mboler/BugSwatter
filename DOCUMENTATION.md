# BugSwatter documentation

BugSwatter consists of `Informant`, which performs one code-review run, and `Marshal`, which dispatches Informant for one or more repositories. This guide covers installation, configuration, operation, deployment, and maintenance. See the [README](README.md) for a shorter introduction.

## Contents

- [Requirements](#requirements)
- [Install a release](#install-a-release)
- [Informant quick start](#informant-quick-start)
- [Safety model](#safety-model)
- [Informant commands](#informant-commands)
- [Informant configuration](#informant-configuration)
- [Environment-variable overrides](#environment-variable-overrides)
- [Secrets](#secrets)
- [Reports, baselines, and retention](#reports-baselines-and-retention)
- [Second opinion](#second-opinion)
- [Email](#email)
- [Marshal](#marshal)
- [Marshal configuration](#marshal-configuration)
- [Triggers](#triggers)
- [Dashboard and API](#dashboard-and-api)
- [Windows service deployment](#windows-service-deployment)
- [Linux systemd deployment](#linux-systemd-deployment)
- [Git credentials for polling and reviews](#git-credentials-for-polling-and-reviews)
- [Cost and retention controls](#cost-and-retention-controls)
- [Build, test, and release](#build-test-and-release)
- [Design notes](#design-notes)

## Requirements

Running BugSwatter requires:

- A supported x64 Windows or Linux system
- Git, including credentials that the running account can use when repositories are private
- .NET 10
- An OpenAI-compatible endpoint for the primary review model
- A model that supports OpenAI-style tool calling for the primary review

The first release has no primary-model API-key setting. The primary endpoint must accept requests without application-level authentication, normally because it is a local model server or an internal gateway. Keep such an endpoint on a trusted network. The optional second opinion supports an API-key reference.

The framework-dependent release archives do not include .NET. Informant needs the .NET 10 runtime. Marshal also hosts Kestrel when its web server is enabled, so installing both the .NET 10 runtime and ASP.NET Core 10 runtime is the simplest supported deployment. The .NET 10 SDK includes both and is sufficient on development machines.

Use `dotnet --list-runtimes` to confirm that `Microsoft.NETCore.App 10.*` and `Microsoft.AspNetCore.App 10.*` are installed. Current platform-specific installation instructions are maintained in the [.NET installation documentation](https://learn.microsoft.com/dotnet/core/install/).

## Install a release

Each GitHub Release contains:

- `BugSwatter-<version>-win-x64.zip`
- `BugSwatter-<version>-linux-x64.tar.gz`
- `SHA256SUMS.txt`

Each archive contains framework-dependent, single-file Informant and Marshal executables plus the project documentation and license. There is no installer.

### Windows

Install Git and the .NET 10 runtimes. With Windows Package Manager, an elevated terminal can use:

```powershell
winget install Git.Git
winget install Microsoft.DotNet.Runtime.10
winget install Microsoft.DotNet.AspNetCore.10
```

Download the Windows archive and `SHA256SUMS.txt` from the same GitHub Release. Compare the published checksum before extracting:

The Windows executables are not code-signed, so Windows may identify them as coming from an unknown publisher or display a SmartScreen warning. Download releases only from this GitHub repository, and proceed only after the archive's checksum matches `SHA256SUMS.txt`.

```powershell
Get-FileHash .\BugSwatter-0.6.1-win-x64.zip -Algorithm SHA256
Expand-Archive .\BugSwatter-0.6.1-win-x64.zip -DestinationPath C:\BugSwatter\releases
Move-Item C:\BugSwatter\releases\BugSwatter-0.6.1-win-x64 C:\BugSwatter\bin
C:\BugSwatter\bin\Informant.exe help
```

Adapt the version in these examples to the release you downloaded.

### Linux

Install Git and your distribution's .NET 10 and ASP.NET Core 10 runtime packages. Package names vary by distribution. On supported Ubuntu releases they are normally `dotnet-runtime-10.0` and `aspnetcore-runtime-10.0`.

After comparing the archive's SHA-256 value with `SHA256SUMS.txt`:

```bash
sudo mkdir -p /opt/bugswatter
sudo tar -xzf BugSwatter-0.6.1-linux-x64.tar.gz -C /opt/bugswatter --strip-components=1
sudo chmod 755 /opt/bugswatter/Informant /opt/bugswatter/Marshal
/opt/bugswatter/Informant help
```

## Informant quick start

Use a separate configuration directory for each repository:

```powershell
New-Item -ItemType Directory C:\BugSwatter\jobs\sample
Set-Location C:\BugSwatter\jobs\sample
C:\BugSwatter\bin\Informant.exe init
notepad informant.json
C:\BugSwatter\bin\Informant.exe validate
C:\BugSwatter\bin\Informant.exe verify
C:\BugSwatter\bin\Informant.exe
```

`Informant init` writes a commented `informant.json` and `review-prompt.txt`. At minimum, edit `repositoryUrl`, `branch`, `workingTreePath`, `gitExecutablePath`, `modelEndpoint`, and `modelName`.

The `workingTreePath` must be an absolute path dedicated to Informant. It is cloned when missing and destructively reset and cleaned on later runs. Never use your development checkout.

`validate` checks configuration, paths, endpoint reachability, and required secret references. `verify` performs the stronger tool-calling probe required by a real review. Run both as the same operating-system account that will run Informant unattended.

## Safety model

The architecture separates deterministic operations from model judgment. Informant owns Git operations, repository reads, report writes, and state. The primary model receives one read-only tool, `read_file_lines`. The model cannot write files, execute commands, run Git, or change the configured working tree.

Before Informant refreshes an existing working tree, it validates matching ownership records inside the tree and beside it. Validation covers the claim identifier, canonical path, repository, branch, `.git` directory, and `origin` remote. Missing, copied, malformed, legacy, or mismatched ownership records stop the run before `fetch`, `reset`, or `clean`.

Repository reads reject:

- Absolute paths and paths resolving outside the allowed root
- Symbolic links
- Directory symbolic links
- Windows junctions and other reparse points
- Mount points encountered as reparse points
- Binary and oversized files

The rejection applies to each path component, not only the final file. Informant does not follow a link even when its target remains inside the repository.

Tool calling is a hard gate. Informant creates a probe file containing an unpredictable token, asks the model to read it through the tool, and requires the correct token in the result. A model that ignores the tool or invents a result cannot begin a review.

These controls limit what the model can do, but they do not make model findings authoritative. Review reports manually before acting on them. See [SECURITY.md](SECURITY.md) for the broader threat model and known limitations.

## Informant commands

| Command | Effect |
| --- | --- |
| `Informant [--config <path>] [--progress json]` | Run a review, optionally adding machine-readable progress snapshots to standard output |
| `Informant init` | Write starter files in the current directory without overwriting existing files |
| `Informant validate [--config <path>]` | Validate configuration, endpoint reachability, paths, and secrets |
| `Informant verify [--config <path>]` | Prove that the primary model performs tool calling |
| `Informant help` | Show command-line help |

Without `--config`, Informant reads `informant.json` from the current directory. Quote paths containing spaces:

```powershell
Informant.exe --config "C:\BugSwatter Jobs\sample\informant.json"
```

Relative paths inside a configuration file resolve from that file's directory, not from the process working directory. `workingTreePath` is the exception and must be absolute.

Standalone Informant keeps its normal human-readable console and log behavior. Passing `--progress json` additionally writes complete, versioned `INFORMANT-PROGRESS:` JSON snapshots, one per line, for Marshal or a line-oriented script. Snapshots report the current phase, file position, selected model profile, whether a non-streaming model request is waiting for a response, the number of requests started, and cumulative token usage returned by the provider. Providers may omit usage, so token fields remain unavailable until a completed response reports them. BugSwatter does not estimate tokens in a response that is still being generated.

Exit code `0` means the command completed successfully. Exit code `1` means a fatal condition was written to standard error and the configured log.

## Informant configuration

JSON comments and trailing commas are supported.

| Field | Meaning | Default |
| --- | --- | --- |
| `repositoryUrl` | Git remote to clone and review | required |
| `branch` | Branch to review | required |
| `workingTreePath` | Absolute path of the dedicated tree Informant owns and refreshes | required |
| `gitExecutablePath` | Git executable path | required |
| `modelEndpoint` | OpenAI-compatible primary model base URL | required |
| `modelName` | Model identifier sent to the endpoint | required |
| `allowedReadRoot` | Root available to `read_file_lines` | working tree |
| `reviewMode` | `changed` or `full` | `changed` |
| `reportDirectory` | Report and change-list directory | `reports` |
| `reportRetentionDays` | Days to keep managed report artifacts; `-1` keeps them forever | `31` |
| `stateFilePath` | Completed-review baseline state | `informant.state.json` |
| `reviewPrompt` | Inline primary review prompt | null |
| `reviewPromptFile` | Prompt file used when inline text is absent | built-in prompt, or `review-prompt.txt` from `init` |
| `promptIncludeFiles` | Root-level Markdown globs or absolute guidance-file paths appended to the prompt | empty; starter config uses `AGENTS.md` |
| `maxContextCharacters` | Character budget per primary review conversation | `24000` |
| `maxFileLines` | File size in lines above which logical chunking begins | `800` |
| `maxFileBytes` | Maximum source-file bytes read | `10485760` |
| `maxModelResponseBytes` | Maximum model response body bytes | `4194304` |
| `perFileRetryCount` | Retries after a failed file-part review | `2` |
| `requestTimeoutSeconds` | Timeout for each primary model request | `1800` |
| `logLevel` | Serilog minimum level | `Information` |
| `logFilePath` | Daily rolling log path | `logs/informant-.log` |
| `consoleLogging` | Force console logging on or off; null auto-detects | null |
| `secondOpinion` | Optional validation model settings | null |
| `email` | Optional report email settings; requires a second opinion | null |

`reviewPrompt` takes precedence over `reviewPromptFile`; the built-in prompt is used when neither supplies content. Relative `promptIncludeFiles` patterns match only at the reviewed repository root. The included Markdown is repository-supplied model guidance, so review those files as part of your prompt policy.

The context budget is measured in characters, not tokens. Lower values reduce prompt size and cost but can reduce useful context. Higher values can slow local inference and increase cloud charges.

## Environment-variable overrides

Both applications layer prefixed environment variables over JSON configuration. A double underscore separates nested sections:

```text
INFORMANT_ModelName=review-model
INFORMANT_SecondOpinion__ModelName=validator-model
INFORMANT_ReportRetentionDays=45
MARSHAL_PerRunTimeoutMinutes=240
MARSHAL_WebServer__Port=5055
MARSHAL_Jobs__0__Poll__Schedule=0 */10 * * * *
```

Only variables beginning with `INFORMANT_` or `MARSHAL_` participate. Environment values override JSON values. Run `Informant validate` or `Marshal validate` in the final service environment to confirm the effective configuration.

## Secrets

The following sensitive Informant values accept only a runtime reference, never a literal:

- `secondOpinion.apiKey`
- `email.password`
- `email.acsConnectionString`

The Windows `--service-password` command-line option also accepts only a reference. Two forms are supported:

- `env:VARIABLE_NAME` reads an environment variable
- `file:PATH` reads a file and trims trailing whitespace

A relative `file:` path resolves from the relevant configuration directory. Restrict secret files to the service identity. On Windows, use an ACL appropriate to the account. On Linux, use ownership plus mode `600` where practical.

Marshal webhook secrets may be literal strings or `env:` and `file:` references. Literals are convenient for a small internal deployment but leave the secret in the configuration file. Protect that file accordingly and do not commit it.

Do not embed tokens in `repositoryUrl`. Use Git's credential manager, a read-only deploy key, or another credential mechanism owned by the account running Marshal and Informant.

## Reports, baselines, and retention

A review with work to do writes:

- `Informant-Report-<timestamp>.md`
- `Informant-Changes-<timestamp>.json`
- `Informant-Report-<timestamp>-validated.md` when the second opinion completes
- `Informant-Report-<timestamp>-validated.json` when the second opinion completes

The primary report records repository, branch, review mode, baseline and tip SHAs, model identity, timing, reviewed files, changed line ranges, findings, skipped files, and completion status. Sections are appended as files complete, so a process failure retains finished work and leaves an incomplete marker in the report.

Deleted files are reviewed from the immutable baseline Git object. The prompt asks the model to examine surviving references and consequences of removal. Renames and filenames containing spaces or leading or trailing spaces are parsed from Git's null-delimited output rather than whitespace splitting.

In `changed` mode, the first run reviews the full tracked tree. Informant advances the stored baseline only after every changed file is either fully reviewed or deliberately classified as not reviewable, such as a binary, empty, oversized, metadata-only, or symbolic-link file. A failed or partially reviewed file leaves the previous baseline unchanged so the next run retries the incomplete change set. A completed second opinion is additive and does not control primary baseline advancement.

When the tip already equals the baseline, Informant writes no report artifacts. If rewritten history makes the baseline unreachable, Informant performs a full review instead of remaining stuck.

At the beginning of each run, retention deletes top-level managed artifacts whose last-write time is older than `reportRetentionDays`. The default is 31 days. Set `-1` to keep reports forever. Retention recognizes only exact Informant report and change-list filename patterns, does not recurse into subdirectories, does not delete logs or state, and refuses symbolic-link or reparse-point artifacts and directories. Cleanup failures are logged but do not prevent the review.

## Second opinion

The optional second opinion sends the primary findings and relevant code excerpts to a second OpenAI-compatible model. It writes separate Markdown and JSON validation reports without changing the primary report.

```jsonc
"secondOpinion": {
  "endpoint": "https://api.example.com/v1",
  "modelName": "validator-model",
  "apiKey": "env:INFORMANT_SECOND_OPINION_KEY",
  "authentication": "bearer",
  "prompt": null,
  "promptFile": null,
  "requestTimeoutSeconds": 1800,
  "contextLines": 30,
  "maxFileReads": 5,
  "reviewSkippedFiles": true
}
```

| Field | Meaning | Default |
| --- | --- | --- |
| `endpoint` | Validator base URL | required |
| `modelName` | Validator model identifier | required |
| `apiKey` | `env:` or `file:` reference; omit for an unauthenticated local endpoint | null |
| `authentication` | `bearer` for an Authorization bearer credential, or `apiKey` for the Azure `api-key` header | `bearer` |
| `prompt` | Inline validation prompt | null |
| `promptFile` | Validation prompt file used when inline text is absent | built-in prompt |
| `requestTimeoutSeconds` | Timeout for each validation request | `1800` |
| `contextLines` | Source lines retained around changed ranges | `30` |
| `maxFileReads` | Additional repository reads allowed per file when the validator supports tools | `5` |
| `reviewSkippedFiles` | Ask the validator to review files the primary model could not complete | `true` |

This simple form preserves the original behavior: every run uses the one configured validator.

### Severity-routed model profiles

Advanced configuration can declare one to three OpenAI-compatible model profiles and map the primary run's candidate severity to one of them:

```jsonc
"secondOpinion": {
  "profiles": {
    "economy": {
      "endpoint": "http://model-server.internal:1234/v1",
      "modelName": "local-validator"
    },
    "balanced": {
      "endpoint": "https://provider.example/v1",
      "modelName": "balanced-model",
      "apiKey": "env:INFORMANT_BALANCED_KEY"
    },
    "premium": {
      "endpoint": "https://your-resource.openai.azure.com/openai/v1",
      "modelName": "premium-deployment",
      "apiKey": "env:INFORMANT_PREMIUM_KEY",
      "authentication": "apiKey"
    }
  },
  "routeBySeverity": {
    "none": "economy",
    "low": "economy",
    "medium": "balanced",
    "high": "premium",
    "critical": "premium",
    "undetermined": "premium"
  },
  "requestTimeoutSeconds": 1800,
  "contextLines": 30,
  "maxFileReads": 5,
  "reviewSkippedFiles": true
}
```

The simple `endpoint`, `modelName`, `apiKey`, and `authentication` fields cannot be mixed with `profiles`. Profile names are local labels. Each profile has its own endpoint, model name, optional secret reference, and authentication mode. The shared prompt, timeout, context, tool-read, and skipped-file settings remain at the `secondOpinion` level.

All six `routeBySeverity` entries are required: `none`, `low`, `medium`, `high`, `critical`, and `undetermined`. Each entry must name one configured profile. The primary model supplies structured candidate severity for each reviewed file. Informant takes the highest candidate severity across the complete run and selects exactly one validator for that run. It does not run several second opinions, rotate by day, or choose a different model for each file.

Informant appends the structured findings contract to the configured primary review prompt at runtime, including an existing editable `review-prompt.txt`. The contract does not replace custom review guidance. If a primary response has malformed or missing structured findings, or any attempted primary review fails or is partial, the run uses the fail-safe `undetermined` route. Selection details are written to the validated Markdown and JSON reports.

The selected validator endpoint is probed before code is sent. Tool calling is optional for the validator. When supported, it can request more lines through the same confined read-only tool, limited by `maxFileReads`. Set `maxFileReads` to `0` to disable validator tool access. Otherwise it works from the supplied excerpt.

A public-cloud validator receives findings, code excerpts, and any extra lines it requests. Disable the second opinion or use an internal model for repositories whose code must remain on your network. The validator provider may retain prompts or metadata according to its own terms.

Structured findings are parsed for severity. Unparseable model output is retained as prose and marked with undetermined severity rather than being silently treated as no findings.

## Email

Email runs only after a second opinion completes. Each attempt is appended to the validated Markdown report as sent, skipped, or failed. An email-provider failure does not fail the completed review or remove reports.

SMTP example:

```jsonc
"email": {
  "provider": "smtp",
  "smtpHost": "smtp.internal",
  "smtpPort": 587,
  "useStartTls": true,
  "from": "bugswatter@example.com",
  "to": ["developer@example.com"],
  "username": "bugswatter",
  "password": "file:secrets/smtp-password.txt",
  "sendOn": "high",
  "attachReports": true
}
```

The SMTP transport supports STARTTLS, normally on port 587, and unencrypted internal relays. The built-in SMTP client does not support implicit TLS on port 465.

Azure Communication Services Email example:

```jsonc
"email": {
  "provider": "azureCommunicationServices",
  "from": "DoNotReply@verified.example.com",
  "to": ["developer@example.com"],
  "acsConnectionString": "env:INFORMANT_ACS_CONNECTION",
  "sendOn": "high",
  "attachReports": true
}
```

The ACS sender address must belong to a domain configured in the ACS resource. A completed ACS operation confirms that ACS accepted the send operation, not that the recipient inbox delivered or displayed the message.

`sendOn` can be `always`, `medium`, or `high`. When severity is undetermined because structured model output was not parseable, Informant sends the email regardless of the threshold so a potentially important report is not suppressed.

## Marshal

Marshal is an optional long-running dispatcher. It reviews no code itself. Triggers enqueue jobs, and one serial worker launches Informant for each job.

| Command | Effect |
| --- | --- |
| `Marshal run --config <path> [--review-all]` | Run in the foreground or under a service manager |
| `Marshal validate --config <path>` | Validate Marshal and run `Informant validate` for every job |
| `Marshal install --config <path> [service options]` | Register an automatic Windows or systemd service |
| `Marshal remove [--use-sc]` | Stop and unregister the installed service |
| `Marshal help` | Show command-line help |

`--review-all` enqueues every configured job once at startup, then continues with normal triggers. Without it, Marshal starts quietly except that repository polling performs one check at startup.

All jobs share one bounded in-memory queue with a single consumer. Duplicate work for the same Informant configuration coalesces. A trigger received while that repository is running creates at most one rerun. The queue holds up to 128 entries and is not persisted across restarts.

Each Informant child has a configurable timeout. Timeout or Marshal shutdown kills the complete Informant process tree where the operating system permits it. Before starting a job, Marshal probes its model endpoint. Unreachable endpoints use per-repository exponential backoff from 30 seconds to 15 minutes.

## Marshal configuration

```jsonc
{
  "informantExecutable": "C:\\BugSwatter\\bin\\Informant.exe",
  "perRunTimeoutMinutes": 360,
  "fileWatchDebounceSeconds": 300,
  "logLevel": "Information",
  "logFilePath": "logs/marshal-.log",
  "historyFilePath": "history/marshal-history.jsonl",
  "consoleLogging": null,
  "webServer": {
    "enabled": true,
    "bindAddress": "localhost",
    "port": 5000
  },
  "webhook": {
    "enabled": false,
    "gitHubSecret": null,
    "azureDevOpsSecret": null
  },
  "jobs": [
    {
      "name": "sample-main",
      "informantConfigPath": "C:\\BugSwatter\\jobs\\sample\\informant.json",
      "schedule": ["03:00"],
      "watchPath": null,
      "poll": {
        "enabled": true,
        "schedule": "0 */5 * * * *"
      }
    }
  ]
}
```

| Field | Meaning | Default |
| --- | --- | --- |
| `informantExecutable` | Informant executable launched for reviews | required |
| `perRunTimeoutMinutes` | Hard timeout for each child review | `360` |
| `fileWatchDebounceSeconds` | Quiet period before a filesystem trigger fires | `300` |
| `logLevel` | Serilog minimum level | `Information` |
| `logFilePath` | Daily rolling log path | `logs/marshal-.log` |
| `historyFilePath` | Append-only completed-run JSON Lines history | `history/marshal-history.jsonl` |
| `consoleLogging` | Force console logging on or off; null auto-detects | null |
| `webServer` | Dashboard, API, and webhook listener settings | null, no listener |
| `webhook` | Global webhook enablement and secrets | null |
| `jobs` | Repository job configurations | empty |

Each job supports:

| Field | Meaning | Default |
| --- | --- | --- |
| `name` | Unique operator-facing name | required |
| `informantConfigPath` | Informant configuration for the repository | required |
| `schedule` | Daily local times in `HH:mm` form | null |
| `watchPath` | Existing directory watched recursively | null |
| `poll` | Outbound branch-tip polling settings | null |
| `webhook` | Provider and repository mapping | null |

Paths relative to `marshal.json` resolve from its directory. Quote a `--config` path containing spaces. JSON string values may contain spaces without special treatment beyond normal JSON escaping.

## Triggers

Triggers can be combined on one job. Queue coalescing prevents simultaneous duplicate runs.

### Daily schedules

`schedule` contains local wall-clock times. The example `"schedule": ["03:00", "15:30"]` runs daily at 3:00 a.m. and 3:30 p.m. in the machine's local time zone. Daylight-saving transitions therefore affect these triggers as ordinary local times.

### Repository polling

Polling is the simplest choice when Marshal cannot accept inbound internet connections. Marshal runs `git ls-remote` against the exact configured branch and compares its remote tip with Informant's last completed-review baseline. It does not fetch or modify the working tree during the poll. A difference enqueues Informant, which performs the normal protected refresh and review.

Polling checks once when Marshal starts and then follows the configured UTC schedule. It accepts:

- Six-field Azure Functions-style NCRONTAB: second, minute, hour, day, month, day of week
- Traditional five-field crontab: minute, hour, day, month, day of week
- An invariant .NET `TimeSpan` interval

No schedule may run more often than once per minute. In a six-field expression, the seconds field must be exactly `0`.

| Expression | Meaning |
| --- | --- |
| `0 */5 * * * *` | Every five minutes, default |
| `0 */10 * * * *` | Every ten minutes |
| `0 0 * * * *` | At the start of every UTC hour |
| `0 0 2 * * *` | Daily at 02:00 UTC |
| `0 0 3 * * 1` | Mondays at 03:00 UTC |
| `0 0 3 1 * *` | First day of each month at 03:00 UTC |
| `00:10:00` | Every ten minutes measured from startup |
| `7.00:00:00` | Every seven days measured from startup |

NCRONTAB day-of-week values use `0` for Sunday through `6` for Saturday. Prefer NCRONTAB for wall-clock UTC schedules and `TimeSpan` for elapsed intervals.

Polling uses the Git credentials of the Marshal service account. A public repository normally needs no secret. Private GitHub and Azure DevOps repositories require credentials that work non-interactively for that same account.

### Filesystem changes

`watchPath` recursively watches changed, created, deleted, and renamed events. A two-second internal check observes the configured quiet window, default five minutes, so a large checkout or build burst normally creates one review. Filesystem watchers can overflow at the operating-system level; Marshal logs watcher errors. Repository polling or schedules are more reliable for remote source-control changes.

### Webhooks

Enable the global webhook listener and add a mapping to each relevant job:

```jsonc
"webServer": { "enabled": true, "bindAddress": "0.0.0.0", "port": 5000 },
"webhook": {
  "enabled": true,
  "gitHubSecret": "file:secrets/github-webhook.txt",
  "azureDevOpsSecret": "file:secrets/azure-webhook.txt"
},
"jobs": [
  {
    "name": "github-sample",
    "informantConfigPath": "jobs/github/informant.json",
    "webhook": { "provider": "gitHub", "repository": "owner/repository" }
  },
  {
    "name": "ado-sample",
    "informantConfigPath": "jobs/ado/informant.json",
    "webhook": { "provider": "azureDevOps", "repository": "Repository Name" }
  }
]
```

GitHub uses `POST /webhook/github`, an `X-Hub-Signature-256` HMAC-SHA256 signature, and `X-GitHub-Delivery` for deduplication. Only `push` enqueues a review; authenticated `ping` succeeds without enqueueing.

Azure DevOps uses `POST /webhook/azuredevops` with HTTP Basic authentication. The configured `azureDevOpsSecret` is the password. Configure a **Code pushed** service hook with **All** resource details so the repository is present in the payload. The root event ID provides deduplication.

Webhook bodies are limited to 1 MiB. Accepted delivery IDs are retained in memory for 24 hours, up to 4,096 IDs. Duplicate deliveries return `202 Accepted`. A full review queue returns `503` and releases the delivery ID so the provider can retry. Restarting Marshal clears the deduplication cache.

## Dashboard and API

When `webServer.enabled` is true, Marshal serves HTTP only. HTTPS and certificate management are intentionally outside the first public release.

| Route | Method | Purpose |
| --- | --- | --- |
| `/` and `/dashboard` | GET | Dashboard |
| `/health` | GET | Liveness, uptime, running job, and queue depth |
| `/api/status` | GET | Process, queue, and current review activity |
| `/api/history` | GET | Up to 100 recent completed runs |
| `/api/jobs` | GET | Jobs, repository mappings, paths, and triggers |
| `/api/jobs/{name}/run` | POST | Enqueue a job |
| `/api/queue` | GET | Running and waiting jobs |
| `/api/queue/{name}` | DELETE | Remove a waiting job |
| `/webhook/github` | POST | GitHub push webhook when enabled |
| `/webhook/azuredevops` | POST | Azure DevOps push webhook when enabled |

The page title is **BugSwatter Dashboard**. While a review runs, it shows the dispatched job and trigger state, review start and elapsed time, phase, file position, selected model and profile, whether a model request is waiting for a response, that request's start and elapsed time, request count, and cumulative provider-reported tokens. Informant uses non-streaming requests, so the token count changes only after a response completes. A provider that does not return OpenAI-compatible usage fields is shown as `not reported`. Marshal ignores malformed, missing, and unsupported progress lines; they never fail the child review, and the basic starting state remains available when no valid progress has arrived.

There is no dashboard or API authentication, authorization, CSRF protection, TLS, or rate limiting. Any client that can reach the listener can inspect operational details, trigger model usage, or cancel waiting work. HTTP traffic can be read or modified by any party able to observe the network path. Azure DevOps Basic credentials are not confidential without a protected transport such as a trusted VPN.

The intended deployment is `localhost` or a trusted internal network with firewall or VPN controls. Do not expose the listener directly to the public internet. For remote access on a trusted LAN, bind an internal address or `0.0.0.0` and add a narrowly scoped host-firewall rule. Omit `webServer` entirely when the dashboard and webhooks are not needed. Outbound polling works without an open inbound port.

The history API includes report paths, the jobs API includes configured watch paths and repository identifiers, and live status includes current file names, model names, and usage counts. The dashboard does not serve report file contents, but the metadata can still be sensitive.

## Windows service deployment

Run the following from an elevated PowerShell prompt after configuration validation:

```powershell
C:\BugSwatter\bin\Marshal.exe validate --config "C:\BugSwatter\config\marshal.json"
C:\BugSwatter\bin\Marshal.exe install --config "C:\BugSwatter\config\marshal.json"
sc.exe start Marshal
```

Without `--service-user`, the installed service runs as LocalSystem. LocalSystem has extensive local privileges. A defect, malicious repository input, compromised dependency, or exposed dashboard would therefore have a larger impact. It also uses the machine account for network access and does not inherit your interactive Git credentials. Use LocalSystem only after accepting those risks and restricting the machine, configuration, and network listener.

For a normal custom account, supply its password through an environment or file reference:

```powershell
$env:MARSHAL_SERVICE_PASSWORD = Read-Host "Service password" -MaskInput
C:\BugSwatter\bin\Marshal.exe install `
  --config "C:\BugSwatter\config\marshal.json" `
  --service-user ".\BugSwatter" `
  --service-password "env:MARSHAL_SERVICE_PASSWORD"
Remove-Item Env:MARSHAL_SERVICE_PASSWORD
sc.exe start Marshal
```

A managed service account or built-in identity that needs no password can use `--service-user` without `--service-password`. The account must have **Log on as a service**, read and execute access to the binaries, read access to configuration and secret files, and modify access to working trees, state, reports, history, and logs.

Custom accounts use the native Service Control Manager API. `--use-sc` is an optional fallback for LocalSystem installation and removal only; it is rejected with `--service-user` so a password cannot appear in an `sc.exe` command line.

To remove the service from an elevated prompt:

```powershell
C:\BugSwatter\bin\Marshal.exe remove
```

Removal requests a stop before deleting the service. Marshal cancellation kills a running Informant child process tree before shutdown completes where supported.

## Linux systemd deployment

Create a dedicated account and data directories, then grant only the access the service needs. After `Marshal validate` succeeds as that account, install from a root shell:

```bash
sudo /opt/bugswatter/Marshal install --config "/etc/bugswatter/marshal.json" --service-user bugswatter
sudo systemctl start marshal
sudo systemctl status marshal
```

The installer writes `/etc/systemd/system/marshal.service`, runs `systemctl daemon-reload`, and enables the unit for startup. It quotes executable and configuration paths, including spaces. `--service-user` writes `User=`. Linux installation does not accept `--service-password`; configure account access and Git credentials through normal Linux mechanisms.

Omitting `--service-user` leaves systemd's root default. Root has the same broad-impact concerns as LocalSystem and is not the recommended default for an exposed or multi-purpose host.

Remove the unit with:

```bash
sudo /opt/bugswatter/Marshal remove
```

The generated unit restarts Marshal after failures with a ten-second delay. Use `journalctl -u marshal` along with the configured rolling log for diagnosis.

## Git credentials for polling and reviews

Marshal polling and Informant cloning run as the operating-system identity that launched them. Existing interactive credentials are useful only when Marshal runs as that same user. Windows services, LocalSystem, custom service accounts, root, and dedicated systemd users each have separate credential and SSH-key stores.

Before unattended deployment, run a noninteractive check as the final identity:

```text
git ls-remote --exit-code --heads <repository-url> refs/heads/<branch>
```

For private repositories, prefer a repository-scoped read-only deploy key or a fine-grained token with read-only contents access. Azure DevOps users can use a read-only repository token or SSH key. Store credentials with the platform's Git credential helper or protected SSH files. Do not put credentials in `informant.json`, `marshal.json`, command history, repository URLs, or this repository.

Polling every five or ten minutes is normally inexpensive because `ls-remote` reads branch references and launches Informant only when the remote tip differs from the completed-review baseline. Consider provider rate limits when configuring many repositories or very short intervals.

## Cost and retention controls

The first review of a repository is a full tracked-tree review. Start with a small repository or temporary branch to estimate model speed and cost.

Useful controls include:

- Keep `reviewMode` as `changed`; `full` reviews the complete tree every run
- Poll every five or ten minutes unless faster detection has operational value
- Reduce `maxContextCharacters` and `maxFileLines` carefully to limit prompt size
- Keep `maxFileBytes` and `maxModelResponseBytes` bounded
- Reduce second-opinion `contextLines` and `maxFileReads`
- Set `reviewSkippedFiles` to false if second-model coverage is less important than cost
- Use `sendOn` to reduce email volume
- Use `reportRetentionDays` to cap report storage, or `-1` only when indefinite retention is intentional
- Monitor local accelerator utilization and cloud-provider usage alerts independently of BugSwatter

Marshal's queue is bounded and serial, so a trigger burst does not create simultaneous model calls. A trigger arriving during a run may still schedule one rerun. Repository changes during an incomplete review remain behind the old baseline and will be reconsidered later.

## Build, test, and release

Install the .NET 10 SDK and PowerShell 7, then run:

```powershell
dotnet restore BugSwatter.slnx
dotnet build BugSwatter.slnx -c Release --no-restore
dotnet test BugSwatter.slnx -c Release --no-build --no-restore
./scripts/check-public-content.ps1
./scripts/check-dependencies.ps1
```

The public-content policy rejects private IPv4 addresses, user-profile and known local-source paths, private-key headers, high-signal token and access-key formats, and the legacy reviewer name in the current tracked tree. The dependency-policy script restores every project, inspects resolved direct and transitive NuGet packages, fails if `Newtonsoft.Json` appears, and lists resolved non-Microsoft packages. CI runs both policies and reports known NuGet vulnerabilities. `System.Text.Json` is not hard-pinned by policy; normal dependency review and vulnerability monitoring remain necessary.

Build local framework-dependent release archives with:

```powershell
./scripts/package-release.ps1 -Runtime win-x64
./scripts/package-release.ps1 -Runtime linux-x64
```

The Linux archive should be produced on Linux so executable permission bits are set correctly. The script reads the version from `Directory.Build.props`, refuses to overwrite an existing archive, and can validate an expected `v<version>` tag.

GitHub Actions runs build, test, dependency policy, vulnerability reporting, and package smoke tests on Windows and Linux for pushes and pull requests. Pushing a tag such as `v0.6.1` first runs the same CI, builds both archives, writes `SHA256SUMS.txt`, and creates a GitHub Release. The tag must exactly match the version in `Directory.Build.props`. Release packages remain framework-dependent and do not bundle .NET.

Opt-in integration tests are skipped in ordinary CI. Live model tests require `INFORMANT_IT=1`, `INFORMANT_IT_ENDPOINT`, and `INFORMANT_IT_MODEL`; optional second-opinion coverage also uses `INFORMANT_IT_SO_ENDPOINT` and `INFORMANT_IT_SO_MODEL`. The ACS email test uses `BUGSWATTER_EMAIL_IT=1`, `BUGSWATTER_EMAIL_IT_ACS_CONNECTION`, `BUGSWATTER_EMAIL_IT_FROM`, and `BUGSWATTER_EMAIL_IT_TO`. Never commit those values.

## Design notes

The model interaction uses native OpenAI-compatible tool calling because Informant owns both sides of a closed read-only loop. Review is one file at a time. Changed line ranges come from `git diff -U0`, while the model can request additional repository ranges through `read_file_lines`.

Files longer than `maxFileLines` are divided at logical brace-depth boundaries where possible. The reader streams bounded ranges and rejects bodies beyond configured byte limits. Per-file failures retry and then remain recorded as failed or partial without discarding completed report sections.

Shared infrastructure lives in focused libraries: `BugSwatter.Common` for configuration, logging, safe paths, secrets, and common contracts; `BugSwatter.Git` for Git execution and working-tree ownership; `BugSwatter.AI` for model protocol and tool loops; and `BugSwatter.Email` for SMTP and ACS transports. Informant and Marshal contain application-specific orchestration.

## Disclaimer

Use BugSwatter at your own risk. You are responsible for the repositories, credentials, model endpoints, network exposure, retention, and costs you configure. Model reviews can miss defects and produce false findings. Cloud endpoints can charge per token and receive source code. Validate behavior on a small scope before unattended use.

The software is provided "as is", without warranty of any kind, to the maximum extent permitted by law. The authors and contributors accept no liability for cost, loss, missed defects, incorrect findings, security incidents, or other damage arising from its use.
