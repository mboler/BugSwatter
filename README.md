# BugSwatter

*Nightly AI code review that hunts down the shady things hiding in your repos.*

There are shady things lurking in your code. Bugs, sharp edges, quiet little mistakes. There always are. Every codebase has them, and you never know about all of them. You never do.

BugSwatter is a small app I put together to help find them. One AI model reviews what changed, and an optional second, stronger model second-guesses the first, so a single model's bad call does not land in the validated report on its own. BugSwatter writes up what it found and can email you when something serious surfaces.

It's a quick, practical tool I run nightly against my own apps and repos. I'm putting it up publicly in case it's handy for someone else too. No grand promises, just an extra set of eyes while you sleep.

Contributions are welcome. Found a bug, smoothed a rough edge, or added something useful? Open a pull request.

## How it works

BugSwatter is self-hosted and ships as two applications:

- **Informant** refreshes a dedicated clone, finds files changed since the last completed review, sends them to an OpenAI-compatible model, and writes timestamped reports
- **Marshal** is an optional long-running dispatcher that starts Informant from schedules, repository polling, filesystem changes, webhooks, or the built-in dashboard

The primary review can stay on your network through LM Studio, llama.cpp, Ollama, or another OpenAI-compatible endpoint. A second opinion can use another local model or a cloud model to validate findings and assign severity.

## Quick start

Install Git and .NET 10, extract the appropriate release archive into `C:\BugSwatter\bin` or `/opt/bugswatter`, and create a directory for one review job:

The Windows executables are not code-signed, so Windows may identify them as coming from an unknown publisher or display a SmartScreen warning. Download releases only from this GitHub repository and verify the archive against `SHA256SUMS.txt` before running it.

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

## Why use it

- **Local-first:** keep primary review code on your network
- **Changed-file reviews:** after the first run, review only changes since the last successfully completed primary review
- **Second opinion:** optionally ask a different local or cloud model to confirm findings and assign severity
- **Read-only model tools:** models can request repository file ranges, but cannot write files, execute commands, or run Git
- **Multiple triggers:** use daily schedules, outbound repository polling, filesystem watching, GitHub webhooks, or Azure DevOps service hooks
- **Unattended operation:** run Marshal in the foreground, as a Windows service, or as a systemd service
- **No installer or bundled runtime:** GitHub Releases provide framework-dependent Windows and Linux archives for machines with .NET 10 installed

## Important safety boundaries

Informant owns and destructively refreshes the absolute `workingTreePath` in its configuration. Never point it at a checkout where you work. Ownership records are validated before destructive Git operations, and repository file reads reject symbolic links, junctions, mount points, other reparse points, absolute paths, and paths outside the configured root.

Marshal's optional dashboard is HTTP-only and has no authentication or authorization. Anyone who can reach it can see operational details, enqueue reviews, and remove waiting jobs. Bind it to `localhost` unless you deliberately place it on a trusted internal or VPN network. Never expose it directly to the public internet.

AI output can be incomplete or wrong. Treat reports as leads for human review, not as proof that code is safe. BugSwatter is pre-1.0, so test it against a noncritical repository before relying on unattended operation.

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
