$dest = Join-Path $env:TEMP 'luna-refs'
New-Item -ItemType Directory -Force $dest | Out-Null
Push-Location $dest
try {
    if (-not (Test-Path 'package')) {
        npm pack zca-js@2.2.0 --silent 2>$null | Out-Null
        tar xzf zca-js-2.2.0.tgz
    }
    if (-not (Test-Path 'ctdozlite')) {
        git clone --depth 1 https://github.com/ctdoteam1110/ctdozlite.git
    }
}
finally { Pop-Location }
"Reference sources ready at $dest"

