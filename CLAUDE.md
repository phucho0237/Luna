@AGENTS.md

## Claude Code
- Work on the task branch; never run `git push` or `git merge` yourself, and do not touch the main branch.
- Use plan mode (Alt+M on Windows) before any task that touches the protocol, security or the public API; agree the plan with Luca, then code.
- Report real results; never claim to have tried the live server.
- Commit messages, code comments, log and exception messages, and docs are English (ASCII for commits and comments). See the "Language" section in AGENTS.md.

## Saving tokens (mandatory)
- Check with `/check` or `pwsh -NoProfile -File scripts/check.ps1` (with `-Only protocol|core|playground|language`). Do not run raw `dotnet test` and read the whole output.
- Look up zca-js/ctdozlite: `pwsh -NoProfile -File scripts/ref-grep.ps1 "<regex>" [-In zca|ctdozlite]`. Do not run `npm pack` or clone yourself. If it reports them missing, ask Luca to run `scripts/refs.ps1`.
- Read only the section you need: `pwsh -NoProfile -File scripts/doc-section.ps1 docs/PROTOCOL.md "<heading>"` instead of the whole file.
- Start a task with `/start-task <code>`.
- Do not use a subagent (Explore) just to read a few files; use targeted Grep/Read. One task per session.
