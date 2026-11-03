# 应急恢复：如果 LightningTaskbar 被强制结束导致系统任务栏没回来，运行此脚本。
Get-Process LightningTaskbar -ErrorAction SilentlyContinue | Stop-Process -Force
# 重启资源管理器会重新显示任务栏并重算工作区
Stop-Process -Name explorer -Force
Start-Sleep -Seconds 1
if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
