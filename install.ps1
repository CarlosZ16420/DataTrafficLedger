param([switch]$NoLaunch,[switch]$EnableWindowsCapture,[string]$ImportData,[string]$InstallRoot)
$ErrorActionPreference='Stop'
$sourceRoot=$PSScriptRoot
$sourceExe=Join-Path $sourceRoot 'dist\LocalTrafficLedger.exe'
if(-not (Test-Path -LiteralPath $sourceExe)){throw '请先运行 build.ps1，或使用包含 dist 文件夹的完整安装包。'}
if(-not $InstallRoot){$InstallRoot=$sourceRoot}
$InstallRoot=[IO.Path]::GetFullPath($InstallRoot)
$dataRoot=Join-Path $InstallRoot 'data'
$appRoot=Join-Path $InstallRoot 'app'
$installedExe=Join-Path $appRoot 'LocalTrafficLedger.exe'
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
if($ImportData -and -not (Test-Path -LiteralPath (Join-Path $dataRoot 'traffic.db'))){
    $importRoot=[IO.Path]::GetFullPath($ImportData)
    if(Test-Path -LiteralPath (Join-Path $importRoot 'runtime.json')){throw '导入前需要先正常停止来源实例。'}
    $sourceDatabase=Join-Path $importRoot 'traffic.db'
    if(-not (Test-Path -LiteralPath $sourceDatabase)){throw '没有找到要导入的数据库。'}
    # A packaged launcher's AppData may be encrypted. Copy contents without its filesystem attributes.
    $inputStream=[IO.File]::OpenRead($sourceDatabase)
    try{$outputStream=[IO.File]::Create((Join-Path $dataRoot 'traffic.db'));try{$inputStream.CopyTo($outputStream)}finally{$outputStream.Dispose()}}finally{$inputStream.Dispose()}
}
if(Test-Path -LiteralPath $installedExe){
    $stopProcess=Start-Process -FilePath $installedExe -ArgumentList '--stop' -WindowStyle Hidden -PassThru
    if(-not $stopProcess.WaitForExit(10000)){throw '旧版本没有及时响应，请从托盘退出后再安装。'}
    $deadline=[DateTime]::UtcNow.AddSeconds(12)
    do {
        $running=@(Get-Process -Name LocalTrafficLedger -ErrorAction SilentlyContinue | Where-Object { try{$_.Path -eq $installedExe}catch{$false} })
        if(-not $running.Count){break}
        Start-Sleep -Milliseconds 300
    } while([DateTime]::UtcNow -lt $deadline)
    if($running.Count){throw '旧版本还在保存记录，请稍后重新安装；历史数据库没有被修改。'}
}
Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force
Copy-Item -LiteralPath ($sourceExe+'.config') -Destination ($installedExe+'.config') -Force
[IO.File]::WriteAllText((Join-Path $appRoot 'portable.json'),'{"data":"../data"}',[Text.UTF8Encoding]::new($false))
$setup=Start-Process -FilePath $installedExe -ArgumentList @('--install-startup','--data',('"'+$dataRoot+'"')) -WindowStyle Hidden -PassThru
$setup.WaitForExit()
if($setup.ExitCode -ne 0){throw '自启动设置失败。'}
$shortcutShell=New-Object -ComObject WScript.Shell
foreach($shortcutRoot in @([Environment]::GetFolderPath('DesktopDirectory'),[Environment]::GetFolderPath('Programs'))){
    if(-not $shortcutRoot){continue}
    $shortcut=$shortcutShell.CreateShortcut((Join-Path $shortcutRoot '流量账本.lnk'))
    $shortcut.TargetPath=$installedExe
    $shortcut.Arguments='--open --data "'+$dataRoot+'"'
    $shortcut.WorkingDirectory=$appRoot
    $shortcut.Description='查看这台电脑的应用、网站和代理节点流量历史'
    $shortcut.IconLocation=$installedExe+',0'
    $shortcut.Save()
    $legacyPath=Join-Path $shortcutRoot '本机流量账本.lnk'
    if(Test-Path -LiteralPath $legacyPath){$legacy=$shortcutShell.CreateShortcut($legacyPath);if($legacy.TargetPath -eq $installedExe){Remove-Item -LiteralPath $legacyPath}}
}
if(-not $NoLaunch){Start-Process -FilePath $installedExe -ArgumentList @('--background','--data',('"'+$dataRoot+'"')) -WindowStyle Hidden}
if($EnableWindowsCapture){
    Start-Process -FilePath $installedExe -ArgumentList @('--setup-etw','--data',('"'+$dataRoot+'"')) -Verb RunAs -WindowStyle Hidden
}
[pscustomobject]@{Application=$installedExe;Data=$dataRoot;Startup='已启用，登录后在托盘运行';Shortcut='桌面和开始菜单：流量账本'} | ConvertTo-Json
