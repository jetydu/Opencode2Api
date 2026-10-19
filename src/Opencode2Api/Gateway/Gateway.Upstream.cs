using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.Config;
using Opencode2Api.Http;
using Opencode2Api.Identity;
using Opencode2Api.JsonUtil;
using Opencode2Api.Models;
using Opencode2Api.Protocol;
using Opencode2Api.Telemetry;

namespace Opencode2Api.Gateway;

/// <summary>
/// 分层重试循环：先匿名线路，再按优先顺序的密钥档位，
/// 然后是档位内的每个密钥（internal/gateway/upstream.go）。
/// </summary>
internal sealed partial class ApiGateway
{
    /// <summary>requestBodyDumpLimit 限定出站请求体最多记录多少。</summary>
    private const int RequestBodyDumpLimit = 64 << 10;

    private readonly record struct UpstreamResult(UpstreamResponse? Response, Route Route, Exception? Error);

    private readonly record struct TierResult(UpstreamResponse? Response, int Attempts, Exception? Error);

    private readonly record struct TieredResult(UpstreamResponse? Response, Route Route, int Attempts, Exception? Error);

    /// <summary>
    /// 先执行完整路由，若上游因过期推理引用返回 400，
    /// 则去掉这些引用重放一次。
    /// </summary>
    private async Task<UpstreamResult> DoUpstreamAsync(
        HttpContext context, Route route, Dictionary<string, byte[]> bodies, RequestIds ids, RequestBudget budget)
    {
        var first = await DoUpstreamTiersAsync(context, route, bodies, ids, 0, budget);
        if (first.Error != null || first.Response == null || first.Response.Status != StatusCodes.Status400BadRequest)
        {
            return new UpstreamResult(first.Response, first.Route, first.Error);
        }
        var original = first.Response;
        var (errorBody, _) = await ReadCappedAsync(original.Body, 1 << 20);
        // 原始网络请求体在这里始终会被关闭：下方的重试路径
        // 复用连接，否则下游拿到的是缓存字节上的
        // 全新内存读取器。
        await original.DrainBodyAsync();
        var response = original.WithBufferedBody(errorBody);
        // 记录上游错误体的截断片段，让 400 的具体类别可见，
        // 而不倾倒完整负载。脱敏器会清除
        // 日志输出中配置的密钥。
        if (errorBody.Length > 0)
        {
            var snippet = _redactor.String(Encoding.UTF8.GetString(errorBody));
            if (snippet.Length > 240)
            {
                snippet = snippet[..240];
            }
            _logger.Info("upstream error returned or retried",
                ("component", "upstream"), ("event", "upstream_error_body"), ("request_id", ids.Request),
                ("model", route.ID), ("status", response.Status), ("snippet", snippet));
        }
        if (!IsStaleReasoningReference(errorBody))
        {
            return new UpstreamResult(response, first.Route, null);
        }
        var (stripped, changed) = StripStaleReasoningInputs(first.Route, bodies);
        if (!changed)
        {
            return new UpstreamResult(response, first.Route, null);
        }
        // 被引用的 reasoning 条目属于本会话已无法访问的上游链路
        // （例如被中断的流）。应去掉它们重放一次，
        // 而不是直接让客户端请求失败。尝试编号
        // 从第一轮继续，使监控不会为同一个请求
        // 显示重复的尝试序号。
        //
        // 客户端会话被有意保留：它承载密钥/代理亲和性与
        // 上游会话关联。重新签发会话会让重放
        // 落到另一个节点上，提示缓存变冷。
        _logger.Info("retrying upstream without stale reasoning references",
            ("component", "upstream"), ("event", "reasoning_reference_retry"), ("request_id", ids.Request),
            ("model", route.ID), ("tier", first.Route.Tier), ("attempt_offset", first.Attempts));
        var retry = await DoUpstreamTiersAsync(context, first.Route, stripped, ids, first.Attempts, budget);
        if (retry.Error != null || retry.Response == null || retry.Response.Status / 100 != 2)
        {
            var retryStatus = 0;
            if (retry.Response != null)
            {
                retryStatus = retry.Response.Status;
                await retry.Response.DrainAndCloseAsync();
            }
            _logger.Warn("reasoning reference retry failed; returning original error",
                ("component", "upstream"), ("event", "reasoning_reference_retry_failed"), ("request_id", ids.Request),
                ("model", route.ID), ("tier", first.Route.Tier), ("error", retry.Error), ("retry_status", retryStatus));
            return new UpstreamResult(response, first.Route, null);
        }
        // 此时第一个响应只包装错误体的内存副本。
        original.Close();
        return new UpstreamResult(retry.Response, retry.Route, null);
    }

    /// <summary>
    /// 判断上游 400 响应体是否描述了服务器不再认识的
    /// reasoning 条目/引用。仅仅是提到 reasoning 的
    /// 通用校验错误绝不能匹配，因此目标短语与
    /// gone/expired 标记都必须命中。
    /// </summary>
    private static bool IsStaleReasoningReference(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body).ToLowerInvariant();
        if (!text.Contains("reasoning item", StringComparison.Ordinal) &&
            !text.Contains("reasoning reference", StringComparison.Ordinal))
        {
            return false;
        }
        return text.Contains("not found", StringComparison.Ordinal) ||
            text.Contains("expir", StringComparison.Ordinal) ||
            text.Contains("does not exist", StringComparison.Ordinal) ||
            text.Contains("no longer", StringComparison.Ordinal);
    }

    /// <summary>
    /// 从 Responses 协议的上游负载中移除服务端签发的 reasoning 引用：
    /// 被重放的 "reasoning" 输入条目以及任何
    /// previous_response_id 链路关联。其他档位/协议原样透传，不做改动。
    /// </summary>
    private static (Dictionary<string, byte[]> Bodies, bool Changed) StripStaleReasoningInputs(
        Route route, Dictionary<string, byte[]> bodies)
    {
        var changed = false;
        var output = new Dictionary<string, byte[]>(bodies.Count);
        foreach (var (tier, body) in bodies)
        {
            if (body.Length == 0 || route.ProtocolFor(tier) != Wire.Responses)
            {
                output[tier] = body;
                continue;
            }
            var payload = Jsonx.TryParseObject(body);
            if (payload == null)
            {
                output[tier] = body;
                continue;
            }
            var tierChanged = false;
            if (payload.ContainsKey("previous_response_id"))
            {
                payload.Remove("previous_response_id");
                tierChanged = true;
            }
            if (payload["input"] is JsonArray raw)
            {
                var kept = new JsonArray();
                foreach (var item in raw)
                {
                    if (item is JsonObject itemObject && JsonValues.StringAt(itemObject, "type") == "reasoning")
                    {
                        tierChanged = true;
                        continue;
                    }
                    // 一个 JsonNode 只能有一个父节点，因此保留的条目
                    // 必须从原数组克隆而非移动。
                    kept.Add(item?.DeepClone());
                }
                if (tierChanged)
                {
                    payload["input"] = kept;
                }
            }
            if (!tierChanged)
            {
                output[tier] = body;
                continue;
            }
            try
            {
                output[tier] = Response.ToUtf8(payload);
                changed = true;
            }
            catch (Exception error) when (error is JsonException or NotSupportedException)
            {
                output[tier] = body;
            }
        }
        return (output, changed);
    }

    private async Task<TieredResult> DoUpstreamTiersAsync(
        HttpContext context, Route route, Dictionary<string, byte[]> bodies, RequestIds ids,
        int attemptOffset, RequestBudget budget)
    {
        UpstreamResponse? lastResponse = null;
        Exception? lastError = null;
        var effectiveRoute = route;
        var attempts = attemptOffset;
        DumpOutboundBodies(route, bodies, ids, attemptOffset);
        if (route.Anonymous)
        {
            var anonymousResult = await DoAnonymousUpstreamAsync(context, route, bodies, ids, attempts, budget);
            attempts += anonymousResult.Attempts;
            if (anonymousResult.Error == null && anonymousResult.Response != null && anonymousResult.Response.Status / 100 == 2)
            {
                return new TieredResult(anonymousResult.Response, route, attempts, null);
            }
            lastResponse = anonymousResult.Response;
            lastError = anonymousResult.Error;
            if (route.KeyTiers.Count > 0)
            {
                _logger.Debug("anonymous request did not succeed; entering preferred key tiers",
                    ("component", "upstream"), ("event", "anonymous_fallback"), ("request_id", ids.Request),
                    ("attempts", attempts), ("key_tiers", string.Join(",", route.KeyTiers)));
            }
        }
        if (budget.Canceled)
        {
            // 匿名阶段已经耗尽请求预算。此时进入
            // 已鉴权档位只会对着已死的预算
            // 立刻发起尝试，冷却那些从未真正试过的密钥。
            if (lastResponse != null)
            {
                return new TieredResult(lastResponse, effectiveRoute, attempts, null);
            }
            lastError ??= new DeadlineExceededException("context deadline exceeded");
            return new TieredResult(null, effectiveRoute, attempts, lastError);
        }

        var keyTiers = new List<string>(route.KeyTiers);
        if (!route.Anonymous && keyTiers.Count == 0 && (route.Tier == Tier.Zen || route.Tier == Tier.Go))
        {
            keyTiers = [route.Tier];
        }
        foreach (var tier in keyTiers)
        {
            var keyRoute = new Route
            {
                ID = route.ID,
                Tier = tier,
                Protocol = route.ProtocolFor(tier),
                Protocols = route.Protocols,
                Anonymous = false,
                KeyTiers = route.KeyTiers,
            };
            effectiveRoute = keyRoute;
            var keyResult = await DoKeyUpstreamAsync(context, keyRoute, bodies, ids, attempts, budget);
            attempts += keyResult.Attempts;
            if (keyResult.Error == null && keyResult.Response != null && keyResult.Response.Status / 100 == 2)
            {
                if (lastResponse != null)
                {
                    await lastResponse.DrainAndCloseAsync();
                }
                return new TieredResult(keyResult.Response, keyRoute, attempts, null);
            }
            if (keyResult.Response != null)
            {
                if (lastResponse != null)
                {
                    await lastResponse.DrainAndCloseAsync();
                }
                lastResponse = keyResult.Response;
            }
            if (keyResult.Error != null)
            {
                lastError = keyResult.Error;
            }
        }
        if (lastResponse != null)
        {
            return new TieredResult(lastResponse, effectiveRoute, attempts, null);
        }
        lastError ??= new GatewayException("no usable upstream route");
        return new TieredResult(null, effectiveRoute, attempts, lastError);
    }

    /// <summary>
    /// 对每个当前可用的代理最多尝试一次。对每个代理，
    /// 优先使用旧版 /zen/v1 线路，失败再回退到较新的
    /// /inference 上游。任何失败（包括 HTTP 错误响应）
    /// 都会推进到下一个上游或代理。只有成功的响应才能
    /// 结束匿名阶段；代理游标耗尽时把控制权交回
    /// 优先的已鉴权档位。
    /// </summary>
    private async Task<TierResult> DoAnonymousUpstreamAsync(
        HttpContext context, Route route, Dictionary<string, byte[]> bodies, RequestIds ids,
        int attemptOffset, RequestBudget budget)
    {
        UpstreamResponse? lastResponse = null;
        Exception? lastError = null;
        var cursor = _anonymous.CursorFor(ids.Session);
        var limit = _anonymous.Nodes.Count;
        if (_anonymous.HasPool && _cfg.Retry.MaxAttempts > limit)
        {
            limit = _cfg.Retry.MaxAttempts;
        }
        var proxies = 0;
        var attempts = 0;
        if (!bodies.TryGetValue(Tier.Zen, out var rawBody) || rawBody.Length == 0)
        {
            return new TierResult(null, 0, new GatewayException("no prepared Zen request body"));
        }
        // 匿名免费档只服务带有必需工具定义的智能体形态流式请求
        // （其他请求会以 403 FreeTierError 拒绝）。按上游门槛
        // 归一化线格式请求体；当下游客户端
        // 要求普通 JSON 回复时，网关会把流折叠回去。
        // 密钥档位保留其原始请求体。
        var protocol = route.ProtocolFor(Tier.Zen);
        var upstreams = AnonymousUpstreams();
        var preparedBodies = new byte[upstreams.Count][];
        var toolAliases = new string[upstreams.Count];
        for (var index = 0; index < upstreams.Count; index++)
        {
            (preparedBodies[index], toolAliases[index]) =
                AnonymousBody.Prepare(rawBody, protocol, AnonymousBody.IsLegacyZenUpstream(upstreams[index]));
        }
        while (proxies < limit)
        {
            if (budget.Canceled)
            {
                // 请求级预算已经用尽。应停止扫描，
                // 而不是发起注定失败的尝试，再把它们
                // 记在未真正尝试过的代理的失败账上。
                lastError ??= new DeadlineExceededException("context deadline exceeded");
                break;
            }
            var node = cursor.Advance();
            if (node == null)
            {
                break;
            }
            proxies++;
            UpstreamResponse? nodeResponse = null;
            Exception? nodeError = null;
            for (var index = 0; index < upstreams.Count; index++)
            {
                var baseURL = upstreams[index];
                if (budget.Canceled)
                {
                    if (lastError == null && lastResponse == null)
                    {
                        lastError = new DeadlineExceededException("context deadline exceeded");
                    }
                    break;
                }
                attempts++;
                var meta = RequestMeta.FromContext(context);
                if (meta != null)
                {
                    meta.Attempts = attemptOffset + attempts;
                    meta.Tier = Tier.Zen;
                    meta.ToolAlias = toolAliases[index];
                }
                if (lastResponse != null)
                {
                    await lastResponse.DrainAndCloseAsync();
                    lastResponse = null;
                }
                HttpRequestMessage request;
                try
                {
                    request = UpstreamRequest.New(baseURL, protocol, preparedBodies[index], ids, AnonymousBody.ZenKey);
                }
                catch (Exception error) when (error is UriFormatException or InvalidOperationException)
                {
                    return new TierResult(null, attempts, error);
                }
                node.Proxy.ApplyTo(request);
                SetRequestCredential(context, Tier.Zen, "anonymous", "anonymous", true, node.Proxy);
                var started = DateTime.UtcNow;
                var (response, error2) = await InferenceAttempt.RunAsync(
                    node.Proxy, request, FirstEventTimeout, _attemptTimeout, budget);
                var duration = DateTime.UtcNow - started;
                if (budget.Canceled)
                {
                    // 父级预算在本次尝试执行期间过期。
                    // 它的结果不能说明这个代理的任何情况，因此不记录
                    // 任何内容并停止扫描。
                    lastResponse = response;
                    lastError = error2;
                    if (lastError == null && lastResponse == null)
                    {
                        lastError = new DeadlineExceededException("context deadline exceeded");
                    }
                    break;
                }
                nodeResponse = response;
                nodeError = error2;
                RecordUpstreamAttempt(context, route, ids, attemptOffset + attempts, "anonymous", "anonymous",
                    true, node.Proxy, response, error2, duration);
                if (error2 == null && response != null && response.Status / 100 == 2)
                {
                    await ObserveAnonymousResultAsync(context, node, response, null);
                    _logger.Debug("anonymous upstream accepted request",
                        ("component", "upstream"), ("event", "anonymous_attempt_succeeded"), ("request_id", ids.Request),
                        ("attempt", attempts), ("tier", Tier.Zen), ("upstream", Secrets.RedactUrl(baseURL)),
                        ("key_id", "anonymous"), ("channel", "anonymous"), ("anonymous", true),
                        ("proxy", Secrets.RedactUrl(node.Proxy.Name)), ("status", response.Status),
                        ("duration_ms", duration.Milliseconds));
                    return new TierResult(response, attempts, null);
                }
                lastResponse = response;
                lastError = ExceptionSignals.AsProxyUnavailable(node.Proxy, error2);
                if (error2 != null)
                {
                    _logger.Debug("anonymous transport attempt failed",
                        ("component", "upstream"), ("event", "anonymous_attempt_transport_failed"), ("request_id", ids.Request),
                        ("attempt", attempts), ("tier", Tier.Zen), ("upstream", Secrets.RedactUrl(baseURL)),
                        ("key_id", "anonymous"), ("channel", "anonymous"), ("anonymous", true),
                        ("proxy", Secrets.RedactUrl(node.Proxy.Name)), ("duration_ms", duration.Milliseconds),
                        ("error", error2));
                    if (ProxyHealth.IsProxyFailure(error2))
                    {
                        break;
                    }
                }
                else
                {
                    _logger.Debug("anonymous upstream returned an error response; trying the next upstream or proxy",
                        ("component", "upstream"), ("event", "anonymous_attempt_response_failed"), ("request_id", ids.Request),
                        ("attempt", attempts), ("tier", Tier.Zen), ("upstream", Secrets.RedactUrl(baseURL)),
                        ("key_id", "anonymous"), ("channel", "anonymous"), ("anonymous", true),
                        ("proxy", Secrets.RedactUrl(node.Proxy.Name)),
                        ("status", response?.Status ?? 0), ("duration_ms", duration.Milliseconds));
                }
                if (index + 1 < upstreams.Count && !node.Proxy.HealthyLoad())
                {
                    break;
                }
            }
            if (budget.Canceled)
            {
                break;
            }
            await ObserveAnonymousResultAsync(context, node, nodeResponse, nodeError);
        }
        if (lastResponse != null)
        {
            return new TierResult(lastResponse, attempts, null);
        }
        if (lastError == null)
        {
            lastError = _transports.HasHealthy()
                ? new GatewayException("no healthy anonymous proxies available")
                : new ProxyBudgetExhaustedException();
        }
        return new TierResult(null, attempts, lastError);
    }

    /// <summary>
    /// 无密钥免费档线路的有序基础 URL：先尝试旧版 /zen/v1
    /// 源，另一路配置的地址是 /inference 源，作为回退。
    /// </summary>
    private List<string> AnonymousUpstreams()
    {
        var legacy = Discovery.LegacyZenBase.TrimEnd('/');
        var primary = _cfg.Upstream.Anonymous.TrimEnd('/');
        if (primary.Length == 0 || string.Equals(legacy, primary, StringComparison.OrdinalIgnoreCase))
        {
            return [Discovery.LegacyZenBase];
        }
        return [Discovery.LegacyZenBase, _cfg.Upstream.Anonymous];
    }

    /// <summary>
    /// 在同一个档位内轮转密钥，直到请求预算、尝试预算
    /// 或一个不可重试的客户端错误结束循环。
    /// </summary>
    private async Task<TierResult> DoKeyUpstreamAsync(
        HttpContext context, Route route, Dictionary<string, byte[]> bodies, RequestIds ids,
        int attemptOffset, RequestBudget budget)
    {
        UpstreamResponse? lastResponse = null;
        Exception? lastError = null;
        var nodes = route.Tier == Tier.Go ? _goNodes : _zenNodes;
        var baseURL = route.Tier == Tier.Go ? _cfg.Upstream.Go : _cfg.Upstream.Zen;
        var cursor = nodes.CursorFor(ids.Session);
        if (nodes.Nodes.Count == 0)
        {
            return new TierResult(null, 0, new GatewayException($"no {route.Tier} nodes configured"));
        }
        var attempts = 0;
        if (!bodies.TryGetValue(route.Tier, out var body) || body.Length == 0)
        {
            return new TierResult(null, 0, new GatewayException($"no prepared {route.Tier} request body"));
        }
        var (shaped, alias, changed) = ShapeKeyBody(body, route, route.Tier);
        if (changed)
        {
            body = shaped;
        }
        var meta = RequestMeta.FromContext(context);
        if (meta != null)
        {
            meta.Shaped = changed;
            meta.ToolAlias = alias;
        }
        while (attempts < _cfg.Retry.MaxAttempts)
        {
            if (budget.Canceled)
            {
                // 与匿名循环相同的保护：耗尽的请求预算
                // 不应浪费在轮转从未真正尝试过的密钥上。
                lastError ??= new DeadlineExceededException("context deadline exceeded");
                break;
            }
            var node = cursor.Advance();
            if (node == null)
            {
                break;
            }
            attempts++;
            // 让请求级跟踪与即将发出的那次尝试保持同步。
            // 只保留脱敏后的密钥后缀。
            meta = RequestMeta.FromContext(context);
            if (meta != null)
            {
                meta.Attempts = attemptOffset + attempts;
                meta.Tier = route.Tier;
            }
            if (lastResponse != null)
            {
                await lastResponse.DrainAndCloseAsync();
                lastResponse = null;
            }
            HttpRequestMessage request;
            try
            {
                request = UpstreamRequest.New(baseURL, route.Protocol, body, ids, node.Key);
            }
            catch (Exception error) when (error is UriFormatException or InvalidOperationException)
            {
                return new TierResult(null, attempts, error);
            }
            var proxy = nodes.ProxyOf(node);
            if (proxy == null)
            {
                lastError = new GatewayException("upstream key has no proxy binding");
                break;
            }
            proxy.ApplyTo(request);
            var keyID = Secrets.KeyDisplayId(node.Key);
            SetRequestCredential(context, route.Tier, keyID, "key", false, proxy);
            var started = DateTime.UtcNow;
            var (response, error2) = await InferenceAttempt.RunAsync(
                proxy, request, FirstEventTimeout, _attemptTimeout, budget);
            var duration = DateTime.UtcNow - started;
            if (budget.Canceled)
            {
                // 本次尝试执行期间请求预算过期。
                // 被取消的预算不能说明密钥或代理的任何情况：没有
                // 这层保护，瞬时超时就会冷却一个健康的密钥，
                // 并把一个健康的代理从池中剔除。
                lastResponse = response;
                lastError = error2;
                if (lastError == null && lastResponse == null)
                {
                    lastError = new DeadlineExceededException("context deadline exceeded");
                }
                break;
            }
            await ObserveKeyResultAsync(context, nodes, node, proxy, response, error2);
            RecordUpstreamAttempt(context, route, ids, attemptOffset + attempts, keyID, "key", false,
                proxy, response, error2, duration);
            if (error2 == null && response != null && response.Status / 100 == 2)
            {
                _logger.Debug("upstream accepted request",
                    ("component", "upstream"), ("event", "attempt_succeeded"), ("request_id", ids.Request),
                    ("attempt", attempts), ("tier", route.Tier), ("key_id", keyID),
                    ("proxy", Secrets.RedactUrl(proxy.Name)), ("status", response.Status),
                    ("duration_ms", duration.Milliseconds));
                return new TierResult(response, attempts, null);
            }
            // 请求形态类错误是确定性的，必须离开本档位，
            // 而不是在无关系的密钥上轮转。外层路由仍可
            // 按优先顺序尝试下一个档位。鉴权、限流、
            // 服务端与传输失败在本档位内依旧可重试。
            if (IsNonRetryableClientResponse(response, error2))
            {
                _logger.Debug("upstream rejected a non-retryable request",
                    ("component", "upstream"), ("event", "attempt_rejected"), ("request_id", ids.Request),
                    ("attempt", attempts), ("tier", route.Tier), ("key_id", keyID),
                    ("status", response?.Status ?? 0), ("proxy", Secrets.RedactUrl(proxy.Name)),
                    ("duration_ms", duration.Milliseconds));
                return new TierResult(response, attempts, null);
            }
            lastResponse = response;
            lastError = ExceptionSignals.AsProxyUnavailable(proxy, error2);
            if (error2 != null)
            {
                _logger.Debug("upstream transport attempt failed",
                    ("component", "upstream"), ("event", "attempt_transport_failed"), ("request_id", ids.Request),
                    ("attempt", attempts), ("tier", route.Tier), ("key_id", keyID),
                    ("proxy", Secrets.RedactUrl(proxy.Name)), ("duration_ms", duration.Milliseconds),
                    ("error", error2));
            }
            else
            {
                _logger.Debug("upstream returned a retryable response",
                    ("component", "upstream"), ("event", "attempt_retryable_response"), ("request_id", ids.Request),
                    ("attempt", attempts), ("tier", route.Tier), ("key_id", keyID),
                    ("status", response?.Status ?? 0), ("proxy", Secrets.RedactUrl(proxy.Name)),
                    ("duration_ms", duration.Milliseconds));
            }
        }
        if (lastResponse != null)
        {
            return new TierResult(lastResponse, attempts, null);
        }
        return new TierResult(null, attempts, lastError);
    }

    /// <summary>
    /// 把一次密钥档位的尝试结果写入冷却、代理健康与绑定关系。
    /// </summary>
    private async Task ObserveKeyResultAsync(
        HttpContext context, NodePool nodes, UpstreamNode node, ProxyTransport? proxy,
        UpstreamResponse? response, Exception? error)
    {
        var status = UpstreamStatus(response);
        var proxyFailed = await SyncProxyResultAsync(context, proxy, status, error);
        if (error == null && status / 100 == 2 || IsNonRetryableClientResponse(response, error))
        {
            nodes.MarkSuccess(node);
            return;
        }
        if (!proxyFailed || nodes.ProxyOf(node) == proxy)
        {
            nodes.MarkFailure(node, error, response);
        }
    }

    private async Task ObserveAnonymousResultAsync(
        HttpContext context, AnonymousNode node, UpstreamResponse? response, Exception? error)
    {
        var status = UpstreamStatus(response);
        await SyncProxyResultAsync(context, node.Proxy, status, error);
        if (error == null && status / 100 == 2)
        {
            _anonymous.MarkSuccess(node);
        }
        else
        {
            _anonymous.MarkFailure(node, error, response);
        }
    }

    private static int UpstreamStatus(UpstreamResponse? response) => response?.Status ?? 0;

    /// <summary>记录当前尝试是由哪个凭据服务的。</summary>
    private void SetRequestCredential(
        HttpContext context, string tier, string keyID, string channel, bool anonymous, ProxyTransport? proxy)
    {
        var meta = RequestMeta.FromContext(context);
        if (meta == null)
        {
            return;
        }
        meta.Tier = tier;
        meta.KeyId = keyID;
        meta.Channel = channel;
        meta.Anonymous = anonymous;
        meta.Proxy = proxy == null ? "" : Secrets.RedactUrl(proxy.Name);
    }

    /// <summary>
    /// 把密钥档位下免费模型的线格式请求体归一化为智能体形态（流式 +
    /// 必需工具），与匿名线路一致。上游会在所有线路上以
    /// 403 FreeTierError 拒绝非智能体形态的免费档请求；
    /// 付费模型保留原始请求体。
    /// </summary>
    private (byte[] Body, string Alias, bool Changed) ShapeKeyBody(byte[] body, Route route, string tier)
    {
        if (!_catalog.IsFreeModel(route.ID))
        {
            return (body, "", false);
        }
        var baseURL = tier == Tier.Go ? _cfg.Upstream.Go : _cfg.Upstream.Zen;
        var (shaped, alias) = AnonymousBody.Prepare(body, route.ProtocolFor(tier), AnonymousBody.IsLegacyZenUpstream(baseURL));
        return (shaped, alias, !shaped.AsSpan().SequenceEqual(body));
    }

    private void RecordUpstreamAttempt(
        HttpContext context, Route route, RequestIds ids, int attempt, string keyID, string channel,
        bool anonymous, ProxyTransport? proxy, UpstreamResponse? response, Exception? error, TimeSpan duration)
    {
        var status = UpstreamStatus(response);
        var success = error == null && status >= 200 && status < 300;
        var outcome = "retryable_failure";
        if (success)
        {
            outcome = "success";
        }
        else if (error != null)
        {
            outcome = "transport_error";
        }
        else if (IsNonRetryableClientResponse(response, null))
        {
            outcome = "rejected";
        }
        var meta = RequestMeta.FromContext(context);
        if (meta != null)
        {
            meta.AttemptOutcome = outcome;
            meta.Protocol = route.Protocol;
            // 追加每次尝试的一段记录（密钥:状态:毫秒，公开
            // 线路做匿名化），使请求日志一眼能看出完整的尝试链。
            // 只保留脱敏后的密钥后缀。
            var leg = (anonymous ? "anon" : keyID) + ":";
            leg += error != null ? ExceptionSignals.ClassifyAttemptError(error) : status.ToString();
            leg += ":" + Math.Max((long)duration.TotalMilliseconds, 0) + "ms";
            meta.Legs.Add(leg);
        }
    }

    /// <summary>除鉴权、禁止访问与限流之外的 4xx：问题出在请求体。</summary>
    private static bool IsNonRetryableClientResponse(UpstreamResponse? response, Exception? error) =>
        error == null && response != null && response.Status >= 400 && response.Status < 500 &&
        response.Status != StatusCodes.Status401Unauthorized &&
        response.Status != StatusCodes.Status403Forbidden &&
        response.Status != StatusCodes.Status429TooManyRequests;

    /// <summary>供运维者调试的可选请求体审计。</summary>
    private void DumpOutboundBodies(
        Route route, Dictionary<string, byte[]> bodies, RequestIds ids, int attemptOffset)
    {
        if (!_cfg.Logging.DumpRequestBodies || bodies.Count == 0)
        {
            return;
        }
        var phase = attemptOffset > 0 ? "retry" : "initial";
        foreach (var name in bodies.Keys.OrderBy(tier => tier, StringComparer.Ordinal))
        {
            var body = bodies[name];
            if (body.Length == 0)
            {
                continue;
            }
            var truncated = body.Length > RequestBodyDumpLimit;
            var payload = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, RequestBodyDumpLimit));
            _logger.Debug("outbound upstream request body",
                ("component", "upstream"), ("event", "upstream_request_body"), ("request_id", ids.Request),
                ("model", route.ID), ("tier", name), ("protocol", route.ProtocolFor(name)), ("phase", phase),
                ("bytes", body.Length), ("sha256", Sha256Prefix(body)), ("truncated", truncated), ("body", payload));
        }
    }

    /// <summary>SHA-256 前 8 字节的十六进制，即 Go 的 hex.EncodeToString(sum[:8])。</summary>
    private static string Sha256Prefix(byte[] body)
    {
        var sum = SHA256.HashData(body);
        var text = new StringBuilder(16);
        for (var index = 0; index < 8; index++)
        {
            text.Append(sum[index].ToString("x2"));
        }
        return text.ToString();
    }

    /// <summary>
    /// 最多读取给定字节数，并报告读取
    /// 本身是否失败，对应 io.ReadAll(io.LimitReader(...))
    /// 把结果与错误成对返回的做法。
    /// </summary>
    private static async Task<(byte[] Body, bool Failed)> ReadCappedAsync(Stream stream, int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        var failed = false;
        try
        {
            long total = 0;
            while (total < limit)
            {
                var take = (int)Math.Min(chunk.Length, limit - total);
                var read = await stream.ReadAsync(chunk.AsMemory(0, take));
                if (read == 0)
                {
                    break;
                }
                buffer.Write(chunk, 0, read);
                total += read;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or HttpRequestException)
        {
            failed = true;
        }
        return (buffer.ToArray(), failed);
    }
}
