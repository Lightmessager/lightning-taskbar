# 浸泡测试：每隔几秒随机写一个调试触发文件（切天气 / 弹通知 / 播边缘特效），看主进程会不会崩。
# 主进程崩溃后守护进程会自动重启它，所以这里按“主进程 PID 变了”来数崩溃，并在结束时对照事件日志。
# 用法：tools\soak.ps1 -Minutes 60 [-IntervalSeconds 4] [-Log 路径]
param(
    [double]$Minutes = 30,
    [double]$IntervalSeconds = 4,
    [string]$Log = (Join-Path $env:APPDATA 'LightningTaskbar\soak.log')
)
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:APPDATA 'LightningTaskbar'
$exe  = Join-Path $env:LOCALAPPDATA 'LightningTaskbar\app\LightningTaskbar.exe'
$weathers = 'Off','Rain','Storm','LightSnow','Snow','Blizzard','Tornado'
$edges    = 'Glow','StormGlow','Frost','Leaves'

function MainPid {
    $p = @(Get-CimInstance Win32_Process -Filter "Name='LightningTaskbar.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -notmatch '--taskbar-guard|--edge-effect' })
    if ($p.Count -gt 0) { $p[0].ProcessId } else { 0 }
}
function Say($text) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $text
    Add-Content -Path $Log -Value $line -Encoding utf8
    Write-Output $line
}

$startMode = $null
try { $startMode = (Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json).Mode } catch { }
$began = Get-Date
$deadline = $began.AddMinutes($Minutes)
$pid0 = MainPid
if ($pid0 -eq 0) { Start-Process $exe; Start-Sleep 6; $pid0 = MainPid }
Say "开始：$Minutes 分钟，间隔 $IntervalSeconds 秒，主进程 PID $pid0"
$steps = 0; $crashes = 0; $gone = 0
$rng = [Random]::new()

while ((Get-Date) -lt $deadline) {
    $steps++
    switch ($rng.Next(10)) {
        { $_ -lt 4 } { Set-Content (Join-Path $data 'debug-weather') $weathers[$rng.Next($weathers.Count)] -NoNewline; break }
        { $_ -lt 7 } { Set-Content (Join-Path $data 'debug-notify') '1' -NoNewline; break }
        default      { Set-Content (Join-Path $data 'debug-edge') $edges[$rng.Next($edges.Count)] -NoNewline }
    }
    Start-Sleep -Milliseconds ([int]($IntervalSeconds * 1000))

    $now = MainPid
    if ($now -ne $pid0) {
        $crashes++
        if ($now -eq 0) {
            # 守护进程重启要一两秒，再等等
            Start-Sleep 8
            $now = MainPid
        }
        if ($now -eq 0) {
            $gone++
            Say "第 $steps 步：主进程没了，守护进程也没有把它拉起来，手动重启"
            Start-Process $exe; Start-Sleep 6; $now = MainPid
        } else {
            Say "第 $steps 步：主进程崩溃后已被重启（PID $pid0 -> $now）"
        }
        $pid0 = $now
    }
    if ($steps % 150 -eq 0) {
        $p = Get-Process -Id $pid0 -ErrorAction SilentlyContinue
        if ($p) { Say ("第 $steps 步：正常。句柄 {0}，私有内存 {1} MB" -f $p.HandleCount, [int]($p.PrivateMemorySize64 / 1MB)) }
    }
}

# 天气恢复成测试前的样子
if ($null -ne $startMode) {
    $name = @('Off','Rain','Storm','LightSnow','Snow','Blizzard','Tornado')[[int]$startMode]
    if ($name) { Set-Content (Join-Path $data 'debug-weather') $name -NoNewline }
}

$events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error'; StartTime = $began } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'LightningTaskbar' })
Say "结束：$steps 步，主进程崩溃 $crashes 次（其中 $gone 次没有被自动重启），事件日志里新增 $($events.Count) 条 LightningTaskbar 故障"
foreach ($e in $events) {
    $m = [regex]::Match($e.Message, '(?:Faulting module name|错误模块名称): ([^,，]+)').Groups[1].Value
    $c = [regex]::Match($e.Message, '(?:Exception code|异常代码): (\S+)').Groups[1].Value
    Say ("  {0:HH:mm:ss} {1} {2}" -f $e.TimeCreated, $m, $c)
}
