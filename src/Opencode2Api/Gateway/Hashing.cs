using System.Text;
using System.Text.RegularExpressions;

namespace Opencode2Api.Gateway;

/// <summary>
/// 64 位 FNV-1a，逐字节实现 Go 的 hash/fnv 所用算法。
/// 运行时自带的哈希在不同进程间不稳定，因此亲和游标
/// 需要这个实现（pool.go 依赖 fnv.New64a）。
/// </summary>
internal static class Fnv1a
{
    public static ulong Hash64(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var octet in Encoding.UTF8.GetBytes(value))
        {
            hash ^= octet;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}

/// <summary>
/// 代理端点不使用 <see cref="Uri"/> 解析，因为 "socks5" 不是
/// 已注册的协议，其主机/端口也无法可靠解码。
/// </summary>
internal sealed record ProxyEndpoint(string Scheme, string Host, int Port, string Username, string? Password)
{
    private static readonly Regex Pattern = new(
        @"^(?<scheme>[a-zA-Z][a-zA-Z0-9+.\-]*)://(?:(?:(?<user>[^:@/]*)(?::(?<pass>[^@/]*))?@)?(?<host>[^:/?#]+)(?::(?<port>\d+))?)(?:[/?#].*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析 "scheme://[user[:pass]@]host[:port]"。当取值
    /// 不含 authority 时返回 null，对应 Go 的 url.Parse 错误路径。
    /// </summary>
    public static ProxyEndpoint? TryParse(string raw)
    {
        var match = Pattern.Match(raw.Trim());
        if (!match.Success)
        {
            return null;
        }
        var scheme = match.Groups["scheme"].Value.ToLowerInvariant();
        var host = match.Groups["host"].Value;
        var portGroup = match.Groups["port"];
        var port = portGroup.Success ? int.Parse(portGroup.Value) : scheme switch
        {
            "http" or "socks5" or "socks5h" => 80,
            "https" => 443,
            _ => 0,
        };
        if (port <= 0 || port > 65535 || host.Length == 0)
        {
            return null;
        }
        // url.Parse 会对 userinfo 组件中的百分号转义做解码。
        var user = match.Groups["user"].Success ? Uri.UnescapeDataString(match.Groups["user"].Value) : "";
        string? password = match.Groups["pass"].Success ? Uri.UnescapeDataString(match.Groups["pass"].Value) : null;
        return new ProxyEndpoint(scheme, host, port, user, password);
    }

    public bool IsSocks => Scheme is "socks5" or "socks5h";

    /// <summary>远程解析是两者之间唯一的差异。</summary>
    public bool RemoteResolution => Scheme == "socks5h";
}
