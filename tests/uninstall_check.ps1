param([string]$Archive=(Join-Path $PSScriptRoot '..\releases\DataTrafficLedger-v1.6.0-win-x64-portable.zip'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '..\Uninstall.ps1')
$testRoot=Join-Path $PSScriptRoot ('run-uninstall-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$service=New-Object -ComObject Schedule.Service;$service.Connect();$folder=$service.GetFolder('\')
$registered=[Collections.Generic.List[string]]::new()
$windowsShell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
function Assert-Check($condition,[string]$message){if(-not $condition){throw $message}}
function Expand-TestApp([string]$name){
    $destination=Join-Path $testRoot $name
    Expand-Archive -LiteralPath $Archive -DestinationPath $destination
    return (Get-ChildItem -LiteralPath $destination -Directory | Select-Object -First 1).FullName
}
function Register-TestTask($context,[string]$kind){
    $name='LocalTrafficLedger-'+$kind+'-'+[Guid]::NewGuid().ToString('N').Substring(0,12).ToUpperInvariant()
    $definition=$service.NewTask(0)
    $definition.Principal.UserId=$context.Sid;$definition.Principal.LogonType=3;$definition.Principal.RunLevel=0
    $definition.Settings.ExecutionTimeLimit='PT0S'
    $action=$definition.Actions.Create(0);$action.Path=$context.Exe
    $action.Arguments=$(if($kind -eq 'ETW'){'--etw-helper'}else{'--background'})+' --data "'+$context.Data+'"'
    $null=$folder.RegisterTaskDefinition($name,$definition,6,$context.Sid,$null,3,$null)
    $registered.Add($name)
    return $name
}
function Invoke-TestUninstaller($context,[string]$flags=''){
    $script=Join-Path $context.Root 'Uninstall.ps1'
    $process=Start-Process -FilePath $windowsShell -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -File "'+$script+'" -Quiet '+$flags) -WindowStyle Hidden -PassThru -Wait
    Assert-Check ($process.ExitCode -eq 0) ('Uninstaller failed: '+$process.ExitCode)
}
function Get-TestTasks {return @($folder.GetTasks(1) | Where-Object {$_.Name -in $registered})}
function Fake-Folder($tasks,[bool]$deny=$false,[bool]$retain=$false){
    $fake=[pscustomobject]@{Tasks=[Collections.Generic.List[object]]::new();Deny=$deny;Retain=$retain}
    foreach($task in $tasks){$fake.Tasks.Add($task)}
    $fake | Add-Member ScriptMethod GetTasks {param($flags) return $this.Tasks.ToArray()}
    $fake | Add-Member ScriptMethod DeleteTask {param($name,$flags) if($this.Deny){throw 'Access denied'};if(-not $this.Retain){foreach($task in $this.Tasks.ToArray()){if($task.Name -eq $name){$null=$this.Tasks.Remove($task)}}}}
    return $fake
}
function Fake-Task($context,[string]$name,[string]$path){
    $actions=[pscustomobject]@{Count=1;Action=[pscustomobject]@{Type=0;Path=$path;Arguments='--etw-helper'}}
    $actions | Add-Member ScriptMethod Item {param($index) return $this.Action}
    [pscustomobject]@{Name=$name;State=3;Enabled=$true;Definition=[pscustomobject]@{Actions=$actions;Principal=[pscustomobject]@{RunLevel=1;UserId=$context.Sid}}}
}
$baseline=@($folder.GetTasks(1) | Where-Object {$_.Name -like 'LocalTrafficLedger-*'} | ForEach-Object {$_.Name+'|'+$_.Xml+'|'+$_.Enabled} | Sort-Object)
try{
    # No real production task names are registered or changed by these tests.
    $keep=Get-LedgerUninstallContext (Expand-TestApp 'keep history')
    New-Item -ItemType Directory -Path $keep.Data | Out-Null
    [IO.File]::WriteAllText((Join-Path $keep.Data 'history-sentinel.txt'),'keep this history')
    [IO.File]::WriteAllText((Join-Path $keep.Data 'settings.json'),'{"etwRequested":true,"theme":"dark"}')
    [IO.File]::WriteAllText((Join-Path $keep.App 'personal.txt'),'keep unknown files')
    $names=@((Register-TestTask $keep 'ETW'),(Register-TestTask $keep 'Tray'))
    Assert-Check (@(Get-LedgerTaskPlan $folder $keep).Count -eq 2) 'Task matching missed account-name principals.'
    # WhatIf must leave every file and task untouched.
    Invoke-TestUninstaller $keep '-WhatIf'
    Assert-Check ((Test-Path -LiteralPath $keep.Exe) -and @(Get-TestTasks).Count -eq 2) 'WhatIf changed files or tasks.'
    $application=Start-Process -FilePath $keep.Exe -ArgumentList '--headless' -WindowStyle Hidden -PassThru
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    while(-not (Test-Path -LiteralPath (Join-Path $keep.Data 'runtime.json'))){if([DateTime]::UtcNow -gt $deadline){throw 'Isolated app failed to start'};Start-Sleep -Milliseconds 100}
    Invoke-TestUninstaller $keep
    Assert-Check ($application.WaitForExit(10000)) 'Uninstall did not exit the running app.'
    Assert-Check (@(Get-TestTasks).Count -eq 0) 'Temporary scheduled tasks were not removed.'
    Assert-Check (-not (Test-Path -LiteralPath $keep.Exe)) 'Program binary remains.'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $keep.App 'Uninstall.cmd'))) 'Portable launcher remains.'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $keep.App 'Uninstall.ps1'))) 'Portable script remains.'
    Assert-Check ((Test-Path -LiteralPath (Join-Path $keep.Data 'history-sentinel.txt')) -and (Test-Path -LiteralPath (Join-Path $keep.App 'personal.txt'))) 'Uninstall removed retained history or personal files.'
    $settings=[IO.File]::ReadAllText((Join-Path $keep.Data 'settings.json')) | ConvertFrom-Json
    Assert-Check (-not $settings.etwRequested -and $settings.theme -eq 'dark') 'Retained settings were not preserved correctly.'
    Write-Output 'PASS: Windows PowerShell 5.1, dry run, running-app shutdown, real temporary tasks, program cleanup and retained history.'

    $delete=Get-LedgerUninstallContext (Expand-TestApp 'delete history')
    New-Item -ItemType Directory -Path (Join-Path $delete.Data 'nested') | Out-Null
    [IO.File]::WriteAllText((Join-Path $delete.Data 'nested\history.txt'),'test history')
    $null=Register-TestTask $delete 'ETW';$null=Register-TestTask $delete 'Tray'
    Invoke-TestUninstaller $delete '-DeleteData'
    Assert-Check (-not (Test-Path -LiteralPath $delete.Data) -and -not (Test-Path -LiteralPath $delete.Exe) -and @(Get-TestTasks).Count -eq 0) 'Complete uninstall left data, binaries or tasks.'
    Write-Output 'PASS: Explicit data deletion removes isolated nested history after task cleanup.'

    $failure=Get-LedgerUninstallContext (Expand-TestApp 'denied cleanup')
    New-Item -ItemType Directory -Path $failure.Data | Out-Null
    [IO.File]::WriteAllText((Join-Path $failure.Data 'history.txt'),'must survive a task error')
    $task=Fake-Task $failure ('LocalTrafficLedger-ETW-'+$failure.Key) $failure.Exe
    foreach($mode in @('denied','remaining')){
        $fake=Fake-Folder @($task) ($mode -eq 'denied') ($mode -eq 'remaining')
        $failed=$false
        try{Invoke-LedgerUninstall $failure $fake -DeleteHistory | Out-Null}catch{$failed=$true}
        Assert-Check ($failed -and (Test-Path -LiteralPath $failure.Exe) -and (Test-Path -LiteralPath (Join-Path $failure.Data 'history.txt'))) ('Cleanup failure deleted files: '+$mode)
    }
    $legacyExe=Join-Path $failure.App 'LocalTrafficLedger.exe'
    Copy-Item -LiteralPath $failure.Exe -Destination $legacyExe
    Copy-Item -LiteralPath ($failure.Exe+'.config') -Destination ($legacyExe+'.config')
    $legacyContext=Get-LedgerUninstallContext $failure.Root
    Assert-Check ($legacyContext.Exe -eq $failure.Exe) 'Mixed installation did not prefer the renamed executable.'
    $legacyTask=Fake-Task $failure 'LocalTrafficLedger-ETW-0123456789AD' $legacyExe
    $other=Fake-Task $failure 'LocalTrafficLedger-ETW-0123456789AB' (Join-Path $keep.App 'DataTrafficLedger.exe')
    $other.Definition.Principal.UserId='S-1-5-18'
    $orphan=Fake-Task $failure ('LocalTrafficLedger-Tray-'+$failure.Key) (Join-Path $testRoot 'moved-away\DataTrafficLedger.exe')
    $unrelated=Fake-Task $failure 'AnotherApp-ETW-0123456789AB' $failure.Exe
    $wrongInstance=Fake-Task $failure 'LocalTrafficLedger-ETW-0123456789AC' (Join-Path $testRoot 'different\DataTrafficLedger.exe')
    $selection=@(Get-LedgerTaskPlan (Fake-Folder @($task,$legacyTask,$other,$orphan,$unrelated,$wrongInstance)) $failure)
    Assert-Check ($selection.Count -eq 3 -and $orphan.Name -in $selection.Name -and $legacyTask.Name -in $selection.Name) 'Task selection crossed account/application boundaries or missed legacy/moved-folder tasks.'
    $blocked=$false;try{Get-LedgerDataInventory $failure.Root $keep.Data | Out-Null}catch{$blocked=$true}
    Assert-Check $blocked 'External history path was not blocked.'
    $junction=Join-Path $failure.Data 'link'
    New-Item -ItemType Junction -Path $junction -Target $keep.Data | Out-Null
    $blocked=$false;try{Get-LedgerDataInventory $failure.Root $failure.Data | Out-Null}catch{$blocked=$true}
    Assert-Check $blocked 'Data junction was not blocked.'
    Remove-Item -LiteralPath $junction -Force
    Remove-Item -LiteralPath $failure.Exe,($failure.Exe+'.config')
    $legacyContext=Get-LedgerUninstallContext $failure.Root
    Assert-Check ($legacyContext.Exe -eq $legacyExe) 'Legacy-only installation did not find its executable.'
    $null=Register-TestTask $legacyContext 'ETW'
    Invoke-TestUninstaller $legacyContext
    Assert-Check (-not (Test-Path -LiteralPath $legacyExe) -and -not (Test-Path -LiteralPath ($legacyExe+'.config')) -and @(Get-TestTasks).Count -eq 0) 'Legacy executable or its task survived uninstall.'
    Write-Output 'PASS: English rename retains task identifiers and supports mixed/legacy executable cleanup.'
    Write-Output 'PASS: Permission failure, deletion verification failure, account/path isolation, orphan matching and external/link deletion guards.'
}finally{
    # Cleanup is restricted to randomly named tasks created by this test invocation.
    foreach($name in $registered){try{$folder.DeleteTask($name,0)}catch{if($_.Exception.HResult -ne -2147024894){Write-Warning $_.Exception.Message}}}
    if($application -and -not $application.HasExited){$application.Kill();$application.WaitForExit()}
    $after=@($folder.GetTasks(1) | Where-Object {$_.Name -like 'LocalTrafficLedger-*'} | ForEach-Object {$_.Name+'|'+$_.Xml+'|'+$_.Enabled} | Sort-Object)
    Assert-Check (-not @(Compare-Object $baseline $after).Count) 'An existing application task was changed.'
}
