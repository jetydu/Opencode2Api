using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.Config;
using Opencode2Api.Identity;
using Opencode2Api.JsonUtil;
using Opencode2Api.Models;
using Opencode2Api.Protocol;

namespace Opencode2Api.Gateway;

/// <summary>
/// 免费模型可达性探测（internal/gateway/probe.go）。探测是唯一
/// 决定某个免费模型是否对外暴露的地方，并对 "auto"
/// 推理模型确定上游所能接受的最高思考等级。
/// </summary>
internal sealed partial class ApiGateway
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>探测可以走的一条凭据线路。</summary>
    private readonly record struct ProbeLane(string Key, string Name, string BaseURL, ProxyTransport Proxy);

    internal sealed class ProbeResult
    {
        public bool Success { get; set; }
        public bool Attempted { get; set; }
        public bool EffortRejected { get; set; }
        public string Reason { get; set; } = "";
        public string Channel { get; set; } = "";
        public EffortProbe Effort { get; set; } = new();
    }

    /// <summary>
    /// 解析需要注入上游请求的思考等级。"auto" 是网关控制项：
    /// 它会被替换为探测证明上游可以接受的最高等级，
    /// 且只用于免费 Zen 线路。
    /// </summary>
    internal string ResolvedEffort(string model, string tier, string protocol)
    {
        var effort = _cfg.ForcedEffort(model);
        if (effort != "auto")
        {
            return effort;
        }
        // 只有免费 Zen 线路会被主动探测。绝不把观测结果迁移到不同的
        // 协议/档位，也不为未探测过的模型猜测等级。
        if (tier != Tier.Zen || protocol == Wire.SystemOne || Availability == null || !_catalog.IsFreeModel(model))
        {
            return "";
        }
        var item = Availability.Get(model);
        if (!_catalog.MetadataForTier(model, tier).Reasoning || item.AutoEffort.Effort == "none")
        {
            // 不要向只接受基线请求的模型注入 reasoning 字段。
            return "";
        }
        if (item.Disabled || item.AutoEffort.Protocol != protocol ||
            DateTime.UtcNow - item.EffortCheckedAt > 2 * AvailabilityStore.FreeModelCheckInterval)
        {
            return "";
        }
        return item.AutoEffort.Effort;
    }

    private static byte[] ProbePayload(string model, string protocol, string effort, bool legacy)
    {
        if (protocol == Wire.SystemOne)
        {
            // TypeSafe 的原生布尔类型是 "noul"，不是 JSON Schema 的 boolean。
            return Response.ToUtf8(new JsonObject
            {
                ["model"] = model,
                ["state"] = "1 + 1 = 2",
                ["questions"] = new JsonObject
                {
                    ["arithmetic"] = new JsonObject
                    {
                        ["type"] = "noul",
                        ["instructions"] = "Is 1 + 1 equal to 2?",
                    },
                },
            });
        }
        var payload = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["max_tokens"] = 64,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Reply with OK." }),
        };
        var converted = RequestBridge.ConvertRequest(Wire.Chat, protocol, payload);
        if (effort.Length != 0)
        {
            RequestBridge.ForcedEffort(protocol, converted, effort);
        }
        return AnonymousBody.Prepare(Response.ToUtf8(converted), protocol, legacy).Body;
    }

    internal async Task<ProbeResult> ProbeFreeModel(CancellationToken cancellationToken, string model)
    {
        if (!_catalog.IsFreeModel(model))
        {
            return new ProbeResult { Reason = "not_free" };
        }
        var diagnostic = _catalog.Diagnostic(model, Wire.Chat, _zenNodes.Nodes.Count > 0, false, _cfg.Anonymous);
        var protocol = diagnostic.NativeProtocols.TryGetValue(Tier.Zen, out var native) ? native : "";
        if (!diagnostic.AvailableZen || !_catalog.Supported(model))
        {
            return new ProbeResult { Reason = "unsupported_probe" };
        }
        var ids = new RequestIds
        {
            Session = RequestIds.CanonicalSessionId("availability:" + model),
            Project = "opencode2api:default-project",
        };
        var lanes = new List<ProbeLane>();
        if (_cfg.Anonymous)
        {
            var cursor = _anonymous.CursorFor(ids.Session);
            var node = cursor.Advance();
            if (node != null)
            {
                // 在回落到 /inference 之前优先使用旧版 /zen/v1 线路。
                foreach (var baseURL in AnonymousUpstreams())
                {
                    lanes.Add(new ProbeLane(AnonymousBody.ZenKey, "anonymous", baseURL, node.Proxy));
                }
            }
        }
        var zenCursor = _zenNodes.CursorFor(ids.Session);
        var zenNode = zenCursor.Advance();
        if (zenNode != null)
        {
            var proxy = _zenNodes.ProxyOf(zenNode);
            if (proxy != null)
            {
                lanes.Add(new ProbeLane(zenNode.Key, "zen", _cfg.Upstream.Zen, proxy));
            }
        }
        var result = new ProbeResult { Reason = "no_probe_channel" };
        var inconclusive = "";
        foreach (var lane in lanes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ProbeResult { Reason = "canceled" };
            }
            async Task<ProbeResult> RunAsync(string effort) => await ProbeAttempt(cancellationToken, lane, model, protocol, ids, effort);
            result = await RunAsync("");
            result.Channel = lane.Name;
            if (!result.Success)
            {
                if (result.Reason != "model_unavailable")
                {
                    inconclusive = result.Reason;
                }
                continue;
            }
            if (_cfg.ForcedEffort(model) != "auto" || protocol == Wire.SystemOne)
            {
                return result;
            }
            if (!_catalog.MetadataForTier(model, Tier.Zen).Reasoning)
            {
                result.Effort = new EffortProbe { Effort = "none", Protocol = protocol };
                return result;
            }
            foreach (var effort in new[] { "max", "xhigh", "high", "medium", "low", "minimal", "none" })
            {
                if (effort == "none")
                {
                    // 基线请求已在不带 effort 字段的情况下成功。
                    result.Effort = new EffortProbe { Effort = "none", Protocol = protocol };
                    break;
                }
                var trial = await RunAsync(effort);
                if (trial.Success)
                {
                    result.Effort = new EffortProbe { Effort = effort, Protocol = protocol };
                    break;
                }
                // 限流或超时并不能说明需要更低的推理
                // 等级，保留既有观测结果。
                if (!trial.EffortRejected)
                {
                    break;
                }
                // 一旦更高等级被明确拒绝，下一次试验没有结论时
                // 也不要保留更早的天花板。
                result.Effort = new EffortProbe { Reset = true };
            }
            return result;
        }
        // 单条线路的鉴权/瞬时失败不能证明全局不可用。
        if (inconclusive.Length != 0)
        {
            result.Reason = inconclusive;
        }
        return result;
    }

    private async Task<ProbeResult> ProbeAttempt(
        CancellationToken parent, ProbeLane lane, string model, string protocol, RequestIds ids, string effort)
    {
        byte[] body;
        try
        {
            body = ProbePayload(model, protocol, effort, AnonymousBody.IsLegacyZenUpstream(lane.BaseURL));
        }
        catch (Exception error) when (error is ProtocolException or ConfigException or JsonException
            or CatalogException or InvalidOperationException)
        {
            return new ProbeResult { Reason = "request_conversion_error", Channel = lane.Name };
        }
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(parent);
        scope.CancelAfter(ProbeTimeout);
        var probeIds = new RequestIds
        {
            Session = ids.Session,
            Request = RequestIds.RandomId("req", 16),
            Project = ids.Project,
            ParentSession = ids.ParentSession,
        };
        HttpRequestMessage request;
        try
        {
            request = UpstreamRequest.New(lane.BaseURL, protocol, body, probeIds, lane.Key);
        }
        catch (Exception error) when (error is UriFormatException or InvalidOperationException)
        {
            _ = error;
            return new ProbeResult { Reason = "request_creation_error", Channel = lane.Name };
        }
        var result = new ProbeResult { Attempted = true, Channel = lane.Name };
        HttpResponseMessage message;
        try
        {
            message = await lane.Proxy.SendAsync(request, scope.Token);
        }
        catch (Exception)
        {
            result.Reason = scope.IsCancellationRequested ? "probe_timeout" : "transport_error";
            return result;
        }
        using (message)
        {
            var status = (int)message.StatusCode;
            if (status / 100 != 2)
            {
                result.Reason = $"http_{status}";
                var (tail, failed) = await ReadCappedAsync(
                    await message.Content.ReadAsStreamAsync(scope.Token), 64 << 10);
                if (!failed)
                {
                    var text = Encoding.UTF8.GetString(tail);
                    result.EffortRejected = IsEffortRejection(status, text);
                    if (IsModelUnavailable(status, text))
                    {
                        result.Reason = "model_unavailable";
                    }
                }
                return result;
            }
            var bodyStream = await message.Content.ReadAsStreamAsync(scope.Token);
            var contentType = message.Content.Headers.TryGetValues("Content-Type", out var values)
                ? string.Join(",", values) : "";
            Exception? error2 = null;
            if (contentType.ToLowerInvariant().Contains("text/event-stream") && protocol != Wire.SystemOne)
            {
                var (events, _) = await ReadCappedAsync(bodyStream, 1 << 20);
                try
                {
                    await Collapse.CollapseAsync(new MemoryStream(events), protocol, model);
                }
                catch (Exception collapseError)
                {
                    error2 = collapseError;
                }
            }
            else
            {
                var (documentBytes, _) = await ReadCappedAsync(bodyStream, 1 << 20);
                var document = Jsonx.ParseObject(Encoding.UTF8.GetString(documentBytes), out error2);
                if (error2 == null && (document == null || HasErrorEnvelope(document)))
                {
                    error2 = new GatewayException("upstream error envelope");
                }
                if (error2 == null)
                {
                    error2 = ValidateProbeDocument(document!, protocol);
                }
            }
            result.Success = error2 == null && !scope.IsCancellationRequested;
            result.Reason = "probe_succeeded";
            if (result.Success)
            {
                return result;
            }
            result.Reason = "invalid_or_incomplete_response";
            if (error2 != null)
            {
                result.EffortRejected = IsEffortRejection(StatusCodes.Status400BadRequest, error2.Message);
                if (IsModelUnavailable(StatusCodes.Status400BadRequest, error2.Message))
                {
                    result.Reason = "model_unavailable";
                }
            }
            if (scope.IsCancellationRequested)
            {
                result.Reason = "probe_timeout";
            }
            return result;
        }
    }

    private static Exception? ValidateProbeDocument(JsonObject document, string protocol)
    {
        if (protocol == Wire.SystemOne)
        {
            var answer = JsonValues.MapAt(document, "answers", "arithmetic");
            var probability = answer != null &&
                answer["noul"]?.GetValueKind() == JsonValueKind.Number ? answer["noul"]!.GetValue<double>() : double.NaN;
            if (double.IsNaN(probability) || probability < 0 || probability > 1 ||
                JsonValues.StringAt(answer!, "type") != "noul")
            {
                return new GatewayException("invalid System One answer");
            }
            return null;
        }
        var field = protocol switch
        {
            Wire.Anthropic => "content",
            Wire.Responses => "output",
            _ => "choices",
        };
        return document[field] is JsonArray ? null : new GatewayException("invalid response schema");
    }

    private static bool IsModelUnavailable(int status, string body)
    {
        if (status is not (400 or 404 or 410 or 422))
        {
            return false;
        }
        var text = body.ToLowerInvariant();
        foreach (var marker in new[]
        {
            "model_not_found", "model_not_available", "model_retired", "model_deprecated",
            "unknown model", "model does not exist", "model has been retired",
            "model is no longer available",
        })
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsEffortRejection(int status, string body)
    {
        if (status is not (400 or 422))
        {
            return false;
        }
        var text = body.ToLowerInvariant();
        if (!text.Contains("effort", StringComparison.Ordinal) &&
            !text.Contains("thinking", StringComparison.Ordinal) &&
            !text.Contains("reasoning", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var marker in new[] { "invalid", "unsupported", "not support", "not allowed", "must be", "allowed values" })
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
