# 项目路径含 "&" 和 emoji，dotnet/MSBuild 会解析 SDK 失败，所以把源码单向复制到干净路径再编译。
# 旧版用的是 %TEMP% 下指向源码的目录联接，清理临时文件时会顺着联接把源码一起删掉——不要再用联接。
# 产物：%LOCALAPPDATA%\LightningTaskbar\app\LightningTaskbar.exe
param([switch]$Run)
$ErrorActionPreference = 'Stop'

$root  = Join-Path $env:LOCALAPPDATA 'LightningTaskbar'
$src   = Join-Path $root 'src'
$out   = Join-Path $root 'app'

# 清掉旧版留下的联接（只删联接本身，不碰目标）
$oldLink = Join-Path $env:TEMP 'LightningTaskbarSrc'
if (Test-Path $oldLink) {
    $item = Get-Item $oldLink -Force
    if ($item.LinkType) { [IO.Directory]::Delete($oldLink, $false) }
}

# 源码 → 构建目录（单向镜像，构建目录随便删）
robocopy $PSScriptRoot $src /MIR /XD bin obj .vs /XF *.user /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "复制源码失败（robocopy $LASTEXITCODE）" }

Get-Process LightningTaskbar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

Push-Location $src
try {
    dotnet build -c Release -nologo -v q -o $out
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }

if ($Run) { Start-Process (Join-Path $out 'LightningTaskbar.exe') -WorkingDirectory $out }
