[CmdletBinding(SupportsShouldProcess=$true)]
param([switch]$DeleteData,[switch]$KeepApplicationFiles,[switch]$Quiet,[string]$OriginalSid)
$ErrorActionPreference='Stop'

function Get-LedgerUserKey([string]$Sid){
    $hash=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Sid)))).Replace('-','').Substring(0,12)}finally{$hash.Dispose()}
}
function Get-LedgerUninstallContext([string]$Root){
    $rootPath=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $legacy=Test-Path -LiteralPath (Join-Path $rootPath 'app\portable.json')
    $appPath=if($legacy){Join-Path $rootPath 'app'}else{$rootPath}
    $marker=Join-Path $appPath 'portable.json'
    if(-not (Test-Path -LiteralPath $marker)){throw 'portable.json was not found. Run the uninstaller from the original application folder.'}
    $location=([IO.File]::ReadAllText($marker) | ConvertFrom-Json).data
    if(-not $location){throw 'The portable data location is invalid.'}
    $dataPath=[IO.Path]::GetFullPath((Join-Path $appPath $location)).TrimEnd('\','/')
    if($dataPath -eq $rootPath -or $dataPath -eq $appPath){throw 'The data folder must be separate from the application folder.'}
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $executables=@((Join-Path $appPath 'DataTrafficLedger.exe'),(Join-Path $appPath 'LocalTrafficLedger.exe'))
    $executable=$executables[0]
    if(-not (Test-Path -LiteralPath $executable) -and (Test-Path -LiteralPath $executables[1])){$executable=$executables[1]}
    [pscustomobject]@{Root=$rootPath;App=$appPath;Exe=$executable;Executables=$executables;Data=$dataPath;Legacy=$legacy;Sid=$sid;Key=(Get-LedgerUserKey $sid)}
}
function Get-LedgerTaskPlan($Folder,$Context){
    foreach($task in $Folder.GetTasks(1)){
        if($task.Name -notmatch '^LocalTrafficLedger-(ETW|Tray)-[A-Fa-f0-9]{12}$'){continue}
        $definition=$task.Definition
        $owner=[string]$definition.Principal.UserId
        if($owner -ne $Context.Sid){try{$owner=([Security.Principal.NTAccount]::new($owner)).Translate([Security.Principal.SecurityIdentifier]).Value}catch{continue}}
        if($owner -ne $Context.Sid){continue}
        if($definition.Actions.Count -ne 1){continue}
        $action=$definition.Actions.Item(1)
        if($action.Type -ne 0 -or [IO.Path]::GetFileName($action.Path) -notin @('DataTrafficLedger.exe','LocalTrafficLedger.exe')){continue}
        if($action.Arguments -notmatch '(?:^|\s)--(?:etw-helper|background)(?:\s|$)'){continue}
        $same=[IO.Path]::GetFullPath($action.Path) -in $Context.Executables
        # Also remove this user's old orphan entry after a portable folder was moved.
        $orphan=($task.Name -in @(('LocalTrafficLedger-ETW-'+$Context.Key),('LocalTrafficLedger-Tray-'+$Context.Key))) -and -not (Test-Path -LiteralPath $action.Path)
        if($same -or $orphan){[pscustomobject]@{Name=$task.Name;Task=$task;Path=$action.Path;High=([int]$definition.Principal.RunLevel -eq 1)}}
    }
}
function Remove-LedgerTasks($Folder,$Plan){
    foreach($entry in $Plan){
        if([int]$entry.Task.State -eq 4){$entry.Task.Stop(0)}
        $Folder.DeleteTask($entry.Name,0)
    }
    $remaining=@($Folder.GetTasks(1) | Where-Object {$_.Name -in @($Plan | ForEach-Object {$_.Name})})
    if($remaining.Count){throw ('Scheduled tasks remain: '+(($remaining | ForEach-Object {$_.Name}) -join ', '))}
}
function Get-LedgerDataInventory([string]$Root,[string]$Data){
    $allowed=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $target=[IO.Path]::GetFullPath($Data).TrimEnd('\','/')
    if(-not $target.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Automatic data deletion is restricted to a data folder inside this application folder. External data was kept.'}
    # Reject junctions/symlinks before enumerating or deleting any contents.
    $check=$target
    while($check){if(Test-Path -LiteralPath $check){if((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Data cleanup cannot traverse a link or junction.'}};if($check -eq $Root){break};$check=[IO.Path]::GetDirectoryName($check)}
    if(-not (Test-Path -LiteralPath $target)){return}
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($target)
    $inventory=[Collections.Generic.List[object]]::new()
    while($pending.Count){$directory=$pending.Pop();$inventory.Add((Get-Item -LiteralPath $directory -Force));foreach($item in Get-ChildItem -LiteralPath $directory -Force){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Data cleanup cannot traverse a link or junction.'}
        if($item.PSIsContainer){$pending.Push($item.FullName)}else{$inventory.Add($item)}
    }}
    return $inventory
}
function Remove-LedgerShortcuts($Context,$Plan){
    $shell=New-Object -ComObject WScript.Shell
    $targets=@($Context.Executables)+@($Plan | ForEach-Object {$_.Path})
    foreach($directory in @([Environment]::GetFolderPath('DesktopDirectory'),[Environment]::GetFolderPath('Programs'))){
        if(-not $directory){continue}
        foreach($name in @('Data Traffic Ledger.lnk','Local Traffic Ledger.lnk',((-join [char[]]@(0x6D41,0x91CF,0x8D26,0x672C))+'.lnk'),((-join [char[]]@(0x672C,0x673A,0x6D41,0x91CF,0x8D26,0x672C))+'.lnk'))){
            $path=Join-Path $directory $name
            if((Test-Path -LiteralPath $path) -and $shell.CreateShortcut($path).TargetPath -in $targets){Remove-Item -LiteralPath $path -Force}
        }
    }
    $run=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run',$true)
    if($run){try{$value=[string]$run.GetValue('LocalTrafficLedger','');if(@($targets | Where-Object {$value.IndexOf('"'+$_+'"',[StringComparison]::OrdinalIgnoreCase) -ge 0 -or $value -eq $_}).Count){$run.DeleteValue('LocalTrafficLedger',$false)}}finally{$run.Dispose()}}
}
function Stop-LedgerForUninstall($Context,$Plan){
    foreach($entry in $Plan){$entry.Task.Enabled=$false}
    foreach($executable in $Context.Executables | Where-Object {Test-Path -LiteralPath $_}){
        $stop=Start-Process -FilePath $executable -ArgumentList @('--stop','--data',('"'+$Context.Data+'"')) -WindowStyle Hidden -PassThru
        if(-not $stop.WaitForExit(10000) -or $stop.ExitCode -ne 0){throw 'The application did not accept the shutdown request. Program files were kept.'}
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do{
        $running=@(Get-Process -Name DataTrafficLedger,LocalTrafficLedger -ErrorAction SilentlyContinue | Where-Object {try{$_.Path -in $Context.Executables}catch{$false}})
        if(-not $running.Count){return}
        Start-Sleep -Milliseconds 200
    }while([DateTime]::UtcNow -lt $deadline)
    throw 'The application is still saving data. Program files were kept; exit the tray and retry.'
}
function Invoke-LedgerUninstall($Context,$Folder,[switch]$DeleteHistory,[switch]$KeepFiles){
    $plan=@(Get-LedgerTaskPlan $Folder $Context)
    # Validate every data path before changing Windows integration or files.
    if($DeleteHistory){$null=Get-LedgerDataInventory $Context.Root $Context.Data}
    Stop-LedgerForUninstall $Context $plan
    Remove-LedgerTasks $Folder $plan
    Remove-LedgerShortcuts $Context $plan
    # Task deletion/verification must succeed before any program or data deletion.
    if($DeleteHistory){
        $inventory=@(Get-LedgerDataInventory $Context.Root $Context.Data)
        foreach($item in $inventory | Where-Object {-not $_.PSIsContainer}){Remove-Item -LiteralPath $item.FullName -Force}
        foreach($item in $inventory | Where-Object {$_.PSIsContainer} | Sort-Object { $_.FullName.Length } -Descending){Remove-Item -LiteralPath $item.FullName -Force}
    }elseif(Test-Path -LiteralPath (Join-Path $Context.Data 'settings.json')){
        $settings=[IO.File]::ReadAllText((Join-Path $Context.Data 'settings.json')) | ConvertFrom-Json
        $settings | Add-Member -NotePropertyName etwRequested -NotePropertyValue $false -Force
        [IO.File]::WriteAllText((Join-Path $Context.Data 'settings.json'),($settings | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    }
    if(-not $KeepFiles){
        $names=@('DataTrafficLedger.exe','DataTrafficLedger.exe.config','LocalTrafficLedger.exe','LocalTrafficLedger.exe.config','portable.json')
        if(-not $Context.Legacy){$names+=@('LICENSE','README.txt','Uninstall.cmd','Uninstall.ps1')}
        foreach($name in $names){$path=Join-Path $Context.App $name;if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
        if((Test-Path -LiteralPath $Context.App) -and -not @(Get-ChildItem -LiteralPath $Context.App -Force).Count){Remove-Item -LiteralPath $Context.App}
    }
    [pscustomobject]@{Success=$true;RemovedTasks=@($plan | ForEach-Object {$_.Name});HistoryKept=(-not $DeleteHistory);DataDirectory=$Context.Data;ProgramFilesKept=[bool]$KeepFiles}
}

# Dot-sourcing exposes the same implementation for isolated cleanup tests.
if($MyInvocation.InvocationName -eq '.'){return}
try{
    $context=Get-LedgerUninstallContext $PSScriptRoot
    if($OriginalSid -and $OriginalSid -ne $context.Sid){throw 'Use administrator approval for the SAME Windows account. Nothing was removed for another account.'}
    $service=New-Object -ComObject Schedule.Service;$service.Connect();$folder=$service.GetFolder('\')
    $plan=@(Get-LedgerTaskPlan $folder $context)
    if($WhatIfPreference){[pscustomobject]@{Executable=$context.Exe;DataDirectory=$context.Data;Tasks=@($plan | ForEach-Object {$_.Name});DeleteData=[bool]$DeleteData;KeepApplicationFiles=[bool]$KeepApplicationFiles} | ConvertTo-Json;exit 0}
    if(-not $Quiet){
        Add-Type -AssemblyName System.Windows.Forms
        $answer=[Windows.Forms.MessageBox]::Show('Uninstall Data Traffic Ledger? This removes its scheduled tasks, startup entries and shortcuts.','Data Traffic Ledger',[Windows.Forms.MessageBoxButtons]::YesNo,[Windows.Forms.MessageBoxIcon]::Question,[Windows.Forms.MessageBoxDefaultButton]::Button2)
        if($answer -ne [Windows.Forms.DialogResult]::Yes){exit 2}
        if(-not $DeleteData){
            $answer=[Windows.Forms.MessageBox]::Show('Keep your traffic history and settings? Yes = keep data; No = permanently delete data; Cancel = cancel uninstall.','Data Traffic Ledger',[Windows.Forms.MessageBoxButtons]::YesNoCancel,[Windows.Forms.MessageBoxIcon]::Question)
            if($answer -eq [Windows.Forms.DialogResult]::Cancel){exit 2};$DeleteData=($answer -eq [Windows.Forms.DialogResult]::No)
        }
    }
    $admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if(@($plan | Where-Object {$_.High}).Count -and -not $admin){
        if($Quiet){throw 'Administrator approval is required to remove the Windows capture task. Run Uninstall.cmd interactively or rerun from an elevated terminal.'}
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Quiet -OriginalSid "'+$context.Sid+'"'
        if($DeleteData){$arguments+=' -DeleteData'};if($KeepApplicationFiles){$arguments+=' -KeepApplicationFiles'}
        try{$elevated=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru}catch{throw 'Administrator approval was canceled or unavailable. Program files were kept.'}
        if($elevated.ExitCode -ne 0){throw 'Cleanup did not finish; some items remain. Task cleanup must succeed before program deletion. Run Uninstall.cmd again as administrator.'}
    }else{
        if($PSCmdlet.ShouldProcess($context.Exe,'Remove Windows integration and uninstall')){Invoke-LedgerUninstall $context $folder -DeleteHistory:$DeleteData -KeepFiles:$KeepApplicationFiles | ConvertTo-Json}
    }
    if(-not $Quiet){$message='Uninstall completed. Scheduled tasks, startup entries and matching shortcuts were removed.';if(-not $DeleteData){$message+="`nHistory was deliberately kept at: "+$context.Data}else{$message+="`nTraffic data was deleted."};[Windows.Forms.MessageBox]::Show($message,'Data Traffic Ledger') | Out-Null}
    exit 0
}catch{
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
    if(-not $Quiet){Add-Type -AssemblyName System.Windows.Forms;[Windows.Forms.MessageBox]::Show($_.Exception.Message,'Uninstall incomplete',[Windows.Forms.MessageBoxButtons]::OK,[Windows.Forms.MessageBoxIcon]::Error) | Out-Null}
    exit 1
}
