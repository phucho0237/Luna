# Luna

**Luna** is an **unofficial** native Windows client for a Vietnamese messaging service, written in C# and WinUI 3 (.NET 10), aiming to be lighter and smoother than the official Electron app.

> **Read before use.** This is a personal project. Luna is not affiliated with or endorsed by VNG or Zalo. An unofficial client may violate the service's terms of use, and **your account may be restricted or locked**. Try it only with a secondary account. The project is under development and **not ready for end users**.

## Status

Working (verified with a secondary account): parameter encryption and request signing, QR code login, DPAPI-encrypted session storage and re-login without scanning, a WebSocket connection that receives real-time events, message parsing (text, sticker, image, file, quote, mention, recall), routing of socket events into typed events (incoming and recalled messages, delivery receipt, seen receipt, reaction, typing notice, friend request, pin, mute; group events are still unparsed), a diagnostic tool that shows event shapes without leaking data, the post-login API call framework (parameter encryption, two-layer response decryption, rate limiting, automatic cooldown after a 429 error), fetching the friend list with a single request, and sending one text message to "My Documents" (formerly `Cloud của tôi`) of the secondary account.

In progress: sending to groups, quoting, recall, and parsing of group events. Not yet available: history, the WinUI 3 interface, notifications, voice calls, a release. The roadmap is in [`docs/ROADMAP.md`](docs/ROADMAP.md).

## Structure

| Project | Role |
|---|---|
| `Luna.Protocol` | Encryption, signing, HTTP, QR login, WebSocket. No UI. |
| `Luna.Core` | Session storage, authentication service. |
| `Luna.App` | WinUI 3 interface (nothing significant yet). |
| `Luna.Protocol.Tests`, `Luna.Core.Tests` | xUnit tests. |
| `Luna.Playground` | Console for developers to try things with a secondary account. Not released. |

The service protocol has no official documentation. [`docs/PROTOCOL.md`](docs/PROTOCOL.md) is based on community open source code and on observations made with a secondary account, and it may change at any time.

## Build and test

Requires Windows 10/11, the .NET 10 SDK and PowerShell 7. Building `Luna.App` also needs Visual Studio 2026 with the WinUI workload.

```powershell
dotnet test .\Luna.Protocol.Tests\Luna.Protocol.Tests.csproj
dotnet test .\Luna.Core.Tests\Luna.Core.Tests.csproj
pwsh -NoProfile -File scripts\check.ps1
```

## Privacy and security

- No telemetry. The code in this repository only talks to the service's own servers.
- The login session is stored encrypted with DPAPI, bound to the Windows account, at `%LOCALAPPDATA%\Luna\session.bin`. DPAPI does **not** protect against malware running under the same Windows account.
- Automated tests never connect to the live server.
- To report a vulnerability, see [`SECURITY.md`](SECURITY.md).

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) and [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md). Rules for AI coding assistants are in [`AGENTS.md`](AGENTS.md).

## License and credits

[MIT](LICENSE). The protocol part is rewritten in C# based on the behavior and source code of [zca-js](https://github.com/RFS-ADRENO/zca-js) and [ctdozlite](https://github.com/ctdoteam1110/ctdozlite). Details are in [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
