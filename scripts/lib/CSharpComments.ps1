# C# comment tokenizer (state machine). Dot-source this file.
#
#   Get-CSharpComments    -Text <string>  -> comments with line numbers (Line, Kind, Text)
#   Remove-CSharpComments -Text <string>  -> the code with comments removed, strings kept
#
# Handles: //, ///, multi-line /* */, regular strings with escapes, verbatim @"..."
# (doubled quotes), interpolated $"..." and $@"..." (nested braces and nested strings in
# holes), raw strings """...""", and char literals such as '"' and '\''. A "//" inside a
# string (for example a URL) is not a comment. A leading U+FEFF is ignored.
#
# Both functions throw when the text ends while still inside a string, raw string,
# interpolated string or block comment. The message names the line only, never the text.

Set-StrictMode -Version Latest

# Returns the index after the closing quote of a regular string, or -1 if unterminated.
# Pos is the index of the opening quote.
function Skip-CSharpRegularString {
    param([string]$T, [int]$Pos)
    $n = $T.Length
    $i = $Pos + 1
    while ($i -lt $n) {
        $c = $T[$i]
        if ($c -eq '\') { $i += 2; continue }
        if ($c -eq '"') { return $i + 1 }
        if ($c -eq [char]10) { return -1 }
        $i++
    }
    return -1
}

# Returns the index after a valid char literal starting at Pos, or -1 if it is not one.
function Skip-CSharpChar {
    param([string]$T, [int]$Pos)
    $n = $T.Length
    $i = $Pos + 1
    if ($i -ge $n) { return -1 }
    if ($T[$i] -eq '\') {
        $i += 2
        $lim = [Math]::Min($n, $i + 8)
        while ($i -lt $lim) {
            if ($T[$i] -eq "'") { return $i + 1 }
            $i++
        }
        return -1
    }
    if ($T[$i] -eq "'" -or $T[$i] -eq [char]10) { return -1 }
    $i++
    if ([char]::IsHighSurrogate($T[$i - 1]) -and $i -lt $n) { $i++ }
    if ($i -lt $n -and $T[$i] -eq "'") { return $i + 1 }
    return -1
}

# Pos is just after the '{' that opens an interpolation hole. Returns the index after
# the matching '}', or -1.
function Skip-CSharpHole {
    param([string]$T, [int]$Pos)
    $n = $T.Length
    $depth = 1
    $i = $Pos
    while ($i -lt $n) {
        $c = $T[$i]
        if ($c -eq '{') { $depth++; $i++ }
        elseif ($c -eq '}') {
            $depth--
            $i++
            if ($depth -eq 0) { return $i }
        }
        elseif ($c -eq '"') {
            $e = Skip-CSharpRegularString $T $i
            if ($e -lt 0) { return -1 }
            $i = $e
        }
        elseif ($c -eq "'") {
            $e = Skip-CSharpChar $T $i
            if ($e -lt 0) { $i++ } else { $i = $e }
        }
        else { $i++ }
    }
    return -1
}

# Regular, verbatim and interpolated strings. Pos is the index of the opening quote.
# Returns the index after the closing quote, or -1 if unterminated.
function Skip-CSharpString {
    param([string]$T, [int]$Pos, [bool]$Verbatim, [bool]$Interp)
    $n = $T.Length
    $i = $Pos + 1
    while ($i -lt $n) {
        $c = $T[$i]
        if ($Verbatim) {
            if ($c -eq '"') {
                if ($i + 1 -lt $n -and $T[$i + 1] -eq '"') { $i += 2; continue }
                return $i + 1
            }
        }
        else {
            if ($c -eq '\') { $i += 2; continue }
            if ($c -eq '"') { return $i + 1 }
            if ($c -eq [char]10) { return -1 }
        }
        if ($Interp -and $c -eq '{') {
            if ($i + 1 -lt $n -and $T[$i + 1] -eq '{') { $i += 2; continue }
            $e = Skip-CSharpHole $T ($i + 1)
            if ($e -lt 0) { return -1 }
            $i = $e
            continue
        }
        $i++
    }
    return -1
}

# Raw string: Quotes is the length of the opening quote run. Returns the index after
# the closing run (at least Quotes quotes), or -1.
function Skip-CSharpRaw {
    param([string]$T, [int]$Pos, [int]$Quotes)
    $n = $T.Length
    $i = $Pos + $Quotes
    while ($i -lt $n) {
        $i = $T.IndexOf([char]34, $i)
        if ($i -lt 0) { return -1 }
        $j = $i
        while ($j -lt $n -and $T[$j] -eq '"') { $j++ }
        if (($j - $i) -ge $Quotes) { return $j }
        $i = $j
    }
    return -1
}

function Update-CSharpLine {
    param([string]$T, [hashtable]$State, [int]$To)
    $k = $State.Pos
    while ($k -lt $To) {
        $p = $T.IndexOf([char]10, $k, $To - $k)
        if ($p -lt 0) { break }
        $State.Line++
        $k = $p + 1
    }
    $State.Pos = $To
}

function Invoke-CSharpScan {
    param([string]$Text)
    if ($Text.Length -gt 0 -and $Text[0] -eq [char]0xFEFF) { $Text = $Text.Substring(1) }
    $n = $Text.Length
    $special = [char[]]@('/', '"', "'")
    $comments = New-Object System.Collections.Generic.List[object]
    $sb = New-Object System.Text.StringBuilder
    $st = @{ Line = 1; Pos = 0 }
    $i = 0
    $copyFrom = 0
    while ($true) {
        $j = $Text.IndexOfAny($special, $i)
        if ($j -lt 0) { break }
        $c = $Text[$j]
        if ($c -eq '/') {
            $nx = [char]0
            if ($j + 1 -lt $n) { $nx = $Text[$j + 1] }
            if ($nx -eq '/') {
                $e = $Text.IndexOf([char]10, $j)
                if ($e -lt 0) { $e = $n }
                Update-CSharpLine $Text $st $j
                $body = $Text.Substring($j, $e - $j).TrimEnd([char]13)
                $kind = 'line'
                if ($body.StartsWith('///') -and -not $body.StartsWith('////')) { $kind = 'doc' }
                $comments.Add([pscustomobject]@{ Line = $st.Line; Kind = $kind; Text = $body })
                [void]$sb.Append($Text, $copyFrom, $j - $copyFrom)
                $i = $e
                $copyFrom = $e
                continue
            }
            if ($nx -eq '*') {
                Update-CSharpLine $Text $st $j
                $e = $Text.IndexOf('*/', $j + 2, [StringComparison]::Ordinal)
                if ($e -lt 0) { throw ("CSharpComments: unterminated block comment starting at line {0}" -f $st.Line) }
                $e += 2
                $comments.Add([pscustomobject]@{ Line = $st.Line; Kind = 'block'; Text = $Text.Substring($j, $e - $j) })
                [void]$sb.Append($Text, $copyFrom, $j - $copyFrom)
                [void]$sb.Append(' ')
                $i = $e
                $copyFrom = $e
                continue
            }
            $i = $j + 1
            continue
        }
        if ($c -eq "'") {
            $e = Skip-CSharpChar $Text $j
            if ($e -lt 0) { $i = $j + 1 } else { $i = $e }
            continue
        }
        # c is a double quote: look back for $ and @ prefixes, then measure the quote run.
        $dollars = 0
        $verbatim = $false
        $p = $j - 1
        while ($p -ge 0 -and ($Text[$p] -eq '$' -or $Text[$p] -eq '@')) {
            if ($Text[$p] -eq '$') { $dollars++ } else { $verbatim = $true }
            $p--
        }
        $q = $j
        while ($q -lt $n -and $Text[$q] -eq '"') { $q++ }
        $run = $q - $j
        if (-not $verbatim -and $run -ge 3) {
            $e = Skip-CSharpRaw $Text $j $run
        }
        elseif (-not $verbatim -and $run -eq 2) {
            $e = $j + 2
        }
        else {
            $e = Skip-CSharpString $Text $j $verbatim ($dollars -gt 0)
        }
        if ($e -lt 0) {
            Update-CSharpLine $Text $st $j
            throw ("CSharpComments: unterminated string starting at line {0}" -f $st.Line)
        }
        $i = $e
    }
    [void]$sb.Append($Text, $copyFrom, $n - $copyFrom)
    return [pscustomobject]@{ Comments = $comments; Code = $sb.ToString() }
}

function Get-CSharpComments {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    $scan = Invoke-CSharpScan $Text
    return , $scan.Comments.ToArray()
}

function Remove-CSharpComments {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    return (Invoke-CSharpScan $Text).Code
}
