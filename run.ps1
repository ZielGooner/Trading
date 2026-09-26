$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    $tradingPython = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $tradingPython)) { throw 'Python environment is missing. See README.md.' }
    & $tradingPython -B -m trading @args
    if ($LASTEXITCODE -ne 0) { throw "Trading replay failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }
