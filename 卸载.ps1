param([switch]$KeepApplicationFiles)
$ErrorActionPreference='Stop'
$dataRoot=Join-Path $PSScriptRoot 'data'
$appRoot=Join-Path $PSScriptRoot 'app'
$exe=Join-Path $appRoot 'LocalTrafficLedger.exe'
if(Test-Path -LiteralPath $exe){
    $stopProcess=Start-Process -FilePath $exe -ArgumentList '--stop' -WindowStyle Hidden -PassThru
    $stopProcess.WaitForExit(10000) | Out-Null
    $removeStartup=Start-Process -FilePath $exe -ArgumentList '--remove-startup' -WindowStyle Hidden -PassThru
    $removeStartup.WaitForExit(10000) | Out-Null
}
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$sha=[Security.Cryptography.SHA256]::Create()
try{$userKey=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sid)))).Replace('-','').Substring(0,12)}finally{$sha.Dispose()}
try{Unregister-ScheduledTask -TaskName ('LocalTrafficLedger-ETW-'+$userKey) -Confirm:$false -ErrorAction Stop}catch{Write-Host '若启用过 Windows 补充采集，请以管理员身份再次运行此脚本，以移除其专用计划任务。'}
try{Unregister-ScheduledTask -TaskName ('LocalTrafficLedger-Tray-'+$userKey) -Confirm:$false -ErrorAction Stop}catch{}
foreach($shortcutRoot in @([Environment]::GetFolderPath('DesktopDirectory'),[Environment]::GetFolderPath('Programs'))){
    if($shortcutRoot){$shortcut=Join-Path $shortcutRoot '本机流量账本.lnk';if(Test-Path -LiteralPath $shortcut){Remove-Item -LiteralPath $shortcut}}
}
if(-not $KeepApplicationFiles){
    # Delete only these two known binaries. The data directory and historical ledger are retained.
    Start-Sleep -Milliseconds 500
    foreach($file in @($exe,($exe+'.config'))){if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file}}
}
Write-Host ('应用已停止，自启动和快捷方式已移除。历史记录保留在：'+$dataRoot)
