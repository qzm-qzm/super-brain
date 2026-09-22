param([Parameter(Mandatory=$true)][string]$Executable, [string]$TestRoot = (Join-Path $env:TEMP 'super-brain-lite-smoke'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$binary = (Resolve-Path -LiteralPath $Executable).Path
$profile = Join-Path $TestRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $profile -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $profile 'config.json'), '{"shortcut":"Ctrl+Alt+Shift+F24"}', [Text.Encoding]::UTF8)
$application = $null
$second = $null
function Wait-For([scriptblock]$Condition) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Condition
        if ($null -ne $value -and $value -ne $false) { return $value }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 12)
    throw 'Timed out waiting for the running application.'
}
function Find-Control([string]$Id) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-Control([string]$Id) {
    $control = Wait-For { Find-Control $Id }
    $control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
try {
    $application = Start-Process -FilePath $binary -ArgumentList ('--data-dir "{0}"' -f $profile) -WindowStyle Hidden -PassThru
    $processCondition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $application.Id)
    $script:window = Wait-For { [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children, $processCondition) }
    Invoke-Control 'new-item'
    (Wait-For { Find-Control 'edit-title' }).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Standalone executable smoke')
    (Wait-For { Find-Control 'edit-body' }).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Local portable data; synthetic test only.')
    Invoke-Control 'back'
    $document = Wait-For {
        $saved = Get-Content -LiteralPath (Join-Path $profile 'notes.json') -Raw | ConvertFrom-Json
        if ($saved.notes[0].body -eq 'Local portable data; synthetic test only.') { return $saved }
        return $null
    }
    if ($document.notes[0].body -ne 'Local portable data; synthetic test only.') { throw 'The standalone executable did not persist the note.' }
    $second = Start-Process -FilePath $binary -ArgumentList ('--data-dir "{0}"' -f $profile) -WindowStyle Hidden -PassThru
    if (-not $second.WaitForExit(5000) -or $second.ExitCode -ne 0) { throw 'Second-instance activation failed.' }
    $application.Refresh()
    $memory = [Math]::Round($application.WorkingSet64 / 1MB, 1)
    Invoke-Control 'settings'
    $dialog = Wait-For {
        $roots = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($candidate in $roots) {
            $id = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, 'quit')
            $quit = $candidate.FindFirst([Windows.Automation.TreeScope]::Descendants, $id)
            if ($null -ne $quit) { return $quit }
        }
        return $null
    }
    $dialog.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not $application.WaitForExit(5000)) { throw 'Quit did not stop the standalone executable.' }
    $result = [PSCustomObject]@{ Success=$true; Checks=@('production exe startup','real UI note save','same-directory profile','single instance','clean quit'); WorkingSetMiB=$memory; Profile=$profile }
    $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $profile 'result.json') -Encoding UTF8
    $result | ConvertTo-Json -Depth 3
} finally {
    if ($null -ne $second -and -not $second.HasExited) { Stop-Process -Id $second.Id -Force }
    if ($null -ne $application -and -not $application.HasExited) { Stop-Process -Id $application.Id -Force }
}
