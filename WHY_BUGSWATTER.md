# Why BugSwatter?

*An extra set of eyes while you sleep, without sending every nightly review to an expensive cloud model.*

BugSwatter is built around a simple split: use a local model for broad, repetitive review work, then let one optional second model challenge the findings that survived the first pass. That second model can be a stronger cloud model, another local model, or nothing at all.

The result is not a promise of perfect review. It is a practical way to add consistent, unattended scrutiny while keeping control of source exposure, hardware use, and cloud spending.

## Spend cloud tokens on judgment, not bulk reading

Source review consumes tokens quickly. Large repositories, long files, and full first-time reviews can turn a useful nightly habit into a large recurring cloud bill.

BugSwatter can keep that broad primary pass on hardware you already own by using an OpenAI-compatible local endpoint such as LM Studio, llama.cpp, or Ollama. If you enable a cloud second opinion, the billable model is used as a validator after the local model has already found and organized candidate issues. It receives the review material and can request bounded extra line ranges when it needs evidence. The cloud bill starts at validation instead of at the first read through the repository.

That does not make cloud review free, and the exact savings depend on the repository, strategy, model, and findings. It does mean you can spend frontier-model tokens on the part where stronger judgment may matter most. If both models are local, source and inference can stay entirely on your own network.

BugSwatter selects one validator for a run. It does not fan every review out to every configured service. Simple setups use one second-opinion model. Advanced setups can route the run to one of as many as three validator profiles according to the highest candidate severity found by the primary model.

## Review a repository, not a pile of unrelated files

BugSwatter rebuilds its repository manifest at the start of every review. It supplies bounded root guidance, configured seed material, changed source, and repository structure, then asks the primary model to group related files into review clusters. Invalid model planning falls back to deterministic clusters so required work is not silently discarded.

After the first completed review, changed mode focuses on work since the last successful baseline while still allowing unchanged supporting files to be included for context. This keeps routine runs smaller without forcing the model to reason about each changed file in isolation.

Two review strategies make the coverage tradeoff explicit. Exhaustive review deeply inspects every reviewable candidate. Adaptive review can defer some deep file review when the repository or change set is too large, while still recording what was reviewed, deferred, excluded, partial, or failed. BugSwatter does not claim it reviewed files that it did not review.

The design is repository-aware without being tied to C#, one framework, or one directory layout. Project guidance and seed paths can help it understand an unfamiliar codebase, but the application does not need a language-specific graph before it can begin.

## Models get context, not hands

BugSwatter is not an agentic coding harness.

The models do not receive a shell, Git access, MCP servers or adapters, general filesystem access, or any tool that can write code. Their only model-directed action is a request for a bounded range of numbered lines through Informant's application-owned `read_file_lines` tool.

Informant performs the read itself. It validates the current manifest, path, repository boundary, file size, line range, content identity, and reparse-point rules before returning anything. Symbolic links, junctions, mount points, other reparse points, out-of-root paths, binary files, and oversized files are rejected. A model can ask for more evidence, but it cannot execute an action on the machine.

Informant does use controlled Git commands to maintain its dedicated review clone. Those commands and arguments come from application code, not model output. The working tree must be a disposable clone created for BugSwatter, never a checkout where someone works.

## Built to keep working after you stop for the day

Informant can run by itself for one review. Marshal adds schedules, outbound repository polling, filesystem triggers, webhooks, a trusted-network dashboard, report retention, and email delivery. Primary endpoints can have ordered fallbacks, provided the models are already running. BugSwatter does not load models or manage GPU placement.

A changed-review baseline advances only after the primary review completes successfully. Reports preserve coverage and failure information, and the dashboard can show the current phase, file, elapsed time, model request state, and provider-reported token use. The intent is straightforward: make unattended operation understandable when it works and diagnosable when it does not.

## The tradeoffs are real

Local inference trades cloud cost for hardware, electricity, heat, setup, and time. Smaller local models can miss issues or use tools poorly. Cloud validation sends selected review material and source context outside your network, and provider billing can still be substantial. Adaptive review may defer files. Any model can invent a problem, misunderstand intent, or overlook a defect.

The built-in dashboard is HTTP-only and has no authentication. It belongs on localhost or a trusted internal or VPN network, never directly on the public internet.

BugSwatter is an additional reviewer, not a replacement for tests, human review, security analysis, or engineering judgment. Its reports are leads to investigate, not proof that a codebase is safe.

## Who it is for

BugSwatter is a good fit for personal projects, home labs, and small teams that want another set of eyes on their repositories every night without handing an AI unrestricted control or paying a frontier model to perform every first-pass read.

It is intentionally a small, self-hosted utility. Bring the models and infrastructure that fit your environment, decide where source may go, and keep the final judgment where it belongs: with the people responsible for the code.
