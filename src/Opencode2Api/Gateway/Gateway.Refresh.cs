using System.Text.Json;
using Opencode2Api.Config;
using Opencode2Api.Models;
using Opencode2Api.Telemetry;

namespace Opencode2Api.Gateway;

/// <summary>
/// 代理健康检查与模型目录的后台任务（internal/gateway/refresh.go）。
/// </summary>
internal sealed partial class ApiGateway
{
    private const string ProxyHealthCheckURL = "https://cloudflare.com/cdn-cgi/trace";
    private static readonly TimeSpan ProxyHealthCheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ProxyHealthCheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CatalogFetchTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 根据真实流量更新代理健康状态。只有超时与连接被拒
    /// 才会把代理标记为不可用；任何 HTTP 响应（包括 4xx/5xx）
    /// 都证明该线路可达，并会恢复此前被瞬时故障
    /// 剔除的代理，因此被限流的出口绝不会
    /// 被归咎于代理线路本身。
    /// </summary>
    private async Task<bool> SyncProxyResultAsync(
        HttpContext? context, ProxyTransport? proxy, int status, Exception? error)
    {
        _ = context;
        if (proxy == null || proxy.Pool)
        {
            return false;
        }
        if (ProxyHealth.IsProxyFailure(error))
        {
            RebindFailedProxy(proxy);
            VerifyProxyAfterError(proxy, status);
            return true;
        }
        if (status >= 200 && status < 400)
        {
            var wasHealthy = proxy.HealthySwap(true);
            if (!wasHealthy)
            {
                RestoreProxy(proxy);
            }
            return false;
        }
        if (error == null && status >= 400 && status < 600)
        {
            // 能从上游收到 HTTP 状态码就证明代理线路
            // 本身可达；应恢复早前瞬时超时留下的
            // 不健康标记，而不是继续剔除该线路。
            var wasHealthy = proxy.HealthySwap(true);
            if (!wasHealthy)
            {
                RestoreProxy(proxy);
            }
            return false;
        }
        if (error != null)
        {
            VerifyProxyAfterError(proxy, status);
        }
        return false;
    }

    /// <summary>
    /// 重新检查刚刚失败的代理，且不让客户端请求自身的
    /// 取消信号破坏这次验证（Go: context.WithoutCancel）。
    /// </summary>
    private void VerifyProxyAfterError(ProxyTransport proxy, int status)
    {
        if (!proxy.ClaimChecking())
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _transports.CheckClaimedProxyAsync(
                    proxy, ProxyHealthCheckURL, ProxyHealthCheckTimeout, CancellationToken.None);
                ApplyProxyHealthResult(result, "upstream HTTP response", status);
            }
            catch (Exception error)
            {
                _logger.Debug("proxy verification failed",
                    ("component", "proxy"), ("event", "health_check_error"),
                    ("proxy", Secrets.RedactUrl(proxy.Name)), ("error", error));
            }
        });
    }

    private (int ZenMoved, int GoMoved) RebindFailedProxy(ProxyTransport? proxy)
    {
        if (proxy == null)
        {
            return (0, 0);
        }
        var wasHealthy = proxy.HealthySwap(false);
        return RebindUnavailableProxy(proxy, wasHealthy);
    }

    private (int ZenMoved, int GoMoved) RebindUnavailableProxy(ProxyTransport proxy, bool wasHealthy)
    {
        var zenMoved = _zenNodes.RebindProxy(proxy.Index);
        var goMoved = _goNodes.RebindProxy(proxy.Index);
        if (wasHealthy || zenMoved + goMoved > 0)
        {
            _logger.Warn("proxy became unavailable",
                ("component", "proxy"), ("event", "proxy_unavailable"), ("proxy", Secrets.RedactUrl(proxy.Name)),
                ("zen_keys_moved", zenMoved), ("go_keys_moved", goMoved));
        }
        return (zenMoved, goMoved);
    }

    private (int ZenMoved, int GoMoved) RestoreProxy(ProxyTransport? proxy)
    {
        if (proxy == null)
        {
            return (0, 0);
        }
        var zenMoved = _zenNodes.RestoreProxy(proxy.Index);
        var goMoved = _goNodes.RestoreProxy(proxy.Index);
        // 匿名线路自带按代理的冷却期；只恢复密钥
        // 会导致故障期之后该线路仍被跳过。
        var anonymousCleared = _anonymous.RestoreProxy(proxy);
        if (zenMoved + goMoved > 0 || anonymousCleared > 0)
        {
            _logger.Info("proxy connectivity restored",
                ("component", "proxy"), ("event", "proxy_restored"), ("proxy", Secrets.RedactUrl(proxy.Name)),
                ("zen_keys_moved", zenMoved), ("go_keys_moved", goMoved),
                ("anonymous_cleared", anonymousCleared));
        }
        return (zenMoved, goMoved);
    }

    /// <summary>周期复检真实流量已经放弃使用的代理（Go: StartProxyHealthChecks）。</summary>
    public void StartProxyHealthChecks(CancellationToken cancellationToken) => BackgroundLoop.Start(
        "proxy-health", _logger, ProxyHealthCheckInterval, false,
        async token =>
        {
            var results = await _transports.CheckHealthAsync(token, ProxyHealthCheckURL, ProxyHealthCheckTimeout);
            foreach (var result in results)
            {
                ApplyProxyHealthResult(result, "scheduled health check", 0);
            }
        }, cancellationToken);

    private void ApplyProxyHealthResult(ProxyHealthResult result, string source, int upstreamStatus)
    {
        var proxy = result.Proxy;
        if (result.Error == null)
        {
            if (!result.WasHealthy)
            {
                RestoreProxy(proxy);
            }
            _logger.Debug("proxy health check passed",
                ("component", "proxy"), ("event", "health_check_passed"), ("source", source),
                ("upstream_status", upstreamStatus), ("proxy", Secrets.RedactUrl(proxy.Name)));
            return;
        }
        if (!result.Failed)
        {
            _logger.Debug("proxy health check was inconclusive",
                ("component", "proxy"), ("event", "health_check_inconclusive"), ("source", source),
                ("upstream_status", upstreamStatus), ("proxy", Secrets.RedactUrl(proxy.Name)),
                ("error", result.Error));
            return;
        }
        if (_transports.HasHealthy())
        {
            var (zenMoved, goMoved) = RebindUnavailableProxy(proxy, result.WasHealthy);
            if (result.WasHealthy || zenMoved + goMoved > 0)
            {
                _logger.Warn("proxy health check failed",
                    ("component", "proxy"), ("event", "health_check_failed"), ("source", source),
                    ("upstream_status", upstreamStatus), ("proxy", Secrets.RedactUrl(proxy.Name)),
                    ("zen_keys_moved", zenMoved), ("go_keys_moved", goMoved), ("error", result.Error));
                return;
            }
        }
        _logger.Debug("proxy health check is still failing",
            ("component", "proxy"), ("event", "health_check_still_failing"), ("source", source),
            ("upstream_status", upstreamStatus), ("proxy", Secrets.RedactUrl(proxy.Name)),
            ("error", result.Error));
    }

    /// <summary>
    /// 启动时先刷新一次模型目录，之后按配置周期刷新（Go: StartModelRefresh）。
    /// 三个数据来源并发拉取，只有全部返回后才一起生效。
    /// </summary>
    public void StartModelRefresh(CancellationToken cancellationToken) => BackgroundLoop.Start(
        "model-refresh", _logger, TimeSpan.FromSeconds(_cfg.Models.RefreshSeconds), true,
        RefreshModelsAsync, cancellationToken);

    private async Task RefreshModelsAsync(CancellationToken cancellationToken)
    {
        List<string>? zen = null;
        List<string>? goModels = null;
        var capabilities = new Capabilities();
        Exception? capabilitiesError = null;
        using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 让整个扇出期间保持取消令牌存活，这样关闭时不会
        // 留下三个抓取中的某一个仍在运行。
        var zenTask = RefreshZenAsync(scope.Token);
        var goTask = RefreshTierAsync(scope.Token, _cfg.Upstream.Go, _goNodes);
        var capabilityTask = RefreshProtocolCapabilitiesAsync(scope.Token);
        try
        {
            await Task.WhenAll(zenTask, goTask, capabilityTask);
        }
        catch (Exception)
        {
            // 每个来源通过任务结果报告各自的结果，
            // 刷新失败绝不能终止后台循环。
        }
        zen = Result(zenTask);
        goModels = Result(goTask);
        if (capabilityTask.IsCompleted)
        {
            if (capabilityTask.Exception != null)
            {
                capabilitiesError = capabilityTask.Exception.GetBaseException();
            }
            else
            {
                capabilities = capabilityTask.Result;
            }
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        if (capabilitiesError != null)
        {
            _logger.Warn("OpenCode capability catalog refresh failed",
                ("component", "models"), ("event", "capability_refresh_failed"), ("error", capabilitiesError));
        }
        if (zen == null && goModels == null)
        {
            return;
        }
        _catalog.ReplaceWithCapabilities(
            zen, goModels, capabilities.Protocols, capabilities.Unsupported, capabilities.Metadata);
        SyncRotation();
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            _catalog.SaveCache();
        }
        catch (Exception error)
        {
            _logger.Warn("model catalog cache write failed",
                ("component", "models"), ("event", "catalog_cache_write_failed"), ("error", error));
        }
        _logger.Info("model catalog refreshed",
            ("component", "models"), ("event", "catalog_refreshed"), ("models", _catalog.List().Count));
    }

    private static List<string>? Result(Task<List<string>?> task) =>
        task is { IsCompletedSuccessfully: true } ? task.Result : null;

    /// <summary>
    /// 通过第一个健康的代理抓取 OpenCode 能力目录，
    /// 池中没有健康代理时回退到直连客户端。
    /// </summary>
    private async Task<Capabilities> RefreshProtocolCapabilitiesAsync(CancellationToken cancellationToken)
    {
        if (_transports.Items.Count == 0)
        {
            using var direct = new HttpClient { Timeout = CatalogFetchTimeout };
            return await Discovery.FetchCapabilitiesAsync(direct, Discovery.CapabilitiesURL, cancellationToken);
        }
        Exception? lastError = null;
        foreach (var proxy in _transports.Items)
        {
            if (!proxy.HealthyLoad())
            {
                continue;
            }
            try
            {
                return await Discovery.FetchCapabilitiesAsync(
                    proxy.Client, Discovery.CapabilitiesURL, cancellationToken);
            }
            catch (Exception error) when (error is DiscoveryException or HttpRequestException
                or OperationCanceledException or IOException or JsonException)
            {
                lastError = error;
            }
        }
        throw lastError ?? new GatewayException("no healthy proxy available for OpenCode capability catalog");
    }

    private async Task<List<string>?> RefreshZenAsync(CancellationToken cancellationToken)
    {
        var zen = await RefreshTierAsync(cancellationToken, _cfg.Upstream.Zen, _zenNodes);
        if (zen != null)
        {
            return zen;
        }
        if (!_cfg.Anonymous)
        {
            return null;
        }
        return await RefreshAnonymousTierAsync(cancellationToken, _cfg.Upstream.Anonymous);
    }

    /// <summary>
    /// 通过无密钥线路列出模型：先尝试旧版 /zen/v1 列表，
    /// 再尝试配置的匿名上游，失败时推进到下一个代理。
    /// </summary>
    private async Task<List<string>?> RefreshAnonymousTierAsync(CancellationToken cancellationToken, string baseURL)
    {
        var cursor = _anonymous.CursorFor("");
        var limit = _anonymous.Nodes.Count;
        if (_anonymous.HasPool && _cfg.Retry.MaxAttempts > limit)
        {
            limit = _cfg.Retry.MaxAttempts;
        }
        for (var attempt = 1; attempt <= limit; attempt++)
        {
            var node = cursor.Advance();
            if (node == null)
            {
                break;
            }
            using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scope.CancelAfter(CatalogFetchTimeout);
            List<string>? models = null;
            var status = 0;
            Exception? error = null;
            // 在回落到配置的匿名上游之前，优先使用旧版 /zen/v1
            // 模型列表。
            foreach (var upstream in AnonymousUpstreams())
            {
                try
                {
                    models = await Discovery.FetchModelsAsync(
                        node.Proxy.Client, upstream, AnonymousBody.ZenKey, scope.Token);
                    status = StatusCodes.Status200OK;
                    error = null;
                    break;
                }
                catch (Exception fetchError) when (fetchError is DiscoveryException or HttpRequestException
                    or OperationCanceledException or IOException)
                {
                    models = null;
                    status = fetchError is DiscoveryException discovery ? discovery.StatusCode : 0;
                    error = fetchError;
                }
            }
            await SyncProxyResultAsync(null, node.Proxy, status, error);
            if (error == null && models != null)
            {
                _anonymous.MarkSuccess(node);
                return models;
            }
            _anonymous.MarkFailure(node, error, null);
            _logger.Debug("anonymous model catalog refresh attempt failed",
                ("component", "models"), ("event", "anonymous_refresh_attempt_failed"),
                ("upstream", Secrets.RedactUrl(baseURL)), ("attempt", attempt),
                ("proxy", Secrets.RedactUrl(node.Proxy.Name)), ("error", error));
        }
        _logger.Warn("anonymous model catalog refresh failed",
            ("component", "models"), ("event", "anonymous_refresh_failed"),
            ("upstream", Secrets.RedactUrl(baseURL)));
        return null;
    }

    private async Task<List<string>?> RefreshTierAsync(
        CancellationToken cancellationToken, string baseURL, NodePool nodes)
    {
        var cursor = nodes.Cursor();
        for (var attempt = 0; attempt < _cfg.Retry.MaxAttempts; attempt++)
        {
            var node = cursor.Advance();
            if (node == null)
            {
                return null;
            }
            using var scope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            scope.CancelAfter(CatalogFetchTimeout);
            var proxy = nodes.ProxyOf(node);
            if (proxy == null)
            {
                return null;
            }
            List<string>? models;
            var status = 0;
            Exception? error = null;
            try
            {
                models = await Discovery.FetchModelsAsync(proxy.Client, baseURL, node.Key, scope.Token);
                status = StatusCodes.Status200OK;
            }
            catch (Exception fetchError) when (fetchError is DiscoveryException or HttpRequestException
                or OperationCanceledException or IOException)
            {
                models = null;
                status = fetchError is DiscoveryException discovery ? discovery.StatusCode : 0;
                error = fetchError;
            }
            await SyncProxyResultAsync(null, proxy, status, error);
            if (error == null && models != null)
            {
                nodes.MarkSuccess(node);
                return models;
            }
            nodes.MarkFailure(node, error, null);
            _logger.Debug("model catalog refresh attempt failed",
                ("component", "models"), ("event", "refresh_attempt_failed"),
                ("upstream", Secrets.RedactUrl(baseURL)), ("attempt", attempt + 1), ("error", error));
        }
        _logger.Warn("model catalog refresh failed",
            ("component", "models"), ("event", "refresh_failed"), ("upstream", Secrets.RedactUrl(baseURL)));
        return null;
    }
}
