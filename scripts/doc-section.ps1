param(
    [Parameter(Mandatory, Position = 0)][string]$File,
    [Parameter(Mandatory, Position = 1)][string]$Heading,
    [int]$MaxLines = 80
)

[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root $File
if (-not (Test-Path $path)) { Write-Error "Not found: $File"; exit 1 }

$lines = Get-Content $path -Encoding utf8
$headings = @()
$inFence = $false
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*```') { $inFence = -not $inFence; continue }
    if (-not $inFence -and $lines[$i] -match '^(#{1,6})\s+(.*)$') {
        $headings += [pscustomobject]@{ Index = $i; Level = $Matches[1].Length; Title = $Matches[2] }
    }
}

$hit = $headings | Where-Object { $_.Title -match $Heading } | Select-Object -First 1
if (-not $hit) { Write-Error "No heading matching '$Heading' found in $File"; exit 1 }

$next = $headings | Where-Object { $_.Index -gt $hit.Index -and $_.Level -le $hit.Level } | Select-Object -First 1
$end = if ($next) { $next.Index } else { $lines.Count }
$lines[$hit.Index..($end - 1)] | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First $MaxLines
