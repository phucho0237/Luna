# Verifies that a range of changes touched comments only: for every .cs file changed
# between -Base and HEAD, the code with comments removed (whitespace-normalized) must be
# identical in the old and new versions. Prints the file path of each mismatch; exit 1
# on any mismatch or tokenizer error.
#
#   -Base <ref>   the commit to compare against
#   -SelfTest     run the internal cases and exit

[CmdletBinding()]
param(
    [string]$Base,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/CSharpComments.ps1')

function Get-NormalizedCode {
    param([string]$Text)
    $code = Remove-CSharpComments -Text $Text
    return ([regex]::Replace($code, '\s+', ' ')).Trim()
}

function Test-SameCode {
    param([string]$Old, [string]$New)
    return (Get-NormalizedCode $Old) -ceq (Get-NormalizedCode $New)
}

if ($SelfTest) {
    $e = [string][char]0xE9
    $bom = [string][char]0xFEFF
    $cases = @(
        @{ Name = 'line comment'; Old = "int a; // one`nint b;"; New = "int a; // two`nint b;"; Same = $true },
        @{ Name = 'comment added on its own line'; Old = "int a;`nint b;"; New = "// note`nint a;`nint b;"; Same = $true },
        @{ Name = 'trailing comment'; Old = 'int a = 1; // x'; New = 'int a = 1; // y z'; Same = $true },
        @{ Name = 'doc comment'; Old = "/// <summary>a</summary>`nint a;"; New = "/// <summary>b</summary>`nint a;"; Same = $true },
        @{ Name = 'block comment'; Old = "/* a`n b */`nint a;"; New = "/* c */`nint a;"; Same = $true },
        @{ Name = 'comment removed'; Old = "int a; /* x */ int b;"; New = "int a; int b;"; Same = $true },
        @{ Name = 'non-ASCII in comment only'; Old = "// x $e`nint a;"; New = "// x e`nint a;"; Same = $true },
        @{ Name = 'non-ASCII in string is code'; Old = "var s = `"$e`";"; New = 'var s = "e";'; Same = $false },
        @{ Name = 'double slash in URL string is code'; Old = 'var u = "http://a/b";'; New = 'var u = "http://a/c";'; Same = $false },
        @{ Name = 'verbatim string is code'; Old = 'var v = @"a ""// b"" c";'; New = 'var v = @"a ""// d"" c";'; Same = $false },
        @{ Name = 'interpolated string is code'; Old = 'var s = $"{a} // b";'; New = 'var s = $"{a} // c";'; Same = $false },
        @{ Name = 'raw string is code'; Old = "var r = `"`"`"`n// x`n`"`"`";"; New = "var r = `"`"`"`n// y`n`"`"`";"; Same = $false },
        @{ Name = 'char literal double quote then comment'; Old = "var c = '`"'; // a`nint b;"; New = "var c = '`"'; // b`nint b;"; Same = $true },
        @{ Name = 'BOM tolerated'; Old = ($bom + "int a; // x"); New = "int a; // y"; Same = $true },
        @{ Name = 'code differs'; Old = "int a = 1; // x"; New = "int a = 2; // x"; Same = $false },
        @{ Name = 'code differs, comment also changed'; Old = "int a; // x"; New = "int b; // y"; Same = $false },
        @{ Name = 'whitespace-only difference'; Old = "int  a;"; New = "int a;`n"; Same = $true }
    )
    $failed = 0
    foreach ($c in $cases) {
        if ((Test-SameCode $c.Old $c.New) -ne $c.Same) {
            Write-Host ("[self-test] FAILED: " + $c.Name)
            $failed++
        }
    }
    if ($failed -gt 0) { Write-Host ("[self-test] verify-comment-only: {0} failed" -f $failed); exit 1 }
    Write-Host ("[self-test] verify-comment-only: {0} cases OK" -f $cases.Count)
    exit 0
}

if (-not $Base) { Write-Host "verify-comment-only: -Base <ref> is required"; exit 2 }

$root = Split-Path -Parent $PSScriptRoot
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Get-BlobText {
    param([string]$Ref, [string]$Path)
    $spec = $Ref + ':' + $Path
    & git -C $root cat-file -e $spec 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $lines = @(& git -C $root show $spec)
    return ($lines -join "`n")
}

$names = @(& git -C $root diff --name-only --no-renames $Base HEAD -- '*.cs')
if ($LASTEXITCODE -ne 0) { Write-Host "verify-comment-only: git diff failed"; exit 2 }

$bad = 0
foreach ($path in $names) {
    $old = Get-BlobText $Base $path
    $new = Get-BlobText 'HEAD' $path
    if ($null -eq $old -or $null -eq $new) {
        Write-Host ("{0}: added or deleted" -f $path)
        $bad++
        continue
    }
    try {
        if (-not (Test-SameCode $old $new)) {
            Write-Host ("{0}: code differs" -f $path)
            $bad++
        }
    }
    catch {
        Write-Host ("{0}: tokenizer error" -f $path)
        $bad++
    }
}
if ($bad -gt 0) {
    Write-Host ("verify-comment-only: FAILED ({0} file(s))" -f $bad)
    exit 1
}
Write-Host ("verify-comment-only: OK ({0} file(s) compared)" -f $names.Count)
exit 0
