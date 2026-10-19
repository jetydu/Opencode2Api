using Opencode2Api.Config;
using Opencode2Api.Protocol;

namespace Opencode2Api.Models;

/// <summary>模型无法路由时抛出（对应 Go 的目录错误值）。</summary>
public sealed class CatalogException(string message) : Exception(message);

/// <summary>
/// 请求实际由哪条上游线路服务，包含重试跨档位时
/// 使用的按档位原生协议映射（models/catalog.go Route）。
/// </summary>
public sealed class Route
{
    public string ID { get; init; } = "";
    public string Tier { get; init; } = "";
    public string Protocol { get; init; } = "";

    // Zen 与 Go 有意不共用一份全局协议：OpenCode 目录
    // 例如把 MiniMax 在 Zen 上以 Chat 暴露、
    // 在 Go 上以 Messages 暴露。因此重试跨档位时必须重新编码请求。
    public Dictionary<string, string> Protocols { get; init; } = [];

    // 匿名路由总是从公共凭据的 Zen 线路开始。
    public bool Anonymous { get; init; }

    // KeyTiers 是有序的已鉴权回退计划。当公共凭据
    // 没有成功时，匿名请求会进入这个列表。
    public List<string> KeyTiers { get; init; } = [];

    public string ProtocolFor(string tier) =>
        Protocols.TryGetValue(tier, out var protocol) && protocol.Length > 0 ? protocol : Protocol;
}

/// <summary>诊断与健康报告使用的路由视图。</summary>
public sealed class RouteDiagnostic
{
    public string Model { get; set; } = "";
    public string RequestedProtocol { get; set; } = "";
    public string NativeProtocol { get; set; } = "";
    public Dictionary<string, string> NativeProtocols { get; set; } = [];
    public string ProtocolSource { get; set; } = "";
    public bool AvailableZen { get; set; }
    public bool AvailableGo { get; set; }
    public string Tier { get; set; } = "";
    public bool Anonymous { get; set; }
    public string KeyID { get; set; } = "";
    public string Channel { get; set; } = "";
    public int Attempts { get; set; }
    public List<string> KeyTiers { get; set; } = [];
    public AnonymousDecision AnonymousEligibility { get; set; } = new();
    public string RouteError { get; set; } = "";
}

public sealed record CatalogSnapshot
{
    public int Zen { get; init; }
    public int Go { get; init; }
    public int Total { get; init; }
    public int Exposed { get; init; }
    public DateTime UpdatedAt { get; init; }
    public string CacheSource { get; init; } = "";
    public bool Stale { get; init; }
}

/// <summary>
/// 模型发现、路由与能力目录（models/catalog.go）。
/// 全部状态由同一把锁保护；锁内不发起任何网络调用。
/// </summary>
public sealed class Catalog
{
    private readonly object _mutex = new();
    private readonly Dictionary<string, string> _protocols;
    private HashSet<string> _zen = [];
    private HashSet<string> _goModels = [];
    private Dictionary<string, Dictionary<string, string>> _nativeProtocols = ModelCatalogCache.NewProtocolMaps();
    private Dictionary<string, Dictionary<string, bool>> _unsupported = ModelCatalogCache.NewBoolMaps();
    private Dictionary<string, Dictionary<string, Metadata>> _modelMeta = ModelCatalogCache.NewMetaMaps();
    private DateTime _updatedAt;
    private string _prefer;
    private PricingStore? _pricing;
    private AvailabilityStore? _availability;
    private string _cachePath = "";
    private string _cacheSource = "none";
    private bool _stale;
    private TimeSpan _refreshAfter;
    private bool _freeOnly;

    public Catalog(string prefer, Dictionary<string, string>? overrides)
    {
        _prefer = prefer;
        _protocols = overrides == null ? [] : new Dictionary<string, string>(overrides);
    }

    public void SetPricingStore(PricingStore? store)
    {
        lock (_mutex)
        {
            _pricing = store;
        }
    }

    public void SetAvailabilityStore(AvailabilityStore? store)
    {
        lock (_mutex)
        {
            _availability = store;
        }
    }

    /// <summary>定价新鲜度报告，不暴露内部存储。</summary>
    public MetadataSnapshot MetadataSnapshot()
    {
        PricingStore? store;
        lock (_mutex)
        {
            store = _pricing;
        }
        return store?.Snapshot() ?? new MetadataSnapshot();
    }

    public void SetCachePath(string path)
    {
        lock (_mutex)
        {
            _cachePath = path;
        }
    }

    public void SetRefreshInterval(TimeSpan interval)
    {
        lock (_mutex)
        {
            _refreshAfter = interval;
        }
    }

    /// <summary>在发现与路由中隐藏所有非免费模型。</summary>
    public void SetFreeOnly(bool enabled)
    {
        lock (_mutex)
        {
            _freeOnly = enabled;
        }
    }

    /// <summary>
    /// 在 models.free_only 置位时拒绝付费模型。两个路由入口
    /// 都会检查它，因此 /v1/models、推理、轮转别名与
    /// 诊断信息对外暴露的范围保持一致。
    /// </summary>
    private void FreeOnlyErrorLocked(string model)
    {
        if (_freeOnly && !AnonymousDecisionLocked(model).Allowed)
        {
            throw new CatalogException($"model \"{model}\" is not free and models.free_only is enabled");
        }
    }

    public void Replace(List<string> zen, List<string> goModels) =>
        ReplaceWithCapabilities(zen, goModels, null, null, null);

    public void ReplaceWithCapabilities(
        List<string>? zen,
        List<string>? goModels,
        Dictionary<string, Dictionary<string, string>>? native,
        Dictionary<string, Dictionary<string, bool>>? unsupported,
        Dictionary<string, Dictionary<string, Metadata>>? metadata)
    {
        lock (_mutex)
        {
            if (zen != null)
            {
                _zen = ToSet(zen);
            }
            if (goModels != null)
            {
                _goModels = ToSet(goModels);
            }
            if (native != null)
            {
                foreach (var tier in Tier.All)
                {
                    if (native.TryGetValue(tier, out var protocols))
                    {
                        _nativeProtocols[tier] = new Dictionary<string, string>(protocols);
                    }
                }
            }
            if (unsupported != null)
            {
                foreach (var tier in Tier.All)
                {
                    if (unsupported.TryGetValue(tier, out var models))
                    {
                        _unsupported[tier] = new Dictionary<string, bool>(models);
                    }
                }
            }
            if (metadata != null)
            {
                _modelMeta = CloneModelMeta(metadata);
            }
            _updatedAt = DateTime.UtcNow;
            _cacheSource = "live";
            _stale = false;
        }
    }

    /// <summary>
    /// 安装一份已经校验过的磁盘快照。它只改变发现到的状态，
    /// 配置里的协议覆盖与路由偏好仍由当前 Config 拥有。
    /// </summary>
    public void LoadCache(string path)
    {
        var cache = CatalogCache.Load(path);
        lock (_mutex)
        {
            _zen = ToSet(cache.Zen);
            _goModels = ToSet(cache.Go);
            _nativeProtocols = CloneTierProtocols(cache.NativeProtocols);
            _unsupported = CloneTierBools(cache.Unsupported);
            _modelMeta = CloneModelMeta(cache.Metadata);
            _updatedAt = cache.UpdatedAt.ToUniversalTime();
            _cacheSource = "disk";
            _stale = true;
            if (_cachePath.Length == 0)
            {
                _cachePath = path;
            }
        }
    }

    /// <summary>只快照公开的模型能力数据。</summary>
    public void SaveCache()
    {
        string path;
        var cache = new ModelCatalogCache();
        lock (_mutex)
        {
            path = _cachePath;
            cache.UpdatedAt = _updatedAt.ToUniversalTime();
            cache.Zen = SortedSetKeys(_zen);
            cache.Go = SortedSetKeys(_goModels);
            cache.NativeProtocols.Clear();
            foreach (var (tier, protocols) in CloneTierProtocols(_nativeProtocols))
            {
                cache.NativeProtocols[tier] = protocols;
            }
            cache.Unsupported.Clear();
            foreach (var (tier, models) in CloneTierBools(_unsupported))
            {
                cache.Unsupported[tier] = models;
            }
            cache.Metadata.Clear();
            foreach (var (tier, models) in CloneModelMeta(_modelMeta))
            {
                cache.Metadata[tier] = models;
            }
        }
        if (path.Length == 0)
        {
            return;
        }
        CatalogCache.Save(path, cache);
    }

    public Route Route(string model, bool hasZenKeys, bool hasGoKeys, bool hasAnonymous)
    {
        lock (_mutex)
        {
            return RouteLocked(model, hasZenKeys, hasGoKeys, hasAnonymous);
        }
    }

    private Route RouteLocked(string model, bool hasZenKeys, bool hasGoKeys, bool hasAnonymous)
    {
        if (_availability?.Disabled(model) == true)
        {
            throw new CatalogException($"model \"{model}\" is disabled after a failed availability check; wait for the next probe or delete the availability state file");
        }
        FreeOnlyErrorLocked(model);
        var keyTiers = KeyTierOrderLocked(model, hasZenKeys, hasGoKeys);
        // OpenCode 的公共凭据是一条仅 Zen 的线路。每个免费模型
        // 都从这里开始，即使当前目录只在 Go 上宣告它：
        // 上游拒绝会把请求转入已鉴权的
        // 回退计划。
        var allowed = AnonymousDecisionLocked(model).Allowed;
        var advertised = _zen.Count == 0 && _goModels.Count == 0 || _zen.Contains(model) || _goModels.Contains(model);
        if (hasAnonymous && allowed &&
            (_protocols.GetValueOrDefault(model, "").Length > 0 || !IsUnsupportedLocked(Tier.Zen, model)) &&
            advertised)
        {
            var protocols = ProtocolsForLocked(model, keyTiers, true);
            return new Route
            {
                ID = model,
                Tier = Tier.Zen,
                Protocol = protocols.GetValueOrDefault(Tier.Zen, ""),
                Protocols = protocols,
                Anonymous = true,
                KeyTiers = keyTiers,
            };
        }
        if (keyTiers.Count > 0)
        {
            var protocols = ProtocolsForLocked(model, keyTiers, false);
            return new Route
            {
                ID = model,
                Tier = keyTiers[0],
                Protocol = protocols.GetValueOrDefault(keyTiers[0], ""),
                Protocols = protocols,
                KeyTiers = keyTiers,
            };
        }
        throw new CatalogException($"model \"{model}\" is not available in the configured Zen or Go pools");
    }

    private Dictionary<string, string> ProtocolsForLocked(string model, List<string> keyTiers, bool includeZen)
    {
        var protocols = new Dictionary<string, string>();
        if (includeZen)
        {
            protocols[Tier.Zen] = ProtocolForLocked(model, Tier.Zen);
        }
        foreach (var tier in keyTiers)
        {
            protocols[tier] = ProtocolForLocked(model, tier);
        }
        return protocols;
    }

    private string ProtocolForLocked(string model, string tier)
    {
        if (_protocols.GetValueOrDefault(model, "").Length > 0)
        {
            return _protocols[model];
        }
        if (_nativeProtocols.TryGetValue(tier, out var native) && native.GetValueOrDefault(model, "").Length > 0)
        {
            return native[model];
        }
        // OpenCode 能力目录在可用时是权威来源。Chat 是唯一
        // 协议中立的可靠回退，用于刚刚出现在
        // /v1/models 但能力快照中尚不存在的 ID。
        return Wire.Chat;
    }

    /// <summary>
    /// 按优先顺序构造已鉴权路由。只有在拥有密钥且宣告了
    /// 该模型时才纳入该档位。在首次成功
    /// 刷新目录之前，配置的密钥池仍可用，因此偶发的
    /// 发现失败不会让网关下线。
    /// </summary>
    private List<string> KeyTierOrderLocked(string model, bool hasZenKeys, bool hasGoKeys)
    {
        var catalogPending = _zen.Count == 0 && _goModels.Count == 0;
        bool Available(string tier) => tier switch
        {
            Tier.Zen => hasZenKeys && (catalogPending || _zen.Contains(model)) && TierSupportedLocked(model, Tier.Zen),
            Tier.Go => hasGoKeys && (catalogPending || _goModels.Contains(model)) && TierSupportedLocked(model, Tier.Go),
            _ => false,
        };
        var order = new List<string> { Tier.Zen, Tier.Go };
        if (_prefer == Tier.Go)
        {
            (order[0], order[1]) = (order[1], order[0]);
        }
        return order.Where(Available).ToList();
    }

    /// <summary>
    /// 构造固定在某个档位上的路由，供按密钥的诊断使用。它
    /// 绝不选择匿名线路，也绝不添加回退档位。
    /// </summary>
    public Route RouteForTier(string model, string tier, bool hasZenKeys, bool hasGoKeys)
    {
        if (tier != Tier.Zen && tier != Tier.Go)
        {
            throw new CatalogException("selected key tier must be zen or go");
        }
        var hasKeys = tier == Tier.Go ? hasGoKeys : hasZenKeys;
        if (!hasKeys)
        {
            throw new CatalogException($"no {tier} key is configured");
        }
        lock (_mutex)
        {
            if (_availability?.Disabled(model) == true)
            {
                throw new CatalogException($"model \"{model}\" is disabled after a failed availability check");
            }
            FreeOnlyErrorLocked(model);
            var catalogPending = _zen.Count == 0 && _goModels.Count == 0;
            var advertised = tier == Tier.Go ? _goModels.Contains(model) : _zen.Contains(model);
            if (!catalogPending && !advertised)
            {
                throw new CatalogException($"model \"{model}\" is not available in the selected {tier} key tier");
            }
            if (!TierSupportedLocked(model, tier))
            {
                throw new CatalogException($"model \"{model}\" uses an upstream protocol that is not available on the selected {tier} key tier");
            }
            var protocol = ProtocolForLocked(model, tier);
            return new Route
            {
                ID = model,
                Tier = tier,
                Protocol = protocol,
                Protocols = new Dictionary<string, string> { [tier] = protocol },
                KeyTiers = [tier],
            };
        }
    }

    private bool IsUnsupportedLocked(string tier, string model) =>
        _unsupported.TryGetValue(tier, out var blocked) && blocked.ContainsKey(model);

    private bool HasNativeProtocolLocked(string tier, string model) =>
        _nativeProtocols.TryGetValue(tier, out var native) && native.ContainsKey(model);

    private AnonymousDecision AnonymousDecisionLocked(string model) =>
        _pricing?.Decide(model) ?? new AnonymousDecision
        {
            Allowed = NameIsFree(model),
            Source = "name_fallback_metadata_pending",
        };

    /// <summary>
    /// 判断仅凭 -free 命名习惯是否就把模型视为免费档
    /// （models/catalog.go isFreeModel）。
    /// </summary>
    public static bool NameIsFree(string model) => model.ToLowerInvariant().Contains("free");

    /// <summary>
    /// 判断目录是否认为该模型属于免费档（定价元数据，
    /// 回退到 -free 命名习惯）。密钥档塑形用它，
    /// 以便名字里没有 "free" 的免费模型也被覆盖。
    /// </summary>
    public bool IsFreeModel(string model)
    {
        lock (_mutex)
        {
            return AnonymousDecisionLocked(model).Allowed;
        }
    }

    public AnonymousDecision AnonymousDecision(string model)
    {
        lock (_mutex)
        {
            return AnonymousDecisionLocked(model);
        }
    }

    public RouteDiagnostic Diagnostic(string model, string requestedProtocol, bool hasZenKeys, bool hasGoKeys, bool hasAnonymous)
    {
        string configured;
        bool availableZen, availableGo, zenKnown, goKnown;
        Dictionary<string, string> nativeProtocols;
        string prefer;
        lock (_mutex)
        {
            configured = _protocols.GetValueOrDefault(model, "");
            availableZen = _zen.Contains(model);
            availableGo = _goModels.Contains(model);
            zenKnown = HasNativeProtocolLocked(Tier.Zen, model);
            goKnown = HasNativeProtocolLocked(Tier.Go, model);
            nativeProtocols = new Dictionary<string, string>
            {
                [Tier.Zen] = ProtocolForLocked(model, Tier.Zen),
                [Tier.Go] = ProtocolForLocked(model, Tier.Go),
            };
            prefer = _prefer;
        }
        var source = "configured";
        if (configured.Length == 0)
        {
            source = zenKnown || goKnown ? "upstream" : "default";
        }
        var protocol = configured;
        if (protocol.Length == 0)
        {
            // 下方的 Route() 会选出首选的可用档位。这里给出的只是
            // 当前无法构造任何路由时显示的回退。
            protocol = nativeProtocols[Tier.Zen];
            if (prefer == Tier.Go)
            {
                protocol = nativeProtocols[Tier.Go];
            }
        }
        var diagnostic = new RouteDiagnostic
        {
            Model = model,
            RequestedProtocol = requestedProtocol,
            NativeProtocol = protocol,
            NativeProtocols = nativeProtocols,
            ProtocolSource = source,
            AvailableZen = availableZen,
            AvailableGo = availableGo,
            AnonymousEligibility = AnonymousDecision(model),
        };
        try
        {
            var route = Route(model, hasZenKeys, hasGoKeys, hasAnonymous);
            diagnostic.NativeProtocol = route.Protocol;
            diagnostic.NativeProtocols = route.Protocols;
            diagnostic.Tier = route.Tier;
            diagnostic.Anonymous = route.Anonymous;
            diagnostic.KeyTiers = [.. route.KeyTiers];
        }
        catch (CatalogException e)
        {
            diagnostic.RouteError = e.Message;
        }
        return diagnostic;
    }

    public List<string> List()
    {
        lock (_mutex)
        {
            return ModelIDsLocked().Where(SupportedLocked).ToList();
        }
    }

    private List<string> ModelIDsLocked()
    {
        var seen = new HashSet<string>(_zen);
        foreach (var model in _goModels)
        {
            seen.Add(model);
        }
        return SortedSetKeys(seen);
    }

    public CatalogSnapshot Snapshot()
    {
        lock (_mutex)
        {
            return SnapshotLocked(ModelIDsLocked());
        }
    }

    /// <summary>
    /// 让发现与就绪检查使用相同的路由过滤，包含
    /// 配置的密钥档位与匿名线路资格。
    /// </summary>
    public (List<Route> Routes, CatalogSnapshot Snapshot) AvailableModels(bool hasZenKeys, bool hasGoKeys, bool hasAnonymous)
    {
        lock (_mutex)
        {
            var ids = ModelIDsLocked();
            var routes = new List<Route>();
            foreach (var model in ids.Where(SupportedLocked))
            {
                try
                {
                    routes.Add(RouteLocked(model, hasZenKeys, hasGoKeys, hasAnonymous));
                }
                catch (CatalogException)
                {
                    // 无法路由的模型就直接不对外暴露。
                }
            }
            var snapshot = SnapshotLocked(ids);
            return (routes, snapshot with { Exposed = routes.Count });
        }
    }

    private CatalogSnapshot SnapshotLocked(List<string> ids)
    {
        var exposed = ids.Count(SupportedLocked);
        var stale = _stale;
        if (!JsonTime.IsZero(_updatedAt) && _refreshAfter > TimeSpan.Zero)
        {
            var threshold = _refreshAfter * 2 > TimeSpan.FromMinutes(1) ? _refreshAfter * 2 : TimeSpan.FromMinutes(1);
            stale = stale || DateTime.UtcNow - _updatedAt > threshold;
        }
        return new CatalogSnapshot
        {
            Zen = _zen.Count,
            Go = _goModels.Count,
            Total = ids.Count,
            Exposed = exposed,
            UpdatedAt = _updatedAt,
            CacheSource = _cacheSource,
            Stale = stale,
        };
    }

    public bool Supported(string model)
    {
        lock (_mutex)
        {
            return SupportedLocked(model);
        }
    }

    /// <summary>
    /// 返回从 opencode 目录捕获的按模型富元数据，取实际
    /// 将服务该请求的档位。目录缺失的限制值
    /// 由 models.dev 填充；能力标记始终来自目录。
    /// </summary>
    public Metadata MetadataForTier(string model, string tier)
    {
        Metadata metadata;
        PricingStore? pricing;
        lock (_mutex)
        {
            metadata = _modelMeta.TryGetValue(tier, out var byTier) && byTier.TryGetValue(model, out var found)
                ? found.Clone()
                : new Metadata();
            pricing = _pricing;
        }
        if (pricing == null || !pricing.TryGetPrice(model, out var price))
        {
            return metadata;
        }
        // OpenCode 能力目录是权威来源。models.dev 的限制值只用于
        // 填补它遗漏的字段，使发现类客户端仍能拿到
        // 按模型的上下文与输出上限。
        if (metadata.ContextWindow == 0)
        {
            metadata.ContextWindow = price.ContextWindow;
        }
        if (metadata.MaxInput == 0)
        {
            metadata.MaxInput = price.MaxInput;
        }
        if (metadata.MaxOutput == 0)
        {
            metadata.MaxOutput = price.MaxOutput;
        }
        if (price.Input != null || price.Output != null)
        {
            metadata.Cost = new Cost
            {
                Input = price.Input,
                Output = price.Output,
                CacheRead = price.CacheRead,
                CacheWrite = price.CacheWrite,
            };
        }
        return metadata;
    }

    private bool SupportedLocked(string model)
    {
        if (_zen.Count == 0 && _goModels.Count == 0)
        {
            return true;
        }
        if (_zen.Contains(model) && TierSupportedLocked(model, Tier.Zen))
        {
            return true;
        }
        return _goModels.Contains(model) && TierSupportedLocked(model, Tier.Go);
    }

    private bool TierSupportedLocked(string model, string tier)
    {
        if (_protocols.GetValueOrDefault(model, "").Length > 0)
        {
            return true;
        }
        if (IsUnsupportedLocked(tier, model))
        {
            return false;
        }
        if (_nativeProtocols.TryGetValue(tier, out var native) && native.GetValueOrDefault(model, "").Length > 0)
        {
            return true;
        }
        // 待决的目录没有上游能力快照可以反驳
        // 配置的密钥，因此保留刷新前的兼容行为。
        return _zen.Count == 0 && _goModels.Count == 0;
    }

    private static HashSet<string> ToSet(IEnumerable<string> items) => [.. items];

    private static List<string> SortedSetKeys(IEnumerable<string> source) =>
        source.OrderBy(model => model, StringComparer.Ordinal).ToList();

    private static Dictionary<string, Dictionary<string, string>> CloneTierProtocols(
        Dictionary<string, Dictionary<string, string>> source)
    {
        var result = ModelCatalogCache.NewProtocolMaps();
        foreach (var tier in Tier.All)
        {
            if (source.TryGetValue(tier, out var protocols))
            {
                result[tier] = new Dictionary<string, string>(protocols);
            }
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, bool>> CloneTierBools(
        Dictionary<string, Dictionary<string, bool>> source)
    {
        var result = ModelCatalogCache.NewBoolMaps();
        foreach (var tier in Tier.All)
        {
            if (source.TryGetValue(tier, out var models))
            {
                result[tier] = new Dictionary<string, bool>(models);
            }
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, Metadata>> CloneModelMeta(
        Dictionary<string, Dictionary<string, Metadata>> source)
    {
        var result = ModelCatalogCache.NewMetaMaps();
        foreach (var tier in Tier.All)
        {
            if (!source.TryGetValue(tier, out var models))
            {
                continue;
            }
            foreach (var (id, metadata) in models)
            {
                result[tier][id] = metadata.Clone();
            }
        }
        return result;
    }
}
