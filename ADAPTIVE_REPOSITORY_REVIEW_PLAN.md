# Adaptive Repository Review Overhaul

Status: Approved, implementation in progress

Last updated: July 14, 2026

This document is the implementation checklist for replacing Informant's isolated file-by-file review flow with a code-agnostic repository planning and clustered review flow. Checkboxes are updated as work is completed. Each implementation phase ends with verification and a separate local commit.

## Objectives

- Recalculate a complete repository manifest after the managed working tree is refreshed at the beginning of every Informant run
- Keep the review engine independent of programming language, framework, and repository layout
- Give the model enough repository structure and related source to plan a coherent review
- Review related files in bounded clusters instead of isolated file conversations
- Support both exhaustive and adaptive review strategies
- Guarantee minimum changed-code coverage even when a small or weak model produces a poor plan
- Let the model request unchanged supporting files through the existing bounded, read-only line retriever
- Reject every symbolic link or Windows reparse point during discovery and again during each read
- Record model tool requests and controller-selected context in a separate audit artifact without copying source text into that artifact
- Stay within configured context limits and degrade safely to deterministic review behavior
- Reduce repeated prompts, unnecessary model calls, local inference time, and cloud-validation cost

## Non-goals

- Do not give a model Git, shell, process, network, write, delete, directory-listing, MCP, or repository-mutation tools
- Do not add language-specific parsing, Roslyn analysis, compiler APIs, or framework-specific planners in the first implementation
- Do not claim complete-file coverage when an adaptive review deliberately defers files
- Do not infer a portable model context limit from an ordinary OpenAI-compatible `/v1/models` response
- Do not load or unload models automatically
- Do not run several primary models or produce several competing primary reports
- Do not parallelize review units in the first implementation

## Accepted design decisions

- [x] The repository manifest is recalculated on every run after the managed working tree is refreshed
- [x] The core review design remains code-agnostic
- [x] Repository guidance, root files, changed content, and configured seed paths form the initial context
- [x] The model may choose additional unchanged files from the supplied manifest
- [x] Related files are reviewed as clusters
- [x] Exhaustive and adaptive strategies are both supported
- [x] Adaptive reports distinguish deep review from deferred coverage
- [x] The model retains only the existing bounded `read_file_lines` capability
- [x] Symbolic links and reparse points are rejected without following them
- [x] Detailed request history is written outside the main Markdown report
- [x] The configured context budget remains authoritative
- [x] Work is performed without subagents
- [x] Implementation uses a `feature/` branch and separate local commits
- [x] Adaptive baselines may advance after selected units and mandatory changed content complete, with deferred files recorded as a documented coverage limitation
- [x] No-change runs rebuild the manifest in memory without persisting manifest or trace artifacts
- [x] Each phase is committed locally and the feature branch is pushed only after final verification
- [x] The approved target version is `0.8.1`

## Review mode and review strategy

The existing review mode and the new review strategy answer different questions and must remain separate.

| Setting | Values | Purpose |
| --- | --- | --- |
| `reviewMode` | `changed`, `full` | Selects the candidate file universe |
| Proposed `reviewStrategy` | `exhaustive`, `adaptive` | Selects how deeply the candidate universe must be reviewed |

Recommended compatibility behavior:

- Existing configurations default to `exhaustive`
- `changed` plus `exhaustive` preserves the present minimum coverage guarantee while using clusters
- `changed` plus `adaptive` supplies every changed hunk and deeply reviews the files selected by the validated plan
- `full` plus `exhaustive` sends every reviewable tracked file through bounded review units
- `full` plus `adaptive` indexes every tracked file and deeply reviews the selected units while recording every deferred file
- A first run without a baseline uses the full tracked tree as its candidate universe, then applies the configured strategy

## Per-run pipeline

1. Refresh the dedicated managed working tree and resolve the immutable tip SHA
2. Recalculate the repository manifest from that refreshed snapshot
3. Reject and record symbolic links, reparse points, binary files, unsafe paths, and other exclusions without following them
4. Detect the changed or full candidate set
5. Write the run manifest and change-set artifacts when a review will proceed
6. Build the bounded initial repository briefing
7. Ask the primary model for a structured review plan
8. Validate every planned path and ensure minimum changed-code coverage
9. Fall back to deterministic clusters if planning fails or is incomplete
10. Pack and review each bounded cluster
11. Permit bounded reads of manifest-approved files while recording every request and disposition
12. Write findings and an explicit coverage ledger
13. Run the existing second-opinion validation against primary findings
14. Advance or preserve the baseline according to the selected strategy and completion rules

## Repository manifest

### Snapshot rules

- Build the manifest only after `WorkingTreeManager.EnsureFreshTreeAsync` completes
- Bind the manifest to the repository URL, branch, tip SHA, run identifier, and normalized working-tree root
- Rebuild it for every Informant invocation, including invocations that ultimately find no changes
- Do not reuse a previous run's manifest as live input
- Keep the working tree read-only for the model throughout the run
- Revalidate every requested file at read time so a filesystem change after discovery cannot bypass the manifest or symbolic-link rules

### Manifest contents

Each tracked entry records only metadata needed for planning and enforcement:

- Normalized repository-relative path
- Byte length
- Line count when safely measurable as text
- Extension or filename category
- Change status for the current run
- Whether it is root-level guidance, documentation, a likely build or package manifest, configured seed content, or ordinary repository content
- Reviewability and any exclusion reason
- Symbolic-link or reparse-point rejection status

The manifest itself contains no API keys, environment values, source bodies, or file contents.

### Persistence

- Proposed artifact name: `Informant-Manifest-{runStamp}.json`
- Persist it for every run that performs a review
- For a no-change run, recalculate it in memory and log its summary without creating a new managed artifact
- Apply normal report retention to persisted manifests

## Initial repository briefing and seed selection

### Automatically prioritized material

The initial briefing ranks material by role rather than programming language:

1. Base review prompt and configured repository guidance
2. Compact directory and file manifest
3. Change summary
4. Root-level agent and contributor guidance such as `AGENTS.md`, `CLAUDE.md`, `README*`, `CONTRIBUTING*`, and `SECURITY*`
5. Root-level build, project, package, deployment, and configuration manifests
6. User-configured seed files and directories
7. Changed hunks with surrounding context
8. Complete small changed files and additional related content while budget remains

### Proposed seed configuration

- Add a `seedPaths` collection of repository-relative files, directories, or glob patterns
- Treat seeds as high-priority context, not permission to exceed the configured budget
- Partition an oversized seed directory across planning inputs or review units
- Reject seed paths outside the repository and reject seeds that resolve through symbolic links or reparse points
- Preserve `promptIncludeFiles` for instructions; do not overload it as source-context selection

### Context budgeting

- Keep `maxContextCharacters` as the authoritative total conversation-content safety budget
- Initially allocate approximately 55 percent to briefing and packed source, 25 percent to tool expansion, and 20 percent to response and safety headroom
- Count system guidance, manifest text, source, assistant messages, and tool results against the appropriate budget
- Pack deterministically so identical inputs and configuration produce identical initial context
- Compact oversized manifests by directory while retaining exact paths for the portion exposed to the planner
- Split planning by top-level directory when even the compact manifest does not fit
- Never silently drop changed content required by the selected strategy

## Structured planning pass

The planning response uses a versioned JSON contract containing:

- Repository summary
- Proposed review units
- Priority and rationale for each unit
- Exact manifest paths assigned to each unit
- Additional supporting paths the model wants to read
- Explicitly deferred paths and reasons in adaptive mode
- Any uncertainty about repository structure or missing context

Plan validation must:

- Reject malformed or unsupported plan versions
- Reject absolute paths, parent traversal, unknown paths, symbolic links, reparse points, excluded paths, and duplicate assignments where duplication is not explicitly allowed
- Normalize path comparisons using the host filesystem's case behavior
- Ensure every mandatory changed file or changed hunk is assigned
- Bound unit count, file count, requested reads, and estimated characters
- Add omitted mandatory content to deterministic fallback units
- Fall back entirely when the model returns unusable planning output

A planning failure is an optimization failure, not a reason to lose review coverage.

## Clustered review

- Review one related unit per bounded conversation
- Pack several related files or changed hunks before relying on tool calls
- Supply the repository summary, unit purpose, relevant change ranges, and exact paths available for expansion
- Keep raw source from one unit out of later units unless the plan explicitly relates them
- Carry only a bounded structured repository summary and coverage state between units
- Preserve deleted-file review from the immutable baseline Git object
- Preserve line-numbered findings and structured severity output
- Run units sequentially in the first implementation for deterministic resource use and simpler recovery
- Append completed unit results immediately so a crash retains finished work

Deterministic fallback grouping should prefer shared directories and path proximity without assuming a language or framework.

## Bounded file requests

The manifest becomes the per-run allowlist for `read_file_lines`.

### Reject immediately

- Paths absent from the current manifest
- Absolute paths or parent traversal
- Files outside the configured read root
- Any path containing or resolving through a symbolic link or reparse point
- Binary, oversized, excluded, missing, or changed-since-manifest files
- Invalid or reversed line ranges
- Requests made after the unit's read or conversation budget is exhausted

### Bound rather than silently truncate

- Retain a per-request maximum line count and add a per-request maximum character count
- When a valid request is too large, return the largest safe prefix that fits
- Mark the result explicitly as partial and include the actual returned range, total line count, truncation reason, and `next_start_line`
- Tell the model to request the next range if it still needs it
- If no useful line can fit, reject the request with the remaining budget and a smaller permitted range
- Never return a shortened range while representing it as complete
- Detect repeated identical rejected requests and stop consuming rounds after a small bounded count

All safety checks run both against the immutable manifest metadata and against the live filesystem immediately before reading.

## Tool and context audit trail

### Artifact

- Proposed artifact name: `Informant-Trace-{runStamp}.jsonl`
- Append events as the run proceeds so a crash retains the trace
- Keep the trace outside the main Markdown report
- Put only the trace filename and aggregate counts in the main report
- Apply normal report retention to trace artifacts

### Events

Record controller and model actions needed to reconstruct coverage:

- Manifest created and summary counts
- Initial context file or range selected by the controller
- Review unit started and completed
- Model tool name and raw requested path and line range
- Normalized path and validation decision
- Request served, partially served, or rejected
- Returned line count and character count, but not returned source text
- Rejection or truncation reason
- Model name and configured profile
- Unit identifier, request sequence, timestamp, and duration
- Unknown or unsupported tool calls
- Planning fallback and coverage-repair actions

Do not log source bodies, prompts, model responses, API keys, authorization headers, environment secret values, or email credentials in the trace.

## Coverage ledger and baseline behavior

The main report records coverage truthfully with these dispositions:

| Disposition | Meaning |
| --- | --- |
| `Indexed` | The path and metadata were visible to the planning process |
| `ChangedContentCovered` | Every required changed hunk or added-file chunk was supplied to a review unit |
| `DeepReviewed` | The file was reviewed in a related cluster with optional fan-out |
| `Deferred` | Adaptive planning deliberately omitted a file from deep review and recorded why |
| `NotReviewable` | The file was deliberately excluded for a bounded reason |
| `Failed` | Required review work did not complete |
| `Partial` | Some but not all required chunks or unit work completed |

Recommended baseline rules:

- Exhaustive runs preserve the existing rule: do not advance while required files are failed or partial
- Adaptive incremental runs require coverage of all changed content before advancement
- Adaptive first or full runs may advance after every selected review unit completes, provided every unselected candidate is explicitly recorded as deferred
- A later modification to a previously deferred file becomes mandatory changed content
- Planning failure falls back to exhaustive minimum coverage rather than advancing on an empty or invalid plan

The report must never use language such as "all files reviewed" for an adaptive run with deferred files.

## Model context discovery

- Continue requiring an explicit configured context budget for every model
- Optionally query recognized local providers, starting with LM Studio's native model API, for the loaded and advertised token limits
- Treat discovered values as advisory because character-to-token ratios vary and ordinary OpenAI-compatible model metadata is not portable
- Use discovery to warn or reduce a clearly unsafe budget, never to increase a configured budget automatically
- Display configured and discovered values during `validate`
- Continue normally when metadata is unavailable but explicit configuration is valid

This work is intentionally scheduled after the core manifest, planning, clustering, and coverage behavior.

## Phase checklist and commit plan

### Phase 0: Approve and checkpoint the plan

- [x] Resolve the decisions listed at the end of this document
- [x] Create branch `feature/adaptive-repository-review`
- [x] Commit this approved plan before production-code changes
- [x] Verify the working tree contains only the approved plan change

Planned commit: `Document adaptive repository review overhaul`

### Phase 1: Per-run manifest and snapshot safety

- [x] Add manifest records and deterministic manifest builder in Informant
- [x] Recalculate after every managed-tree refresh
- [x] Detect text metadata without reading unsafe or unbounded bodies
- [x] Reject symbolic links and reparse points during discovery
- [x] Persist review-run manifests and integrate retention
- [x] Add tests for mutation between runs, path normalization, binary files, oversized files, and cross-platform symbolic links
- [x] Build the solution and run the affected tests
- [x] Update this document with results

Planned commit: `Add per-run repository manifest`

### Phase 2: Bounded reads and audit trace

- [x] Make the current manifest an allowlist for every model file request
- [x] Add line and character response bounds with explicit partial-result metadata
- [x] Recheck the live path and symbolic-link chain immediately before every read
- [x] Add append-only JSONL trace writing
- [x] Record tool requests, context selections, outcomes, counts, and reasons without source bodies
- [x] Integrate trace artifacts with retention and report summary metadata
- [x] Add tests for traversal, unknown files, symlink swaps, oversized ranges, exhausted budgets, repeated invalid requests, and trace redaction
- [x] Build the solution and run the affected tests
- [x] Update this document with results

Planned commit: `Audit and bound model file requests`

### Phase 3: Repository briefing, context packer, and planning contract

- [x] Add root guidance and manifest prioritization
- [x] Add validated `seedPaths` configuration
- [x] Implement deterministic character-budget packing
- [x] Add compact and partitioned manifest representations
- [x] Define the versioned planning JSON contract
- [x] Parse and validate planned paths, units, priorities, requested context, and deferrals
- [x] Add deterministic fallback and omitted-change repair
- [x] Add tests using C#, Python, JavaScript, mixed-language, documentation-only, and unknown-extension fixtures
- [x] Add tests for malformed JSON, invented paths, duplicated paths, omitted changes, tiny budgets, and weak planning output
- [x] Build the solution and run the affected tests
- [x] Update this document with results

Planned commit: `Add code-agnostic repository planning`

### Phase 4: Clustered exhaustive review

- [x] Replace isolated file orchestration with bounded sequential review units
- [x] Pack related files and hunks into each unit
- [x] Preserve deleted, renamed, added, modified, and large-file behavior
- [x] Preserve primary failover, progress reporting, partial artifacts, and structured severity
- [x] Ensure exhaustive strategy covers every required candidate
- [x] Add integration tests with scripted model responses and tool calls
- [x] Compare current and clustered exhaustive coverage on representative fixtures
- [x] Build the solution and run the complete test suite
- [x] Update this document with results

Planned commit: `Review related files in bounded clusters`

### Phase 5: Adaptive strategy and coverage ledger

- [x] Add the `exhaustive` and `adaptive` strategy configuration
- [x] Keep exhaustive as the compatibility default
- [x] Enforce mandatory changed-content coverage in adaptive incremental runs
- [x] Record selected, deep-reviewed, deferred, excluded, failed, and partial paths
- [x] Implement the approved adaptive baseline rule
- [x] Update Markdown and JSON reports without embedding the detailed trace
- [x] Add tests proving reports never overstate adaptive coverage
- [x] Add tests for first run, large incremental run, full sweep, no-change run, failure, retry, and baseline advancement
- [x] Build the solution and run the complete test suite
- [x] Update this document with results

Planned commit: `Add adaptive review strategy and coverage ledger`

### Phase 6: Optional model-capacity advisory

- [x] Add optional provider metadata discovery without weakening explicit configuration
- [x] Support LM Studio loaded and maximum context metadata first
- [x] Warn on a clearly unsafe configured budget
- [x] Do not fail generic OpenAI-compatible providers that omit metadata
- [x] Add validation output and tests for available, missing, malformed, and contradictory metadata
- [x] Build the solution and run the affected tests
- [x] Update this document with results

Planned commit: `Validate model context capacity when available`

### Phase 7: Documentation, performance evaluation, and release preparation

- [x] Update README, DOCUMENTATION, SECURITY, sample configuration, and operating walkthroughs
- [x] Document exhaustive versus adaptive guarantees and costs
- [x] Document manifest, trace, seed paths, context budgets, symlink rejection, retention, and privacy considerations
- [x] Measure requests, input characters, tool calls, duration, completion, and report quality on representative repositories
- [x] Exercise at least one smaller local model and Ornith without requiring either model in normal CI
- [x] Confirm the second opinion receives findings and bounded excerpts rather than planning noise or complete manifests
- [x] Run formatting checks, dependency policy, build, and the complete test suite
- [x] Scan tracked files and test artifacts for local addresses, secrets, and personal environment details
- [x] Review the complete branch diff against this plan
- [x] Update this document so every completed item contains actual verification evidence

Planned commit: `Document and validate adaptive repository review`

### Phase 8: Version, publish branch, and review

- [x] Apply the approved minor-version bump in a separate commit
- [x] Run the final clean build and complete test suite
- [x] Confirm the working tree is clean
- [x] Push `feature/adaptive-repository-review`
- [x] Open a draft pull request with the phase commits preserved
- [ ] Wait for owner review and approval before merging
- [ ] Tag and release only after the pull request is merged and the release workflow is green

Approved version: `0.8.1`

Planned commit: `Bump version for adaptive repository review`

## Verification matrix

At minimum, automated coverage must include:

- Empty, small, medium, and manifest-too-large repositories
- New repository, no-change run, normal incremental change, large incremental change, and explicit full review
- Exhaustive and adaptive strategies
- Added, modified, renamed, deleted, binary, empty, oversized, and metadata-only files
- Root and nested symbolic links, directory links, Windows reparse points, and time-of-check/time-of-use replacement attempts
- Root guidance, nested guidance, missing seed, file seed, directory seed, glob seed, and oversized seed
- Valid plan, invalid JSON, unknown version, invented path, traversal, duplicate path, omitted change, empty plan, excessive plan, timeout, and model failure
- Full, partial, rejected, repeated, and budget-exhausted file requests
- Primary failover during planning and during unit review
- Crash after manifest, during planning, and after one or more completed units
- Accurate trace redaction, retention, coverage reporting, and baseline behavior
- Small configured context and large configured context
- Provider metadata present, absent, malformed, and unsupported

## Definition of done

- The manifest is rebuilt from the refreshed repository snapshot on every run
- No manifest or read path follows a symbolic link or reparse point
- Every model tool request has an audit disposition without source text or secrets in the trace
- No packed context or tool response exceeds its configured bound
- Incremental changed content cannot be silently omitted by a model plan
- Exhaustive runs retain complete required coverage
- Adaptive runs clearly identify deep-reviewed and deferred files
- Planning failure produces deterministic safe fallback behavior
- Existing primary failover, second opinion, email, retention, dashboard progress, and baseline protections continue to work
- All relevant tests pass on Windows and Linux CI
- Documentation accurately explains guarantees, limitations, cost controls, and safety boundaries
- The feature branch contains reviewable phase commits and no unrelated changes

## Approved implementation decisions

1. **Adaptive baseline advancement**
   - Advance after all selected units and mandatory changed content complete
   - Permanently record deferred files and document that adaptive review can miss issues in them

2. **No-change artifacts**
   - Rebuild the manifest in memory and log summary counts
   - Do not persist manifest or trace artifacts when the tip equals the baseline

3. **Feature-branch backup cadence**
   - Make a verified local commit at every phase
   - Push only after the complete branch passes final verification

4. **Version**
   - Release this major pre-1.0 feature wave as `0.8.1`

## Progress log

| Date | Phase | Status | Verification and notes | Commit |
| --- | --- | --- | --- | --- |
| 2026-07-14 | Plan | Approved | Decisions approved, feature branch created, and no production code changed | `Document adaptive repository review overhaul` |
| 2026-07-14 | 1 | Complete | Immutable Git-tree catalog and bounded common file inspection added; no-change integration proves in-memory recalculation without artifacts or a model call; solution build has zero warnings; 556 tests ran, 550 passed, and 6 opt-in live tests skipped | `Add per-run repository manifest` |
| 2026-07-14 | 2 | Complete | Manifest-gated live reads now verify path, type, size, line count, and SHA-256 content; tool results have explicit line and character bounds; controller selections and model tool activity are written to a metadata-only retained JSONL trace; 571 tests ran, 565 passed, and 6 opt-in live tests skipped | `Audit and bound model file requests` |
| 2026-07-14 | 3 | Complete | Added safe repository-relative path syntax in Common, validated seed paths, code-agnostic role-ranked briefings, bounded exact-path manifest partitions, deterministic whole-block context packing, and a versioned plan validator with fallback and coverage repair; production orchestration remains unchanged until Phase 4; solution build has zero warnings; 603 tests ran, 597 passed, and 6 opt-in live tests skipped | `Add code-agnostic repository planning` |
| 2026-07-14 | 4 | Complete | Replaced isolated production file calls with bounded manifest planning and sequential clustered units; invalid planning falls back without reducing exhaustive coverage; whole units restart on primary-model failover; completed units persist immediately and aggregate back to the existing per-file baseline and second-opinion contract; scripted tool-call, response attribution, malformed-severity, coverage, and failover tests were added; solution build has zero warnings; 618 tests ran, 612 passed, and 6 opt-in live tests skipped | `Review related files in bounded clusters` |
| 2026-07-14 | 5 | Complete | Added exhaustive and adaptive strategy selection with exhaustive as the default; adaptive deferrals receive mandatory changed-line windows with surrounding context; metadata-only coverage artifacts distinguish deep, mandatory-change, deferred, excluded, failed, and partial outcomes; reports state adaptive limitations and baseline advancement now follows the approved coverage rule; solution build has zero warnings; 632 tests ran, 626 passed, and 6 opt-in live tests skipped | `Add adaptive review strategy and coverage ledger` |
| 2026-07-14 | 6 | Complete | Added bounded LM Studio v1 metadata discovery in BugSwatter.AI and character-budget advisory policy in Informant; validation reports loaded and maximum context, warns without overriding explicit configuration, and treats missing generic-provider metadata as normal; malformed and contradictory metadata are non-blocking warnings; solution build has zero warnings; 392 affected tests ran, 387 passed, and 5 opt-in live tests skipped | `Validate model context capacity when available` |
| 2026-07-14 | 7 | Complete | Connected manifest-verified root, seed, and changed source to bounded production planning; added metadata-only model request size, lifecycle, duration, and provider token telemetry; README, operating documentation, and security guidance now cover strategies, artifacts, costs, retention, and privacy. Scripted fixtures verify clustered planning, tool counts, redaction, bounded second-opinion inputs, and report completion. Opt-in live checks passed completion, native tool use, and defect detection on both a 12B local model in 39.3 seconds and a 35B local model in 3 minutes 40 seconds. The solution build has zero warnings; 646 tests ran, 640 passed, and 6 opt-in tests skipped; branch-added C# passed `dotnet format`; dependency policy found no Newtonsoft.Json; the NuGet vulnerability check found no known vulnerable packages; and the 207-file disclosure scan found no local addresses, local paths, legacy name, private keys, or high-signal secrets | `Document and validate adaptive repository review` |
| 2026-07-14 | 8 | In review | Shared product version changed to 0.8.1 in its own checkpoint; final Release build completed with zero warnings; 646 Release tests ran, 640 passed, and 6 opt-in live tests skipped | `Bump version for adaptive repository review` |
