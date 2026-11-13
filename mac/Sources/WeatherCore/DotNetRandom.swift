/// 与 .NET `new Random(seed)` 逐位一致的伪随机数发生器（Knuth 减法发生器，.NET 的 Net5CompatSeedImpl）。
/// Windows 版的合成器都用带种子的 System.Random，移植后同一种子得到同一串数，测试才能和 C# 输出逐样本比对。
/// 全部用 Int32 环绕运算，复现 C# unchecked 整数的溢出行为。
public final class DotNetRandom {
    private var seedArray = [Int32](repeating: 0, count: 56)
    private var inext = 0
    private var inextp = 21

    public init(seed: Int32) {
        let subtraction: Int32 = seed == Int32.min ? Int32.max : abs(seed)
        var mj: Int32 = 161_803_398 &- subtraction
        seedArray[55] = mj
        var mk: Int32 = 1
        var ii = 0
        for _ in 1..<55 {
            ii += 21
            if ii >= 55 { ii -= 55 }
            seedArray[ii] = mk
            mk = mj &- mk
            if mk < 0 { mk = mk &+ Int32.max }
            mj = seedArray[ii]
        }
        for _ in 1..<5 {
            for i in 1..<56 {
                var n = i + 30
                if n >= 55 { n -= 55 }
                seedArray[i] = seedArray[i] &- seedArray[1 + n]
                if seedArray[i] < 0 { seedArray[i] = seedArray[i] &+ Int32.max }
            }
        }
    }

    /// 不指定种子：每次运行不同（对应 C# 的 `new Random()`）。
    public convenience init() {
        self.init(seed: Int32.random(in: 1...Int32.max))
    }

    @inline(__always)
    private func internalSample() -> Int32 {
        var locINext = inext + 1
        if locINext >= 56 { locINext = 1 }
        var locINextp = inextp + 1
        if locINextp >= 56 { locINextp = 1 }
        var retVal = seedArray[locINext] &- seedArray[locINextp]
        if retVal == Int32.max { retVal -= 1 }
        if retVal < 0 { retVal = retVal &+ Int32.max }
        seedArray[locINext] = retVal
        inext = locINext
        inextp = locINextp
        return retVal
    }

    /// [0, 1)
    @inline(__always)
    public func nextDouble() -> Double {
        Double(internalSample()) * (1.0 / Double(Int32.max))
    }

    /// [0, maxValue)
    @inline(__always)
    public func next(_ maxValue: Int) -> Int {
        Int(nextDouble() * Double(maxValue))
    }

    /// [0, Int32.max)
    public func next() -> Int32 {
        internalSample()
    }
}
