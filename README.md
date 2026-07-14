# BugSwatter

*Nightly AI code review that hunts down the shady things hiding in your repos.*

There are shady things lurking in your code. Bugs, sharp edges, quiet little mistakes. There always are. Every codebase has them, and you never know about all of them. You never do.

BugSwatter is a small app I put together to help find them. One AI model reviews what changed, and an optional second, stronger model second-guesses the first, so a single model's bad call does not land in the validated report on its own. BugSwatter writes up what it found and can email you when something serious surfaces.

It's a quick, practical tool I run nightly against my own apps and repos. I'm putting it up publicly in case it's handy for someone else too. No grand promises, just an extra set of eyes while you sleep.

Contributions are welcome. Found a bug, smoothed a rough edge, or added something useful? Open a pull request.

## How it works

BugSwatter is self-hosted and ships as two applications:

- **Informant** refreshes a dedicated clone, rebuilds a safe repository manifest, plans related review clusters, sends bounded source to an OpenAI-compatible model, and writes timestamped reports with explicit coverage
- **Marshal** is an optional long-running dispatcher that starts Informant from schedules, repository polling, filesystem changes, webhooks, or the built-in dashboard

The primary review can stay on your network through LM Studio, llama.cpp, Ollama, or another OpenAI-compatible endpoint. Informant first supplies a bounded mix of root guidance, root files, configured seeds, changed source, and repository structure, then asks the model to group related files. Invalid planning falls back to deterministic path-based clusters instead of losing required coverage. Informant can also fail over to ordered, already-running models on other endpoints without loading or unloading models and still produces one primary report. A second opinion can use one other local or cloud model to validate findings and assign severity. Advanced configuration can select that one validator from as many as three profiles according to the highest candidate severity in the primary run.

BugSwatter is deliberately not a general-purpose agentic coding harness. The models do not get a shell, MCP servers or adapters, Git access, general filesystem access, or tools that can edit code. Their only model-directed action is asking Informant for a bounded range of numbered lines through its application-owned `read_file_lines` tool. Informant validates every request and either performs the read itself or refuses it. The model can ask for context and return review text; it cannot execute actions on your machine.

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

On Linux, use `/opt/bugswatter/Informant` and Linux paths instead. `Informant init` creates a commented starter configuration and the default review prompt. Set the repository URL, branch, a dedicated absolute working-tree path, Git executable path, model endpoint, and model name before running `verify`. Optional fallbacks must already be loaded and answering at their configured endpoints; BugSwatter does not manage model processes or GPU placement.

The first run uses the full tracked tree as its candidate universe. The default `exhaustive` strategy deeply reviews every reviewable candidate; `adaptive` may defer full-file review and records that limitation explicitly. Later `changed` runs compare the current tip with the last completed-review baseline. Reports are retained for 31 days by default; set `reportRetentionDays` to `-1` to keep them indefinitely.

## Why use it

- **Local-first:** keep primary review code on your network
- **Changed-file reviews:** after the first run, review only changes since the last successfully completed primary review
- **Repository-aware clusters:** group related files and unchanged supporting context without assuming a language or framework
- **Honest coverage:** choose exhaustive review or adaptive review with explicit deep-reviewed, changed-content, deferred, excluded, failed, and partial outcomes
- **Second opinion:** use one validator for every run, or route each complete run to one of as many as three local or cloud model profiles according to its highest primary candidate severity
- **No agentic harness:** models can request bounded, read-only line ranges through Informant's single application-owned tool, but cannot write files, execute commands, or invoke Git; no MCP server or adapter is involved
- **Multiple triggers:** use daily schedules, outbound repository polling, filesystem watching, GitHub webhooks, or Azure DevOps service hooks
- **Unattended operation:** run Marshal in the foreground, as a Windows service, or as a systemd service
- **Live review status:** the dashboard shows the current phase, file, model request state, elapsed time, and provider-reported token usage
- **No installer or bundled runtime:** GitHub Releases provide framework-dependent Windows and Linux archives for machines with .NET 10 installed

## Important safety boundaries

The model does not control Git. Informant itself uses application-controlled Git operations to clone the configured repository, detect changes, read baseline versions of deleted files, and refresh its dedicated working tree. Model output is never converted into a command line, and the model cannot choose Git commands or arguments.

Working-tree refresh is intentionally destructive: Informant uses `fetch`, `reset --hard`, and `clean -fdx` to make its dedicated clone match the configured branch. Before doing that, it validates matching ownership records inside and outside the working tree, the canonical path, repository URL, branch, origin remote, `.git` directory, and reparse-point boundaries. Never point `workingTreePath` at a checkout where you work.

Informant supplies controller-selected source from the current manifest and exposes one model-directed tool, `read_file_lines`. The tool can return at most 400 numbered lines per call from a bounded text file inside the repository root. It rejects paths absent from the current manifest, absolute paths, paths outside the root, symbolic links, junctions, mount points, other reparse points, binary files, oversized files, and content that changed after manifest creation. This uses ordinary OpenAI-compatible model tool calling through Informant, not MCP, and gives the model no direct filesystem access.

Marshal's optional dashboard is HTTP-only and has no authentication or authorization. Anyone who can reach it can see operational details, including current file and model names, enqueue reviews, and remove waiting jobs. Bind it to `localhost` unless you deliberately place it on a trusted internal or VPN network. Never expose it directly to the public internet.

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
