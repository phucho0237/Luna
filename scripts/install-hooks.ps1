# Points git at the tracked hooks in .githooks (currently: commit-msg, which rejects
# non-ASCII commit messages). Run once per clone.

$root = Split-Path -Parent $PSScriptRoot
& git -C $root config core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) { Write-Error "git config failed"; exit 1 }
"Git hooks enabled: core.hooksPath = .githooks"
