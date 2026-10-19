namespace Opencode2Api.Config;

/// <summary>
/// 代理条目解析与 proxyfile 载入，对应 internal/config/proxy.go。
/// </summary>
public static class Proxy
{
    /// <summary>
    /// 从配置的代理字符串中拆出可选的动态池标记（"pool+" 协议前缀或
    /// "#pool" URL 片段），返回干净的拨号 URL，
    /// 并给出该条目是否为轮转代理池。
    /// </summary>
    public static (string Clean, bool Pool) ParseProxyEntry(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed == "direct")
        {
            return (trimmed, false);
        }
        var pool = false;
        if (trimmed.StartsWith("pool+", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["pool+".Length..].Trim();
            pool = true;
        }
        // Go 先试 url.Parse 再回落到 LastIndex('#')，两条分支最终得到的拨号串一致，
        // 因此这里统一按最后一个 '#' 片段判断是否为代理池。
        var idx = trimmed.LastIndexOf('#');
        if (idx >= 0 && string.Equals(trimmed[(idx + 1)..].Trim(), "pool", StringComparison.OrdinalIgnoreCase))
        {
            pool = true;
            trimmed = trimmed[..idx].Trim();
        }
        return (trimmed, pool);
    }

    /// <summary>
    /// 把 ProxyFile 条目解析进生效代理列表。Config.Proxies 有意保持为
    /// appsettings.json 中书写的那份列表，这样读取时绝不会
    /// 把 ProxyFile 载入的值混进来。
    /// </summary>
    public static List<string> ResolveEffective(string configPath, IReadOnlyList<string> proxies, string proxyFile)
    {
        var effective = new List<string>();
        foreach (var value in TrimList(proxies))
        {
            effective.Add(value);
        }
        if (proxyFile.Length > 0)
        {
            var resolved = proxyFile;
            if (!Path.IsPathRooted(resolved))
            {
                var directory = Path.GetDirectoryName(configPath);
                resolved = Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, resolved);
            }
            effective.AddRange(ReadProxyFile(resolved));
        }
        effective = UniqueProxyEntries(effective);
        if (effective.Count == 0)
        {
            effective.Add("direct");
        }
        return effective;
    }

    private static List<string> ReadProxyFile(string path)
    {
        List<string> proxies = [];
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"load proxy file {path}: {e.Message}");
        }
        foreach (var line in lines)
        {
            var value = StripProxyLineComment(line.TrimStart('\uFEFF').Trim()).Trim();
            if (value.Length > 0)
            {
                proxies.Add(value);
            }
        }
        return proxies;
    }

    private static string StripProxyLineComment(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (i > 0 && line[i - 1] != ' ' && line[i - 1] != '\t')
            {
                continue;
            }
            if (line[i] == '#' || line[i] == ';' || (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/'))
            {
                return line[..i];
            }
        }
        return line;
    }

    /// <summary>
    /// 去掉完全重复的条目，但保留 #pool 条目与拨向同一端点的静态代理并存：
    /// 它们是不同的线路。
    /// </summary>
    private static List<string> UniqueProxyEntries(IReadOnlyList<string> items)
    {
        var output = new List<string>(items.Count);
        var seen = new HashSet<string>();
        var pools = new HashSet<string>();
        foreach (var item in items)
        {
            var (key, pool) = ParseProxyEntry(item);
            if (key.Length == 0)
            {
                continue;
            }
            if (pool)
            {
                if (pools.Add(key))
                {
                    output.Add(item);
                }
                continue;
            }
            if (seen.Add(key))
            {
                output.Add(item);
            }
        }
        return output;
    }

    public static List<string> TrimList(IEnumerable<string> items)
    {
        var output = new List<string>();
        foreach (var item in items)
        {
            var value = item.Trim();
            if (value.Length > 0)
            {
                output.Add(value);
            }
        }
        return output;
    }
}
