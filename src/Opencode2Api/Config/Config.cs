using Microsoft.Extensions.Configuration;
using Opencode2Api.Protocol;

namespace Opencode2Api.Config;

/// <summary>三条上游线路的基地址（upstream）。</summary>
public sealed class UpstreamConfig
{
    public string Zen { get; set; } = "https://opencode.ai/zen";
    public string Go { get; set; } = "https://opencode.ai/zen/go";
    public string Anonymous { get; set; } = "https://opencode.ai/inference";
}

/// <summary>重试与请求超时（retry）。</summary>
public sealed class RetryConfig
{
    public int MaxAttempts { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>模型目录参数（models）。</summary>
public sealed class ModelsConfig
{
    public int RefreshSeconds { get; set; } = 300;
    public Dictionary<string, string> Protocols { get; set; } = [];
    public bool FreeOnly { get; set; }
}

/// <summary>日志段配置，对应 JSON 的 Logging 对象。</summary>
public sealed class LoggingConfig
{
    public string Level { get; set; } = "info";
    public bool DumpRequestBodies { get; set; }
}

/// <summary>
/// 连接与超时参数，对应 JSON 的 Performance 对象。
/// Go 版的 max_idle_conns / max_idle_conns_per_host 在 .NET 连接池里没有等价项，
/// 因此不再保留；每主机并发上限由 MaxConnsPerHost 承担。
/// </summary>
public sealed class PerformanceConfig
{
    public int MaxConnsPerHost { get; set; }
    public int IdleConnTimeoutSeconds { get; set; } = 120;
    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int FailureCooldownSeconds { get; set; } = 15;
    public int AttemptTimeoutSeconds { get; set; }
    public int FirstEventTimeoutSeconds { get; set; }

    /// <summary>
    /// AttemptTimeout 限定单次上游尝试在放弃该次尝试、改试下一个节点之前，
    /// 最多可以等待响应头的时间。
    /// 取值 &lt;= 0 时保留历史行为，即使用请求级
    /// 重试超时；结果不会超过 requestTimeout。
    /// </summary>
    public TimeSpan AttemptTimeout(TimeSpan requestTimeout)
    {
        if (AttemptTimeoutSeconds > 0)
        {
            var attempt = TimeSpan.FromSeconds(AttemptTimeoutSeconds);
            if (requestTimeout > TimeSpan.Zero && attempt > requestTimeout)
            {
                return requestTimeout;
            }
            return attempt;
        }
        return requestTimeout;
    }
}

/// <summary>
/// ReasoningConfig 为未声明思考等级的请求强制指定一个等级。
/// 客户端显式发送的等级永远优先，因此强制等级只会替换
/// 本来需要推导出来的那个值。
/// </summary>
public sealed class ReasoningConfig
{
    public string Effort { get; set; } = "";
    public Dictionary<string, string>? EffortByModel { get; set; }
}

/// <summary>服务配置（internal/config/config.go）。</summary>
public sealed class Config
{
    public RotationConfig Rotation { get; set; } = new();
    public List<string> ServerKeys { get; set; } = [];
    public Dictionary<string, ServerKeyMetadata> ServerKeyMetadata { get; set; } = [];
    public List<string> ZenKeys { get; set; } = [];
    public List<string> GoKeys { get; set; } = [];
    public bool Anonymous { get; set; }
    public List<string> Proxies { get; set; } = [];
    public string ProxyFile { get; set; } = "";
    public UpstreamConfig Upstream { get; set; } = new();
    public RetryConfig Retry { get; set; } = new();
    public ModelsConfig Models { get; set; } = new();
    public PerformanceConfig Performance { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public string Prefer { get; set; } = Tier.Go;
    public ReasoningConfig Reasoning { get; set; } = new();

    /// <summary>
    /// 运行期状态文件的存放目录。相对路径按配置文件所在目录解析，
    /// 置空则退回 Go 版行为（状态文件紧贴配置文件）。
    /// </summary>
    public string StateDir { get; set; } = "state";

    internal List<string> EffectiveProxies { get; set; } = [];

    /// <summary>appsettings.json 中本服务配置所在的节名。</summary>
    public const string SectionName = "Opencode2Api";

    /// <summary>指向额外配置 JSON 文件的环境变量，等价于 --config。</summary>
    public const string ExtraFileEnvVar = "OPENCODE2API_CONFIG";

    /// <summary>
    /// 从 .NET 配置系统读取网关配置：appsettings.json 的 Opencode2Api 节（可由
    /// 环境变量 Opencode2Api__Prefer、命令行 --Opencode2Api:Prefer 或分环境文件覆盖），
    /// 绑定后再走 Normalize 的校验与派生字段计算。
    /// 监听地址不在这里，它用的是宿主标配的 urls（见 Program.cs）；
    /// configPath 只用于解析 ProxyFile / StateDir 类相对路径。
    /// </summary>
    public static Config FromConfiguration(IConfiguration section, string configPath)
    {
        if (!section.GetChildren().Any())
        {
            throw new ConfigException($"{configPath} 里没有 \"{SectionName}\" 配置节，或该节为空");
        }
        var cfg = new Config();
        try
        {
            // ErrorOnUnknownConfiguration 接管了 Go 版「拒绝未知字段」的行为：
            // 拼错的键会直接报错，而不是被静默忽略。
            section.Bind(cfg, options => options.ErrorOnUnknownConfiguration = true);
        }
        catch (InvalidOperationException error)
        {
            throw new ConfigException($"绑定 {SectionName} 配置失败：{error.Message}");
        }
        return Normalize(configPath, cfg);
    }

    /// <summary>
    /// Normalize 对已经绑定好的 Config 做去空格、默认值补齐与范围校验，
    /// 并算出派生字段（生效代理列表），对应 Go 版 config.Normalize。
    /// </summary>
    public static Config Normalize(string path, Config cfg)
    {
        var next = Clone(cfg);
        next.Rotation.Defaults();
        next.Rotation.Validate(null);
        next.ServerKeys = Proxy.TrimList(next.ServerKeys);
        next.ZenKeys = Proxy.TrimList(next.ZenKeys);
        next.GoKeys = Proxy.TrimList(next.GoKeys);
        next.ProxyFile = next.ProxyFile.Trim();
        next.StateDir = next.StateDir.Trim();
        next.EffectiveProxies = Proxy.ResolveEffective(path, next.Proxies, next.ProxyFile);
        if (next.Prefer != Tier.Zen && next.Prefer != Tier.Go)
        {
            throw new ConfigException("Prefer 必须是 \"zen\" 或 \"go\"");
        }
        next.Upstream.Zen = next.Upstream.Zen.Trim();
        next.Upstream.Go = next.Upstream.Go.Trim();
        next.Upstream.Anonymous = next.Upstream.Anonymous.Trim();
        if (next.Upstream.Anonymous.Length == 0)
        {
            next.Upstream.Anonymous = "https://opencode.ai/inference";
        }
        foreach (var (name, raw) in new Dictionary<string, string>
                 {
                     ["Upstream.Zen"] = next.Upstream.Zen,
                     ["Upstream.Go"] = next.Upstream.Go,
                     ["Upstream.Anonymous"] = next.Upstream.Anonymous,
                 })
        {
            if (!IsHttpUrl(raw.Trim()))
            {
                throw new ConfigException($"{name} 必须是 http 或 https URL");
            }
        }
        if (next.ServerKeys.Count == 0)
        {
            throw new ConfigException("ServerKeys 至少需要包含一个本地 Key");
        }
        if (next.FirstEnabledServerKey().Length == 0)
        {
            throw new ConfigException("至少保留一个启用的 API Key");
        }
        if (!next.Anonymous && next.ZenKeys.Count == 0 && next.GoKeys.Count == 0)
        {
            throw new ConfigException("开启 Anonymous 之前，ZenKeys 或 GoKeys 必须至少有一个上游 Key");
        }
        if (next.Retry.MaxAttempts < 1)
        {
            throw new ConfigException("Retry.MaxAttempts 至少为 1");
        }
        if (next.Retry.TimeoutSeconds < 1)
        {
            throw new ConfigException("Retry.TimeoutSeconds 至少为 1");
        }
        if (next.Models.RefreshSeconds < 1)
        {
            throw new ConfigException("Models.RefreshSeconds 至少为 1");
        }
        if (next.Performance.MaxConnsPerHost < 0
            || next.Performance.IdleConnTimeoutSeconds < 1 || next.Performance.ConnectTimeoutSeconds < 1 || next.Performance.FailureCooldownSeconds < 1)
        {
            throw new ConfigException("Performance 的取值必须为正数（MaxConnsPerHost 可以为 0，表示不限）");
        }
        if (next.Performance.AttemptTimeoutSeconds < 0)
        {
            throw new ConfigException("Performance.AttemptTimeoutSeconds 不能为负（0 表示沿用 Retry.TimeoutSeconds）");
        }
        if (next.Performance.FirstEventTimeoutSeconds < 0 || next.Performance.FirstEventTimeoutSeconds > 86400)
        {
            throw new ConfigException("Performance.FirstEventTimeoutSeconds 必须介于 0 到 86400（0 关闭首包超时）");
        }
        if (next.Logging.Level is not ("debug" or "info" or "warn" or "error"))
        {
            throw new ConfigException("Logging.Level 必须是 debug、info、warn 或 error");
        }
        foreach (var raw in next.RuntimeProxies())
        {
            if (raw == "direct")
            {
                continue;
            }
            var (clean, _) = Proxy.ParseProxyEntry(raw);
            var scheme = ProxyScheme(clean);
            if (scheme == null)
            {
                throw new ConfigException($"代理地址无效：\"{Secrets.RedactUrl(raw)}\"");
            }
            switch (scheme)
            {
                case "http":
                case "https":
                case "socks5":
                case "socks5h":
                    break;
                default:
                    throw new ConfigException($"不支持的代理协议：\"{scheme}\"");
            }
        }
        foreach (var (model, protocol) in next.Models.Protocols)
        {
            if (model.Length == 0 || !Wire.IsValid(protocol))
            {
                throw new ConfigException($"Models.Protocols 包含无效映射 \"{model}\": \"{protocol}\"");
            }
        }
        NormalizeReasoning(next);
        return next;
    }

    private static void NormalizeReasoning(Config cfg)
    {
        cfg.Reasoning.Effort = cfg.Reasoning.Effort.Trim().ToLowerInvariant();
        ValidateEffort("Reasoning.Effort", cfg.Reasoning.Effort);
        if (cfg.Reasoning.EffortByModel == null)
        {
            return;
        }
        if (cfg.Reasoning.EffortByModel.Count == 0)
        {
            cfg.Reasoning.EffortByModel = null;
            return;
        }
        var normalized = new Dictionary<string, string>();
        foreach (var (model, effort) in cfg.Reasoning.EffortByModel)
        {
            var key = model.Trim();
            if (key.Length == 0)
            {
                throw new ConfigException("Reasoning.EffortByModel 不能包含空模型 ID");
            }
            var level = effort.Trim().ToLowerInvariant();
            ValidateEffort($"Reasoning.EffortByModel[\"{key}\"]", level);
            normalized[key] = level;
        }
        cfg.Reasoning.EffortByModel = normalized;
    }

    private static void ValidateEffort(string name, string value)
    {
        switch (value)
        {
            case "" or "auto" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "none":
                return;
            default:
                throw new ConfigException($"{name} 必须是 auto、minimal、low、medium、high、xhigh、max、none 之一，或置空以关闭覆盖");
        }
    }

    private static bool IsHttpUrl(string raw)
    {
        var scheme = ProxyScheme(raw);
        return scheme is "http" or "https";
    }

    private static string? ProxyScheme(string raw)
    {
        var idx = raw.IndexOf("://", StringComparison.Ordinal);
        if (idx <= 0)
        {
            return null;
        }
        var scheme = raw[..idx].ToLowerInvariant();
        if (!Uri.CheckSchemeName(scheme))
        {
            return null;
        }
        // authority 必须非空："://" 之后的字符不能是
        // 路径/查询/片段标记，且字符串必须继续向后。
        if (idx + 3 >= raw.Length || raw[idx + 3] is '/' or '?' or '#')
        {
            return null;
        }
        return scheme;
    }

    /// <summary>
    /// ResolveStateBase 计算运行期状态文件的基准路径：轮转游标、可用性判定、
    /// 模型目录与 models.dev 价格缓存都在其后拼接后缀。
    /// StateDir 为空时直接返回配置文件路径（Go 版布局）；
    /// 否则返回 <StateDir>/<StateFilePrefix>，相对路径按配置文件所在目录解析，
    /// 以免把 appsettings 之类的文件名带进状态文件名。
    /// </summary>
    public string ResolveStateBase(string configPath)
    {
        if (configPath.Length == 0)
        {
            return "";
        }
        if (StateDir.Length == 0)
        {
            return configPath;
        }
        var directory = Path.GetDirectoryName(configPath);
        var target = Path.IsPathRooted(StateDir)
            ? StateDir
            : Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, StateDir);
        return Path.Combine(target, StateFilePrefix);
    }

    /// <summary>使用 StateDir 时的状态文件名前缀。</summary>
    public const string StateFilePrefix = "opencode2api";

    /// <summary>
    /// RuntimeProxies 返回解析后的代理列表，包含 proxyfile
    /// 条目；Config.Proxies 有意保持为存储的列表。
    /// </summary>
    public List<string> RuntimeProxies()
    {
        if (EffectiveProxies.Count > 0)
        {
            return EffectiveProxies;
        }
        if (Proxies.Count > 0)
        {
            return Proxies;
        }
        return ["direct"];
    }

    /// <summary>ForcedEffort 返回运维者为某个模型强制指定的等级。</summary>
    public string ForcedEffort(string model)
    {
        if (Reasoning.EffortByModel != null && Reasoning.EffortByModel.TryGetValue(model, out var effort))
        {
            return effort;
        }
        return Reasoning.Effort;
    }

    public bool ServerKeyEnabled(string key) =>
        !ServerKeyMetadata.TryGetValue(Secrets.Fingerprint(key), out var meta) || !meta.Disabled;

    public string FirstEnabledServerKey()
    {
        foreach (var key in ServerKeys)
        {
            if (ServerKeyEnabled(key))
            {
                return key;
            }
        }
        return "";
    }

    public static Config Clone(Config cfg)
    {
        var copy = new Config
        {
            Anonymous = cfg.Anonymous,
            ProxyFile = cfg.ProxyFile,
            StateDir = cfg.StateDir,
            Prefer = cfg.Prefer,
            ServerKeys = [.. cfg.ServerKeys],
            ZenKeys = [.. cfg.ZenKeys],
            GoKeys = [.. cfg.GoKeys],
            Proxies = [.. cfg.Proxies],
            EffectiveProxies = [.. cfg.EffectiveProxies],
            ServerKeyMetadata = new Dictionary<string, ServerKeyMetadata>(cfg.ServerKeyMetadata),
            Upstream = new UpstreamConfig { Zen = cfg.Upstream.Zen, Go = cfg.Upstream.Go, Anonymous = cfg.Upstream.Anonymous },
            Retry = new RetryConfig { MaxAttempts = cfg.Retry.MaxAttempts, TimeoutSeconds = cfg.Retry.TimeoutSeconds },
            Models = new ModelsConfig
            {
                RefreshSeconds = cfg.Models.RefreshSeconds,
                FreeOnly = cfg.Models.FreeOnly,
                Protocols = new Dictionary<string, string>(cfg.Models.Protocols),
            },
            Performance = new PerformanceConfig
            {
                MaxConnsPerHost = cfg.Performance.MaxConnsPerHost,
                IdleConnTimeoutSeconds = cfg.Performance.IdleConnTimeoutSeconds,
                ConnectTimeoutSeconds = cfg.Performance.ConnectTimeoutSeconds,
                FailureCooldownSeconds = cfg.Performance.FailureCooldownSeconds,
                AttemptTimeoutSeconds = cfg.Performance.AttemptTimeoutSeconds,
                FirstEventTimeoutSeconds = cfg.Performance.FirstEventTimeoutSeconds,
            },
            Logging = new LoggingConfig
            {
                Level = cfg.Logging.Level,
                DumpRequestBodies = cfg.Logging.DumpRequestBodies,
            },
            Reasoning = new ReasoningConfig
            {
                Effort = cfg.Reasoning.Effort,
                EffortByModel = cfg.Reasoning.EffortByModel == null ? null : new Dictionary<string, string>(cfg.Reasoning.EffortByModel),
            },
        };
        copy.Rotation = new RotationConfig
        {
            Sota = new RotationGroup { Alias = cfg.Rotation.Sota.Alias, Order = [.. cfg.Rotation.Sota.Order] },
            Sweet = new RotationGroup { Alias = cfg.Rotation.Sweet.Alias, Order = [.. cfg.Rotation.Sweet.Order] },
            FirstOutputSeconds = cfg.Rotation.FirstOutputSeconds,
        };
        return copy;
    }
}
