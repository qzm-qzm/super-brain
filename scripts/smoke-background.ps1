param([Parameter(Mandatory=$true)][string]$Executable, [string]$TestRoot = (Join-Path $env:TEMP 'super-brain-background-smoke'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BrainBackgroundProbe {
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr className, string title);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr handle, uint msg, IntPtr wParam, IntPtr lParam);
 [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
 [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr handle, int id);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
}
'@
$binary = (Resolve-Path -LiteralPath $Executable).Path
$profile = Join-Path $TestRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $profile -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $profile 'config.json'), '{"shortcut":"Ctrl+Alt+Shift+F24"}', [Text.Encoding]::UTF8)
$sha = [Security.Cryptography.SHA256]::Create()
try { $identity = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($profile).ToUpperInvariant())))).Replace('-','').Substring(0,24) } finally { $sha.Dispose() }
$application = $null
$ui = $null
$second = $null
function Wait-For([scriptblock]$Predicate) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do { $value = & $Predicate; if ($null -ne $value -and $value -ne $false) { return $value }; Start-Sleep -Milliseconds 100 } while ($watch.Elapsed.TotalSeconds -lt 15)
    throw ('Timed out waiting for background application: ' + ((Get-PSCallStack | Select-Object -Skip 1 -First 1).ScriptLineNumber))
}
function Child { $item = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($application.Id)" | Select-Object -First 1; if ($null -ne $item) { Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue } }
function Find-Control([string]$Id) { $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id); $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition) }
function Invoke-Control([string]$Id) { (Wait-For { Find-Control $Id }).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Await-Window {
    $script:ui = Wait-For { Child }
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$ui.Id)
    $script:window = Wait-For { [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition) }
}
function Assert-Hotkey([uint32]$Key) {
    $owned = Wait-For {
        $available = [BrainBackgroundProbe]::RegisterHotKey([IntPtr]::Zero,991,0x4007,$Key)
        if ($available) { [BrainBackgroundProbe]::UnregisterHotKey([IntPtr]::Zero,991) | Out-Null; return $false }
        return $true
    }
    if (-not $owned) { throw 'Global shortcut was not retained.' }
}
try {
    $application = Start-Process -FilePath $binary -ArgumentList ('--background --data-dir "{0}"' -f $profile) -WindowStyle Hidden -PassThru
    $listener = Wait-For { $handle = [BrainBackgroundProbe]::FindWindow([IntPtr]::Zero,'SuperBrain.Background.'+$identity); if ($handle -ne [IntPtr]::Zero) { return $handle }; return $null }
    Assert-Hotkey 135
    if ($null -ne (Child)) { throw 'Silent background startup created a UI process.' }
    $application.Refresh(); $idle = [Math]::Round($application.WorkingSet64/1MB,1)
    if (@($application.Modules | Where-Object { $_.ModuleName -match 'PresentationFramework' }).Count -ne 0) { throw 'Idle process loaded WPF.' }
    $cpuBefore = $application.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Milliseconds 1200
    $application.Refresh(); $idleCpuMs = [Math]::Round($application.TotalProcessorTime.TotalMilliseconds-$cpuBefore,1)
    [BrainBackgroundProbe]::PostMessage($listener,0x312,[IntPtr]61,[IntPtr]::Zero) | Out-Null
    Await-Window
    Invoke-Control 'new-item'
    (Wait-For { Find-Control 'edit-title' }).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Background lifecycle test')
    (Wait-For { Find-Control 'edit-body' }).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Final accepted text before window release.')
    $application.Refresh(); $ui.Refresh(); $visible = [Math]::Round(($application.WorkingSet64+$ui.WorkingSet64)/1MB,1)
    Invoke-Control 'hide-window'
    if (-not $ui.WaitForExit(5000)) { throw 'Hiding did not release the WPF process.' }
    Assert-Hotkey 135
    $saved = Get-Content -LiteralPath (Join-Path $profile 'notes.json') -Raw | ConvertFrom-Json
    if ($saved.notes[0].body -ne 'Final accepted text before window release.') { throw 'Hiding lost the final accepted edit.' }
    $application.Refresh(); $afterHide = [Math]::Round($application.WorkingSet64/1MB,1)
    # A duplicate startup launch must stay silent.
    $second = Start-Process -FilePath $binary -ArgumentList ('--background --data-dir "{0}"' -f $profile) -WindowStyle Hidden -PassThru
    if (-not $second.WaitForExit(5000) -or $second.ExitCode -ne 0) { throw 'Duplicate background instance failed.' }
    if ($null -ne (Child)) { throw 'Duplicate startup unexpectedly showed a window.' }
    # Read the new preference when returning to background instead of caching an obsolete key.
    $config = Get-Content -LiteralPath (Join-Path $profile 'config.json') -Raw | ConvertFrom-Json
    $config.shortcut = 'Ctrl+Alt+Shift+F23'; $config | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $profile 'config.json') -Encoding UTF8
    $second = Start-Process -FilePath $binary -ArgumentList ('--data-dir "{0}"' -f $profile) -WindowStyle Hidden -PassThru
    if (-not $second.WaitForExit(5000)) { throw 'Double-click activation did not reuse the host.' }
    Await-Window
    if ((Get-Content -LiteralPath (Join-Path $profile 'notes.json') -Raw | ConvertFrom-Json).notes.Count -ne 1) { throw 'Reopen changed notes.' }
    Invoke-Control 'hide-window'
    if (-not $ui.WaitForExit(5000)) { throw 'Second hide did not release UI.' }; Assert-Hotkey 134
    [BrainBackgroundProbe]::PostMessage($listener,0x312,[IntPtr]61,[IntPtr]::Zero) | Out-Null
    Await-Window
    Invoke-Control 'settings'
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$ui.Id)
    $quit = Wait-For { foreach ($root in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)) { $id = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'quit'); $control = $root.FindFirst([Windows.Automation.TreeScope]::Descendants,$id); if ($null -ne $control) { return $control } }; return $null }
    $quit.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not $application.WaitForExit(5000) -or -not $ui.WaitForExit(5000)) { throw 'Quit left a background process running.' }
    # Quit while the child is still starting, before its message window is ready.
    $application = Start-Process -FilePath $binary -ArgumentList ('--background --data-dir "{0}"' -f $profile) -WindowStyle Hidden -PassThru
    $listener = Wait-For { $handle = [BrainBackgroundProbe]::FindWindow([IntPtr]::Zero,'SuperBrain.Background.'+$identity); if ($handle -ne [IntPtr]::Zero) { return $handle }; return $null }
    [BrainBackgroundProbe]::PostMessage($listener,0x312,[IntPtr]61,[IntPtr]::Zero) | Out-Null
    [BrainBackgroundProbe]::PostMessage($listener,[BrainBackgroundProbe]::RegisterWindowMessage('SuperBrainLite.ExitHost.'+$identity),[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
    if (-not $application.WaitForExit(10000)) { $ui = Child; throw 'Quit during UI startup left the host running.' }
    $ui = Child
    if ($null -ne $ui) { throw 'Quit during UI startup left a window process running.' }
    $result = [pscustomobject]@{Success=$true;Checks=@('silent startup','no WPF in idle host','registered shortcut','lazy window launch','save before release','UI process exits on hide','duplicate startup stays silent','double-click reuses host','changed shortcut reload','quit stops both processes','quit during window startup');IdleWorkingSetMiB=$idle;VisibleTotalWorkingSetMiB=$visible;AfterHideWorkingSetMiB=$afterHide;IdleCpuMsOver1_2Seconds=$idleCpuMs;Profile=$profile}
    $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $profile 'background-result.json') -Encoding UTF8
    $result | ConvertTo-Json -Depth 3
} finally {
    if ($null -ne $second -and -not $second.HasExited) { Stop-Process -Id $second.Id -Force }
    if ($null -ne $ui -and -not $ui.HasExited) { Stop-Process -Id $ui.Id -Force }
    if ($null -ne $application -and -not $application.HasExited) { Stop-Process -Id $application.Id -Force }
}
