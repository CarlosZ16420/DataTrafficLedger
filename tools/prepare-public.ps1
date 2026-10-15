param([string]$SourceRef='origin/main')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$commit=(& git -C $root rev-parse --verify ($SourceRef+'^{commit}')).Trim()
if($LASTEXITCODE -ne 0){throw '找不到要导出的已提交版本。'}
$output=Join-Path $root ('releases\public-export-'+$commit.Substring(0,12))
if(Test-Path -LiteralPath $output){throw '该版本已导出，请复用已有快照或使用新的提交。'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$archive=Join-Path $root ('releases\public-source-'+$commit.Substring(0,12)+'.zip')
$allowed=@('.gitattributes','.gitignore','.github','README.md','CHANGELOG.md','LICENSE','build.ps1','package.ps1','install.ps1','安装.cmd','卸载.ps1','src','web','tests','docs','assets','tools')
$files=@(& git -C $root ls-tree -r --name-only $commit -- @allowed)
if($LASTEXITCODE -ne 0){throw '无法读取源提交文件清单。'}
foreach($file in $files){
    if($file -match '(^|/)(data|app|dist|releases|public-snapshot|sources|output|chrome-[^/]*|live[^/]*|run-[^/]*)/' -or $file -match '\.(db|sqlite|sqlite3|log|csv|zip|exe)$' -or $file -match '(^|/)(runtime|settings|capture-state)\.json$'){
        throw "不允许发布运行资料：$file"
    }
}
& git -C $root archive --format=zip "--output=$archive" $commit -- @allowed
if($LASTEXITCODE -ne 0){throw '导出失败。'}
Expand-Archive -LiteralPath $archive -DestinationPath $output
# Git history and remote configuration are deliberately not included.
[pscustomobject]@{SourceCommit=$commit;Directory=$output;Files=$files.Count;SourceArchive=$archive} | ConvertTo-Json
