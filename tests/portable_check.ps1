param([string]$Archive=(Join-Path $PSScriptRoot '..\releases\LocalTrafficLedger-v1.2.0-win-x64-portable.zip'))
$ErrorActionPreference='Stop'
$testRoot=Join-Path $PSScriptRoot ('run-portable-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$extract=Join-Path $testRoot '解压 即用'
Expand-Archive -LiteralPath $Archive -DestinationPath $extract
$appDirectory=(Get-ChildItem -LiteralPath $extract -Directory | Select-Object -First 1).FullName
$taskProcess=$null
function Start-Portable([string]$directory){
    $exe=Join-Path $directory 'LocalTrafficLedger.exe'
    $script:taskProcess=Start-Process -FilePath $exe -ArgumentList '--headless' -WindowStyle Hidden -PassThru
    $runtime=Join-Path $directory 'data\runtime.json'
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do{if(Test-Path -LiteralPath $runtime){try{return [IO.File]::ReadAllText($runtime) | ConvertFrom-Json}catch{}};Start-Sleep -Milliseconds 150}while([DateTime]::UtcNow -lt $deadline)
    throw '便携程序没有正常启动。'
}
function Request-Portable($state,[string]$path,[switch]$Post){
    $headers=@{Cookie='ledger_session='+$state.token}
    if($Post){$headers['X-Ledger-Token']=$state.token}
    Invoke-RestMethod -Uri ('http://127.0.0.1:'+$state.port+$path) -Headers $headers -Method $(if($Post){'Post'}else{'Get'}) -NoProxy -TimeoutSec 10
}
function Stop-Portable($state){
    Request-Portable $state '/api/quit' -Post | Out-Null
    if(-not $script:taskProcess.WaitForExit(15000)){throw '便携程序没有正常退出。'}
    $script:taskProcess=$null
}
try{
    $state=Start-Portable $appDirectory
    $status=Request-Portable $state '/api/status'
    if(-not $status.portable -or $status.version -ne '1.2.0' -or $status.dataDirectory -ne (Join-Path $appDirectory 'data')){throw '便携数据位置错误。'}
    Request-Portable $state '/api/preferences?theme=dark&autoRefresh=true' -Post | Out-Null
    Stop-Portable $state
    $moved=Join-Path $testRoot '搬迁后 新目录'
    $absoluteRoot=[IO.Path]::GetFullPath($testRoot)+[IO.Path]::DirectorySeparatorChar
    foreach($target in @($appDirectory,$moved)){if(-not [IO.Path]::GetFullPath($target).StartsWith($absoluteRoot,[StringComparison]::OrdinalIgnoreCase)){throw '移动目标超出测试目录。'}}
    Move-Item -LiteralPath $appDirectory -Destination $moved
    $state=Start-Portable $moved
    $status=Request-Portable $state '/api/status'
    if($status.dataDirectory -ne (Join-Path $moved 'data') -or $status.preferences.theme -ne 'dark' -or -not $status.preferences.autoRefresh){throw '搬迁后没有保留数据和偏好。'}
    if(-not (Test-Path -LiteralPath (Join-Path $moved 'data\traffic.db'))){throw '便携数据库未创建。'}
    Stop-Portable $state
    Write-Output 'PASS: ZIP extraction, Chinese/spaced paths, native startup, adjacent data, clean exit, directory move and preference persistence.'
}finally{
    if($taskProcess -and -not $taskProcess.HasExited){$taskProcess.Kill();$taskProcess.WaitForExit()}
    # Test-only artifacts remain ignored for inspection; no user data is touched.
}
