# Luna: agent instructions

Luna is a **native Windows** client for a Vietnamese messaging service (C# + WinUI 3, .NET 10), smoother than the official Electron app. This is an **unofficial** client, not affiliated with or endorsed by the service operator. The goal is production quality.

Maintainer and reviewer: Luca (a person). The project is Luna (the product). These are two different names; do not mix them up. The code is written by the agent (Claude Code); Claude on claude.ai reviews when Luca sends a diff. Everything starts from `docs/ROADMAP.md`.

## Structure (dependencies go one way only: App → Core → Protocol)

| Project | Target | Role |
|---|---|---|
| `Luna.Protocol` | net10.0 | Encryption, signing, HTTP, QR login, WebSocket. No UI, no platform dependency. |
| `Luna.Core` | net10.0 | Session storage (DPAPI), `AuthService`. Later: SQLite, sync. |
| `Luna.App` | net10.0-windows10.0.19041.0 | WinUI 3 (packaged), UI only, MVVM. |
| `Luna.Protocol.Tests`, `Luna.Core.Tests` | net10.0 | xUnit. |
| `Luna.Playground` | net10.0-windows | Console for a **human** to try things on the live server with a secondary account. Not released. |

`Luna.Protocol` never references `Core` or `App`.

## Commands (PowerShell, solution root)

```powershell
pwsh -NoProfile -File scripts\check.ps1                         # tests + build, compact summary (in Claude Code: /check)
dotnet test .\Luna.Protocol.Tests\Luna.Protocol.Tests.csproj    # current count: run /check
dotnet test .\Luna.Core.Tests\Luna.Core.Tests.csproj            # current count: run /check
dotnet run --project .\Luna.Playground -- --shape               # touches the live server, run by a human only (also has --events; --friends-list and --send-cloud send real requests)
```

Do not run `dotnet test` at the solution root (the WinUI project needs Visual Studio, Debug | x64). `Luna.App` is built and run in Visual Studio with F5. The test count may only go up, never down.

## Code rules

- `Directory.Build.props` enables `Nullable` and `TreatWarningsAsErrors`: **0 warnings**. Disable a warning only with an explanatory comment (existing examples: CA5351 for MD5 as the protocol requires, CS0618 for BouncyCastle).
- Identifiers, comments, XML docs, log and exception messages are English: see the "Language" section below.
- Classes are sealed by default. Async methods take a `CancellationToken` as the last parameter (default `default`); in `Protocol` and `Core` use `ConfigureAwait(false)`, in `App` do not.
- Use only `System.Text.Json`. **Every server id** (`msgId`, `cliMsgId`, `uid`, `ts`...) is a 64-bit or larger number: keep it as a **string** or as the `GetRawText()` of a `JsonElement`. Never convert it to `double`.
- **Do not add a new NuGet package without asking.** Current ones: `BouncyCastle.Cryptography`, `System.Security.Cryptography.ProtectedData`, xUnit.
- Every new behavior has an xUnit test. Protocol logic must be testable **without a network**: inject an `HttpMessageHandler` or split it into a pure state machine (see `SocketProtocol`, `QrLoginClient` as models). Fakes go in the `Support/` folder.
- For encryption and formats: tests must have a **reference vector from an independent source** (zca-js run with Node). A test that only encrypts and then decrypts is **not enough**.
- Imitate existing patterns (for example `LoginApi`, `QrLoginClient`, `SocketClient`) instead of inventing a new architecture.

## Language (MANDATORY)

Everything the project ships or stores is English, except end-user UI text.

- Commit messages: English, ASCII only, including the body and the `Co-Authored-By` trailer. No emoji.
- Code comments and XML docs (`//`, `///`, `/* */`, `#` in scripts): English, ASCII only. Use `'`, `"`, `-`, `->`; no curly quotes, long dashes or Unicode arrows.
- Log, trace and exception messages, and the console output of `Luna.Playground` and `scripts/`: English.
- Production code (`Luna.Protocol`, `Luna.Core`, `Luna.Playground`, `scripts/`): no non-ASCII character in any source line (a leading BOM is tolerated). Write needed Unicode as `\uXXXX` escapes.
- Test projects: comments and failure messages are English ASCII. Test data may contain non-ASCII text on purpose (Unicode round-trip tests).
- Documentation (`README.md`, `docs/`, `AGENTS.md`, `CLAUDE.md`, `CONTRIBUTING.md`, `SECURITY.md`, `.github` templates, `.claude/skills`): English. Vietnamese appears only inside backticks, for example a UI label of the messaging service such as `Cloud của tôi`.
- Evidence markers in docs and comments: `[Live]` = observed on the real server, `[Source]` = only from zca-js or ctdozlite.
- End-user UI text (future `Luna.App`) is Vietnamese and lives in resource files (`.resw`), never hard-coded in C# or XAML logic.
- Before reporting done, run `scripts/check-language.ps1` (it is also part of `scripts/check.ps1`).
- The rule applies to new commits. A few early commit subjects (the T2 events work) stay in Vietnamese and are not rewritten.

## Security (MANDATORY)

- **Never** log, print or commit: cookie values, `zpw_sek`, `zpw_enk`, cipher keys, QR code content, message content, real `uid`/IMEI in tests or docs. Diagnostics may print only **names, JSON shapes, counts** (models: `JsonShape`, `QrLoginClient.Shape`). The only exception: the `act_type` and `act` values of events 601/602, and only when passed through `JsonShape.SafeToken`.
- A record that holds secrets must override `ToString()` (model: `LoginInfo`).
- The session may only be stored with `EncryptedFileSessionStore` + DPAPI. Writing cookies or tokens to disk in plain text is forbidden. `.gitignore` already blocks `session*.json`, `*.db`, `*.log`: do not loosen it.
- Cookies are accepted only for the domains `*.zalo.me` and `*.zaloapp.com`. Do not call or send data to any other host, and do not add telemetry.
- **Tests must never touch the live server.** Only `Luna.Playground` does that, run by a human, with a secondary account.

## Protocol

Read `docs/PROTOCOL.md` before changing `Luna.Protocol`. Order of trust: (1) behavior verified on the live server, (2) the zca-js 2.2.0 source (`npm pack zca-js@2.2.0`), (3) the `ctdoteam1110/ctdozlite` repo (Rust, open source). Do not guess field names or endpoints: open the source and read it. Anything not yet verified must be stated clearly in a comment and in `PROTOCOL.md` as "not verified on the live server". Never say you tried it for real if you have no way to run it.

## Way of working

- Do the first unfinished task in `docs/ROADMAP.md`. One branch or one series of small commits per task, in the style `feat(protocol): ...`, `fix(core): ...`, `test: ...`, `docs: ...`.
- `main` is protected: every change goes through a Pull Request and CI (`build-test`). The agent does not run `git push` or `git merge` itself; the maintainer pushes the branch and opens the PR.
- **Done when:** the build has 0 warnings, all tests pass, `PROTOCOL.md` is updated if there is new protocol knowledge, the task in `ROADMAP.md` is marked, and the diff contains no secrets.
- Do not refactor outside the task scope. If the public API of `Protocol`/`Core` changes, say so in the report.
- **Stop and ask a human** before: adding a NuGet package, changing the session file format or encryption, changing the frequency or manner of server calls in a way that affects account safety, touching `.gitignore` or the security rules, calling an endpoint not yet in `PROTOCOL.md`.

## Review handoff

At the end of a task, report only: test counts, the list of commits, assumptions not verified on the live server, and open questions. Luca sends the diff to the reviewer (Claude on claude.ai); do not generate a review file.

## Reference files

Download the reference source code (zca-js, ctdozlite) into a folder OUTSIDE the repo, for example $env:TEMP\luna-refs. Do not leave downloaded files in the project folder.

## Lessons learned (apply when writing code that reads data from the server)

- `JsonElement.GetString()` throws `InvalidOperationException` for strings with a lone surrogate (a name cut in the middle of an emoji). Every place that reads a string from the server must catch this and degrade to the field level (that field becomes null), except ids: a corrupt id drops the whole element. The router and the client never throw because of unexpected data.
- A missing `error_code` field must not be treated as success: throw `InvalidDataException`. When an error carries a server code, keep that code even if `error_message` is corrupt.
- Every host taken from a server response must be validated (https, DNS name under `zalo.me` or `zaloapp.com`, default port, no path) and the URL must be built from the normalized form, before anything is sent.
- Pagination does not stop just because a page is shorter than `pageSize` (the `count` ceiling is unknown); stop when a page is empty, there are no new `userId` values, or the page limit is reached. An error midway propagates; do not return a partial result.
- Every time-based wait must be clamped to an upper bound (the system clock can be set backwards), and every network call (including retries or failover) goes through the rate limiter.
- Bug fixes: write the test FIRST and confirm it is red, then fix. Secret tests: the fake value must BE in the input data before asserting that it is absent from the output.
- Never call `ToString()` or log any record that holds a uid, name, phone number or content; override `ToString()` to print only the kind and flags.
- Each live run makes only a few requests a few seconds apart; after any 429, wait at least one hour and do not retry; the API client must go into cooldown by itself after a 429 and send nothing during that time.
- The friend list is fetched with one large count request at startup or when the user presses refresh, with no pagination; each heavy endpoint has its own minimum interval and is never called in a loop.
- Sending a message is a write operation: no failover, no retry, no loop, one message per run, and only to the secondary account's own `My Documents` or to a recipient named by the owner; an ambiguous error (timeout, network error, 5xx) means it is not known whether the message arrived, so never resend automatically.
- Tests must not block thread-pool threads while they wait for each other (`Barrier`, `ManualResetEvent`, `CountdownEvent`): the CI runner has 2 cores and the pool grows slowly, so such a test times out there while passing on a many-core machine. Start the callers on dedicated threads (`Task.Factory.StartNew` with `TaskCreationOptions.LongRunning`) or coordinate asynchronously, and check every concurrency test with `DOTNET_PROCESSOR_COUNT=2`.
