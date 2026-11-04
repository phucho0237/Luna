---
name: start-task
description: Start a task from the ROADMAP, read only what is needed, then make a plan.
disable-model-invocation: true
---
Task: $ARGUMENTS
1. Read only the task section: `pwsh -NoProfile -File scripts/doc-section.ps1 docs/ROADMAP.md "$ARGUMENTS"`. Read further only the relevant sections of docs/PROTOCOL.md with scripts/doc-section.ps1, not the whole file.
2. Run `git branch --show-current`: it must be the task branch, not main. If it is not, stop and tell Luca.
3. Run `pwsh -NoProfile -File scripts/check.ps1` to record the current test counts as a baseline (test counts may only go up from this baseline).
4. Summarize the plan in at most 15 lines, do not use a subagent (Explore), read code with targeted Read or Grep, and wait for Luca's approval before coding. Look up zca-js/ctdozlite with scripts/ref-grep.ps1.
