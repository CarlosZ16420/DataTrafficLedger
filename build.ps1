param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$sdkRoot = & dotnet --list-sdks | Select-Object -Last 1
if (-not $sdkRoot) { throw '需要本机 .NET SDK 编译器。运行应用本身只需要 Windows 的 .NET Framework 4.8。' }
$sdkVersion = ($sdkRoot -split ' ')[0]
$sdkDirectory = ($sdkRoot -replace '^.*\[','' -replace '\]$','')
$compiler = Join-Path $sdkDirectory "$sdkVersion\Roslyn\bincore\csc.dll"
$outputRoot = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$references = @('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Net.Http.dll','Microsoft.CSharp.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object FullName
$resources = Get-ChildItem -LiteralPath (Join-Path $projectRoot 'web') -File | ForEach-Object { '/resource:' + $_.FullName + ',web.' + $_.Name }
& dotnet $compiler /nologo /utf8output /langversion:10 /unsafe /optimize+ /platform:x64 /target:winexe "/out:$outputRoot\LocalTrafficLedger.exe" "/win32manifest:$projectRoot\src\app.manifest" @references @resources @sources
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\app.config') -Destination (Join-Path $outputRoot 'LocalTrafficLedger.exe.config') -Force
if ($Test) {
    & dotnet $compiler /nologo /utf8output /langversion:10 /unsafe /platform:x64 /target:exe "/out:$outputRoot\LedgerTests.exe" /main:LocalTrafficLedger.Tests @references @resources @sources (Join-Path $projectRoot 'tests\Tests.cs')
    if ($LASTEXITCODE -ne 0) { throw '测试编译失败' }
    & "$outputRoot\LedgerTests.exe"
    if ($LASTEXITCODE -ne 0) { throw '测试失败' }
}
Get-Item -LiteralPath "$outputRoot\LocalTrafficLedger.exe" | Select-Object FullName,Length
