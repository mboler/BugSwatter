# BugSwatter

*Nightly AI code review that hunts down the shady things hiding in your repos.*

There are shady things lurking in your code. Bugs, sharp edges, quiet little mistakes. There always are. Every codebase has them, and you never know about all of them. You never do.

BugSwatter is a small app I put together to help find them. It runs on a schedule and puts two different AI models on the case: one reviews the code, and a second, stronger one second-guesses the first, so a single model's bad call doesn't land in the report on its own. It looks at what changed, writes up what it found, and can email you when something serious surfaces.

It's a quick, practical tool I run nightly against my own apps and repos. I'm putting it up publicly in case it's handy for someone else too. No grand promises, just an extra set of eyes while you sleep.

Contributions are welcome. Found a bug, smoothed a rough edge, added something useful? Open a pull request.

## How it works

BugSwatter is an unattended, scheduled code-review harness. On a timer or on demand, it refreshes a dedicated clone of a configured branch, computes exactly what changed since its last run, and feeds each changed file to a locally hosted AI model that reviews it and reports findings. Findings land in a timestamped Markdown report on disk. An optional Second Opinion stage can then have a stronger model, cloud or local, validate those findings against the actual code and write a separate validated report. Acting on findings is deliberately out of scope.

It ships as two executables: the reviewer (`Informant`), the harness above, and the dispatcher (`Marshal`), an optional long-running supervisor that watches repositories and runs the reviewer on schedule, file-change, or webhook triggers, with a dashboard and a small local API over the run history.

- **Deterministic harness, judgment-only model.** The AI can only read code through one confined tool; it never writes, executes, or runs git.
- **Local-first.** The review model is your own endpoint (LM Studio, llama.cpp, Ollama, a gateway); code never leaves your network unless you opt into a cloud Second Opinion.
- **Second Opinion.** A stronger model validates the local findings against the actual code, and can read more of a file on demand to catch what the first pass missed.
- **No heavy dependencies.** Email uses the framework's built-in SMTP client (or Azure Communication Services); no third-party email or crypto libraries.
- **Secrets are never literals.** Passwords, API keys and webhook secrets are `env:` or `file:` references resolved at runtime.
- **Single-file executables** per platform (win-x64, linux-x64).

## Quick start

```text
cd C:\jobs\my-repo-review        # the directory that will hold this job's config
Informant init                   # writes informant.json and review-prompt.txt
notepad informant.json           # set repository, branch, working tree, git path, endpoint, model
Informant verify                 # prove the model does tool-calling before anything else
Informant                        # run a review
```

The first run reviews every tracked file and records the reviewed tip SHA; each later run reviews only what changed since. Reports land in the report directory, named by run timestamp, and are never auto-deleted.

## Documentation

Full documentation — configuration and environment-variable overrides, the Second Opinion pass, email, secrets and unattended deployment, and the Marshal dispatcher with its dashboard and API — is in **[DOCUMENTATION.md](DOCUMENTATION.md)**.

## Disclaimer

Use BugSwatter at your own risk. It drives AI models that you choose and configure, and running AI models can cost money — cloud endpoints in particular typically bill per token, and options that let a model read more of your code (such as the second opinion's per-file read budget) increase that usage. You alone are responsible for understanding the configuration and how the tool interacts with the models you point it at, for any costs a run incurs, and for verifying on a small scope that it behaves as you expect before you run it unattended. To the maximum extent permitted by law the software is provided "as is", without warranty of any kind, and the authors and contributors accept no liability for any cost, loss, or damage of any kind arising from its use, including but not limited to runaway token consumption or unexpected charges. Confirm proper behaviour yourself before trusting it unattended.

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
