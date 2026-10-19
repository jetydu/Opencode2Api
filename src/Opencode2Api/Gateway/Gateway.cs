using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.Config;
using Opencode2Api.Http;
using Opencode2Api.Identity;
using Opencode2Api.JsonUtil;
using Opencode2Api.Models;
using Opencode2Api.Protocol;
using Opencode2Api.Rotation;
using Opencode2Api.Telemetry;

namespace Opencode2Api.Gateway;

/// <summary>
/// 把推理请求路由到受管理的上游节点池
/// （internal/gateway/gateway.go）。类名避开了 Go 类型
/// 与其自身包同名的命名空间。
/// </summary>
internal sealed partial class ApiGateway
{
    /// <summary>maxRequestBody：超过上限的负载在解析前即被拒绝。</summary>
    public const int MaxRequestBodyBytes = 32 << 20;

    /// <summary>responseBodyLimit 限定非流式响应最多缓冲多少内容。</summary>
    private const int ResponseBodyLimit = 64 << 20;

    /// <summary>
    /// 等价于 nginx 的 499：客户端在网关应答前已经离开，因此该失败
    /// 既不属于上游也不属于我们自己。
    /// </summary>
    private const int StatusClientClosedRequest = 499;

    /// <summary>
    /// 当所有尝试都失败在代理线路本身时返回，提示运维者
    /// 去检查自己的代理而不是上游。
    /// </summary>
    private const string ProxyUnavailableMessage = "all proxies are unavailable; the upstream could not be reached";

    /// <summary>旧版匿名线路坚持要求的唯一工具名。</summary>
    private const string LegacyReadTool = "read";

    private readonly Config.Config _cfg;
    private readonly StructuredLogger _logger;
    private readonly Monitor _monitor;
    private readonly TransportPool _transports;
    private readonly NodePool _zenNodes;
    private readonly NodePool _goNodes;
    private readonly AnonymousPool _anonymous;
    private readonly Catalog _catalog;
    private readonly TimeSpan _attemptTimeout;
    private readonly SecretRedactor _redactor;

    /// <summary>由运行时管理器替换，共享状态由其持有。</summary>
    internal RotationManager Rotation { get; set; }

    internal AvailabilityStore? Availability { get; set; }

    private ApiGateway(
        Config.Config cfg,
        StructuredLogger logger,
        Monitor monitor,
        TransportPool transports,
        NodePool zenNodes,
        NodePool goNodes,
        AnonymousPool anonymous,
        Catalog catalog,
        RotationManager rotation,
        TimeSpan attemptTimeout)
    {
        _cfg = cfg;
        _logger = logger;
        _monitor = monitor;
        _transports = transports;
        _zenNodes = zenNodes;
        _goNodes = goNodes;
        _anonymous = anonymous;
        _catalog = catalog;
        Rotation = rotation;
        _attemptTimeout = attemptTimeout;
        _redactor = new SecretRedactor();
        _redactor.Replace(cfg);
    }

    /// <summary>first_event_timeout_seconds：流被接受后允许静默多久。</summary>
    private TimeSpan FirstEventTimeout => TimeSpan.FromSeconds(_cfg.Performance.FirstEventTimeoutSeconds);

    // 运行时管理器为定价路由与资源快照需要的
    // 只读视图。Go 直接访问同样的字段。
    internal TransportPool Transports => _transports;
    internal Catalog Catalog => _catalog;
    internal NodePool ZenNodes => _zenNodes;
    internal NodePool GoNodes => _goNodes;
    internal AnonymousPool AnonymousNodes => _anonymous;

    /// <summary>
    /// attemptDeadline 约束整次上游尝试（拨号 + 写入 + 响应头 +
    /// 首个事件），而不仅是等待响应头。仅在设置了
    /// attempt_timeout_seconds 时才启用：否则请求级预算已经
    /// 约束了它本会覆盖的那一次尝试。
    /// </summary>
    private static TimeSpan AttemptDeadline(Config.Config cfg) => cfg.Performance.AttemptTimeoutSeconds <= 0
        ? TimeSpan.Zero
        : cfg.Performance.AttemptTimeout(TimeSpan.FromSeconds(cfg.Retry.TimeoutSeconds));

    /// <summary>对应 Go 的 New(cfg, logger, monitor)。</summary>
    public static ApiGateway Create(Config.Config cfg, StructuredLogger logger, Monitor monitor)
    {
        cfg.Rotation.Defaults();
        var rotation = new RotationManager("", cfg.Rotation);
        var transports = TransportPool.Create(
            cfg.RuntimeProxies(), cfg.Performance,
            cfg.Performance.AttemptTimeout(TimeSpan.FromSeconds(cfg.Retry.TimeoutSeconds)));
        var cooldown = TimeSpan.FromSeconds(cfg.Performance.FailureCooldownSeconds);
        var zenNodes = WrapPool("zen node pool", () => NodePool.Create(cfg.ZenKeys, transports, cooldown));
        var goNodes = WrapPool("go node pool", () => NodePool.Create(cfg.GoKeys, transports, cooldown));
        var catalog = new Catalog(cfg.Prefer, cfg.Models.Protocols);
        catalog.SetRefreshInterval(TimeSpan.FromSeconds(cfg.Models.RefreshSeconds));
        catalog.SetFreeOnly(cfg.Models.FreeOnly);
        return new ApiGateway(cfg, logger, monitor, transports, zenNodes, goNodes,
            AnonymousPool.Create(cfg.Anonymous, transports, cooldown), catalog, rotation, AttemptDeadline(cfg));
    }

    private static NodePool WrapPool(string context, Func<NodePool> build)
    {
        try
        {
            return build();
        }
        catch (Exception error) when (error is GatewayException or ConfigException)
        {
            throw new GatewayException($"{context}: {error.Message}");
        }
    }

    /// <summary>用本地 API Key 校验包裹处理器（Go: authenticate）。</summary>
    public Func<HttpContext, Task> Authenticate(Func<HttpContext, Task> next)
    {
        return async context =>
        {
            var candidates = new List<string> { context.Request.Headers["x-api-key"].ToString().Trim() };
            var authorization = context.Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(authorization[7..].Trim());
            }
            string? matched = null;
            foreach (var key in _cfg.ServerKeys)
            {
                if (!_cfg.ServerKeyEnabled(key))
                {
                    continue;
                }
                foreach (var candidate in candidates)
                {
                    if (candidate.Length == key.Length &&
                        CryptographicOperations.FixedTimeEquals(
                            System.Text.Encoding.UTF8.GetBytes(candidate), System.Text.Encoding.UTF8.GetBytes(key)))
                    {
                        matched = key;
                    }
                }
            }
            if (matched == null)
            {
                var protocol = (context.Request.Path.Value ?? "").StartsWith("/v1/messages", StringComparison.Ordinal)
                    ? Wire.Anthropic
                    : Wire.Chat;
                await ProtocolError.Write(context, protocol, StatusCodes.Status401Unauthorized,
                    "invalid local API key", "authentication_error", "");
                return;
            }
            var meta = RequestMeta.FromContext(context);
            if (meta != null)
            {
                meta.ServerKeyId = Secrets.Fingerprint(matched);
            }
            await next(context);
        };
    }

    /// <summary>POST /v1/chat/completions、/v1/responses 与 /v1/messages。</summary>
    public async Task HandleInference(HttpContext context, string external)
    {
        var body = await ReadBoundedBodyAsync(context, MaxRequestBodyBytes);
        if (body == null)
        {
            await ProtocolError.Write(context, external, StatusCodes.Status400BadRequest,
                "request body is too large or unreadable", "invalid_request_error", "");
            return;
        }
        var payload = Jsonx.TryParseObject(body);
        if (payload == null)
        {
            await ProtocolError.Write(context, external, StatusCodes.Status400BadRequest,
                "request body must be a JSON object", "invalid_request_error", "");
            return;
        }
        var model = JsonValues.StringAt(payload, "model");
        var meta = RequestMeta.FromContext(context);
        if (meta != null)
        {
            meta.Model = model;
        }
        if (model.Length == 0)
        {
            await ProtocolError.Write(context, external, StatusCodes.Status400BadRequest,
                "model is required", "invalid_request_error", "model");
            return;
        }
        var group = Rotation.GroupId(model);
        if (group.Length != 0)
        {
            await HandleRotation(context, external, payload, group, model);
            return;
        }
        if (!_catalog.Supported(model))
        {
            await ProtocolError.Write(context, external, StatusCodes.Status400BadRequest,
                "the model uses an upstream protocol that opencode2api does not expose", "invalid_request_error", "model");
            return;
        }
        Route route;
        try
        {
            route = _catalog.Route(model, _cfg.ZenKeys.Count > 0, _cfg.GoKeys.Count > 0, _cfg.Anonymous);
        }
        catch (Exception error) when (error is CatalogException or ConfigException)
        {
            await ProtocolError.Write(context, external, StatusCodes.Status400BadRequest,
                error.Message, "invalid_request_error", "model");
            return;
        }
        if (meta != null)
        {
            meta.Tier = route.Tier;
            meta.Protocol = route.Protocol;
        }
        // System One 模型没有消息形态的等价协议，因此在消息端点提交的决策
        // 负载会原样中继，而不是做转换。这让只能寻址
        // /v1/chat/completions 或 /v1/responses 的客户端
        // 依然可以访问该模型。
        if (route.Protocol == Wire.SystemOne)
        {
            var questions = JsonValues.MapAt(payload, "questions");
            if (questions == null || questions.Count == 0 || payload["state"] == null)
            {
                await ProtocolError.Write(context, external, StatusCodes.Status400BadRequest,
                    "this model uses System One decisions, not chat; send state and typed questions to /v1/systemone",
                    "invalid_request_error", "model");
                return;
            }
            await ForwardSystemOne(context, body, payload, model, route);
            return;
        }
        Dictionary<string, byte[]> bodies;
        try
        {
            bodies = PrepareRouteBodies(external, route, payload);
        }
        catch (Exception error) when (error is ProtocolException or CatalogException or ConfigException)
        {
            await ProtocolError.Write(context, external, StatusCodes.Status400BadRequest,
                error.Message, "invalid_request_error", "");
            return;
        }
        var ids = RequestIds.Derive(context, payload);
        if (meta != null)
        {
            meta.Request = ids.Request;
            meta.Session = ids.Session;
        }
        var stream = JsonValues.BoolAt(payload, "stream");
        using var budget = RequestBudget.Start(context.RequestAborted, TimeSpan.FromSeconds(_cfg.Retry.TimeoutSeconds));
        var result = await DoUpstreamAsync(context, route, bodies, ids, budget);
        if (result.Error != null || result.Response == null)
        {
            await WriteUpstreamFailureAsync(context, external, ids, route, result.Error, budget);
            return;
        }
        var response = result.Response;
        try
        {
            if (meta != null)
            {
                meta.Tier = result.Route.Tier;
                meta.Protocol = result.Route.Protocol;
            }
            context.Response.Headers["x-request-id"] = ids.Request;
            if (response.Status / 100 != 2)
            {
                await CopyErrorResponse(context, external, response, ids.Request);
                return;
            }
            var toolAlias = meta?.ToolAlias ?? "";
            if (stream)
            {
                if (meta != null)
                {
                    meta.Stream = true;
                }
                _monitor?.BeginStream();
                try
                {
                    context.Response.ContentType = "text/event-stream; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-cache";
                    context.Response.Headers["X-Accel-Buffering"] = "no";
                    context.Response.StatusCode = response.Status;
                    var streamBody = RenameStreamReader.Wrap(response.Body, result.Route.Protocol, LegacyReadTool, toolAlias);
                    StreamOutcome outcome;
                    if (external == result.Route.Protocol)
                    {
                        outcome = await StreamBridge.ForwardAsync(
                            streamBody, context.Response.Body, result.Route.Protocol, model, budget.Client);
                    }
                    else
                    {
                        outcome = await StreamBridge.TranscodeAsync(
                            streamBody, context.Response.Body, result.Route.Protocol, external, model, budget.Client);
                    }
                    if (meta != null)
                    {
                        meta.Usage = outcome.Usage;
                        meta.UsageReported = outcome.Reported;
                        if (outcome.Error != null)
                        {
                            meta.Outcome = "stream_error";
                            if (StreamBridge.ClientCanceled(budget.Client, outcome.Error))
                            {
                                meta.Outcome = "client_canceled";
                            }
                        }
                    }
                    if (outcome.Error != null && outcome.Error is not OperationCanceledException)
                    {
                        _logger.Warn("downstream stream ended with an error",
                            ("component", "stream"), ("event", "stream_failed"), ("request_id", ids.Request),
                            ("model", model), ("tier", result.Route.Tier), ("error", outcome.Error));
                    }
                }
                finally
                {
                    _monitor?.EndStream();
                }
                return;
            }
            var responseBody = await ReadAtMostAsync(response.Body, ResponseBodyLimit);
            if (responseBody == null)
            {
                await ProtocolError.Write(context, external, StatusCodes.Status502BadGateway,
                    "failed to read upstream response", "upstream_error", ids.Request);
                return;
            }
            if (result.Route.Anonymous || (meta?.Shaped ?? false))
            {
                // 密钥档位的塑形请求此前像匿名线路一样被强制转为流式；
                // 这里同样做折叠。匿名线路始终以流式对外服务，
                // 因此事件会折叠回这个非流式客户端所请求的
                // 单个文档。
                try
                {
                    responseBody = await Collapse.CollapseAsync(new MemoryStream(responseBody), result.Route.Protocol, model);
                }
                catch (Exception error)
                {
                    _logger.Warn("anonymous stream collapse failed",
                        ("component", "conversion"), ("event", "anonymous_collapse_failed"), ("request_id", ids.Request),
                        ("model", model), ("source_protocol", result.Route.Protocol), ("error", error));
                    await ProtocolError.Write(context, external, StatusCodes.Status502BadGateway,
                        "unsupported upstream response", "upstream_error", ids.Request);
                    return;
                }
            }
            responseBody = Response.RenameTool(responseBody, result.Route.Protocol, LegacyReadTool, toolAlias);
            var (usage, reported) = Response.ResponseUsage(result.Route.Protocol, responseBody);
            if (meta != null)
            {
                meta.Usage = usage;
                meta.UsageReported = reported;
            }
            if (external != result.Route.Protocol)
            {
                try
                {
                    responseBody = Response.Convert(result.Route.Protocol, external, responseBody);
                }
                catch (Exception error)
                {
                    _logger.Warn("response protocol conversion failed",
                        ("component", "conversion"), ("event", "response_conversion_failed"), ("request_id", ids.Request),
                        ("model", model), ("source_protocol", result.Route.Protocol),
                        ("target_protocol", external), ("error", error));
                    await ProtocolError.Write(context, external, StatusCodes.Status502BadGateway,
                        "unsupported upstream response", "upstream_error", ids.Request);
                    return;
                }
            }
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = response.Status;
            await context.Response.Body.WriteAsync(responseBody, budget.Client);
        }
        finally
        {
            await response.DisposeAsync();
        }
    }

    /// <summary>
    /// POST /v1/systemone。只有上游协议为 System One 的模型能在此
    /// 服务；chat 或 responses 模型应走各自的端点，
    /// 在那里桥接才能转换它。
    /// </summary>
    public async Task HandleSystemOne(HttpContext context)
    {
        var body = await ReadBoundedBodyAsync(context, MaxRequestBodyBytes);
        if (body == null)
        {
            await ProtocolError.Write(context, Wire.SystemOne, StatusCodes.Status400BadRequest,
                "request body is too large or unreadable", "invalid_request_error", "");
            return;
        }
        var payload = Jsonx.TryParseObject(body);
        if (payload == null)
        {
            await ProtocolError.Write(context, Wire.SystemOne, StatusCodes.Status400BadRequest,
                "request body must be a JSON object", "invalid_request_error", "");
            return;
        }
        var model = JsonValues.StringAt(payload, "model");
        var meta = RequestMeta.FromContext(context);
        if (meta != null)
        {
            meta.Model = model;
        }
        if (model.Length == 0)
        {
            await ProtocolError.Write(context, Wire.SystemOne, StatusCodes.Status400BadRequest,
                "model is required", "invalid_request_error", "model");
            return;
        }
        if (!_catalog.Supported(model))
        {
            await ProtocolError.Write(context, Wire.SystemOne, StatusCodes.Status400BadRequest,
                "the model uses an upstream protocol that opencode2api does not expose", "invalid_request_error", "model");
            return;
        }
        Route route;
        try
        {
            route = _catalog.Route(model, _cfg.ZenKeys.Count > 0, _cfg.GoKeys.Count > 0, _cfg.Anonymous);
        }
        catch (Exception error) when (error is CatalogException or ConfigException)
        {
            await ProtocolError.Write(context, Wire.SystemOne, StatusCodes.Status400BadRequest,
                error.Message, "invalid_request_error", "model");
            return;
        }
        if (route.Protocol != Wire.SystemOne)
        {
            await ProtocolError.Write(context, Wire.SystemOne, StatusCodes.Status400BadRequest,
                "the model does not use the systemone protocol", "invalid_request_error", "model");
            return;
        }
        await ForwardSystemOne(context, body, payload, model, route);
    }

    /// <summary>
    /// 把 System One 决策负载原样中继到上游 systemone 端点，并原样返回
    /// 带类型的应答文档。该负载把自由形态状态与带类型的问题配对，
    /// 没有任何消息形态的上游协议能接受它，
    /// 因此从不翻译。
    /// </summary>
    private async Task ForwardSystemOne(
        HttpContext context, byte[] body, JsonObject payload, string model, Route route)
    {
        var meta = RequestMeta.FromContext(context);
        if (meta != null)
        {
            meta.Model = model;
            meta.Tier = route.Tier;
            meta.Protocol = route.Protocol;
        }
        // 一份原样字节体适用于所有档位：决策负载没有按档位
        // 区分的编码，因此不尝试协议转换。
        var bodies = new Dictionary<string, byte[]> { [route.Tier] = body };
        foreach (var tier in route.KeyTiers)
        {
            bodies[tier] = body;
        }
        var ids = RequestIds.Derive(context, payload);
        if (meta != null)
        {
            meta.Request = ids.Request;
            meta.Session = ids.Session;
        }
        using var budget = RequestBudget.Start(context.RequestAborted, TimeSpan.FromSeconds(_cfg.Retry.TimeoutSeconds));
        var result = await DoUpstreamAsync(context, route, bodies, ids, budget);
        if (result.Error != null || result.Response == null)
        {
            await WriteUpstreamFailureAsync(context, Wire.SystemOne, ids, route, result.Error, budget);
            return;
        }
        var response = result.Response;
        try
        {
            if (meta != null)
            {
                meta.Tier = result.Route.Tier;
                meta.Protocol = result.Route.Protocol;
            }
            context.Response.Headers["x-request-id"] = ids.Request;
            if (response.Status / 100 != 2)
            {
                await CopyErrorResponse(context, Wire.SystemOne, response, ids.Request);
                return;
            }
            var contentType = response.Header("Content-Type");
            if (contentType.StartsWith("text/event-stream", StringComparison.Ordinal))
            {
                if (meta != null)
                {
                    meta.Stream = true;
                }
                context.Response.ContentType = contentType;
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.StatusCode = response.Status;
                await CopyStreamAsync(response.Body, context.Response.Body, budget.Client);
                return;
            }
            var responseBody = await ReadAtMostAsync(response.Body, ResponseBodyLimit);
            if (responseBody == null)
            {
                await ProtocolError.Write(context, Wire.SystemOne, StatusCodes.Status502BadGateway,
                    "failed to read upstream response", "upstream_error", ids.Request);
                return;
            }
            var (usage, reported) = Response.ResponseUsage(result.Route.Protocol, responseBody);
            if (meta != null)
            {
                meta.Usage = usage;
                meta.UsageReported = reported;
            }
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = response.Status;
            await context.Response.Body.WriteAsync(responseBody, budget.Client);
        }
        finally
        {
            await response.DisposeAsync();
        }
    }

    /// <summary>
    /// 为路由可能回落到的每个档位预先编码客户端请求体。线格式要求更严格的
    /// 回落档位会被跳过，而不是在首选上游
    /// 尚未尝试之前就拒绝整个请求。
    /// </summary>
    private Dictionary<string, byte[]> PrepareRouteBodies(string from, Route route, JsonObject input)
    {
        var tiers = new List<string>();
        var seen = new HashSet<string>();
        void AddTier(string tier)
        {
            if ((tier != Tier.Zen && tier != Tier.Go) || !seen.Add(tier))
            {
                return;
            }
            tiers.Add(tier);
        }
        AddTier(route.Tier);
        foreach (var tier in route.KeyTiers)
        {
            AddTier(tier);
        }
        if (tiers.Count == 0)
        {
            throw new GatewayException("no usable upstream tier");
        }
        var bodies = new Dictionary<string, byte[]>();
        foreach (var tier in tiers)
        {
            var protocol = route.ProtocolFor(tier);
            var baseURL = tier == Tier.Go ? _cfg.Upstream.Go : _cfg.Upstream.Zen;
            JsonObject upstreamPayload;
            try
            {
                upstreamPayload = RequestBridge.Prepare(from, protocol, input, baseURL);
            }
            catch (Exception error) when (error is ProtocolException or ConfigException)
            {
                if (tier != route.Tier)
                {
                    _logger.Debug("skipping fallback tier with incompatible request shape",
                        ("component", "conversion"), ("event", "fallback_tier_skipped"),
                        ("model", JsonValues.StringAt(input, "model")), ("tier", tier),
                        ("protocol", protocol), ("error", error));
                    continue;
                }
                throw new ProtocolException($"prepare {tier} upstream request: {error.Message}");
            }
            var effort = ResolvedEffort(JsonValues.StringAt(upstreamPayload, "model"), tier, protocol);
            if (effort.Length != 0)
            {
                RequestBridge.ForcedEffort(protocol, upstreamPayload, effort);
            }
            try
            {
                bodies[tier] = Response.ToUtf8(upstreamPayload);
            }
            catch (Exception error) when (error is JsonException or NotSupportedException)
            {
                throw new ProtocolException("request contains unsupported JSON values");
            }
        }
        return bodies;
    }

    /// <summary>
    /// 报告上游阶段失败的真实原因，而不是一律归咎于上游：
    /// 客户端断开是 499，代理线路失效是
    /// 503，预算耗尽是 504。
    /// </summary>
    private async Task WriteUpstreamFailureAsync(
        HttpContext context, string protocol, RequestIds ids, Route route, Exception? error, RequestBudget budget)
    {
        var meta = RequestMeta.FromContext(context);
        var finalTier = route.Tier;
        if (meta != null && meta.Tier.Length != 0)
        {
            finalTier = meta.Tier;
        }
        var failure = error ?? new GatewayException("no usable upstream route");
        if (!ExceptionSignals.Is<DeadlineExceededException>(failure) &&
            StreamBridge.ClientCanceled(budget.Client, failure))
        {
            _logger.Debug("client canceled before upstream response",
                ("component", "upstream"), ("event", "request_canceled"), ("request_id", ids.Request),
                ("tier", finalTier), ("key_id", meta?.KeyId ?? ""), ("channel", meta?.Channel ?? ""),
                ("anonymous", meta?.Anonymous ?? false));
            if (meta != null)
            {
                meta.Outcome = "client_canceled";
            }
            await ProtocolError.Write(context, protocol, StatusClientClosedRequest,
                "client closed request", "client_canceled", ids.Request);
            return;
        }
        _logger.Warn("all upstream attempts failed",
            ("component", "upstream"), ("event", "request_failed"), ("request_id", ids.Request),
            ("tier", finalTier), ("key_id", meta?.KeyId ?? ""), ("channel", meta?.Channel ?? ""),
            ("anonymous", meta?.Anonymous ?? false), ("error", failure));
        if (ExceptionSignals.Is<ProxyUnavailableSentinel>(failure))
        {
            await ProtocolError.Write(context, protocol, StatusCodes.Status503ServiceUnavailable,
                ProxyUnavailableMessage, "proxy_error", ids.Request);
            return;
        }
        if (ExceptionSignals.Is<DeadlineExceededException>(failure))
        {
            await ProtocolError.Write(context, protocol, StatusCodes.Status504GatewayTimeout,
                "upstream request timed out", "upstream_timeout", ids.Request);
            return;
        }
        await ProtocolError.Write(context, protocol, StatusCodes.Status502BadGateway,
            "all upstream attempts failed", "upstream_error", ids.Request);
    }

    /// <summary>把上游错误体中继为协议形态的错误。</summary>
    private static async Task CopyErrorResponse(
        HttpContext context, string protocol, UpstreamResponse response, string requestId)
    {
        var body = await ReadAtMostAsync(response.Body, 4 << 20) ?? [];
        var retryAfter = response.Header("Retry-After");
        if (retryAfter.Length != 0)
        {
            context.Response.Headers.RetryAfter = retryAfter;
        }
        var message = ReasonPhrase(response.Status);
        var payload = Jsonx.TryParseObject(body);
        if (payload != null)
        {
            message = JsonValues.FirstString(
                JsonValues.StringAt(payload, "error", "message"),
                JsonValues.StringAt(payload, "message"),
                message);
        }
        await ProtocolError.Write(context, protocol, response.Status, message, "upstream_error", requestId);
    }

    /// <summary>网关实际会转发的状态码对应的 http.StatusText。</summary>
    private static string ReasonPhrase(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        402 => "Payment Required",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        406 => "Not Acceptable",
        408 => "Request Timeout",
        409 => "Conflict",
        410 => "Gone",
        413 => "Request Entity Too Large",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "unknown status code",
    };

    /// <summary>
    /// 读取请求体但拒绝超过上限的内容，与 http.MaxBytesReader
    /// 的做法一致（null 表示过大或不可读）。
    /// </summary>
    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpContext context, int limit)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted);
                if (read == 0)
                {
                    break;
                }
                if (buffer.Length + read > limit)
                {
                    return null;
                }
                buffer.Write(chunk, 0, read);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// 最多读取给定字节数；触达上限不算错误，
    /// 对应 io.ReadAll(io.LimitReader(...))。null 表示读取本身失败。
    /// </summary>
    private static async Task<byte[]?> ReadAtMostAsync(Stream stream, int limit)
    {
        var (body, failed) = await ReadCappedAsync(stream, limit);
        return failed ? null : body;
    }

    /// <summary>
    /// 用于 SSE 中继的 io.Copy，每个数据块都立即 flush，使客户端
    /// 能持续看到事件按时到达。
    /// </summary>
    private static async Task CopyStreamAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var chunk = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    return;
                }
                await output.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
        }
    }
}
