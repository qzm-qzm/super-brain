param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../dist-lite'), [switch]$Tests)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.8 is required.' }
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$references = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','WPF/WindowsBase.dll','WPF/PresentationCore.dll','WPF/PresentationFramework.dll','System.Xaml.dll')
$arguments = @('/nologo','/optimize+','/platform:x64','/codepage:65001',('/win32icon:' + (Join-Path $projectRoot 'assets/icon.ico')),('/win32manifest:' + (Join-Path $projectRoot 'native/app.manifest')))
$arguments += $references | ForEach-Object { '/reference:' + (Join-Path $frameworkRoot $_) }
$arguments += @(
  ('/resource:' + (Join-Path $projectRoot 'native/Theme.xaml') + ',SuperBrain.Theme.xaml'),
  ('/resource:' + (Join-Path $projectRoot 'assets/icon.png') + ',SuperBrain.icon.png'),
  ('/resource:' + (Join-Path $projectRoot 'assets/icon.ico') + ',SuperBrain.icon.ico')
)
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'native') -Filter '*.cs' -File | ForEach-Object FullName)
if ($Tests) {
  $outputFile = Join-Path $outputPath 'NativeTests.exe'
  $arguments += @('/target:exe','/main:SuperBrain.Tests')
  $sourceFiles += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'native/tests') -Filter '*.cs' -File | ForEach-Object FullName)
} else {
  $outputFile = Join-Path $outputPath '超强大脑.exe'
  $arguments += @('/target:winexe','/main:SuperBrain.Program')
}
$arguments += '/out:' + $outputFile
& $compiler @arguments @sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
Get-Item -LiteralPath $outputFile | Select-Object FullName,Length
