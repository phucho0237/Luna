---
name: check
description: Run the compact check (tests for both projects and the Playground build) and print only a summary.
disable-model-invocation: true
---
Run `pwsh -NoProfile -File scripts/check.ps1` (add `-Only protocol|core|playground|language` if only one part is needed).
Report the summary lines exactly. Do not run raw `dotnet test` and read the whole output. Only when the script prints FAILED, look at the failure details it already printed.
