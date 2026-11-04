# Language check: production code and commit messages must be ASCII; test projects are
# checked for comments only. Tracked documentation (*.md, .github/**/*.yml,
# .github/CODEOWNERS) must contain no Vietnamese letters outside fenced code blocks and
# inline code spans. Prints file:line (never the offending text); exit 1 on any
# violation.
#
#   -Commits <range>   also require every commit message in the range to be ASCII
#   -SummaryOnly       print per-project counts instead of every file:line
#   -SelfTest          run the internal cases and exit
#

[CmdletBinding()]
param(
    [string]$Commits,
    [switch]$SummaryOnly,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/CSharpComments.ps1')

function Test-HasNonAscii {
    param([string]$S)
    return [regex]::IsMatch($S, '[^\x00-\x7F]')
}

# Returns objects with Line and Kind ('comment' or 'code') for lines with non-ASCII
# characters. With CommentsOnly, only lines inside comments are returned.
# Throws if the tokenizer reports an unterminated construct.
function Get-NonAsciiViolations {
    param([string]$Text, [bool]$CommentsOnly)
    $found = New-Object System.Collections.Generic.List[object]
    $commentLines = New-Object 'System.Collections.Generic.HashSet[int]'
    $comments = Get-CSharpComments -Text $Text
    foreach ($c in $comments) {
        $parts = $c.Text.Split([char]10)
        for ($k = 0; $k -lt $parts.Length; $k++) {
            if (Test-HasNonAscii $parts[$k]) { [void]$commentLines.Add($c.Line + $k) }
        }
    }
    if ($CommentsOnly) {
        foreach ($ln in ($commentLines | Sort-Object)) {
            $found.Add([pscustomobject]@{ Line = $ln; Kind = 'comment' })
        }
        return , $found.ToArray()
    }
    $lines = $Text.Split([char]10)
    for ($k = 0; $k -lt $lines.Length; $k++) {
        $s = $lines[$k]
        if ($k -eq 0 -and $s.Length -gt 0 -and $s[0] -eq [char]0xFEFF) { $s = $s.Substring(1) }
        if (Test-HasNonAscii $s) {
            $kind = 'code'
            if ($commentLines.Contains($k + 1)) { $kind = 'comment' }
            $found.Add([pscustomobject]@{ Line = $k + 1; Kind = $kind })
        }
    }
    return , $found.ToArray()
}

# Non-C# files (scripts): any non-ASCII character is a violation. No tokenizer.
function Get-NonAsciiLinesPlain {
    param([string]$Text)
    $found = New-Object System.Collections.Generic.List[object]
    $lines = $Text.Split([char]10)
    for ($k = 0; $k -lt $lines.Length; $k++) {
        $s = $lines[$k]
        if ($k -eq 0 -and $s.Length -gt 0 -and $s[0] -eq [char]0xFEFF) { $s = $s.Substring(1) }
        if (Test-HasNonAscii $s) { $found.Add([pscustomobject]@{ Line = $k + 1; Kind = 'code' }) }
    }
    return , $found.ToArray()
}

# Vietnamese letters with diacritics plus d with stroke, upper and lower case, built from
# code point ranges so this script stays ASCII.
$script:VietnameseLetters = & {
    $ranges = @(
        @(0xC0, 0xC3), @(0xC8, 0xCA), @(0xCC, 0xCD), @(0xD2, 0xD5), @(0xD9, 0xDA), @(0xDD, 0xDD),
        @(0xE0, 0xE3), @(0xE8, 0xEA), @(0xEC, 0xED), @(0xF2, 0xF5), @(0xF9, 0xFA), @(0xFD, 0xFD),
        @(0x102, 0x103), @(0x110, 0x111), @(0x128, 0x129), @(0x168, 0x169), @(0x1A0, 0x1A1),
        @(0x1AF, 0x1B0), @(0x1EA0, 0x1EF9)
    )
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('[')
    foreach ($p in $ranges) {
        [void]$sb.Append([char]$p[0])
        if ($p[1] -ne $p[0]) { [void]$sb.Append('-'); [void]$sb.Append([char]$p[1]) }
    }
    [void]$sb.Append(']')
    $sb.ToString()
}

# Returns the 1-based line numbers of a documentation file that contain a Vietnamese
# letter outside fenced code blocks and inline code spans.
function Get-VietnameseDocLines {
    param([string]$Text)
    $found = New-Object System.Collections.Generic.List[int]
    $lines = $Text.Normalize([System.Text.NormalizationForm]::FormC).Split([char]10)
    $fenceChar = ''
    $fenceLen = 0
    for ($k = 0; $k -lt $lines.Length; $k++) {
        $s = $lines[$k].TrimEnd([char]13)
        if ($fenceChar -ne '') {
            $close = [regex]::Match($s, '^\s*(`{3,}|~{3,})\s*$')
            if ($close.Success -and $close.Groups[1].Value[0] -eq $fenceChar -and $close.Groups[1].Value.Length -ge $fenceLen) {
                $fenceChar = ''
            }
            continue
        }
        $open = [regex]::Match($s, '^\s*(`{3,}|~{3,})')
        if ($open.Success) {
            $fenceChar = $open.Groups[1].Value[0]
            $fenceLen = $open.Groups[1].Value.Length
            continue
        }
        $stripped = [regex]::Replace($s, '(`+)(?!`).+?(?<!`)\1(?!`)', '')
        if ([regex]::IsMatch($stripped, $script:VietnameseLetters)) { $found.Add($k + 1) }
    }
    return , $found.ToArray()
}

# Old brand name rule. The forbidden word is assembled from pieces so that this script
# never contains it literally and therefore passes its own check. Exceptions are exact:
#   E1 host strings (the two service domains), any file
#   E2 the server JSON key, only in the files listed in $script:BrandKeyFiles
#   E3 one disclaimer sentence, only in README.md
#   E4 one deny-rule line, only in .claude/settings.json
$script:Brand = 'za' + 'lo'
$script:BrandCap = 'Za' + 'lo'
$script:BrandKey = $script:Brand + 'Name'
$script:BrandKeyFiles = @(
    'Luna.Protocol/Api/FriendParser.cs',
    'Luna.Protocol.Tests/Api/FriendParserTests.cs',
    'docs/PROTOCOL.md'
)
$script:BrandE3Sentence = 'Luna is not affiliated with or endorsed by VNG or ' + $script:BrandCap + '.'
$script:BrandE4File = '.claude/settings.json'
$script:BrandE4Line = '"Read(~/AppData/Local/' + $script:BrandCap + 'Native/**)",'

# Returns the 1-based line numbers of a tracked text file that contain the old brand name
# (case-insensitive) outside the exact exceptions above.
function Get-BrandLines {
    param([string]$Rel, [string]$Text)
    $found = New-Object System.Collections.Generic.List[int]
    $lines = $Text.Split([char]10)
    $ci = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    for ($k = 0; $k -lt $lines.Length; $k++) {
        $s = $lines[$k].TrimEnd([char]13)
        if (-not [regex]::IsMatch($s, $script:Brand, $ci)) { continue }
        if ($Rel -ceq $script:BrandE4File -and $s.Trim() -ceq $script:BrandE4Line) { continue }
        $t = [regex]::Replace($s, $script:Brand + 'app\.com(?![A-Za-z0-9])', '', $ci)
        $t = [regex]::Replace($t, $script:Brand + '\.me(?![A-Za-z0-9])', '', $ci)
        if ($script:BrandKeyFiles -ccontains $Rel) { $t = $t.Replace($script:BrandKey, '') }
        if ($Rel -ceq 'README.md') { $t = $t.Replace($script:BrandE3Sentence, '') }
        if ([regex]::IsMatch($t, $script:Brand, $ci)) { $found.Add($k + 1) }
    }
    return , $found.ToArray()
}

if ($SelfTest) {
    $e = [string][char]0xE9
    $bom = [string][char]0xFEFF
    $cases = New-Object System.Collections.Generic.List[object]
    function Add-Case {
        param([string]$Name, [string]$Text, [bool]$CommentsOnly, [int[]]$ExpectedLines, [string]$ExpectedKind = '')
        $cases.Add([pscustomobject]@{ Name = $Name; Text = $Text.Replace('~E~', $e); CommentsOnly = $CommentsOnly; Lines = $ExpectedLines; Kind = $ExpectedKind })
    }
    Add-Case 'line comment' "// ~E~`nint a;" $true @(1)
    Add-Case 'trailing comment' "int a; // ~E~" $true @(1)
    Add-Case 'doc comment' "/// <summary>~E~</summary>`nint a;" $true @(1)
    Add-Case 'multi-line block comment' "/*`n ~E~`n*/`nint a;" $true @(2)
    Add-Case 'non-ASCII in string is not a comment' 'var s = "~E~";' $true @()
    Add-Case 'double slash in URL string' 'var u = "http://x.test/~E~";' $true @()
    Add-Case 'verbatim string' 'var v = @"a ""// ~E~"" b";' $true @()
    Add-Case 'interpolated string' 'var s = $"{a} // ~E~ {b}";' $true @()
    Add-Case 'raw string' "var r = `"`"`"`n// ~E~`n`"`"`";" $true @()
    Add-Case 'char literal double quote' "var c = '`"'; // ~E~" $true @(1)
    Add-Case 'BOM tolerated' ($bom + "// ok`nint a;") $false @()
    Add-Case 'production mode flags comment' "int a; // ~E~" $false @(1) 'comment'
    Add-Case 'production mode flags string' 'var s = "~E~";' $false @(1) 'code'
    Add-Case 'BOM plus non-ASCII elsewhere' ($bom + "int a;`nint b; // ~E~") $false @(2) 'comment'
    Add-Case 'escaped quote then comment' 'var s = "a\"b"; // ~E~' $true @(1)

    $failed = 0
    foreach ($case in $cases) {
        $got = Get-NonAsciiViolations -Text $case.Text -CommentsOnly $case.CommentsOnly
        $gotLines = @($got | ForEach-Object { $_.Line })
        $ok = ($gotLines.Count -eq $case.Lines.Count)
        if ($ok) {
            for ($k = 0; $k -lt $gotLines.Count; $k++) { if ($gotLines[$k] -ne $case.Lines[$k]) { $ok = $false } }
        }
        if ($ok -and $case.Kind -ne '' -and $got.Count -gt 0 -and $got[0].Kind -ne $case.Kind) { $ok = $false }
        if (-not $ok) { Write-Host ("[self-test] FAILED: " + $case.Name); $failed++ }
    }
    foreach ($bad in @(@('unterminated block comment', "int a; /* x"), @('unterminated string', 'var s = "abc;'), @('unterminated raw string', "var s = `"`"`"abc"))) {
        $threw = $false
        try { [void](Get-CSharpComments -Text $bad[1]) } catch { $threw = $true }
        if (-not $threw) { Write-Host ("[self-test] FAILED: " + $bad[0]); $failed++ }
    }
    # Documentation rule. ~A~ = U+1EA1 (a with dot below), ~D~ = U+0111 (d with stroke),
    # ~M~ = U+2014 (long dash). The fence lines use three backticks or three tildes.
    $fence = '```'
    $docCases = New-Object System.Collections.Generic.List[object]
    function Add-DocCase {
        param([string]$Name, [string]$Text, [int[]]$ExpectedLines)
        $t = $Text.Replace('~A~', [string][char]0x1EA1).Replace('~D~', [string][char]0x111).Replace('~M~', [string][char]0x2014)
        $docCases.Add([pscustomobject]@{ Name = $Name; Text = $t; Lines = $ExpectedLines })
    }
    Add-DocCase 'vietnamese sentence flagged' "Intro`nXin ch~A~o c~A~c b~A~n" @(2)
    Add-DocCase 'vietnamese inside backticks allowed' 'The label `Cloud c~A~a t~D~i` is fine.' @()
    Add-DocCase 'vietnamese inside fenced block allowed' "$fence`n~A~ line`n$fence" @()
    Add-DocCase 'text after a fenced block is checked' "$fence`n~A~`n$fence`n~A~" @(4)
    Add-DocCase 'tilde fence allowed' "~~~text`n~A~`n~~~" @()
    Add-DocCase 'english with a long dash allowed' 'Plain text ~M~ with a long dash.' @()
    Add-DocCase 'heading flagged' "# Ti~D~u ~D~~A~" @(1)
    Add-DocCase 'table cell flagged' "| a | b |`n|---|---|`n| x | ~A~ |" @(3)
    Add-DocCase 'd with stroke flagged' 'only ~D~ here' @(1)
    Add-DocCase 'decomposed letter flagged' ("a" + [string][char]0x323 + " tail") @(1)
    Add-DocCase 'double backtick span allowed' 'Use ``a ` ~A~`` here.' @()
    Add-DocCase 'text outside a span on the same line flagged' 'x `ok` ~A~' @(1)
    foreach ($case in $docCases) {
        $got = Get-VietnameseDocLines -Text $case.Text
        $ok = ($got.Count -eq $case.Lines.Count)
        if ($ok) {
            for ($k = 0; $k -lt $got.Count; $k++) { if ($got[$k] -ne $case.Lines[$k]) { $ok = $false } }
        }
        if (-not $ok) { Write-Host ("[self-test] FAILED: " + $case.Name); $failed++ }
    }

    # Old brand name rule. $b = capitalized word, $l = lowercase word (both built from pieces).
    $b = $script:BrandCap
    $l = $script:Brand
    $brandCases = New-Object System.Collections.Generic.List[object]
    function Add-BrandCase {
        param([string]$Name, [string]$Rel, [string]$Text, [int[]]$ExpectedLines)
        $brandCases.Add([pscustomobject]@{ Name = $Name; Rel = $Rel; Text = $Text; Lines = $ExpectedLines })
    }
    $e4 = '      "Read(~/AppData/Local/' + $b + 'Native/**)",'
    Add-BrandCase 'brand word flagged' 'docs/x.md' "We use $b here" @(1)
    Add-BrandCase 'brand word flagged in upper case' 'docs/x.md' ("USE " + $b.ToUpperInvariant()) @(1)
    Add-BrandCase 'brand inside an identifier flagged' 'Luna.Core/A.cs' "var x = ${b}Native;" @(1)
    Add-BrandCase 'E1 host me allowed' 'docs/x.md' "https://chat.$l.me/path" @()
    Add-BrandCase 'E1 host app.com allowed' 'Luna.Core/A.cs' "var h = `"id.${l}app.com`";" @()
    Add-BrandCase 'E1 lookalike namespace flagged' 'Luna.Core/A.cs' "using $b.Core;" @(1)
    Add-BrandCase 'E1 lookalike longer host flagged' 'docs/x.md' "see $l.mexico" @(1)
    Add-BrandCase 'E1 host plus the word on the same line flagged' 'docs/x.md' "chat.$l.me and $b" @(1)
    Add-BrandCase 'E2 key allowed in the friend parser' 'Luna.Protocol/Api/FriendParser.cs' "Text(element, `"${l}Name`")," @()
    Add-BrandCase 'E2 key flagged in another file' 'Luna.Core/Other.cs' "Text(element, `"${l}Name`")," @(1)
    Add-BrandCase 'E3 sentence allowed in README' 'README.md' $script:BrandE3Sentence @()
    Add-BrandCase 'E3 sentence flagged in another file' 'AGENTS.md' $script:BrandE3Sentence @(1)
    Add-BrandCase 'E3 altered sentence flagged in README' 'README.md' "Luna is not affiliated with or endorsed by $b." @(1)
    Add-BrandCase 'E3 sentence plus the word flagged in README' 'README.md' ($script:BrandE3Sentence + " See $b.") @(1)
    Add-BrandCase 'E4 line allowed in settings' '.claude/settings.json' $e4 @()
    Add-BrandCase 'E4 line flagged in another file' '.gitignore' $e4 @(1)
    Add-BrandCase 'E4 other line flagged in settings' '.claude/settings.json' ('      "Read(~/AppData/Local/' + $b + 'Other/**)",') @(1)
    Add-BrandCase 'clean text not flagged' 'docs/x.md' "Luna is a native client. Lunar zeal and a halo of light." @()
    Add-BrandCase 'line numbers are reported' 'docs/x.md' "ok`nuses $b`nok" @(2)
    Add-BrandCase 'CRLF line endings' 'docs/x.md' "a`r`n$b`r`nb`r`n" @(2)
    foreach ($case in $brandCases) {
        $got = Get-BrandLines -Rel $case.Rel -Text $case.Text
        $ok = ($got.Count -eq $case.Lines.Count)
        if ($ok) {
            for ($k = 0; $k -lt $got.Count; $k++) { if ($got[$k] -ne $case.Lines[$k]) { $ok = $false } }
        }
        if (-not $ok) { Write-Host ("[self-test] FAILED: " + $case.Name); $failed++ }
    }

    if ($failed -gt 0) { Write-Host ("[self-test] check-language: {0} failed" -f $failed); exit 1 }
    Write-Host ("[self-test] check-language: {0} cases OK" -f ($cases.Count + 3 + $docCases.Count + $brandCases.Count))
    exit 0
}

$root = Split-Path -Parent $PSScriptRoot
$total = 0
$summary = [ordered]@{}

function Add-Violations {
    param([string]$Project, [string]$Rel, $Items)
    $script:total += $Items.Count
    if (-not $script:summary.Contains($Project)) {
        $script:summary[$Project] = [pscustomobject]@{ Lines = 0; Files = 0; Comment = 0; Code = 0 }
    }
    $s = $script:summary[$Project]
    $s.Files++
    foreach ($it in $Items) {
        $s.Lines++
        if ($it.Kind -eq 'comment') { $s.Comment++ } else { $s.Code++ }
        if (-not $SummaryOnly) { Write-Host ("{0}:{1}" -f $Rel, $it.Line) }
    }
}

function Read-Utf8 {
    param([string]$Path)
    return [System.IO.File]::ReadAllText($Path, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-RelPath {
    param([string]$Full)
    return $Full.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
}

$productionProjects = @('Luna.Protocol', 'Luna.Core', 'Luna.Playground', 'Luna.App')
$projectDirs = Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -like 'Luna.*' }
foreach ($dir in $projectDirs) {
    $isTest = $dir.Name.EndsWith('.Tests')
    if (-not $isTest -and ($productionProjects -notcontains $dir.Name)) { continue }
    $files = Get-ChildItem -LiteralPath $dir.FullName -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    foreach ($f in $files) {
        $rel = Get-RelPath $f.FullName
        $text = Read-Utf8 $f.FullName
        try {
            $items = Get-NonAsciiViolations -Text $text -CommentsOnly $isTest
        }
        catch {
            Write-Host ("{0}:1" -f $rel)
            Write-Host "  tokenizer error (unterminated string or comment)"
            $total++
            continue
        }
        if ($items.Count -gt 0) { Add-Violations $dir.Name $rel $items }
    }
}

$scriptFiles = Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Recurse -File -Filter '*.ps1'
foreach ($f in $scriptFiles) {
    $items = Get-NonAsciiLinesPlain (Read-Utf8 $f.FullName)
    if ($items.Count -gt 0) { Add-Violations 'scripts' (Get-RelPath $f.FullName) $items }
}

$tracked = @(& git -C $root ls-files)
if ($LASTEXITCODE -ne 0) { Write-Host "[language] cannot list tracked files"; exit 1 }
foreach ($rel in $tracked) {
    $isDoc = ($rel -match '\.md$') -or ($rel -match '^\.github/.+\.ya?ml$') -or ($rel -eq '.github/CODEOWNERS')
    if (-not $isDoc) { continue }
    $full = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
    $nums = Get-VietnameseDocLines (Read-Utf8 $full)
    if ($nums.Count -gt 0) {
        $items = @($nums | ForEach-Object { [pscustomobject]@{ Line = $_; Kind = 'code' } })
        Add-Violations 'docs' $rel $items
    }
}

$binaryExt = @('.png', '.jpg', '.jpeg', '.gif', '.ico', '.bin', '.dll', '.exe', '.pdf', '.zip')
foreach ($rel in $tracked) {
    if ($binaryExt -contains [System.IO.Path]::GetExtension($rel).ToLowerInvariant()) { continue }
    $full = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
    $nums = Get-BrandLines $rel (Read-Utf8 $full)
    if ($nums.Count -gt 0) {
        $items = @($nums | ForEach-Object { [pscustomobject]@{ Line = $_; Kind = 'code' } })
        Add-Violations 'old-brand' $rel $items
    }
}

if ($Commits) {
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    $hashes = @(& git -C $root rev-list $Commits)
    if ($LASTEXITCODE -ne 0) { Write-Host "[language] cannot list commits for the given range"; exit 1 }
    foreach ($h in $hashes) {
        $msgLines = @(& git -C $root log -1 --format=%B $h)
        for ($k = 0; $k -lt $msgLines.Count; $k++) {
            if (Test-HasNonAscii ([string]$msgLines[$k])) {
                $items = @([pscustomobject]@{ Line = $k + 1; Kind = 'code' })
                Add-Violations 'commits' ("commit " + $h.Substring(0, 8)) $items
            }
        }
    }
}

foreach ($name in $summary.Keys) {
    $s = $summary[$name]
    Write-Host ("[language] {0}: {1} line(s) in {2} file(s) (comment {3}, other {4})" -f $name, $s.Lines, $s.Files, $s.Comment, $s.Code)
}
if ($total -gt 0) {
    Write-Host ("[language] FAILED: {0} violation(s)" -f $total)
    exit 1
}
Write-Host "[language] OK"
exit 0
