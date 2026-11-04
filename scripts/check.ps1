param([ValidateSet('all', 'protocol', 'core', 'playground', 'language')][string]$Only = 'all')

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new()
$script:failed = $false

function Invoke-Step([string]$Name, [string[]]$DotnetArgs, [bool]$IsTest) {
    $raw = & dotnet @DotnetArgs 2>&1 | Out-String
    $exit = $LASTEXITCODE
    $lines = $raw -split "`r?`n"

    $warnings = @($lines | Where-Object { $_ -match '\bwarning\s+[A-Za-z]+\d+' } | ForEach-Object { $_.Trim() } | Sort-Object -Unique)
    $errors = @($lines | Where-Object { $_ -match '\berror\s+[A-Za-z]+\d+' } | ForEach-Object { $_.Trim() } | Sort-Object -Unique)
    $ok = ($exit -eq 0) -and ($errors.Count -eq 0)

    $m = $null
    if ($IsTest) {
        foreach ($l in $lines) {
            if ($l -match '^(Passed|Failed)!\s+- Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)') { $m = $Matches }
        }
        if ($m -and $m[1] -ne 'Passed') { $ok = $false }
    }

    if ($m) {
        $state = if ($ok) { 'OK' } else { 'FAILED' }
        "[$Name] $state passed=$($m[3]) failed=$($m[2]) skipped=$($m[4]) total=$($m[5]) warnings=$($warnings.Count)"
    }
    else {
        $state = if ($ok) { 'OK' } else { 'FAILED' }
        "[$Name] $state warnings=$($warnings.Count)"
        if (-not $ok -and $errors.Count -eq 0) {
            "  (could not read the summary, last 15 lines:)"
            $lines | Where-Object { $_.Trim() } | Select-Object -Last 15 | ForEach-Object { "  $_" }
        }
    }

    if (-not $ok) { $script:failed = $true }

    $failedTests = @($lines | Where-Object { $_ -match '^\s+Failed\s+\S+' } | ForEach-Object { $_.Trim() } | Select-Object -Unique -First 10)
    foreach ($t in $failedTests) { "  $t" }

    $shown = 0
    for ($i = 0; $i -lt $lines.Count -and $shown -lt 10; $i++) {
        if ($lines[$i] -match '^\s+Error Message:' -and ($i + 1) -lt $lines.Count) {
            "    " + $lines[$i + 1].Trim()
            $shown++
        }
    }

    foreach ($e in ($errors | Select-Object -First 10)) { "  $e" }
    foreach ($w in ($warnings | Select-Object -First 5)) { "  $w" }
}

$steps = @(
    @{ Key = 'protocol'; Args = @('test', '.\Luna.Protocol.Tests\Luna.Protocol.Tests.csproj', '--nologo'); Test = $true },
    @{ Key = 'core'; Args = @('test', '.\Luna.Core.Tests\Luna.Core.Tests.csproj', '--nologo'); Test = $true },
    @{ Key = 'playground'; Args = @('build', '.\Luna.Playground\Luna.Playground.csproj', '--nologo'); Test = $false }
)

foreach ($s in $steps) {
    if ($Only -eq 'all' -or $Only -eq $s.Key) { Invoke-Step $s.Key $s.Args $s.Test }
}

if ($Only -eq 'all' -or $Only -eq 'language') {
    $langOut = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'check-language.ps1') 2>&1)
    if ($LASTEXITCODE -eq 0) {
        "[language] OK"
    }
    else {
        "[language] FAILED"
        $langOut | Select-Object -First 10 | ForEach-Object { "  $_" }
        $script:failed = $true
    }
}

if ($script:failed) { exit 1 } else { exit 0 }
