// swift-tools-version:5.10
import PackageDescription

let package = Package(
    name: "LightningWeather",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "LightningWeather", targets: ["LightningWeather"]),
        .executable(name: "weather-render", targets: ["weather-render"]),
    ],
    targets: [
        // 纯计算：雨声/风声/雷声合成、天气引擎、雨景模拟、设置。只依赖 Foundation，可离线测试。
        .target(name: "WeatherCore"),
        // 读取系统通知数据库（usernoted 的 SQLite 库）。
        .target(name: "NotifyCore", linkerSettings: [.linkedLibrary("sqlite3")]),
        // 把雨景模拟画成 CALayer 树；应用和离线渲染工具共用，保证 CI 截出来的图就是应用画的。
        .target(name: "RainLayers", dependencies: ["WeatherCore"]),
        .executableTarget(name: "LightningWeather", dependencies: ["WeatherCore", "NotifyCore", "RainLayers"]),
        // 开发用：离线渲染 WAV / PNG 和分析报告，CI 上传成产物供在 Windows 上试听、查看。
        .executableTarget(name: "weather-render", dependencies: ["WeatherCore", "RainLayers", "NotifyCore"]),
        .testTarget(name: "WeatherCoreTests", dependencies: ["WeatherCore"], resources: [.copy("Fixtures")]),
        .testTarget(name: "NotifyCoreTests", dependencies: ["NotifyCore"]),
    ]
)
