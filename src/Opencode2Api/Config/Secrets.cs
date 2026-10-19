using System.Security.Cryptography;
using System.Text;

namespace Opencode2Api.Config;

/// <summary>
/// 密钥掩码、脱敏与稳定标识，对应
/// internal/config/secrets.go。
/// </summary>
public static class Secrets
{
    private static string LastRunes(string value, int count)
    {
        var runes = new List<Rune>(value.EnumerateRunes());
        if (runes.Count <= count)
        {
            return value;
        }
        var builder = new StringBuilder();
        foreach (var rune in runes.GetRange(runes.Count - count, count))
        {
            builder.Append(rune);
        }
        return builder.ToString();
    }

    public static string KeyDisplayId(string value)
    {
        if (value.EnumerateRunes().Take(6).Count() <= 5)
        {
            return value;
        }
        return LastRunes(value, 5);
    }

    /// <summary>脱敏代理或上游 URL 中内嵌的凭据。</summary>
    public static string RedactUrl(string raw)
    {
        var (clean, pool) = Proxy.ParseProxyEntry(raw);
        if (clean.Length > 0)
        {
            raw = clean;
        }
        var schemeIdx = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx <= 0)
        {
            return raw;
        }
        var authorityStart = schemeIdx + 3;
        var authorityEnd = raw.Length;
        for (var i = authorityStart; i < raw.Length; i++)
        {
            if (raw[i] is '/' or '?' or '#')
            {
                authorityEnd = i;
                break;
            }
        }
        var authority = raw[authorityStart..authorityEnd];
        var atIdx = authority.LastIndexOf('@');
        if (atIdx >= 0)
        {
            authority = "***" + authority[atIdx..];
            raw = raw[..authorityStart] + authority + raw[authorityEnd..];
        }
        if (pool && !raw.Contains('#'))
        {
            raw += "#pool";
        }
        return raw;
    }

    /// <summary>从绝对 URL 中提取 (用户名, 密码) userinfo。</summary>
    public static (string Username, string? Password)? ParseUserInfo(string raw)
    {
        var schemeIdx = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx <= 0)
        {
            return null;
        }
        var authorityStart = schemeIdx + 3;
        var authorityEnd = raw.Length;
        for (var i = authorityStart; i < raw.Length; i++)
        {
            if (raw[i] is '/' or '?' or '#')
            {
                authorityEnd = i;
                break;
            }
        }
        var authority = raw[authorityStart..authorityEnd];
        var atIdx = authority.LastIndexOf('@');
        if (atIdx < 0)
        {
            return null;
        }
        var userInfo = authority[..atIdx];
        var colonIdx = userInfo.IndexOf(':');
        return colonIdx < 0
            ? (Uri.UnescapeDataString(userInfo), null)
            : (Uri.UnescapeDataString(userInfo[..colonIdx]), Uri.UnescapeDataString(userInfo[(colonIdx + 1)..]));
    }

    /// <summary>用作稳定且不可逆密钥标识符的 SHA-256 前缀。</summary>
    public static string Fingerprint(string value)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(sum)[..10].ToLowerInvariant();
    }
}

/// <summary>
/// 在诊断输出中把配置的凭据替换为 "***"。
/// </summary>
public sealed class SecretRedactor
{
    private string[] _values = [];

    public void Replace(Config cfg)
    {
        var values = new List<string>();
        values.AddRange(cfg.ServerKeys);
        values.AddRange(cfg.ZenKeys);
        values.AddRange(cfg.GoKeys);
        foreach (var value in new[] { cfg.Upstream.Zen, cfg.Upstream.Go, cfg.Upstream.Anonymous })
        {
            var userInfo = Secrets.ParseUserInfo(value);
            if (userInfo != null)
            {
                values.Add(value);
                values.Add(userInfo.Value.Username);
                if (userInfo.Value.Password != null)
                {
                    values.Add(userInfo.Value.Password);
                }
            }
        }
        foreach (var value in cfg.RuntimeProxies())
        {
            if (value == "direct")
            {
                continue;
            }
            var (clean, _) = Proxy.ParseProxyEntry(value);
            values.Add(value);
            values.Add(clean);
            var userInfo = Secrets.ParseUserInfo(clean);
            if (userInfo != null)
            {
                values.Add(userInfo.Value.Username);
                if (userInfo.Value.Password != null)
                {
                    values.Add(userInfo.Value.Password);
                }
            }
        }
        values.Sort((a, b) => b.Length.CompareTo(a.Length));
        Interlocked.Exchange(ref _values, [.. values]);
    }

    public string String(string value)
    {
        foreach (var secret in _values)
        {
            if (secret.Length >= 4)
            {
                value = value.Replace(secret, "***");
            }
        }
        return value;
    }
}
