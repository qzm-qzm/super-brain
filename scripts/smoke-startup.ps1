param([Parameter(Mandatory=$true)][string]$Executable, [string]$TestRoot = (Join-Path $env:TEMP 'super-brain-startup-smoke'))
# Run from an interactive Windows desktop. The test creates and removes its own task/profile.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class StartupProbe {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(IntPtr cls,string title);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern uint RegisterWindowMessage(string text);
 [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
 public static void Shortcut() {
  byte[] keys={17,18,16,135};
  try { foreach(byte key in keys) keybd_event(key,0,0,UIntPtr.Zero); }
  finally { for(int i=keys.Length-1;i>=0;i--) keybd_event(keys[i],0,2,UIntPtr.Zero); }
 }
}
'@
$source = (Resolve-Path -LiteralPath $Executable).Path
[Reflection.Assembly]::LoadFrom($source) | Out-Null
$suffix = [Guid]::NewGuid().ToString('N')
$fixture = [IO.Path]::GetFullPath((Join-Path $TestRoot ('app space & '+$suffix)))
$profile = Join-Path $fixture 'data'
$binary = Join-Path $fixture 'Brain.exe'
New-Item -ItemType Directory -Path $profile -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $binary
[IO.File]::WriteAllText((Join-Path $profile 'config.json'),'{"shortcut":"Ctrl+Alt+Shift+F24"}',[Text.Encoding]::UTF8)
$taskName = [SuperBrain.ScheduledStartup]::TaskName + '-Test-' + $suffix
$keyPath = 'Software\SuperBrainLite.Tests\'+$suffix
$identity = [SuperBrain.Platform]::Identity($profile)
$application = $null
$ui = $null
$service = $null
$folder = $null
$task = $null
function Wait-For([scriptblock]$Predicate) {
 $watch=[Diagnostics.Stopwatch]::StartNew()
 do { $value=& $Predicate; if($null -ne $value -and $value -ne $false){return $value}; Start-Sleep -Milliseconds 100 } while($watch.Elapsed.TotalSeconds -lt 20)
 throw ('Startup test timed out: '+((Get-PSCallStack | ForEach-Object {$_.FunctionName+':'+$_.ScriptLineNumber}) -join ' > '))
}
function Find-Child { $child=Get-CimInstance Win32_Process -Filter "ParentProcessId=$($application.Id)" | Select-Object -First 1; if($null -ne $child){Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue} }
try {
 [SuperBrain.StartupRegistration]::SetEnabled($true,$binary,$keyPath,$taskName)
 $service=New-Object -ComObject Schedule.Service
 $service.Connect(); $folder=$service.GetFolder('\'); $task=$folder.GetTask($taskName)
 [IO.File]::WriteAllText((Join-Path $fixture 'task.xml'),$task.Xml,[Text.Encoding]::UTF8)
 $running=$task.Run($null)
 [Runtime.InteropServices.Marshal]::FinalReleaseComObject($running) | Out-Null
 $listener=Wait-For { $h=[StartupProbe]::FindWindow([IntPtr]::Zero,'SuperBrain.Background.'+$identity); if($h -ne [IntPtr]::Zero){return $h}; return $null }
 [uint32]$owner=0; [StartupProbe]::GetWindowThreadProcessId($listener,[ref]$owner) | Out-Null
 $application=Get-Process -Id $owner
 if($application.Path -ne $binary){throw 'Task started an unexpected executable.'}
 if($null -ne (Find-Child)){throw 'Login task displayed a window instead of waiting silently.'}
 $application.Refresh(); $memory=[Math]::Round($application.WorkingSet64/1MB,1)
 # Deliver actual keyboard events, not an artificial WM_HOTKEY message.
 [StartupProbe]::Shortcut()
 $ui=Wait-For { Find-Child }
 $condition=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$ui.Id)
 $window=Wait-For { [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition) }
 $controlId=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,'hide-window')
 $hide=Wait-For {$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$controlId)}
 $hide.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 if(-not $ui.WaitForExit(5000)){throw 'Task-launched window did not release on hide.'}
 $listener=Wait-For { $h=[StartupProbe]::FindWindow([IntPtr]::Zero,'SuperBrain.Background.'+$identity); if($h -ne [IntPtr]::Zero){return $h}; return $null }
 $running=$task.Run($null); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($running) | Out-Null
 if($null -ne (Find-Child)){throw 'Running the startup task twice displayed a window.'}
 [SuperBrain.StartupRegistration]::SetEnabled($false,$binary,$keyPath,$taskName)
 if([SuperBrain.StartupRegistration]::IsEnabled($binary,$keyPath,$taskName)){throw 'Disabling did not remove the task.'}
 $application.Refresh(); if($application.HasExited){throw 'Disabling future startup killed the active host.'}
 [StartupProbe]::Shortcut(); $ui=Wait-For {Find-Child}
 $exitMessage=[StartupProbe]::RegisterWindowMessage('SuperBrainLite.ExitHost.'+$identity)
 [StartupProbe]::PostMessage([IntPtr]0xffff,$exitMessage,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
 if(-not $application.WaitForExit(10000) -or -not $ui.WaitForExit(5000)){throw 'Task-launched processes did not exit cleanly.'}
 $result=[pscustomobject]@{Success=$true;Checks=@('registered Windows login task','real Task Scheduler launch','quiet startup in user desktop','keyboard shortcut opens UI','hide releases UI','duplicate task launch stays quiet','disable removes task without killing host','keyboard works after disabling future startup','clean exit');IdleWorkingSetMiB=$memory;Fixture=$fixture}
} catch {
 $_ | Out-String | Set-Content -LiteralPath (Join-Path $fixture 'failure.txt') -Encoding UTF8
 throw
} finally {
 [SuperBrain.ScheduledStartup]::WriteXml($null,$taskName)
 [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($keyPath,$false)
 if($null -ne $ui -and -not $ui.HasExited){Stop-Process -Id $ui.Id -Force}
 if($null -ne $application -and -not $application.HasExited){Stop-Process -Id $application.Id -Force}
 foreach($value in @($task,$folder,$service)){if($null -ne $value){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($value) | Out-Null}}
}
$result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $fixture 'startup-result.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 3
