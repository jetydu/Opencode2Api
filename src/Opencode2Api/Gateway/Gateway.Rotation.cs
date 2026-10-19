using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.Config;
using Opencode2Api.Identity;
using Opencode2Api.JsonUtil;
using Opencode2Api.Models;
using Opencode2Api.Protocol;
using Opencode2Api.Rotation;
using Opencode2Api.Telemetry;

namespace Opencode2Api.Gateway;

/// <summary>
/// 模型分组轮转：粘性选择加上可回滚的流式路径
/// （internal/gateway/rotation.go）。分组请求最多尝试三个模型，
/// 每个模型最多三次尝试，并且只有所选模型确实
/// 产出输出之后才提交响应。
/// </summary>
internal sealed partial class ApiGateway
{
    private static readonly TimeSpan RotationDefaultPause = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AttemptPause = TimeSpan.FromSeconds(60);

    /// <summary>针对单个分组模型的一次轮转尝试的结果。</summary>
    private sealed class GroupAttempt
    {
        public Exception? Error { get; set; }
        public bool Quota { get; set; }
        public bool Committed { get; set; }
        public TimeSpan Pause { get; set; } = AttemptPause;
        public string Reason { get; set; } = "upstream_error";
    }

    /// <summary>
    /// 只识别显式配额标记：普通的提供方错误必须保留其预算。
    /// </summary>
    internal static bool QuotaError(int status, string body)
    {
        if (status == StatusCodes.Status429TooManyRequests)
        {
            return true;
        }
        var text = body.ToLowerInvariant();
        foreach (var marker in new[]
        {
            "insufficient_quota", "quota_exceeded", "quota exceeded", "quota exhausted",
            "rate_limit", "rate limit", "rate-limit", "usage_limit", "usage limit",
            "insufficient balance", "credit balance is too low", "额度不足", "配额已用尽",
        })
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 发布目录当前能在 Go 线路上路由哪些分组模型，
    /// 因此轮转绝不会选出一个会导致路由失败的模型。
    /// </summary>
    internal void SyncRotation()
    {
        var available = new Dictionary<string, bool>();
        // 分组轮转只能走 Go 线路。密钥池为空时直接发布「全部不可用」，
        // 不必逐个模型去碰 RouteForTier 的 no go key 异常 —— Go 版那里返回
        // error 被忽略，移植成异常后每次都会被调试器当成 first-chance 打断。
        if (_goNodes.Nodes.Count > 0)
        {
            foreach (var model in _catalog.List())
            {
                try
                {
                    if (_catalog.RouteForTier(model, Tier.Go, false, true).Protocol != Wire.SystemOne)
                    {
                        available[model] = true;
                    }
                }
                catch (CatalogException)
                {
                }
            }
        }
        Rotation.Sync(available);
    }

    private async Task HandleRotation(
        HttpContext context, string external, JsonObject payload, string id, string alias)
    {
        SyncRotation();
        using var budget = RequestBudget.Start(context.RequestAborted, TimeSpan.FromSeconds(_cfg.Retry.TimeoutSeconds));
        var ids = RequestIds.Derive(context, payload);
        var meta = RequestMeta.FromContext(context);
        if (meta != null)
        {
            meta.Request = ids.Request;
            meta.Session = ids.Session;
            meta.ModelAlias = alias;
        }
        var visited = new HashSet<string>();
        var ordinaryModels = 0;
        var total = 0;
        while (!budget.Canceled)
        {
            var selected = Rotation.Select(id, visited);
            if (selected == null)
            {
                break;
            }
            var token = selected.Value;
            visited.Add(token.Model);
            Route route;
            try
            {
                route = _catalog.RouteForTier(token.Model, Tier.Go, false, _goNodes.Nodes.Count > 0);
            }
            catch (CatalogException)
            {
                continue;
            }
            var counted = false;
            for (var n = 1; n <= 3 && !budget.Canceled; n++)
            {
                total++;
                meta = RequestMeta.FromContext(context);
                if (meta != null)
                {
                    meta.Model = token.Model;
                    meta.Tier = Tier.Go;
                    meta.Protocol = route.Protocol;
                    meta.Attempts = total;
                    meta.Usage = new Usage();
                    meta.UsageReported = false;
                }
                var result = await RotationAttempt(context, external, payload, alias, route, ids, total, budget);
                var outcome = result.Reason;
                if (result.Error == null)
                {
                    outcome = "success";
                }
                var canceled = context.RequestAborted.IsCancellationRequested ||
                    ExceptionSignals.Is<ClientWriteException>(result.Error);
                meta = RequestMeta.FromContext(context);
                if (canceled)
                {
                    outcome = "client_canceled";
                    if (meta != null)
                    {
                        meta.Outcome = outcome;
                    }
                }
                Rotation.Record(new Attempt
                {
                    Request = ids.Request,
                    Alias = alias,
                    Model = token.Model,
                    Number = total,
                    Outcome = outcome,
                });
                if (Rotation.TakeSnapshot().PersistenceError.Length > 0)
                {
                    _logger.Error("rotation state persistence failed",
                        ("component", "rotation"), ("event", "rotation_persistence_failed"));
                }
                _logger.Info("model rotation attempt",
                    ("component", "rotation"), ("event", "rotation_attempt"), ("request_id", ids.Request),
                    ("alias", alias), ("model", token.Model), ("attempt", total), ("reason", outcome));
                if (canceled)
                {
                    return;
                }
                if (result.Error == null)
                {
                    return;
                }
                if (result.Committed)
                {
                    if (meta != null)
                    {
                        meta.Outcome = "stream_error";
                    }
                    Rotation.Fail(token, result.Quota ? result.Pause : RotationDefaultPause, result.Reason);
                    return;
                }
                if (budget.Canceled)
                {
                    Rotation.Fail(token, RotationDefaultPause, "request_timeout");
                    break;
                }
                if (result.Quota)
                {
                    Rotation.Fail(token, result.Pause, result.Reason);
                    break;
                }
                if (!counted)
                {
                    ordinaryModels++;
                    counted = true;
                }
                if (n == 3)
                {
                    Rotation.Fail(token, RotationDefaultPause, result.Reason);
                }
            }
            if (ordinaryModels >= 3)
            {
                break;
            }
        }
        if (context.RequestAborted.IsCancellationRequested)
        {
            return;
        }
        var status = StatusCodes.Status503ServiceUnavailable;
        var message = "该分组暂无可用模型（已暂停、下架、额度不足或重试次数已用尽），请稍后重试";
        if (budget.Canceled)
        {
            status = StatusCodes.Status504GatewayTimeout;
            message = "分组请求整体超时";
        }
        await ProtocolError.Write(context, external, status, message, "rotation_unavailable", ids.Request);
    }

    /// <summary>
    /// 针对一个分组模型恰好执行一次 HTTP 调用。密钥选择可能在
    /// 各次尝试之间变化，但这里没有嵌套重试、没有过期推理重放，
    /// 也没有跨模型的密钥封禁：重试预算由轮转掌控。
    /// </summary>
    private async Task<GroupAttempt> RotationAttempt(
        HttpContext context, string external, JsonObject payload, string alias, Route route,
        RequestIds ids, int attempt, RequestBudget budget)
    {
        var result = new GroupAttempt();
        using var firstOutput = new CancellationTokenSource();
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, firstOutput.Token);
        // 对应 Go 的 time.AfterFunc(firstOutputSeconds, cancel)。Stop() 的返回值
        // 表示定时器是否仍在等待，据此区分
        // 迟到的首个 token 与健康的流。
        var pending = 1;
        using var timer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref pending, 0) == 1)
            {
                firstOutput.Cancel();
            }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(TimeSpan.FromSeconds(Rotation.TakeSnapshot().FirstOutputSeconds), Timeout.InfiniteTimeSpan);
        bool TimerStopped() => Interlocked.Exchange(ref pending, 0) == 1;

        var copy = CloneObject(payload);
        copy["model"] = route.ID;
        Dictionary<string, byte[]> bodies;
        try
        {
            bodies = PrepareRouteBodies(external, route, copy);
        }
        catch (Exception error) when (error is ProtocolException or CatalogException or ConfigException)
        {
            result.Error = error;
            result.Reason = "request_conversion_error";
            return result;
        }
        var body = bodies.TryGetValue(Tier.Go, out var prepared) ? prepared : [];
        var (shapedBody, toolAlias, shaped) = ShapeKeyBody(body, route, Tier.Go);
        body = shapedBody;
        var meta = RequestMeta.FromContext(context);
        if (meta != null)
        {
            meta.ToolAlias = toolAlias;
        }
        var cursor = _goNodes.CursorFor(ids.Session);
        UpstreamNode? node = null;
        for (var n = 0; n < attempt; n++)
        {
            node = cursor.Advance();
        }
        if (node == null)
        {
            result.Error = new GatewayException("no Go key");
            return result;
        }
        var proxy = _goNodes.ProxyOf(node);
        if (proxy == null)
        {
            result.Error = new GatewayException("no proxy");
            return result;
        }
        HttpRequestMessage request;
        try
        {
            request = UpstreamRequest.New(_cfg.Upstream.Go, route.Protocol, body, ids, node.Key);
        }
        catch (Exception error) when (error is UriFormatException or InvalidOperationException)
        {
            result.Error = error;
            return result;
        }
        // 复用的连接不得重放这个 POST：使用不跟随重定向的客户端
        // 等价于 Go 清空 GetBody 并固定 CheckRedirect。
        var keyID = Secrets.KeyDisplayId(node.Key);
        SetRequestCredential(context, Tier.Go, keyID, "key", false, proxy);
        var started = DateTime.UtcNow;
        UpstreamResponse? response = null;
        Exception? error2 = null;
        try
        {
            var message = await proxy.SendAsync(request, scope.Token, noRedirect: true);
            response = new UpstreamResponse
            {
                Message = message,
                Body = await message.Content.ReadAsStreamAsync(scope.Token),
                StreamToken = scope.Token,
            };
        }
        catch (Exception sendError)
        {
            error2 = sendError;
        }
        var duration = DateTime.UtcNow - started;
        RecordUpstreamAttempt(context, route, ids, attempt, keyID, "key", false,
            proxy, response, error2, duration);
        try
        {
            if (error2 != null)
            {
                result.Error = error2;
                result.Reason = scope.IsCancellationRequested ? "first_output_timeout" : "transport_error";
                return result;
            }
            var retryAfter = response!.Header("Retry-After");
            var pause = RetryAfter.Parse(retryAfter);
            if (pause > TimeSpan.Zero)
            {
                result.Pause = pause;
            }
            else if (retryAfter.Trim() == "0" || RetryAfter.IsHttpDate(retryAfter))
            {
                result.Pause = TimeSpan.Zero;
            }
            if (response.Status / 100 != 2)
            {
                var tail = await ReadCappedAsync(response.Body, 1 << 20);
                var text = Encoding.UTF8.GetString(tail.Body);
                result.Error = new GatewayException($"upstream HTTP {response.Status}");
                result.Quota = QuotaError(response.Status, text);
                result.Reason = result.Quota ? "quota_or_rate_limit" : $"http_{response.Status}";
                return result;
            }
            var stream = JsonValues.BoolAt(payload, "stream");
            async Task ReadyAsync()
            {
                if (!TimerStopped() || scope.IsCancellationRequested)
                {
                    throw new DeadlineExceededException("context deadline exceeded");
                }
                context.Response.Headers["X-Resolved-Model"] = route.ID;
                context.Response.Headers["x-request-id"] = ids.Request;
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers["X-Accel-Buffering"] = "no";
            }
            meta = RequestMeta.FromContext(context);
            if (stream)
            {
                if (meta != null)
                {
                    meta.Stream = true;
                }
                _monitor?.BeginStream();
                try
                {
                    var streamBody = RenameStreamReader.Wrap(response.Body, route.Protocol, LegacyReadTool, toolAlias);
                    // 只有客户端自身断开才允许中止
                    // 下游写入；首输出定时器中止的是上游读取。
                    var outcome = await RotationStream.ForwardAsync(streamBody, context.Response.Body,
                        route.Protocol, external, alias, ReadyAsync, budget.Client);
                    if (meta != null)
                    {
                        meta.Usage = outcome.Usage;
                        meta.UsageReported = outcome.Reported;
                    }
                    result.Error = outcome.Error;
                    result.Committed = outcome.Committed;
                    result.Reason = "stream_error";
                    var failure = ExceptionSignals.Walk(outcome.Error).OfType<StreamFailureException>().FirstOrDefault();
                    if (failure != null)
                    {
                        result.Quota = QuotaError(0, failure.Type + " " + failure.Detail);
                    }
                    if (result.Quota)
                    {
                        result.Reason = "quota_or_rate_limit";
                    }
                    else if (!result.Committed && scope.IsCancellationRequested)
                    {
                        result.Reason = "first_output_timeout";
                    }
                    return result;
                }
                finally
                {
                    _monitor?.EndStream();
                }
            }
            var (bodyBytes, readFailed) = await ReadCappedAsync(response.Body, (64 << 20) + 1);
            Exception? conversionError = readFailed
                ? new GatewayException("failed to read upstream response")
                : null;
            if (conversionError == null && bodyBytes.Length > ResponseBodyLimit)
            {
                conversionError = new GatewayException("response exceeds size limit");
            }
            if (conversionError != null)
            {
                result.Error = conversionError;
                result.Reason = "response_read_error";
                return result;
            }
            if (!TimerStopped() || scope.IsCancellationRequested)
            {
                result.Error = new DeadlineExceededException("context deadline exceeded");
                result.Reason = "first_output_timeout";
                return result;
            }
            var payload2 = bodyBytes;
            if (shaped)
            {
                try
                {
                    payload2 = await Collapse.CollapseAsync(new MemoryStream(payload2), route.Protocol, route.ID);
                }
                catch (Exception error)
                {
                    conversionError = error;
                }
            }
            if (conversionError == null)
            {
                payload2 = Response.RenameTool(payload2, route.Protocol, LegacyReadTool, toolAlias);
            }
            JsonObject? document = null;
            if (conversionError == null)
            {
                document = Jsonx.ParseObject(Encoding.UTF8.GetString(payload2), out conversionError);
            }
            if (conversionError == null && document == null)
            {
                conversionError = new GatewayException("empty upstream response");
            }
            if (conversionError == null && HasErrorEnvelope(document!))
            {
                result.Quota = QuotaError(0, Encoding.UTF8.GetString(payload2));
                conversionError = new GatewayException("upstream error envelope");
            }
            if (conversionError == null && !ValidDocumentSchema(document!, route.Protocol))
            {
                conversionError = new GatewayException("invalid response schema");
            }
            if (conversionError == null && external != route.Protocol)
            {
                try
                {
                    payload2 = Response.Convert(route.Protocol, external, payload2);
                }
                catch (Exception error)
                {
                    conversionError = error;
                }
            }
            if (conversionError != null)
            {
                result.Error = conversionError;
                result.Reason = result.Quota ? "quota_or_rate_limit" : "response_conversion_error";
                return result;
            }
            if (meta != null)
            {
                (meta.Usage, meta.UsageReported) = Response.ResponseUsage(external, payload2);
            }
            document = Jsonx.ParseObject(Encoding.UTF8.GetString(payload2), out Exception? reparseError);
            if (reparseError != null)
            {
                result.Error = reparseError;
                result.Reason = "response_conversion_error";
                return result;
            }
            if (document == null)
            {
                result.Error = new GatewayException("empty upstream response");
                result.Reason = "response_conversion_error";
                return result;
            }
            document["model"] = alias;
            var rendered = Encoding.UTF8.GetBytes(document.ToJsonString());
            context.Response.Headers["X-Resolved-Model"] = route.ID;
            context.Response.Headers["x-request-id"] = ids.Request;
            context.Response.ContentType = "application/json";
            try
            {
                await context.Response.Body.WriteAsync(rendered, budget.Client);
            }
            catch (Exception error)
            {
                result.Error = new ClientWriteException(error);
            }
            result.Committed = true;
            return result;
        }
        finally
        {
            if (response != null)
            {
                await response.DisposeAsync();
            }
        }
    }

    private static bool HasErrorEnvelope(JsonObject document) =>
        document.ContainsKey("error") && document["error"]?.GetValueKind() != JsonValueKind.Null;

    private static bool ValidDocumentSchema(JsonObject document, string protocol) => protocol switch
    {
        Wire.Chat => document["choices"] is JsonArray,
        Wire.Anthropic => document["content"] is JsonArray,
        Wire.Responses => document["output"] is JsonArray,
        _ => false,
    };

    /// <summary>
    /// 请求文档的浅拷贝：轮转循环按尝试改写 "model"，
    /// 不修改调用方的负载。
    /// </summary>
    private static JsonObject CloneObject(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var property in source)
        {
            copy[property.Key] = property.Value?.DeepClone();
        }
        return copy;
    }
}
