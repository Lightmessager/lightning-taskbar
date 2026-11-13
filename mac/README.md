# ⚡ 闪电天气（macOS 版）

Windows 版 Lightning Taskbar 的天气和通知部分移植到 macOS：一个只在菜单栏显示的小工具，**不替换** Dock 和菜单栏。

- **桌面雷雨**：雨层放在壁纸之上、桌面图标和所有窗口之下，鼠标完全穿透，出现在所有桌面空间（Space）里，可以每块显示器都下。多层景深雨丝、地面溅射、乌云压暗、分叉闪电与云层照亮、底部雨雾，参数与 Windows 版一致。被全屏或铺满屏幕的窗口挡住时暂停绘制，声音照常。
- **音效全程序化合成**：雨声、风声、雷声的算法逐行移植自 Windows 版，同一种子下输出与 C# 版逐样本一致（见下文“验证”）。雷声按“光速 vs 声速”延迟，越亮越近越响。插拔耳机、切换输出设备、睡眠唤醒后自动恢复。
- **玻璃通知**：读取系统通知，在左上角或右上角显示玻璃卡片（macOS 26 用系统液态玻璃，更早的系统用磨砂），6 秒后收起，鼠标停留时不消失，点击打开对应应用。开着“专注模式”时不弹。
- 菜单栏菜单：晴 / 雨 / 雷雨、打个雷、音效开关和音量、玻璃通知开关和位置、所有显示器都下雨、登录时启动、退出。
- **设置窗口**：首次启动会弹出；以后再次打开应用（双击或聚焦搜索）也会弹出。有刘海的 MacBook 上菜单栏图标多了会被藏到刘海后面，这时就靠它；也可以按住 ⌘ 把菜单栏图标拖到左边。窗口里还显示通知读取状态。
- **来通知时屏幕边缘闪光**：每弹出一张通知卡片，屏幕四周亮起一圈流动的紫蓝粉渐变光（类似 ChatGPT / Apple 智能），约 2.4 秒后淡出。可在设置里关掉。
- **诊断日志**：`~/Library/Logs/LightningWeather.log`，记录启动、通知库状态、每条新通知是显示还是为什么跳过。设置窗口里点“诊断日志”可以直接找到它。
- 设置保存在 `~/Library/Application Support/LightningWeather/settings.json`。启动参数 `--rain` / `--storm` 可直接指定天气。

## 关于通知（请先读）

macOS **没有**让第三方应用读取其它应用通知的公开接口（Windows 有 `UserNotificationListener`），所以这里直接读通知中心的数据库：

- macOS 15 起在 `~/Library/Group Containers/group.com.apple.usernoted/db2/db`，更早在 `$(getconf DARWIN_USER_DIR)/com.apple.notificationcenter/db2/db`。
- 这个位置受隐私保护，需要在 **系统设置 › 隐私与安全性 › 完全磁盘访问权限** 里加入“闪电天气”。没授权时菜单里会出现“需要完全磁盘访问权限…”，点一下直接跳到设置页。
- 这是非公开格式，系统大版本升级可能改变；读不到时只是不显示玻璃卡片，其它功能不受影响。
- 系统自己的通知横幅**仍会弹出**。如果只想看玻璃卡片：在 系统设置 › 通知 里把对应应用的提醒样式改成“无”——通知仍会进通知中心，也就仍能被读到。
- 专注模式只检测手动打开的（读 `~/Library/DoNotDisturb/DB/Assertions.json`）；按时间表自动开启的可能检测不到。

## 安装

从 GitHub Actions 的 `LightningWeather-macos-15` 产物下载 `LightningWeather-macOS.zip`，解压后把 `LightningWeather.app` 拖进“应用程序”。

应用是临时签名（没有 Apple 开发者证书），第一次打开会被拦下。任选其一：

- 在访达里右键应用 › 打开 › 打开；
- 或在终端执行：

```bash
xattr -dr com.apple.quarantine /Applications/LightningWeather.app
```

注意：临时签名的应用每次重新编译后，系统会把它当成新应用，“完全磁盘访问权限”需要重新勾选一次。

## 自己编译

需要 macOS 14+ 和 Xcode 16+（或 Command Line Tools）。

```bash
cd mac && swift test
```

```bash
cd mac && bash scripts/bundle.sh
```

产物在 `mac/build/LightningWeather.app` 和 `mac/build/LightningWeather-macOS.zip`。

## 验证（没有 Mac 也能检查）

`.github/workflows/mac.yml` 在 GitHub 的 macOS 虚拟机上（macOS 15 和 26 各一份）：

1. **单元测试**：`DotNetParityTests` 把 Swift 版雨声、风声、雷声和随机数发生器的输出，与 Windows 上用原版 C# 代码同种子生成的参考数据（`Tests/WeatherCoreTests/Fixtures`）逐样本比对，日志里会打印最大误差；还有天气引擎、雨景模拟、设置读写、混音器和通知数据库读取的测试。
2. **离线渲染**（`weather-render`）：`scene.wav`（与 Windows 版 `RainRender.RunScene` 相同的 70 秒脚本）、`mixer.wav`（经过实时混音器）、`scene.txt`（响度与频段分析，格式与 Windows 版相同）和 4 张雨景 PNG。下载 `render-macos-15` 产物即可在 Windows 上试听、查看。
3. **真机截屏**：启动打包好的应用（`--storm --demo-notification --demo-strike`），截下桌面，检查雨层是否在壁纸之上、卡片是否出现。

参考数据的生成程序在 `tools/fixturegen/Program.cs.txt`（扩展名是 .txt，免得被仓库根目录的 WPF 项目一起编译）。重新生成：把它复制到任意空目录改名为 `Program.cs`，旁边放一个 `net10.0` 控制台项目（`AllowUnsafeBlocks`、`ImplicitUsings` 打开），再把仓库里的 `Audio/RainSynth.cs`、`Audio/WindSynth.cs`、`Audio/StormAudio.cs` 复制进去，`dotnet run -- 输出目录`。

## 代码结构

| 目录 | 内容 |
| --- | --- |
| `Sources/WeatherCore` | 纯计算：与 .NET 一致的随机数、雨/风/雷合成、混音器、天气引擎、雨景模拟、设置、WAV 与频谱分析 |
| `Sources/NotifyCore` | 读通知数据库、专注模式状态 |
| `Sources/RainLayers` | 把雨景模拟画成 Core Animation 图层树（应用和离线渲染共用） |
| `Sources/LightningWeather` | 菜单栏应用：雨层窗口、音频输出、遮挡检测、通知卡片、菜单 |
| `Sources/weather-render` | 离线渲染工具 |

## 与 Windows 版的差异

- 不做 Dock、Finder 栏、开始菜单、托盘——macOS 自带。
- 雨层是透明窗口叠在系统壁纸上，不自己画壁纸，动态壁纸也能用。
- 溅射的透明度分 8 档（Windows 版 16 档），肉眼看不出差别。
- 没有任务栏玻璃上的雨珠和闪电照亮任务栏。
