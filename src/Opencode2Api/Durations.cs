namespace Opencode2Api;

/// <summary>
/// TimeSpan 取极值的辅助方法。
/// BCL 的 <c>Math.Max/Min</c> 没有 TimeSpan 重载，<c>TimeSpan</c> 也没有静态二参 Max/Min，
/// 因此这里提供唯一一份实现，避免各调用点重复自定义本地函数。
/// </summary>
internal static class Durations
{
    /// <summary>返回两个时长中较大的一个。</summary>
    public static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    /// <summary>返回两个时长中较小的一个。</summary>
    public static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;
}
