param([string]$Version='1.2.0',[switch]$SkipBuild)
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-zA-Z0-9.]+)?$'){throw '版本号格式无效。'}
if(-not $SkipBuild){& (Join-Path $PSScriptRoot 'build.ps1') -Test}
$outputRoot=Join-Path $PSScriptRoot 'releases'
$name="LocalTrafficLedger-v$Version-win-x64-portable"
$staging=Join-Path $outputRoot ('staging-'+[Guid]::NewGuid().ToString('N'))
$packageRoot=Join-Path $staging $name
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
# Only explicitly listed release files enter the ZIP. No runtime or user data is copied.
foreach($file in @('LocalTrafficLedger.exe','LocalTrafficLedger.exe.config')){
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "dist\$file") -Destination (Join-Path $packageRoot $file)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $packageRoot
[IO.File]::WriteAllText((Join-Path $packageRoot 'portable.json'),'{"data":"data"}',[Text.UTF8Encoding]::new($false))
$guide=@'
本机流量账本 / Local Traffic Ledger

完整解压到一个可写文件夹，双击 LocalTrafficLedger.exe 即可使用。
不需要安装，不需要 Node.js、Python 或 .NET SDK。
运行要求：Windows 10/11 x64，.NET Framework 4.8（Windows 11 自带）。

程序在系统托盘运行，用默认浏览器打开本地仪表盘。
关闭网页后继续采集；从托盘退出才会停止。
双击程序或点击托盘图标可以重新打开。无需记住端口。

数据保存在程序相邻的 data 文件夹，退出后可一起备份或搬迁。
首次启动不会自动开启开机自启动；可在网页设置或托盘右键 → 设置中切换。
移动目录前先关闭自启动、退出程序；移动后重新开启。
如果忘记关闭自启动，设置面板会提示修复原来的路径。
Windows 补充采集的授权绑定程序路径；移动后需重新启用并授权。

支持 Clash Verge / Mihomo 本机命名管道。可选 Windows 补充采集需一次管理员授权。
统计从启动后开始，不能恢复此前记录；短连接和中断时段可能存在采样差额。
默认自动选择空闲回环端口，仅当前电脑可访问。固定端口被占用时自动回退。

更新：退出旧程序，用新 EXE、EXE.config 和 portable.json 替换原文件，保留 data。
卸载：先关闭自启动；如启用补充采集，以管理员身份运行：
LocalTrafficLedger.exe --disable-etw
然后从托盘退出，再删除整个文件夹。删除 data 会永久删除你的历史账本。

本项目由 AI（OpenAI Codex）根据维护者需求完成开发，采用 MIT 许可证。
源码和反馈：https://github.com/CarlosZ16420/LocalTrafficLedger
友情链接：LINUX DO https://linux.do/
'@
[IO.File]::WriteAllText((Join-Path $packageRoot '使用说明.txt'),$guide,[Text.UTF8Encoding]::new($true))
$zip=Join-Path $outputRoot ($name+'.zip')
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zip -Force
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zip+'.sha256'),$hash+'  '+[IO.Path]::GetFileName($zip)+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
    $expected=@('LICENSE','LocalTrafficLedger.exe','LocalTrafficLedger.exe.config','portable.json','使用说明.txt') | ForEach-Object {"$name/$_"}
    $actual=@($archive.Entries | ForEach-Object {$_.FullName.Replace('\','/')})
    if(@(Compare-Object $expected $actual).Count){throw '发布包清单与允许文件不一致。'}
}finally{$archive.Dispose()}
[pscustomobject]@{Archive=$zip;SHA256=$hash;Files=$actual.Count;Unpacked=$packageRoot} | ConvertTo-Json
