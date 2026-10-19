using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Opencode2Api.Config;
using Opencode2Api.Http;
using Opencode2Api.Models;
using Opencode2Api.Protocol;

namespace Opencode2Api.Gateway;

/// <summary>
/// 面向运维者的只读端点：/healthz 与 /v1/models
/// （internal/gateway/health.go）。JSON 结构与 Go 实现逐字段兼容，
/// 因此既有看板与探针可以继续工作。
/// </summary>
internal sealed partial class ApiGateway
{
    /// <summary>
    /// 网关接受并转发的思考等级，从低到高。"auto"
    /// 与 "none" 是网关控制项，不是等级。
    /// </summary>
    private static readonly List<string> ReasoningLevels =
    ["minimal", "low", "medium", "high", "xhigh", "max"];

    private sealed record HealthModels
    {
        [JsonPropertyName("status")] public string Status { get; init; } = "";
        [JsonPropertyName("total")] public int Total { get; init; }
        [JsonPropertyName("exposed")] public int Exposed { get; init; }
        [JsonPropertyName("zen")] public int Zen { get; init; }
        [JsonPropertyName("go")] public int Go { get; init; }
        [JsonPropertyName("last_refresh")] public DateTime? LastRefresh { get; init; }
        [JsonPropertyName("stale_after_seconds")] public int StaleAfterSeconds { get; init; }
        [JsonPropertyName("cache_source")] public string? CacheSource { get; init; }
        [JsonPropertyName("stale")] public bool Stale { get; init; }
    }

    private sealed record HealthKeys
    {
        [JsonPropertyName("zen")] public int Zen { get; init; }
        [JsonPropertyName("go")] public int Go { get; init; }
        [JsonPropertyName("total")] public int Total { get; init; }
        [JsonPropertyName("anonymous")] public bool Anonymous { get; init; }
    }

    private sealed record HealthProxies
    {
        [JsonPropertyName("total")] public int Total { get; init; }
        [JsonPropertyName("healthy")] public int Healthy { get; init; }
        [JsonPropertyName("unhealthy")] public int Unhealthy { get; init; }
    }

    private sealed record HealthResponse
    {
        [JsonPropertyName("status")] public string Status { get; init; } = "";
        [JsonPropertyName("ready")] public bool Ready { get; init; }
        [JsonPropertyName("version")] public string Version { get; init; } = "";
        [JsonPropertyName("models")] public HealthModels Models { get; init; } = new();
        [JsonPropertyName("keys")] public HealthKeys Keys { get; init; } = new();
        [JsonPropertyName("proxies")] public HealthProxies Proxies { get; init; } = new();
        [JsonPropertyName("issues")] public List<string>? Issues { get; init; }
    }

    /// <summary>GET /healthz。</summary>
    public async Task HandleHealth(HttpContext context)
    {
        var (_, models) = AvailableModels();
        var (proxyTotal, proxyHealthy) = _transports.HealthCounts();
        var zenKeys = _zenNodes.Nodes.Count;
        var goKeys = _goNodes.Nodes.Count;
        var staleAfter = Durations.Max(TimeSpan.FromSeconds(2 * _cfg.Models.RefreshSeconds), TimeSpan.FromMinutes(1));

        var modelStatus = "ready";
        DateTime? lastRefresh = null;
        var issues = new List<string>(3);
        if (models.UpdatedAt == default)
        {
            modelStatus = "pending";
            issues.Add("model_catalog_pending");
        }
        else
        {
            var updatedAt = DateTime.SpecifyKind(models.UpdatedAt, DateTimeKind.Utc);
            lastRefresh = updatedAt;
            if (models.Exposed == 0)
            {
                modelStatus = "empty";
                issues.Add("model_catalog_empty");
            }
            else if (models.Stale || DateTime.UtcNow - models.UpdatedAt > staleAfter)
            {
                modelStatus = "stale";
                issues.Add("model_catalog_stale");
            }
        }
        if (zenKeys + goKeys == 0 && !_cfg.Anonymous)
        {
            issues.Add("no_upstream_keys");
        }
        if (proxyHealthy == 0)
        {
            issues.Add("no_healthy_proxies");
        }

        var status = "ok";
        if (issues.Count > 0)
        {
            status = "degraded";
        }
        var blocking = modelStatus == "pending" || modelStatus == "empty" || proxyHealthy == 0;
        var httpStatus = StatusCodes.Status200OK;
        var ready = !blocking;
        if (blocking)
        {
            httpStatus = StatusCodes.Status503ServiceUnavailable;
            if (modelStatus == "pending")
            {
                status = "starting";
            }
        }
        await HttpResponses.WriteJson(context, httpStatus, new HealthResponse
        {
            Status = status,
            Ready = ready,
            Version = BuildInfo.Version,
            Models = new HealthModels
            {
                Status = modelStatus,
                Total = models.Total,
                Exposed = models.Exposed,
                Zen = models.Zen,
                Go = models.Go,
                LastRefresh = lastRefresh,
                StaleAfterSeconds = (int)staleAfter.TotalSeconds,
                CacheSource = models.CacheSource.Length > 0 ? models.CacheSource : null,
                Stale = models.Stale,
            },
            Keys = new HealthKeys { Zen = zenKeys, Go = goKeys, Total = zenKeys + goKeys, Anonymous = _cfg.Anonymous },
            Proxies = new HealthProxies
            {
                Total = proxyTotal,
                Healthy = proxyHealthy,
                Unhealthy = proxyTotal - proxyHealthy,
            },
            Issues = issues.Count > 0 ? issues : null,
        });
    }

    internal (List<Route> Routes, CatalogSnapshot Snapshot) AvailableModels() =>
        _catalog.AvailableModels(_zenNodes.Nodes.Count > 0, _goNodes.Nodes.Count > 0, _cfg.Anonymous);

    /// <summary>
    /// 列出推理模型可被请求的等级。当 "auto" 探测已找到上游
    /// 所能接受的最高等级时，列表就在那里截断，
    /// 避免客户端提供会被拒绝的等级。
    /// </summary>
    private List<string> ReasoningEfforts(string model, Route route)
    {
        if (_cfg.ForcedEffort(model) == "auto")
        {
            var ceiling = ReasoningLevels.IndexOf(ResolvedEffort(model, route.Tier, route.Protocol));
            if (ceiling >= 0)
            {
                return [.. ReasoningLevels.GetRange(0, ceiling + 1)];
            }
        }
        return [.. ReasoningLevels];
    }

    /// <summary>GET /v1/models（由本地服务密钥鉴权）。</summary>
    public async Task HandleModels(HttpContext context)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var (routes, _) = AvailableModels();
        var data = new JsonArray();
        foreach (var route in routes)
        {
            var model = route.ID;
            // 按档位给出的元数据：对外宣告的上下文窗口必须与
            // 实际服务该请求的档位一致（匿名 ⇒ Zen）。
            var md = _catalog.MetadataForTier(model, route.Tier);
            var entry = new JsonObject
            {
                ["id"] = model,
                ["object"] = "model",
                ["created"] = now,
                ["owned_by"] = "opencode",
                ["metadata"] = md.ToJson(),
                ["native_protocol"] = route.Protocol,
            };
            entry["supported_endpoints"] = route.Protocol == Wire.SystemOne
                ? new JsonArray(JsonValue.Create("/v1/systemone"))
                : new JsonArray(
                    JsonValue.Create("/v1/chat/completions"),
                    JsonValue.Create("/v1/responses"),
                    JsonValue.Create("/v1/messages"));
            // 顶层 OpenAI 标准字段：发现类客户端会在每个模型条目的
            // 顶层读取 context/reasoning。
            if (md.ContextWindow > 0)
            {
                entry["context_window"] = md.ContextWindow;
                entry["context_length"] = md.ContextWindow;
            }
            if (md.MaxInput > 0)
            {
                entry["max_input"] = md.MaxInput;
            }
            if (md.MaxOutput > 0)
            {
                entry["max_output"] = md.MaxOutput;
            }
            if (md.Reasoning)
            {
                entry["reasoning"] = true;
                entry["supports_reasoning"] = true;
                var efforts = ReasoningEfforts(model, route);
                entry["reasoning_efforts"] = new JsonArray([.. efforts.Select(e => (JsonNode?)JsonValue.Create(e))]);
                // OpenClaw 之类的运行框架从 compat 读取思考等级而不是自行
                // 推断，否则只会提供 off/max 两档。
                entry["compat"] = new JsonObject
                {
                    ["supportsReasoningEffort"] = true,
                    ["supportedReasoningEfforts"] = new JsonArray([.. efforts.Select(e => (JsonNode?)JsonValue.Create(e))]),
                };
                var effort = ResolvedEffort(model, route.Tier, route.Protocol);
                if (effort.Length != 0 && effort != "none")
                {
                    entry["default_reasoning_effort"] = effort;
                }
            }
            if (md.Cost != null)
            {
                var cost = new JsonObject();
                // 驼峰别名匹配 OpenClaw 的模型费用结构。
                AddCost(cost, "input", md.Cost.Input);
                AddCost(cost, "output", md.Cost.Output);
                AddCost(cost, "cache_read", md.Cost.CacheRead, "cacheRead");
                AddCost(cost, "cache_write", md.Cost.CacheWrite, "cacheWrite");
                entry["cost"] = cost;
            }
            if (md.ToolCall)
            {
                entry["tool_call"] = true;
            }
            if (md.StructuredOutput)
            {
                entry["structured_output"] = true;
            }
            data.Add(entry);
        }
        foreach (var group in Rotation.TakeSnapshot().Groups)
        {
            if (_goNodes.Nodes.Count == 0)
            {
                continue;
            }
            var available = false;
            foreach (var model in group.Order)
            {
                try
                {
                    if (_catalog.RouteForTier(model, Tier.Go, false, true).Protocol != Wire.SystemOne)
                    {
                        available = true;
                        break;
                    }
                }
                catch (CatalogException)
                {
                }
            }
            if (!available)
            {
                continue;
            }
            data.Add(new JsonObject
            {
                ["id"] = group.Alias,
                ["object"] = "model",
                ["created"] = now,
                ["owned_by"] = "opencode-go-rotation",
            });
        }
        await HttpResponses.WriteJson(context, StatusCodes.Status200OK,
            new JsonObject { ["object"] = "list", ["data"] = data });
    }

    private static void AddCost(JsonObject cost, string name, double? value, string? alias = null)
    {
        if (value == null)
        {
            return;
        }
        cost[name] = JsonValue.Create(value.Value);
        if (alias != null)
        {
            cost[alias] = JsonValue.Create(value.Value);
        }
    }

}
