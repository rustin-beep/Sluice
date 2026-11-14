namespace Sluice;

/// <summary>平台展示名单源（v2.38 Linux 批）：文案里凡平台相关者从此取——
/// §8 消息语义不变，只换指称；selftest 锁两侧各自的完整句。</summary>
internal static class Platform
{
    /// <summary>二进制名（Windows = sluice.exe；Linux = sluice）。</summary>
    public static string BinName =>
#if WINDOWS
        "sluice.exe";
#else
        "sluice";
#endif

    /// <summary>同意因子名（Windows = Windows Hello；Linux = 口令）。</summary>
    public static string ConsentName =>
#if WINDOWS
        "Windows Hello";
#else
        "口令";
#endif
}
