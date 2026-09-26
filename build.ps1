param([switch]$Test, [switch]$Preview)

$ErrorActionPreference = 'Stop'
if ($Preview -and -not $Test) { throw 'Use -Test with -Preview.' }
$tradingCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $tradingCompiler)) {
    $tradingCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $tradingCompiler)) { throw 'Windows .NET Framework C# compiler is missing.' }
$tradingSource = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'app') -Filter '*.cs' -File | Select-Object -ExpandProperty FullName)
$tradingExe = Join-Path $PSScriptRoot 'Trading.exe'
& $tradingCompiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/out:$tradingExe" $tradingSource
if ($LASTEXITCODE -ne 0) { throw 'Trading.exe build failed.' }
Get-Item -LiteralPath $tradingExe | Select-Object FullName,Length
if ($Test) {
    $tradingTestExe = Join-Path $PSScriptRoot 'Trading.LauncherTest.exe'
    try {
        & $tradingCompiler /nologo /target:exe /codepage:65001 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/reference:$tradingExe" "/out:$tradingTestExe" (Join-Path $PSScriptRoot 'tests\LauncherSmokeTest.cs')
        if ($LASTEXITCODE -ne 0) { throw 'Launcher test build failed.' }
        if ($Preview) {
            & $tradingTestExe $PSScriptRoot (Join-Path $PSScriptRoot 'reports\launcher-preview.png')
        } else {
            & $tradingTestExe $PSScriptRoot
        }
        if ($LASTEXITCODE -ne 0) { throw 'Launcher verification failed.' }
    }
    finally {
        if (Test-Path -LiteralPath $tradingTestExe) { Remove-Item -LiteralPath $tradingTestExe -Force }
    }
}
