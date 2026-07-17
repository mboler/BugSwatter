# Contributing to BugSwatter

Bug reports, focused improvements, tests, and documentation corrections are welcome. BugSwatter is a small utility, so contributions should favor clear behavior and minimal dependencies over broad frameworks or speculative abstractions.

## Before opening an issue

Search existing issues first. For a bug, include the BugSwatter version or commit, operating system, command, relevant configuration with secrets removed, expected result, actual result, and the smallest reproduction you can provide.

Do not report security vulnerabilities in a public issue. Follow [SECURITY.md](SECURITY.md).

Feature requests should describe the operator problem and expected behavior. A small design discussion before a large pull request can avoid work that does not fit the project's scope.

## Development setup

Install:

- The .NET 10 SDK
- Git
- PowerShell 7 for repository scripts

Clone your fork and create a focused branch. Restore, build, and test from the repository root:

```powershell
dotnet restore BugSwatter.slnx
dotnet build BugSwatter.slnx -c Release --no-restore
dotnet test BugSwatter.slnx -c Release --no-build --no-restore
./scripts/check-public-content.ps1
./scripts/check-dependencies.ps1
```

Ordinary tests and CI do not contact live model or email services. A normal successful run skips the opt-in integration tests.

## Code expectations

- Keep changes focused on the stated problem
- Add or update xUnit tests for behavior changes and regressions
- Use nullable reference types and address new warnings
- Use modern C# supported by the target framework
- Follow the repository's `.editorconfig`, including Allman braces and file-scoped namespaces
- Use `async` and `await` through asynchronous call paths
- Add XML documentation to public types and members
- Prefer `System.Text.Json`; `Newtonsoft.Json` is prohibited by CI
- Avoid new dependencies when the platform already provides a suitable implementation
- Do not add per-file Apache license headers; the repository-level `LICENSE` and `NOTICE` files carry the license information
- Do not include secrets, private network addresses, personal paths, private repository names, or production data in source, tests, fixtures, logs, or documentation

Match the existing style around the code you change. Do not combine unrelated cleanup with a bug fix or feature.

## Tests

Run the complete suite before submitting a pull request. When changing platform-specific behavior, test on the relevant platform and describe what you verified. GitHub Actions will build, test, inspect dependencies, and smoke-test release packaging on Windows and Linux.

Opt-in live model tests use:

```text
INFORMANT_IT=1
INFORMANT_IT_ENDPOINT=<OpenAI-compatible base URL>
INFORMANT_IT_MODEL=<model name>
INFORMANT_IT_SO_ENDPOINT=<optional validator base URL>
INFORMANT_IT_SO_MODEL=<optional validator model name>
```

Opt-in Azure Communication Services email testing uses:

```text
BUGSWATTER_EMAIL_IT=1
BUGSWATTER_EMAIL_IT_ACS_CONNECTION=<connection string>
BUGSWATTER_EMAIL_IT_FROM=<verified sender>
BUGSWATTER_EMAIL_IT_TO=<recipient>
```

Never put live values into a test file or commit them. Use reserved example domains rather than private network addresses in committed examples. Live tests can consume compute, tokens, or paid service capacity and can send real email. Run them only against resources you own and intend to use.

## Pull requests

Once the repository is public, changes are accepted through pull requests rather than direct pushes. A useful pull request:

- Explains the problem and why the change is needed
- Keeps the diff limited to that problem
- Includes meaningful tests
- Updates public documentation when behavior or configuration changes
- Reports the exact build and test commands run
- Calls out platform limitations, compatibility changes, cost changes, or security tradeoffs
- Does not weaken an assertion merely to make a test pass

Maintainers may ask for a smaller change, additional tests, or a design adjustment. Review feedback should stay technical and respectful.

## Dependency changes

Do not add a package only for convenience. Explain why the framework or existing libraries are insufficient, review its license and maintenance health, and include it in `NOTICE` when attribution requires it. The dependency-policy script must continue to pass, and direct or transitive `Newtonsoft.Json` is not accepted.

There is no permanent version lock policy for allowed packages. Each dependency update is reviewed on its merits, including security advisories and transitive changes.

## License

By submitting a contribution, you agree that it is licensed under the repository's [Apache License 2.0](LICENSE), consistent with the contribution terms in that license.
