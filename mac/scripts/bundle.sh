#!/bin/bash
# 编译通用二进制（Apple 芯片 + Intel）并打包成 build/LightningWeather.app，再压成 zip。
# 用临时签名（ad-hoc），没有开发者证书也能运行；首次打开需要右键 › 打开，或去掉隔离属性。
set -euo pipefail
cd "$(dirname "$0")/.."

ARCHS=(--arch arm64 --arch x86_64)
swift build -c release "${ARCHS[@]}" --product LightningWeather
BIN="$(swift build -c release "${ARCHS[@]}" --show-bin-path)/LightningWeather"

APP=build/LightningWeather.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/LightningWeather"
cp scripts/Info.plist "$APP/Contents/Info.plist"

# 应用图标：用代码画出各尺寸，再转成 .icns
rm -rf build/AppIcon.iconset
swift run -c release weather-render --icon build/AppIcon.iconset
iconutil -c icns build/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"
codesign --force --sign - --timestamp=none "$APP"
codesign --verify --verbose "$APP"
lipo -info "$APP/Contents/MacOS/LightningWeather"

rm -f build/LightningWeather-macOS.zip
(cd build && ditto -c -k --keepParent LightningWeather.app LightningWeather-macOS.zip)
echo "打包完成：$(pwd)/build/LightningWeather-macOS.zip"
