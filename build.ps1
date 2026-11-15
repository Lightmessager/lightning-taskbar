# 项目路径含 "&" 和 emoji，dotnet/MSBuild 会解析 SDK 失败，所以把源码单向复制到干净路径再编译。
# 旧版用的是 %TEMP% 下指向源码的目录联接，清理临时文件时会顺着联接把源码一起删掉——不要再用联接。
# 产物默认在 %LOCALAPPDATA%\LightningTaskbar\app\LightningTaskbar.exe。
# 想装到别处：把目标目录写进 %USERPROFILE%\.lightning-taskbar\install-dir.txt（一行，UTF-8），或者传 -InstallDir。
# （标记文件不放 AppData：从打包应用里的终端运行时，AppData 会被重定向到应用自己的私有目录，换个终端就找不到了。）
# 安装目录可以带 "&"、emoji：先在干净路径里编译，再把产物镜像过去。
param([switch]$Run, [string]$InstallDir)
$ErrorActionPreference = 'Stop'

$root  = Join-Path $env:LOCALAPPDATA 'LightningTaskbar'
$src   = Join-Path $root 'src'
$out   = Join-Path $root 'app'

$installFile = Join-Path $env:USERPROFILE '.lightning-taskbar\install-dir.txt'
if (-not $InstallDir -and (Test-Path -LiteralPath $installFile)) {
    $InstallDir = (Get-Content -LiteralPath $installFile -Encoding utf8 -TotalCount 1).Trim()
}
# 装到别处时，编译产物先放在这个中转目录
if ($InstallDir) { $out = Join-Path $root 'build' }

# 清掉旧版留下的联接（只删联接本身，不碰目标）
$oldLink = Join-Path $env:TEMP 'LightningTaskbarSrc'
if (Test-Path $oldLink) {
    $item = Get-Item $oldLink -Force
    if ($item.LinkType) { [IO.Directory]::Delete($oldLink, $false) }
}

# 源码 → 构建目录（单向镜像，构建目录随便删）
robocopy $PSScriptRoot $src /MIR /XD bin obj .vs /XF *.user /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "复制源码失败（robocopy $LASTEXITCODE）" }

# 先结束守护进程（--taskbar-guard），再结束主进程。主进程被强制结束时守护进程本来也只恢复系统任务栏、不会重启，
# 但它不退出的话会占着 exe，编译产物写不进去。
$all = @(Get-CimInstance Win32_Process -Filter "Name='LightningTaskbar.exe'" -ErrorAction SilentlyContinue)
$all | Where-Object { $_.CommandLine -match '--taskbar-guard' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-Process LightningTaskbar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Push-Location $src
try {
    dotnet build -c Release -nologo -v q -o $out
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }

$app = $out
if ($InstallDir) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    # 只覆盖和补充文件，不用 /MIR：安装目录是用户指定的，里面可能有别的东西，不能删
    robocopy $out $InstallDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "复制到安装目录失败（robocopy $LASTEXITCODE）" }
    $app = $InstallDir
}

if ($Run) { Start-Process -FilePath (Join-Path $app 'LightningTaskbar.exe') -WorkingDirectory $app }
# robocopy 成功时的退出码不是 0，别让它变成整个脚本的退出码
$global:LASTEXITCODE = 0
