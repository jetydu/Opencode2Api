using System.Globalization;

namespace Opencode2Api.Models;

/// <summary>
/// RFC3339 UTC 往返序列化，用于 Go 与本实现
/// 共用的 JSON 状态文件。Go 把零值时间写为 0001-01-01T00:00:00Z，
/// 因此这里也输出同一字面量。
/// </summary>
public static class JsonTime
{
    private const string ZeroLiteral = "0001-01-01T00:00:00Z";

    public static string Format(DateTime value)
    {
        if (value == default)
        {
            return ZeroLiteral;
        }
        return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }

    public static DateTime Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return default;
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.UtcDateTime;
        }
        return default;
    }

    public static bool IsZero(DateTime value) => value == default;
}
