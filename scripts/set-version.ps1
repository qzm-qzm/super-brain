param([Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a stable x.y.z version.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
$source = Join-Path $projectRoot 'native/Platform.cs'
$text = [IO.File]::ReadAllText($source)
$text = [regex]::Replace($text, '(Assembly(?:File)?Version\(")[^"]+("\))', '${1}' + $Version + '.0${2}')
$text = [regex]::Replace($text, '(const string Version = ")[^"]+(";)', '${1}' + $Version + '${2}')
[IO.File]::WriteAllText($source, $text, (New-Object Text.UTF8Encoding($false)))
$manifest = Join-Path $projectRoot 'native/app.manifest'
$text = [regex]::Replace([IO.File]::ReadAllText($manifest), '(assemblyIdentity version=")[^"]+(" name=)', '${1}' + $Version + '.0${2}')
[IO.File]::WriteAllText($manifest, $text, (New-Object Text.UTF8Encoding($false)))
Write-Output "Version updated to $Version. Update CHANGELOG.md and run the build/tests before tagging."
