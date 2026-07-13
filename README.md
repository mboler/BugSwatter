# BugSwatter

*Unattended AI code review for the changes landing in your repositories*

BugSwatter is a small, self-hosted code-review utility. It refreshes a dedicated clone, finds the files changed since the last completed review, asks an OpenAI-compatible model to review them, and writes timestamped Markdown reports. An optional second model can validate the first model's findings, and optional email delivery can notify you when the validated severity crosses a configured threshold.

BugSwatter ships as two applications:

- **Informant** performs one deterministic review run
- **Marshal** is an optional long-running dispatcher that starts Informant from schedules, repository polling, filesystem changes, webhooks, or the built-in dashboard

The project is pre-1.0. Test it against a noncritical repository and review its reports before relying on unattended operation.

## Why use it

- **Local-first:** point the primary review at LM Studio, llama.cpp, Ollama, or another OpenAI-compatible endpoint on your network
- **Changed-file reviews:** after the first run, review only changes since the last successfully completed primary review
- **Second opinion:** optionally ask a different local or cloud model to confirm findings and assign severity
- **Read-only model tools:** models can request repository file ranges, but cannot write files, execute commands, or run Git
- **Multiple triggers:** use daily local-time schedules, outbound repository polling with NCRONTAB expressions, filesystem watching, GitHub webhooks, or Azure DevOps service hooks
- **Unattended operation:** run Marshal in the foreground, as a Windows service, or as a systemd service
- **No installer or bundled runtime:** GitHub Releases provide framework-dependent Windows and Linux archives for machines with .NET 10 installed

## Important safety boundaries

Informant owns and destructively refreshes the absolute `workingTreePath` in its configuration. Never point it at a checkout where you work. Ownership records are validated before destructive Git operations, and repository file reads reject symbolic links, junctions, mount points, other reparse points, absolute paths, and paths outside the configured root.

Marshal's optional dashboard is HTTP-only and has no authentication or authorization. Anyone who can reach it can see operational details, enqueue reviews, and remove waiting jobs. Bind it to `localhost` unless you deliberately place it on a trusted internal or VPN network. Never expose it directly to the public internet.

AI output can be incomplete or wrong. Treat reports as leads for human review, not as proof that code is safe.

## Quick start

Install Git and .NET 10, extract the appropriate release archive into `C:\BugSwatter\bin` or `/opt/bugswatter`, and create a directory for one review job:

```text
mkdir C:\BugSwatter\jobs\sample
cd C:\BugSwatter\jobs\sample
C:\BugSwatter\bin\Informant.exe init
notepad informant.json
C:\BugSwatter\bin\Informant.exe verify
C:\BugSwatter\bin\Informant.exe
```

On Linux, use `/opt/bugswatter/Informant` and Linux paths instead. `Informant init` creates a commented starter configuration and the default review prompt. Set the repository URL, branch, a dedicated absolute working-tree path, Git executable path, model endpoint, and model name before running `verify`.

The first successful run reviews all tracked files. Later `changed` runs compare the current tip with the last completed-review baseline. Reports are retained for 31 days by default; set `reportRetentionDays` to `-1` to keep them indefinitely.

## Documentation

See [DOCUMENTATION.md](DOCUMENTATION.md) for installation, complete configuration references, polling expressions, service deployment, webhooks, email, retention, cost controls, building, and GitHub Releases.

Security limitations and private vulnerability reporting are described in [SECURITY.md](SECURITY.md). Development and pull-request guidance is in [CONTRIBUTING.md](CONTRIBUTING.md).

## Build from source

Install the .NET 10 SDK, then run:

```text
dotnet restore BugSwatter.slnx
dotnet build BugSwatter.slnx -c Release --no-restore
dotnet test BugSwatter.slnx -c Release --no-build --no-restore
```

The release packaging script builds both applications for one supported runtime:

```powershell
./scripts/package-release.ps1 -Runtime win-x64
./scripts/package-release.ps1 -Runtime linux-x64
```

## Disclaimer

Use BugSwatter at your own risk. You choose the AI endpoints, models, repositories, schedules, credentials, and network exposure. Local and cloud models can consume substantial compute or billable tokens, and a cloud second opinion sends selected source code outside your network. Confirm behavior and cost on a small scope before unattended use.

The software is provided "as is", without warranty of any kind, to the maximum extent permitted by law. The authors and contributors accept no liability for costs, losses, missed defects, incorrect findings, security incidents, or other damage arising from its use.

## License

BugSwatter is licensed under the [Apache License 2.0](LICENSE). Required notices are in [NOTICE](NOTICE).
