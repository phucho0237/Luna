# Contributing

Thank you for your interest. This is a personal project, and the maintainer may decline changes that do not fit its direction. For large changes, **open an issue to discuss first** before writing code.

## Before you start

Read `README.md` (account warning), `AGENTS.md` (code rules), `docs/PROTOCOL.md` and `docs/ROADMAP.md`.

## Environment

Windows 10/11, .NET 10 SDK, Git, PowerShell 7 (`pwsh`). Building and running `Luna.App` needs Visual Studio 2026 with the WinUI workload.

```powershell
dotnet test .\Luna.Protocol.Tests\Luna.Protocol.Tests.csproj
dotnet test .\Luna.Core.Tests\Luna.Core.Tests.csproj
pwsh -NoProfile -File scripts\check.ps1
```

## Workflow

1. Fork, then create a branch from `main`.
2. Make small commits following Conventional Commits (`feat(protocol): ...`, `fix(core): ...`, `test: ...`, `docs: ...`, `chore: ...`). Everything except end-user UI text is English (commit messages and comments ASCII only); see the "Language" section in `AGENTS.md`.
3. Open a Pull Request into `main`. Keep PRs small (about 400 changed lines or fewer). CI must be green.

## Code rules (summary; details in `AGENTS.md`)

- The build has **0 warnings** (`Nullable` is on, warnings are errors).
- Every new behavior has a test. Protocol logic must be testable **without a network**. For encryption and formats, a reference vector from an independent source is required, not just encrypt then decrypt.
- Server ids are large numbers: keep them as **strings**, never convert them to numbers.
- Do not add a new NuGet package without discussing it first.

## Privacy and security when contributing (mandatory)

- **Never** put any of these in a commit, issue, PR, log or screenshot: cookies, tokens, `zpw_sek`, `zpw_enk`, QR codes, real uids or phone numbers, message content.
- Test data must be **fake**. Real data may only be shared as a **shape** (field names and types) through `Luna.Playground --shape`, which already filters out values.
- Automated tests **never** call the live server. Live runs use a **secondary account** only, and you run them yourself.
- If you leak data by mistake: sign out that device on the account, and tell the maintainer so the history can be cleaned up.

## Protocol contributions

- Mark clearly whether a finding is **[Live]** (verified) or **[Source]** (only read from open source), as in `docs/PROTOCOL.md`, with evidence (shapes, no values).
- Do not guess field names or endpoints; cite the source.

## What we do not accept

- Bulk messaging, spam, automation for advertising, or farming or managing accounts in bulk.
- Collecting other people's data, or getting around the service's protections or rate limits.
- Malware, exploiting service vulnerabilities, or anything that harms users.
- Telemetry, or calls to servers other than the service's own, without prior discussion.
- Adding copyrighted assets from the service or third parties to the repo (stickers, icons, fonts, images).

## AI tools

Allowed. You are responsible for the code you submit: you have read it, understood it and run the tests. When AI writes a significant part, credit it with a `Co-Authored-By` line naming the exact tool. Do not paste personal data into AI tools. PRs without tests, or that cannot be explained, will be closed.

## Contribution license

By submitting a Pull Request, you agree that your contribution is released under the same license as the project (see `LICENSE`).

## Reporting a security vulnerability

Do not open a public issue. See `SECURITY.md`.
