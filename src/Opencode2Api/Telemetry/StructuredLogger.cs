using System.Globalization;
using System.Text;
using NLog;
using Opencode2Api.Config;

namespace Opencode2Api.Telemetry;

/// <summary>
/// 在 NLog 之上的结构化键值日志，带密钥脱敏，
/// 与 telemetry.NewStructuredLogger 的行为一致。包含
/// password/authorization/cookie/secret 的字段键一律被遮蔽。
/// </summary>
public sealed class StructuredLogger
{
    private readonly Logger _logger = LogManager.GetLogger("opencode2api");
    private readonly SecretRedactor _redactor;
    private int _level = (int)Threshold.Info;

    private enum Threshold
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
    }

    public StructuredLogger(SecretRedactor redactor, string level)
    {
        _redactor = redactor;
        SetLevel(level);
    }

    public void SetLevel(string value)
    {
        var parsed = value switch
        {
            "debug" => Threshold.Debug,
            "warn" => Threshold.Warn,
            "error" => Threshold.Error,
            _ => Threshold.Info,
        };
        Volatile.Write(ref _level, (int)parsed);
    }

    public bool Enabled(string level) =>
        (level switch
        {
            "debug" => Threshold.Debug,
            "warn" => Threshold.Warn,
            "error" => Threshold.Error,
            _ => Threshold.Info,
        }) >= (Threshold)Volatile.Read(ref _level);

    public void Debug(string message, params (string Key, object? Value)[] attrs) => Write(Threshold.Debug, message, attrs);

    public void Info(string message, params (string Key, object? Value)[] attrs) => Write(Threshold.Info, message, attrs);

    public void Warn(string message, params (string Key, object? Value)[] attrs) => Write(Threshold.Warn, message, attrs);

    public void Error(string message, params (string Key, object? Value)[] attrs) => Write(Threshold.Error, message, attrs);

    private void Write(Threshold level, string message, (string Key, object? Value)[] attrs)
    {
        if ((int)level < Volatile.Read(ref _level))
        {
            return;
        }
        var builder = new StringBuilder(_redactor.String(message));
        foreach (var (key, value) in attrs)
        {
            builder.Append(" ").Append(key).Append('=');
            if (IsSensitive(key) || value == null)
            {
                builder.Append(value == null ? "null" : "***");
                continue;
            }
            var rendered = Format(value);
            var needsQuotes = rendered.Contains(' ');
            builder.Append(needsQuotes ? "\"" + _redactor.String(rendered) + "\"" : _redactor.String(rendered));
        }
        var nlogLevel = level switch
        {
            Threshold.Debug => NLog.LogLevel.Debug,
            Threshold.Warn => NLog.LogLevel.Warn,
            Threshold.Error => NLog.LogLevel.Error,
            _ => NLog.LogLevel.Info,
        };
        var info = new LogEventInfo(nlogLevel, "opencode2api", builder.ToString());
        _logger.Log(info);
    }

    private static bool IsSensitive(string key)
    {
        var lower = key.ToLowerInvariant();
        return lower.Contains("password") || lower.Contains("authorization") || lower.Contains("cookie") || lower.Contains("secret");
    }

    private static string Format(object value) => value switch
    {
        string text => text,
        bool flag => flag ? "true" : "false",
        TimeSpan duration => duration.ToString(@"d\.hh\:mm\:ss\.fff", CultureInfo.InvariantCulture),
        DateTime time => time.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset time => time.ToString("o", CultureInfo.InvariantCulture),
        Exception exception => exception.Message,
        FormattableString formattable => formattable.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
