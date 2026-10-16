param([string]$Version='1.5.1',[switch]$SkipBuild)
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$'){throw '版本号格式无效。'}
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -Test}
$outputRoot=Join-Path $PSScriptRoot 'releases'
$name="DataTrafficLedger-v$Version-win-x64-portable"
$staging=Join-Path $outputRoot ('staging-'+[Guid]::NewGuid().ToString('N'))
$packageRoot=Join-Path $staging $name
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
# Only explicitly listed release files enter the ZIP. No runtime or user data is copied.
foreach($file in @('DataTrafficLedger.exe','DataTrafficLedger.exe.config')){
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "dist\$file") -Destination (Join-Path $packageRoot $file)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $packageRoot
foreach($file in @('Uninstall.cmd','Uninstall.ps1')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $packageRoot}
[IO.File]::WriteAllText((Join-Path $packageRoot 'portable.json'),'{"data":"data"}',[Text.UTF8Encoding]::new($false))
$guide=@'
Data Traffic Ledger

Extract the entire ZIP into a writable folder and run DataTrafficLedger.exe.
No installation, Node.js, Python or .NET SDK is required.
Requires Windows 10/11 x64 and .NET Framework 4.8 (included in Windows 11).
The application interface is in Simplified Chinese. Package names and this guide are English.

The application runs in the system tray and opens a local browser dashboard.
Closing the page does not stop collection. Exit from the tray to stop it.
Click the tray icon or run the EXE again to open the dashboard.
An available loopback port is assigned automatically; no fixed port is needed.
If a configured fixed port is occupied, the application falls back to a free port.

Data is saved in the adjacent data folder. Exit before backing up or moving it.
Startup is disabled on first portable launch. Enable it in Settings or the tray menu.
Before moving the folder, disable startup and exit; enable startup after moving.
Settings can repair a startup entry that still points to the old location.

Clash Verge / Mihomo traffic is read through local named pipes.
Optional Windows ETW collection requires administrator approval ONCE per Windows
account and application/data location. The approved scheduled task is reused on
later launches without another UAC prompt. Enable application startup to resume
automatically after Windows login. If the task is removed, the account changes,
or the application/data folder moves, enable Windows collection again to authorize
the new location. TUN mode can temporarily suspend it to prevent double counting.

Collection starts when the application runs. Earlier or missed traffic cannot be
recovered. Sampling can miss short connections and does not equal a billing meter.
Path badges and actual proxy node names appear at every level of traffic details.

Update: exit the old version, replace program files from the new ZIP,
and KEEP the data folder.
Before v1.5.1 the executable was named LocalTrafficLedger.exe.
After upgrading, run DataTrafficLedger.exe, repair startup in Settings if enabled,
and reauthorize Windows collection if previously enabled to update its EXE path.
Then remove the old LocalTrafficLedger.exe and its .config file if present.
Uninstall: run Uninstall.cmd, or use the uninstall entry in the tray Settings menu.
Confirm uninstall and choose whether to keep or permanently delete traffic data.
If Windows collection was authorized, approve administrator access for the SAME
Windows account. Cleanup removes the approved capture task, login startup task,
matching shortcuts and program files. Task removal is verified before deleting
program files. If approval is canceled or cleanup fails, retry the uninstaller.
Simply deleting the folder does NOT remove Windows scheduled tasks.
Unknown files in the application folder are kept. Back up history if needed.

Built by AI (OpenAI Codex) from maintainer requirements. Licensed under MIT.
Source and feedback: https://github.com/CarlosZ16420/DataTrafficLedger
Community: LINUX DO https://linux.do/
'@
[IO.File]::WriteAllText((Join-Path $packageRoot 'README.txt'),$guide,[Text.UTF8Encoding]::new($false))
$zip=Join-Path $outputRoot ($name+'.zip')
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zip -Force
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zip+'.sha256'),$hash+'  '+[IO.Path]::GetFileName($zip)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
    $expected=@('LICENSE','DataTrafficLedger.exe','DataTrafficLedger.exe.config','portable.json','README.txt','Uninstall.cmd','Uninstall.ps1') | ForEach-Object {"$name/$_"}
    $actual=@($archive.Entries | ForEach-Object {$_.FullName.Replace('\','/')})
    if(@(Compare-Object $expected $actual).Count){throw '发布包清单与允许文件不一致。'}
    if(@($actual | Where-Object {$_ -match '[^\x20-\x7e]'}).Count -or $guide -match '[^\x00-\x7f]'){throw 'Release filenames and README.txt must use English ASCII characters.'}
}finally{$archive.Dispose()}
[pscustomobject]@{Archive=$zip;SHA256=$hash;Files=$actual.Count;Unpacked=$packageRoot} | ConvertTo-Json
