using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.Config;
using Opencode2Api.Http;
using Opencode2Api.IO;
using Opencode2Api.JsonUtil;
using Opencode2Api.Telemetry;

namespace Opencode2Api.Models;

/// <summary>单个模型的公开元数据（models/pricing.go Price）。</summary>
public sealed class Price
{
    public string ID { get; set; } = "";
    public double? Input { get; set; }
    public double? Output { get; set; }
    public double? CacheRead { get; set; }
    public double? CacheWrite { get; set; }
    public int ContextWindow { get; set; }
    public int MaxInput { get; set; }
    public int MaxOutput { get; set; }
    public bool Deprecated { get; set; }

    public JsonObject ToJson()
    {
        var obj = new JsonObject { ["id"] = ID };
        if (Input != null)
        {
            obj["input_cost"] = JsonValue.Create(Input.Value);
        }
        if (Output != null)
        {
            obj["output_cost"] = JsonValue.Create(Output.Value);
        }
        if (CacheRead != null)
        {
            obj["cache_read_cost"] = JsonValue.Create(CacheRead.Value);
        }
        if (CacheWrite != null)
        {
            obj["cache_write_cost"] = JsonValue.Create(CacheWrite.Value);
        }
        if (ContextWindow != 0)
        {
            obj["context_window"] = ContextWindow;
        }
        if (MaxInput != 0)
        {
            obj["max_input"] = MaxInput;
        }
        if (MaxOutput != 0)
        {
            obj["max_output"] = MaxOutput;
        }
        obj["deprecated"] = Deprecated;
        return obj;
    }

    public static Price FromJson(JsonObject obj, string fallbackID) =>
        new Price
        {
            ID = JsonValues.FirstString(JsonValues.StringAt(obj, "id")).Length > 0 ? JsonValues.StringAt(obj, "id") : fallbackID,
            Input = JsonValues.OptionalNumber(obj, "input_cost"),
            Output = JsonValues.OptionalNumber(obj, "output_cost"),
            CacheRead = JsonValues.OptionalNumber(obj, "cache_read_cost"),
            CacheWrite = JsonValues.OptionalNumber(obj, "cache_write_cost"),
            ContextWindow = JsonValues.IntAt(obj, "context_window"),
            MaxInput = JsonValues.IntAt(obj, "max_input"),
            MaxOutput = JsonValues.IntAt(obj, "max_output"),
            Deprecated = JsonValues.BoolAt(obj, "deprecated"),
        };
}

/// <summary>
/// 匿名线路是否可以承载某个模型，以及是什么依据做出了该判定
/// （models/pricing.go AnonymousDecision）。
/// </summary>
public sealed record AnonymousDecision
{
    public bool Allowed { get; init; }
    public string Source { get; init; } = "";
    public bool Known { get; init; }
    public bool Deprecated { get; init; }
    public double? InputCost { get; init; }
    public double? OutputCost { get; init; }

    public JsonObject ToJson()
    {
        var obj = new JsonObject { ["allowed"] = Allowed, ["source"] = Source, ["known"] = Known, ["deprecated"] = Deprecated };
        if (InputCost != null)
        {
            obj["input_cost"] = JsonValue.Create(InputCost.Value);
        }
        if (OutputCost != null)
        {
            obj["output_cost"] = JsonValue.Create(OutputCost.Value);
        }
        return obj;
    }
}

/// <summary>定价元数据的新鲜度，通过健康检查暴露。</summary>
public sealed record MetadataSnapshot
{
    public bool Ready { get; init; }
    public int Models { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public bool Stale { get; init; }
    public string LastError { get; init; } = "";
    public string CachePath { get; init; } = "";
    public DateTime? NextRefresh { get; init; }
}

/// <summary>
/// 带磁盘缓存与每日后台刷新的 models.dev 定价/元数据存储
/// （models/pricing.go PricingStore）。它是匿名
/// （免费）线路的权威来源。
/// </summary>
public sealed class PricingStore
{
    private static readonly TimeSpan ModelsDevRefresh = TimeSpan.FromHours(24);
    private static readonly TimeSpan ModelsDevTimeout = TimeSpan.FromSeconds(30);

    private readonly object _mutex = new();
    private readonly string _cachePath;
    private readonly string _endpoint = Discovery.CapabilitiesURL;
    private readonly HttpClient _client = new() { Timeout = ModelsDevTimeout };
    private readonly StructuredLogger? _logger;
    private Func<IReadOnlyList<HttpClient>>? _clientProvider;
    private Dictionary<string, Price> _models = [];
    private DateTime _updatedAt;
    private string _lastError = "";

    public PricingStore(string configPath, StructuredLogger? logger)
    {
        _cachePath = configPath.Length > 0 ? configPath + ".models.dev.json" : "";
        _logger = logger;
        try
        {
            LoadCache();
        }
        catch (FileNotFoundException)
        {
            // 缓存缺失是正常的首次启动。
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ConfigException)
        {
            _lastError = "load metadata cache: " + e.Message;
        }
    }

    /// <summary>
    /// 为刷新尝试提供候选 HTTP 客户端，按优先级从前往后，
    /// 使周期性的 models.dev 刷新能复用当前活动
    /// 网关运行时的代理传输。
    /// </summary>
    public void SetClientProvider(Func<IReadOnlyList<HttpClient>> provider)
    {
        lock (_mutex)
        {
            _clientProvider = provider;
        }
    }

    private IReadOnlyList<HttpClient> RefreshClients()
    {
        Func<IReadOnlyList<HttpClient>>? provider;
        lock (_mutex)
        {
            provider = _clientProvider;
        }
        if (provider == null)
        {
            return [_client];
        }
        var clients = provider();
        return clients.Count == 0 ? [_client] : clients;
    }

    /// <summary>启动时先拉一次 models.dev 元数据，之后按 ModelsDevRefresh 周期刷新。</summary>
    public void Start(CancellationToken cancellationToken) => BackgroundLoop.Start(
        "models-dev", _logger, ModelsDevRefresh, true, RefreshAndLogAsync, cancellationToken);

    private async Task RefreshAndLogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or DiscoveryException)
        {
            _logger?.Warn("models.dev metadata refresh failed", ("component", "models"), ("event", "metadata_refresh_failed"), ("error", e));
            return;
        }
        _logger?.Info("models.dev metadata refreshed", ("component", "models"), ("event", "metadata_refreshed"), ("models", Snapshot().Models));
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var client in RefreshClients())
        {
            byte[] data;
            try
            {
                data = await FetchAsync(client, cancellationToken);
            }
            catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                lastError = e;
                continue;
            }
            Dictionary<string, Price> models;
            try
            {
                models = DecodeModelsDev(data);
            }
            catch (Exception e)
            {
                throw RecordError(e);
            }
            var now = DateTime.UtcNow;
            if (_cachePath.Length > 0)
            {
                try
                {
                    SaveCache(_cachePath, now, models);
                }
                catch (Exception e)
                {
                    throw RecordError(e);
                }
            }
            lock (_mutex)
            {
                _models = models;
                _updatedAt = now;
                _lastError = "";
            }
            return;
        }
        lastError ??= new ConfigException("no HTTP client available for models.dev refresh");
        throw RecordError(lastError);
    }

    private async Task<byte[]> FetchAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ModelsDevTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgents.Current());
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if ((int)response.StatusCode / 100 != 2)
        {
            throw new DiscoveryException($"models.dev returned HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        }
        return await Discovery.ReadLimitedAsync(response, 32L << 20, timeout.Token);
    }

    private Exception RecordError(Exception error)
    {
        lock (_mutex)
        {
            _lastError = error.Message;
        }
        return error;
    }

    public AnonymousDecision Decide(string model)
    {
        Price? price;
        bool ready;
        lock (_mutex)
        {
            price = _models.TryGetValue(model, out var found) ? found : null;
            ready = !JsonTime.IsZero(_updatedAt) && _models.Count > 0;
        }
        var nameFree = Catalog.NameIsFree(model);
        AnonymousDecision Fallback(string source) =>
            nameFree ? new AnonymousDecision { Allowed = true, Source = "name_free" } : new AnonymousDecision { Source = source };
        if (!ready)
        {
            return Fallback("metadata_pending");
        }
        if (price == null)
        {
            return Fallback("metadata_model_missing");
        }
        var metadataFree = !price.Deprecated && price.Input != null && price.Output != null
            && price.Input == 0 && price.Output == 0;
        var decision = new AnonymousDecision
        {
            Known = true,
            Deprecated = price.Deprecated,
            InputCost = price.Input,
            OutputCost = price.Output,
        };
        if (nameFree || metadataFree)
        {
            decision = decision with
            {
                Allowed = true,
                Source = nameFree && metadataFree ? "name_and_metadata_free" : nameFree ? "name_free" : "metadata_free",
            };
            return decision;
        }
        if (price.Deprecated)
        {
            return decision with { Source = "metadata_deprecated" };
        }
        if (price.Input == null || price.Output == null)
        {
            return decision with { Known = false, Source = "metadata_cost_unknown" };
        }
        return decision with { Source = "metadata_paid" };
    }

    public bool TryGetPrice(string model, out Price price)
    {
        lock (_mutex)
        {
            return _models.TryGetValue(model, out price!);
        }
    }

    public MetadataSnapshot Snapshot()
    {
        lock (_mutex)
        {
            var snapshot = new MetadataSnapshot
            {
                Ready = !JsonTime.IsZero(_updatedAt) && _models.Count > 0,
                Models = _models.Count,
                LastError = _lastError,
                CachePath = _cachePath,
            };
            if (JsonTime.IsZero(_updatedAt))
            {
                return snapshot;
            }
            var updated = _updatedAt.ToUniversalTime();
            return snapshot with
            {
                UpdatedAt = updated,
                NextRefresh = updated.Add(ModelsDevRefresh),
                Stale = DateTime.UtcNow - updated > ModelsDevRefresh,
            };
        }
    }

    private void LoadCache()
    {
        if (_cachePath.Length == 0)
        {
            return;
        }
        var text = File.ReadAllText(_cachePath);
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new ConfigException("metadata cache is not a JSON object");
        var updatedAt = JsonTime.Parse(JsonValues.StringAt(root, "updated_at"));
        var models = new Dictionary<string, Price>();
        if (JsonValues.MapAt(root, "models") is JsonObject items)
        {
            foreach (var property in items)
            {
                if (property.Value is JsonObject item)
                {
                    models[property.Key] = Price.FromJson(item, property.Key);
                }
            }
        }
        if (JsonTime.IsZero(updatedAt) || models.Count == 0)
        {
            throw new ConfigException("metadata cache is empty or missing updated_at");
        }
        lock (_mutex)
        {
            _models = models;
            _updatedAt = updatedAt;
        }
    }

    private static void SaveCache(string path, DateTime updatedAt, Dictionary<string, Price> models)
    {
        var root = new JsonObject { ["updated_at"] = JsonTime.Format(updatedAt), ["models"] = new JsonObject() };
        foreach (var (id, price) in models.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            root["models"]!.AsObject()[id] = price.ToJson();
        }
        AtomicText.Write(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ".models-dev-");
    }

    /// <summary>
    /// 从 models.dev 文档中提取属于 OpenCode 的模型元数据。
    /// 对提供方排序，使 OpenCode 自己的条目在 ID 重复时胜出。
    /// </summary>
    internal static Dictionary<string, Price> DecodeModelsDev(byte[] data)
    {
        var root = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(data))?.AsObject()
            ?? throw new ConfigException($"decode models.dev: expected a JSON object");
        var keys = root
            .Select(property => property.Key)
            .OrderBy(MetadataProviderRank)
            .ThenBy(key => key, StringComparer.Ordinal)
            .ToList();
        var result = new Dictionary<string, Price>();
        foreach (var key in keys)
        {
            if (MetadataProviderRank(key) > 1)
            {
                continue;
            }
            if (root[key] is not JsonObject provider)
            {
                continue;
            }
            if (MetadataProviderRank(key) == 1)
            {
                var identity = (JsonValues.FirstString(JsonValues.StringAt(provider, "id"), JsonValues.StringAt(provider, "name"))).ToLowerInvariant();
                if (!identity.Contains("opencode"))
                {
                    continue;
                }
            }
            if (JsonValues.MapAt(provider, "models") is not JsonObject models)
            {
                continue;
            }
            foreach (var modelProperty in models)
            {
                if (modelProperty.Value is not JsonObject model)
                {
                    continue;
                }
                var modelID = JsonValues.FirstString(JsonValues.StringAt(model, "id"), modelProperty.Key);
                // OpenCode 排在最前，因此 ID 重复时保留它的元数据，
                // 其他 OpenCode 档位只用于填补空缺。
                if (result.ContainsKey(modelID))
                {
                    continue;
                }
                var cost = JsonValues.MapAt(model, "cost") ?? [];
                var limit = JsonValues.MapAt(model, "limit") ?? [];
                result[modelID] = new Price
                {
                    ID = modelID,
                    Input = JsonValues.OptionalNumber(cost, "input"),
                    Output = JsonValues.OptionalNumber(cost, "output"),
                    CacheRead = JsonValues.OptionalNumber(cost, "cache_read"),
                    CacheWrite = JsonValues.OptionalNumber(cost, "cache_write"),
                    ContextWindow = JsonValues.IntAt(limit, "context"),
                    MaxInput = JsonValues.IntAt(limit, "input"),
                    MaxOutput = JsonValues.IntAt(limit, "output"),
                    Deprecated = MetadataDeprecated(model),
                };
            }
        }
        if (result.Count > 0)
        {
            return result;
        }
        throw new ConfigException("models.dev contains no OpenCode model metadata");
    }

    private static int MetadataProviderRank(string key)
    {
        var lower = key.ToLowerInvariant();
        if (lower is "opencode" or "opencode-zen" or "opencode_zen")
        {
            return 0;
        }
        return lower.Contains("opencode") ? 1 : 2;
    }

    private static bool MetadataDeprecated(JsonObject model)
    {
        if (JsonValues.BoolAt(model, "deprecated"))
        {
            return true;
        }
        var status = JsonValues.FirstString(JsonValues.StringAt(model, "status"), JsonValues.StringAt(model, "lifecycle")).ToLowerInvariant();
        if (status is "deprecated" or "retired" or "disabled")
        {
            return true;
        }
        return model["deprecated_at"] != null || model["retirement_date"] != null;
    }
}
