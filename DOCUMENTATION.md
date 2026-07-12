# BugSwatter documentation

BugSwatter ships as two executables: the reviewer (`Informant`) and the dispatcher (`Marshal`). This document covers both in full. For a short overview see the [README](README.md).

## Disclaimer

Use BugSwatter at your own risk. It drives AI models that you choose and configure, and running AI models can cost money — cloud endpoints in particular typically bill per token, and options that let a model read more of your code (such as the second opinion's per-file read budget) increase that usage. You alone are responsible for understanding the configuration and how the tool interacts with the models you point it at, for any costs a run incurs, and for verifying on a small scope that it behaves as you expect before you run it unattended. To the maximum extent permitted by law the software is provided "as is", without warranty of any kind, and the authors and contributors accept no liability for any cost, loss, or damage of any kind arising from its use, including but not limited to runaway token consumption or unexpected charges. Confirm proper behaviour yourself before trusting it unattended.

## Contents

- [Safety model](#safety-model)
- [Requirements](#requirements)
- [Informant commands](#informant-commands)
- [Configuration](#configuration)
- [Environment-variable overrides](#environment-variable-overrides)
- [Secrets and unattended deployment](#secrets-and-unattended-deployment)
- [Reports](#reports)
- [Second Opinion](#second-opinion)
- [Email](#email)
- [Building and publishing](#building-and-publishing)
- [Marshal, the review dispatcher](#marshal-the-review-dispatcher)
- [Design notes](#design-notes)

## Safety model

The architecture separates hands from judgment. Informant, the deterministic harness, owns every capability with side effects: git operations, file reading, tool execution, report writing. The model is a judgment-only component whose sole capability is one read-only tool, `read_file_lines`, invoked through native OpenAI-style tool-calling. The model can never write, delete, execute, or run git, so a hallucinating model running unattended at 2 a.m. has no hands to cause damage with.

Three guardrails protect the rest of the machine:

- The working tree is always the absolute path declared in config. The current directory only selects which config file to load; nothing destructive ever targets it.
- On first use Informant clones the branch and claims the directory with a `.informant` marker file. Every later run refuses to touch a non-empty directory without that marker, so pointing the config at a real checkout by accident is caught and stopped before any destructive git command runs.
- The `read_file_lines` tool rejects any path that resolves outside the configured read root, returns structured errors the model can recover from, and never throws into the run.

Tool-calling is a hard requirement. Before any review, Informant verifies that the configured model actually performs tool-calling through the configured endpoint by making it read a probe file and echo a token that exists nowhere else. If that round trip fails, the run aborts with a clear message. There is no silent text-only fallback.

## Requirements

.NET 10 runtime, a git executable, and an OpenAI-compatible endpoint hosting a tool-calling-capable model (LM Studio, a LiteLLM gateway, llama-server, Ollama, and similar all work). Windows x64 is the primary target and Linux x64 is supported; the git path is a config value so both work unchanged.

## Informant commands

| Command | Effect |
| --- | --- |
| `Informant [--config <path>]` | Run a review |
| `Informant init` | Write a starter `informant.json` and `review-prompt.txt` into the current directory |
| `Informant verify [--config <path>]` | Run only the tool-calling verification gate, exit 0 on pass |
| `Informant validate [--config <path>]` | Check config, endpoint reachability and secrets without running a review, exit 0 on pass |
| `Informant help` | Show usage |

`--config` names the config file explicitly, which removes any dependency on the current working directory; a scheduler or Marshal can invoke `Informant --config D:\jobs\my-repo\informant.json` from anywhere. Relative paths inside the config (reports, state, logs, prompt file) resolve against the config file's directory. Without `--config`, `informant.json` is read from the current directory.

Exit code 0 means success; 1 means a fatal condition that is described on stderr and in the log.

## Configuration

`informant.json` is read from the current working directory, or from the file named by `--config`. Comments and trailing commas are allowed. Relative paths resolve against the directory the config was loaded from; the working tree path must be absolute.

| Field | Meaning | Default |
| --- | --- | --- |
| `repositoryUrl` | Git remote to clone and review | required |
| `branch` | Branch to review | required |
| `workingTreePath` | Absolute path of the tree Informant owns and destructively refreshes | required |
| `gitExecutablePath` | Full path of the git executable | required |
| `modelEndpoint` | OpenAI-compatible base URL, for example `http://localhost:1234/v1` | required |
| `modelName` | Model identifier passed to the endpoint | required |
| `allowedReadRoot` | Directory the read tool is confined to | working tree |
| `reviewMode` | `changed` or `full` | `changed` |
| `reportDirectory` | Where reports and change lists are written | `reports` |
| `stateFilePath` | JSON file holding baseline SHAs keyed by repo and branch | `informant.state.json` |
| `reviewPrompt` | Inline prompt text, wins over the file | null |
| `reviewPromptFile` | Path to the prompt file | `review-prompt.txt` via init |
| `promptIncludeFiles` | Glob patterns for Markdown guidance files appended to the prompt | none (the starter config opts in with `["AGENTS.md"]`) |
| `secondOpinion` | Optional validation pass by a stronger model, see [Second Opinion](#second-opinion) | null |
| `email` | Optional report email, see [Email](#email); requires `secondOpinion` | null |
| `maxContextCharacters` | Character budget per review call, kept well below the model window | `24000` |
| `maxFileLines` | Line count above which a file is chunked | `800` |
| `maxFileBytes` | Maximum source-file size read; larger files are reported as oversized | `10485760` (10 MiB) |
| `maxModelResponseBytes` | Maximum model HTTP response body before rejection | `4194304` (4 MiB) |
| `perFileRetryCount` | Retries per file part before skipping | `2` |
| `requestTimeoutSeconds` | Timeout per model request | `1800` |
| `logLevel` | Serilog minimum level | `Information` |
| `logFilePath` | Rolling log file path | `logs/informant-.log` |
| `consoleLogging` | `true` forces console logging on, `false` off, `null` auto-detects | `null` |

The context budget is characters, not tokens; roughly four characters approximate one token for typical source. It is deliberately conservative because local models reason better over a modest amount of relevant context than over a packed window.

The review prompt is assembled from three layers: the base prompt (inline `reviewPrompt` wins, else `reviewPromptFile`, else the built-in default), plus every Markdown file matched by `promptIncludeFiles`. Relative patterns match at the working-tree root, so a repository that carries its own `AGENTS.md` (or `AGENT*.md`, or whatever patterns you configure) has that guidance appended to its review; absolute paths name exact files, and an empty list disables the mechanism. Everything is editable without recompiling.

## Environment-variable overrides

Both apps load their JSON config through the standard .NET configuration system, so an environment variable can override any config key. Variables are prefixed and use a double underscore as the section separator:

- `INFORMANT_` for the reviewer, `MARSHAL_` for the dispatcher.
- `INFORMANT_ModelName=gpt-x` overrides the top-level `modelName`.
- `INFORMANT_SecondOpinion__ModelName=gpt-x` overrides the nested `secondOpinion.modelName`.

Only prefixed variables participate, so unrelated machine variables never bleed into the config. This is the usual layered pattern: a JSON file for the baseline, environment variables for per-environment overrides. For secrets specifically, prefer the `file:` references below rather than overriding a value with a machine-wide variable.

## Secrets and unattended deployment

Every secret in a config (SMTP password, ACS connection string, second-opinion API key, and Marshal's webhook secrets) is a reference, never a literal, in one of two forms:

- `env:VARIABLE_NAME` reads the value from an environment variable.
- `file:PATH` reads it from a file, trimmed of trailing whitespace; a relative path resolves against the config's directory.

For a Windows service, a systemd unit, or a cron job, prefer `file:`. A service runs under its own account and sees the machine-wide environment, not your user environment, so an `env:` secret set as a user variable is invisible to it, while a machine-wide variable is readable by every process on the box. A `file:` secret sidesteps both: put each secret in its own file and restrict it to the account that runs the tool (`icacls` on Windows, `chmod 600` on Linux), the same pattern as an SSH key or `.pgpass`.

Three more things bite when the tool starts automatically rather than from your logged-in session:

- **File access and the service account.** The account needs read access to the executables, configs, git, and prompt files, and read-write access to the working tree (refreshed destructively every run), reports, logs, history, and state. Keep all of it under a path that account owns (for example `C:\BugSwatter`), not under a user profile, and grant a dedicated least-privilege account Modify on that tree rather than running as an all-powerful account.
- **Git credentials for private repositories.** An unattended account has its own credential store; a token you authenticated interactively is not visible to it. Give the service account its own read-only deploy key or a fine-grained read-only token, or point it at a public repository or a local mirror. Cloning fails fast rather than hanging, because interactive git prompts are disabled.
- **Working directory.** Both executables anchor their relative config paths to the config's own directory on load, so a service whose working directory is a system folder still writes logs, history, and reports beside its config.

## Reports

One report per run, named `Informant-Report-<timestamp>.md`. A deterministic metadata header records repository, branch, mode, baseline and tip SHAs, the review model and its endpoint, the context budget, the run's start, completion and duration, and the reviewed and skipped counts. Each reviewed file gets its own clearly delimited section with its changed line ranges and the model's findings verbatim; skipped files are listed with reasons. Sections are appended as each file completes, so a crashed run keeps everything finished up to that point, and pending markers left in the header mean the run did not complete. The model's text is only ever findings content: structure, metadata, and any cross-file summary are written by the harness, never generated by the model.

A `Informant-Changes-<timestamp>.json` file accompanies each report, recording the exact changed-file set and line ranges the run reviewed.

A run that finds nothing to review writes no artifacts at all: the baseline is recorded, one completion line is logged, and no report or change-set file is created.

## Second Opinion

When the `secondOpinion` config block is present, each run gets a second stage after the local review completes: every reviewed file's findings are sent, together with the referenced code read fresh from the working tree, to a stronger model (a cloud frontier model or a stronger local one) that validates each claim against the actual code. It confirms the real findings with calibrated severity, discards false positives, and produces a verdict per file. The result is written as a separate `Informant-Report-<timestamp>-validated.md` next to the original; the local report is never modified.

```jsonc
"secondOpinion": {
  "endpoint": "https://api.openai.com/v1",
  "modelName": "gpt-5",
  "apiKey": "env:INFORMANT_SECOND_OPINION_KEY",
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
| `endpoint` | OpenAI-compatible endpoint, cloud or local | required |
| `modelName` | Validating model | required |
| `apiKey` | `env:` or `file:` reference; omit for local endpoints that need no auth | null |
| `prompt` / `promptFile` | Inline text wins, else the file, else the built-in default | null |
| `requestTimeoutSeconds` | Timeout per validation request | `1800` |
| `contextLines` | Lines of surrounding code kept around each changed range in the excerpt | `30` |
| `maxFileReads` | Cap on `read_file_lines` calls the validator may make per file when its endpoint supports tool-calling | `5` |
| `reviewSkippedFiles` | Also send the validator files the local reviewer could not review, for a fresh look | `true` |

The API key is never stored in the config file: `apiKey` must be an `env:` or `file:` reference when present, read at runtime; literal keys are rejected at load. If a referenced source is unset when a run reaches the second pass, the pass is skipped with a logged error and the local review stands; nothing is sent anywhere without it. Before any code leaves the machine, the pass verifies the endpoint, model and key with a minimal round trip.

The local reviewer must support tool-calling; the validating model need not. At the start of the pass Informant probes the validating endpoint: when it supports tool-calling, the validator is offered the same read-only `read_file_lines` tool, confined to the working tree and capped at `maxFileReads` calls per file, so it can pull more of a file to check something the local reviewer may have missed, without reading the whole tree; when it does not, the pass runs from the excerpt alone. With `reviewSkippedFiles` on (the default), a changed file the local pass could not review is sent to the validator for a fresh review rather than left silently unreviewed; set it false to validate only files that produced findings and save cost.

**Privacy trade-off, decide per repository:** a cloud Second Opinion sends the local model's findings and the referenced code excerpts to an external API, plus any additional lines the validator reads on its own initiative when the endpoint supports tool-calling. For non-sensitive repositories that is a fair exchange for better validation. For sensitive code, do not enable a public-cloud second opinion. A validating model on your own network keeps everything inside the firewall and needs no API key. The feature is opt-in per config so this stays a conscious choice.

Alongside the Markdown validated report, the pass writes a machine-readable `Informant-Report-<timestamp>-validated.json` (confirmed and discarded findings, per-file severity, and a run-level maximum), which drives the email severity gate and the Marshal dashboard.

## Email

When an `email` block is present, Informant emails the reports at the end of a run. Email is gated on a completed Second Opinion, so it never fires for a raw local review, and it is gated on severity so it only interrupts you when it matters. Every decision is recorded as an Email delivery section appended to the validated report, noting whether the mail was sent, skipped or failed, when, the provider, and the recipients.

Two transports are supported via `provider`: plain SMTP (the default, using the framework's built-in client, so the tool carries no third-party email or crypto dependency; works with any STARTTLS or unencrypted relay including Microsoft 365 SMTP AUTH) and Azure Communication Services Email. Implicit TLS on port 465 is not supported by the built-in SMTP client; use 587 with STARTTLS.

```jsonc
"email": {
  "provider": "smtp",
  "smtpHost": "smtp.internal",
  "smtpPort": 587,
  "useStartTls": true,
  "from": "bugswatter@you.com",
  "to": ["dev@you.com", "lead@you.com"],
  "username": "bugswatter",
  "password": "env:INFORMANT_SMTP_PASSWORD",
  "sendOn": "high",
  "attachReports": true
}
```

For the ACS provider set `"provider": "azureCommunicationServices"`, a `from` on a domain verified in the ACS resource, and `"acsConnectionString": "env:..."` (an `env:` or `file:` reference). `sendOn` reads the second-opinion severities: `high` sends only on a confirmed high or critical, `medium` on any confirmed medium or worse, `always` whenever the pass ran. When the model returns no parseable structured findings, the email is sent anyway with a "severity undetermined" note rather than risk dropping a real finding. A missing secret skips the email and leaves the reports on disk. Email requires `secondOpinion`, enforced at config load.

## Building and publishing

```text
dotnet build                       # develop
dotnet test BugSwatter.slnx         # full suite (xUnit v3 on Microsoft Testing Platform)
dotnet publish src/Informant -c Release -r win-x64
dotnet publish src/Informant -c Release -r linux-x64
dotnet publish src/Marshal   -c Release -r win-x64
dotnet publish src/Marshal   -c Release -r linux-x64
```

Publishing produces one framework-dependent single-file executable per runtime identifier (the .NET 10 runtime must be installed on the target). The shared `BugSwatter.Common` library is merged into each executable. To produce a fully self-contained executable that bundles the runtime, add `-p:SelfContained=true`. Because Marshal hosts Kestrel, its target box needs the **ASP.NET Core 10 runtime** (`dotnet --list-runtimes` should show `Microsoft.AspNetCore.App 10.*`); Informant needs only the base .NET runtime.

## Marshal, the review dispatcher

Marshal is a long-running supervisor that watches one or more repositories and, on a trigger, runs Informant against the relevant one. Marshal reviews nothing itself. One executable runs three ways: as a console app in the foreground, as a Windows service, or as a Linux systemd service; the hosting integrations detect their context automatically.

```text
Marshal run --config D:\marshal\marshal.json [--review-all]
Marshal validate --config D:\marshal\marshal.json              (pre-flight check, exit 0 on pass)
Marshal install --config D:\marshal\marshal.json [--use-sc]    (register as service or systemd unit; needs admin)
Marshal remove [--use-sc]                                        (unregister)
```

On Windows, `install` and `remove` talk to the Service Control Manager directly through the API by default; pass `--use-sc` to shell out to sc.exe instead. On Linux they write or remove the systemd unit and need root. `validate` loads the config, checks the Informant executable and each job's watch path, resolves webhook secrets, and runs `Informant validate` for every job. `--review-all` enqueues every configured repository once at startup and then keeps watching; without it Marshal starts quiet and acts only on triggers.

```jsonc
{
  "informantExecutable": "D:\\deploy\\Informant.exe",
  "perRunTimeoutMinutes": 360,
  "fileWatchDebounceSeconds": 300,
  "logLevel": "Information",
  "logFilePath": "logs/marshal-.log",
  "consoleLogging": null,
  "historyFilePath": "history/marshal-history.jsonl",
  "webServer": { "enabled": true, "bindAddress": "localhost", "port": 5000 },
  "webhook": {
    "enabled": true,
    "gitHubSecret": "env:MARSHAL_GITHUB_SECRET",
    "azureDevOpsSecret": "env:MARSHAL_ADO_SECRET"
  },
  "jobs": [
    {
      "name": "bugswatter-main",
      "informantConfigPath": "D:\\jobs\\bugswatter\\informant.json",
      "schedule": ["03:00"],
      "watchPath": "D:\\source\\example\\repository",
      "webhook": { "provider": "gitHub", "repository": "mboler/BugSwatter" }
    }
  ]
}
```

### Dispatch model

All reviews share the single local model endpoint, so Marshal runs at most one Informant review at a time, globally: triggers feed a bounded queue (128 entries; overflow dropped with a warning) consumed by a single serial executor. Duplicates coalesce by repository identity (the job's Informant config path, compared case-insensitively on Windows). If a trigger arrives for the repository currently under review, the running review is never killed: a single pending-rerun flag is set, and when the run finishes that repository is enqueued once more. Each child runs as `Informant --config <job config>` under supervision; a child exceeding the per-run timeout (default 6 hours) has its whole process tree killed. Marshal reads the report path Informant prints on stdout, so it records the exact artifact rather than guessing by timestamp. The queue is in-memory and transient: a restart starts quiet, or re-kick everything with `--review-all`.

Before launching a job Marshal health-checks that job's model endpoint. If it is unreachable the run is deferred and the job re-queued after a per-repository exponential backoff (30 seconds doubling to a 15 minute cap, reset on the first success), so a down endpoint becomes a slow poll rather than a doomed launch.

### Web server, dashboard and API

With a `webServer` block Marshal runs a Kestrel listener serving a self-contained dashboard and a small JSON API. The dashboard shows current status, the configured jobs with a **Run now** button each (and a **Cancel** button while a job is queued), and the recent run history. The dashboard exposes repository names and findings, and the API has no authentication, so bind the listener to an internal or VPN-reachable interface only. Omit the `webServer` block entirely to run with no web server and no open port.

| Route | Method | Purpose |
| --- | --- | --- |
| `/` and `/dashboard` | GET | The self-contained dashboard page |
| `/health` | GET | Liveness: status, uptime, running job, queue depth |
| `/api/status` | GET | Status JSON |
| `/api/history` | GET | Recent completed runs |
| `/api/jobs` | GET | Configured jobs and their triggers |
| `/api/jobs/{name}/run` | POST | Manually enqueue a configured job |
| `/api/queue` | GET | What is running and what is waiting |
| `/api/queue/{name}` | DELETE | Cancel a waiting review (never the one running) |
| `/webhook/github` | POST | GitHub webhook (HMAC-SHA256), when webhooks are enabled |
| `/webhook/azuredevops` | POST | Azure DevOps webhook (basic auth), when webhooks are enabled |

Each completed run is appended to `historyFilePath` (a JSON-lines file: job, trigger, timing, exit code, outcome, report path, and the max confirmed severity), which the dashboard reads, so history survives restarts even though the queue does not.

### Triggers

Time triggers fire daily at each configured local time. Filesystem triggers watch a directory (subdirectories included) and enqueue after changes settle for the debounce window (default 5 minutes), so a checkout touching thousands of files coalesces into one review. Webhook triggers listen on `/webhook/github` and `/webhook/azuredevops`; GitHub posts are validated against the shared secret via the `X-Hub-Signature-256` HMAC-SHA256 header, Azure DevOps service hooks via basic-auth credentials whose password is the shared secret. Validation is mandatory and uses fixed-time comparison; failures are rejected with 401. Request bodies are capped at 1 MiB, enforced during the read so chunked requests cannot bypass it.

### Webhook deployment topology

Do not expose Marshal's endpoint to the public internet. The recommended production path posts to a small public-facing endpoint (a relay or an existing route) that forwards the request down a VPN tunnel to Marshal on the internal network. Bind Marshal's listener to an internal or VPN-reachable interface only. Signature validation is required on every path regardless. On Windows, binding a non-localhost address as a non-admin console process requires a one-time URL reservation (`netsh http add urlacl url=http://+:5000/ user=<account>`); running as a service avoids this.

## Design notes

The model interaction uses the endpoint's native tool-calling, not MCP, because Informant owns both sides of a closed
loop and needs no interoperability layer. Review is one file at a time; cross-file context is pulled by the model on
demand through `read_file_lines`, which caps each response and stops serving content once the conversation exceeds the
context budget. Repository reads reject traversal, absolute paths, symbolic links, junctions, mount points, and other
reparse points. The tool streams requested ranges rather than retaining the entire file. Files larger than
`maxFileBytes` are reported as oversized, and model bodies larger than `maxModelResponseBytes` are rejected before JSON
parsing. Files longer than `maxFileLines` are chunked at logical boundaries via a brace-depth heuristic, never
mid-method when a boundary exists. Line ranges come from `git diff -U0` hunk headers, so focus hints bracket exactly the
changed lines. If a recorded baseline SHA no longer exists because history was rewritten, the run degrades to a full
review instead of failing forever. Per-file failures retry per config and then skip with the reason recorded; one bad
file never kills the night's run.

The two executables share a small `BugSwatter.Common` library (logging setup, secret resolution, the config loader, the reviewer/dispatcher stdout contract), so a fix to shared plumbing lands in both.
