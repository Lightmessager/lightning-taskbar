import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// 应用图标：深蓝的雷雨夜空、一朵白云、发光的黄色闪电和斜雨，外形是 macOS 标准的圆角方形加玻璃高光。
/// 用代码画，打包时生成 .iconset 再由 iconutil 转成 .icns，不需要任何图片素材。
/// 坐标按 1024×1024 设计，y 轴向上。
enum AppIcon {
    private static func rgba(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat = 1) -> CGColor {
        CGColor(srgbRed: r / 255, green: g / 255, blue: b / 255, alpha: a)
    }

    private static func linear(_ ctx: CGContext, _ colors: [CGColor], _ locations: [CGFloat], from: CGPoint, to: CGPoint) {
        let g = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: colors as CFArray, locations: locations)!
        ctx.drawLinearGradient(g, start: from, end: to, options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
    }

    private static func radial(_ ctx: CGContext, _ colors: [CGColor], center: CGPoint, radius: CGFloat) {
        let g = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: colors as CFArray, locations: [0, 1])!
        ctx.drawRadialGradient(g, startCenter: center, startRadius: 0, endCenter: center, endRadius: radius, options: [])
    }

    static let body = CGRect(x: 100, y: 100, width: 824, height: 824)

    static var cloud: CGPath {
        var p: CGPath = CGPath(roundedRect: CGRect(x: 270, y: 470, width: 500, height: 150), cornerWidth: 75, cornerHeight: 75, transform: nil)
        for (cx, cy, r) in [(390.0, 625.0, 115.0), (535.0, 690.0, 165.0), (680.0, 615.0, 110.0)] {
            p = p.union(CGPath(ellipseIn: CGRect(x: cx - r, y: cy - r, width: r * 2, height: r * 2), transform: nil))
        }
        return p
    }

    static var bolt: CGPath {
        let pts: [CGPoint] = [
            CGPoint(x: 575, y: 520), CGPoint(x: 455, y: 330), CGPoint(x: 530, y: 330), CGPoint(x: 460, y: 150),
            CGPoint(x: 640, y: 385), CGPoint(x: 560, y: 385), CGPoint(x: 640, y: 520),
        ]
        let p = CGMutablePath()
        p.addLines(between: pts)
        p.closeSubpath()
        return p
    }

    static func draw(in ctx: CGContext, size: CGFloat) {
        ctx.saveGState()
        ctx.scaleBy(x: size / 1024, y: size / 1024)
        let shape = CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil)

        // 底座投影
        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: rgba(0, 0, 0, 0.35))
        ctx.addPath(shape)
        ctx.setFillColor(rgba(14, 20, 48))
        ctx.fillPath()
        ctx.restoreGState()

        ctx.saveGState()
        ctx.addPath(shape)
        ctx.clip()

        // 雷雨夜空
        linear(ctx, [rgba(64, 80, 140), rgba(24, 30, 68), rgba(9, 12, 30)], [0, 0.55, 1],
               from: CGPoint(x: 512, y: 924), to: CGPoint(x: 512, y: 100))
        // 闪电照亮的一团光
        radial(ctx, [rgba(255, 214, 110, 0.38), rgba(255, 214, 110, 0)], center: CGPoint(x: 540, y: 360), radius: 380)

        // 斜雨
        ctx.setLineCap(.round)
        ctx.setLineWidth(10)
        ctx.setStrokeColor(rgba(170, 200, 255, 0.38))
        for (i, y) in [235.0, 335.0, 435.0].enumerated() {
            var x = 225.0 + Double(i % 2) * 55
            while x < 830 {
                ctx.move(to: CGPoint(x: x, y: y))
                ctx.addLine(to: CGPoint(x: x - 18, y: y - 72))
                x += 110
            }
        }
        ctx.strokePath()

        // 闪电：先画一层大范围辉光，再用渐变填本体
        ctx.saveGState()
        ctx.setShadow(offset: .zero, blur: 70, color: rgba(255, 196, 60, 0.95))
        ctx.addPath(bolt)
        ctx.setFillColor(rgba(255, 210, 80))
        ctx.fillPath()
        ctx.restoreGState()
        ctx.saveGState()
        ctx.addPath(bolt)
        ctx.clip()
        linear(ctx, [rgba(255, 252, 225), rgba(255, 204, 60)], [0, 1], from: CGPoint(x: 0, y: 520), to: CGPoint(x: 0, y: 150))
        ctx.restoreGState()

        // 云：压住闪电根部
        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -16), blur: 34, color: rgba(0, 0, 25, 0.5))
        ctx.addPath(cloud)
        ctx.setFillColor(rgba(222, 230, 246))
        ctx.fillPath()
        ctx.restoreGState()
        ctx.saveGState()
        ctx.addPath(cloud)
        ctx.clip()
        linear(ctx, [rgba(255, 255, 255), rgba(192, 205, 236)], [0, 1], from: CGPoint(x: 0, y: 855), to: CGPoint(x: 0, y: 470))
        ctx.restoreGState()

        // 玻璃高光：上半部一层淡淡的白
        linear(ctx, [rgba(255, 255, 255, 0.20), rgba(255, 255, 255, 0)], [0, 1],
               from: CGPoint(x: 0, y: 924), to: CGPoint(x: 0, y: 600))
        ctx.restoreGState()

        // 边缘亮线
        ctx.addPath(CGPath(roundedRect: body.insetBy(dx: 2, dy: 2), cornerWidth: 183, cornerHeight: 183, transform: nil))
        ctx.setStrokeColor(rgba(255, 255, 255, 0.22))
        ctx.setLineWidth(4)
        ctx.strokePath()
        ctx.restoreGState()
    }

    static func png(size: Int) -> Data? {
        guard let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.interpolationQuality = .high
        draw(in: ctx, size: CGFloat(size))
        guard let image = ctx.makeImage() else { return nil }
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(dest, image, nil)
        return CGImageDestinationFinalize(dest) ? data as Data : nil
    }

    /// 写出 iconutil 需要的 AppIcon.iconset 目录。
    static func writeIconset(to dir: URL) throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        for base in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let name = scale == 1 ? "icon_\(base)x\(base).png" : "icon_\(base)x\(base)@2x.png"
                guard let data = png(size: base * scale) else { continue }
                try data.write(to: dir.appendingPathComponent(name))
            }
        }
    }
}
