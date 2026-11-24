param([switch]$Test, [string]$OutputDirectory = 'artifacts')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$referenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $referenceRoot)) { throw '.NET Framework 4.8 Developer Pack is required to build.' }
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$refs = @('mscorlib.dll','System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Windows.Forms.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','UIAutomationClient.dll','UIAutomationTypes.dll')
$arguments = @('/nologo','/noconfig','/nostdlib+','/target:winexe','/platform:anycpu','/optimize+','/utf8output','/codepage:65001',('/out:' + (Join-Path $outputRoot 'CaptionCopy.exe')),('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')),('/win32icon:' + (Join-Path $projectRoot 'assets\App.ico')),('/resource:' + (Join-Path $projectRoot 'assets\App.ico') + ',CaptionCopy.App.ico'),('/resource:' + (Join-Path $projectRoot 'src\MainWindow.xaml') + ',CaptionCopy.MainWindow.xaml'))
foreach ($ref in $refs) {
    $refPath = Join-Path $referenceRoot $ref
    if (!(Test-Path -LiteralPath $refPath)) { $refPath = Join-Path (Join-Path $referenceRoot 'WPF') $ref }
    if (!(Test-Path -LiteralPath $refPath)) { throw "Missing reference: $ref" }
    $arguments += '/reference:' + $refPath
}
$arguments += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot '使用说明.txt') -Destination $outputRoot -Force
Write-Output ('Built: ' + (Join-Path $outputRoot 'CaptionCopy.exe'))
if ($Test) {
    $testArgs = @('/nologo','/noconfig','/nostdlib+','/target:exe','/optimize+','/utf8output','/codepage:65001',('/out:' + (Join-Path $outputRoot 'CaptionCopy.Tests.exe')),('/reference:' + (Join-Path $outputRoot 'CaptionCopy.exe')))
    foreach ($ref in $refs) {
        $refPath = Join-Path $referenceRoot $ref
        if (!(Test-Path -LiteralPath $refPath)) { $refPath = Join-Path (Join-Path $referenceRoot 'WPF') $ref }
        $testArgs += '/reference:' + $refPath
    }
    $testArgs += Join-Path $projectRoot 'tests\Program.cs'
    & $compiler @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & (Join-Path $outputRoot 'CaptionCopy.Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
