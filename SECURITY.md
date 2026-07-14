# Security policy

## Supported versions

BugSwatter is pre-1.0. Security fixes are made only on the latest development line and latest published release. Older pre-1.0 versions are not maintained after a replacement is released.

| Version | Supported |
| --- | --- |
| Latest 0.x release | Yes |
| Earlier versions | No |

## Report a vulnerability privately

Do not disclose an unpatched vulnerability, exploit, credential, private repository detail, or private network detail in a public issue or discussion.

Use the repository's **Security** tab and select **Report a vulnerability** to open a private report. Include:

- The affected version or commit
- Operating system and deployment mode
- Whether Informant, Marshal, the dashboard, a webhook, or a library is affected
- Reproduction steps or a minimal proof of concept
- Expected and observed impact
- Any suggested mitigation, if known

If private vulnerability reporting is temporarily unavailable, open a public issue asking the maintainer to establish private contact, but include no vulnerability details.

Reports are handled on a best-effort basis. Please allow time to reproduce the issue, prepare a fix, and coordinate disclosure. Do not test against systems, repositories, accounts, or model endpoints you do not own or have explicit permission to assess.

## Security boundaries

BugSwatter reduces model privileges, but it is not a security boundary for an untrusted network or hostile machine.

- Informant destructively refreshes its configured working tree. Use a dedicated absolute path and never a development checkout
- Repository reads reject symbolic links, junctions, mount points, other reparse points, absolute paths, and paths outside the allowed root
- Informant rebuilds a tip-bound repository manifest for every run and rechecks manifest size, line count, hash, and path safety before model-directed reads
- Models receive controller-selected bounded source plus one read-only file-range tool and cannot execute commands, write files, or run Git through Informant
- Repository planning output can group or defer only exact reviewable manifest paths. Invalid planning falls back to deterministic controller grouping
- Repository content and prompt guidance are untrusted model input and can influence model output
- Adaptive review can explicitly defer full-file analysis and therefore miss defects outside selected units. Its report and coverage ledger identify that limitation
- AI findings are advisory and can contain false positives or miss real vulnerabilities
- A cloud model receives configured code excerpts and may retain prompts or metadata under the provider's terms
- Marshal's dashboard and API use unauthenticated HTTP. They are intended only for localhost, a trusted internal network, or a protected VPN
- Any client that reaches the dashboard API can inspect operational metadata, including current file and model names and provider-reported usage, enqueue reviews, and remove waiting reviews
- GitHub webhook signatures authenticate payloads but do not encrypt HTTP traffic
- Azure DevOps webhook Basic credentials are exposed to anyone who can observe an unprotected HTTP path
- Repository polling uses outbound Git and avoids opening an inbound port, but still relies on the service account's Git credentials
- LocalSystem on Windows and root on Linux give Marshal extensive machine privileges. A dedicated least-privilege account limits impact
- Manifest, coverage, trace, report, and log artifacts can reveal repository URLs, local paths, file names, hashes, model names, and timing even when a particular artifact contains no source bodies

The first public release intentionally does not provide dashboard authentication, authorization, TLS termination, certificate management, or internet-facing hardening. Do not expose Marshal directly to the public internet. If confidentiality is required across network segments, keep Marshal behind a trusted VPN or use a separately managed authenticated TLS reverse proxy.

## Secret handling

Never commit passwords, API keys, ACS connection strings, personal access tokens, SSH private keys, webhook secrets, or configuration files containing them.

Informant model API, SMTP, and ACS secrets must use `env:` or `file:` references, including API keys declared inside second-opinion model profiles. Windows service passwords supplied to the installer also require a reference. Marshal webhook secrets additionally permit literals for small internal deployments; when using a literal, protect the configuration file as a secret.

Prefer narrowly scoped, read-only repository credentials. Restrict secret files to the Marshal service identity and rotate a credential if it appears in a log, report, command history, issue, test artifact, or commit.

## Dependency policy

CI checks the current tracked tree for private-network addresses, local paths, private-key headers, high-signal token formats, and the legacy reviewer name. It also inspects resolved direct and transitive NuGet dependencies, rejects `Newtonsoft.Json`, lists non-Microsoft packages, and reports known NuGet vulnerabilities. These are guardrails, not substitutes for reviewing commits and dependency updates. Security issues in `System.Text.Json`, .NET, model servers, Git, operating systems, and other dependencies still require normal patch monitoring.

## Not a vulnerability

The following are expected limitations unless they demonstrate an impact beyond the documented behavior:

- A model produces a wrong, incomplete, or inconsistent review
- A slow model exceeds its configured timeout
- A cloud model incurs usage charges that match the configured workload
- The HTTP dashboard lacks authentication or TLS on a network where it was deliberately exposed
- A service identity lacks permission to read credentials or modify its dedicated working tree
- A report is deleted after its configured retention period

Documentation improvements for these risks are still welcome through normal issues and pull requests.
