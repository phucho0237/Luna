param(
    [Parameter(Mandatory, Position = 0)][string]$Pattern,
    [ValidateSet('all', 'zca', 'ctdozlite')][string]$In = 'all',
    [int]$Context = 1,
    [int]$Max = 40
)

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$base = Join-Path $env:TEMP 'luna-refs'
if (-not (Test-Path $base)) {
    Write-Error "No reference source yet. Luca should run: pwsh -NoProfile -File scripts/refs.ps1"
    exit 1
}

$roots = @()
if ($In -in 'all', 'zca') { $roots += Join-Path $base 'package\dist' }
if ($In -in 'all', 'ctdozlite') { $roots += Join-Path $base 'ctdozlite\src'; $roots += Join-Path $base 'ctdozlite\docs' }
$roots = @($roots | Where-Object { Test-Path $_ })

$files = @(Get-ChildItem -Path $roots -Recurse -File -Include '*.js', '*.ts', '*.rs', '*.md' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\cjs\\' -and $_.Name -notmatch '\.d\.ts$' })
if ($files.Count -eq 0) { 'No reference files found.'; exit 1 }

function Cut([string]$s) {
    $t = $s.Trim()
    if ($t.Length -gt 200) { $t.Substring(0, 200) + '...' } else { $t }
}

$out = @(foreach ($h in ($files | Select-String -Pattern $Pattern -Context $Context)) {
    $rel = $h.Path.Substring($base.Length + 1)
    "-- ${rel}:$($h.LineNumber)"
    foreach ($l in $h.Context.PreContext) { "   $(Cut $l)" }
    "=> $(Cut $h.Line)"
    foreach ($l in $h.Context.PostContext) { "   $(Cut $l)" }
})

if ($out.Count -eq 0) { "No results for '$Pattern'."; exit 0 }
$out | Select-Object -First $Max
if ($out.Count -gt $Max) { "... $($out.Count - $Max) more line(s). Narrow the regex or use -In zca|ctdozlite." }
