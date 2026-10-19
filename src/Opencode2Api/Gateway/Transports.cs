using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Opencode2Api.Config;
using Opencode2Api.Http;

namespace Opencode2Api.Gateway;

/// <summary>
/// 一条代理线路，自带 HttpClient、健康标记与检查占用标记
/// （pool.go proxyTransport）。两个布尔用 Interlocked 更新，
/// 因为健康检查与真实流量会并发修改它们。
/// </summary>
internal sealed class ProxyTransport
{
    private int _healthy = 1;
    private int _checking;

    public required int Index { get; init; }
    public required string Name { get; init; }
    public required bool Pool { get; init; }
    public required HttpClient Client { get; init; }

    /// <summary>
    /// 对应 Go 的 Transport.ResponseHeaderTimeout。运行时没有传输级
    /// 等价能力，因此经此代理的每次发送改为用
    /// 按请求的截止时间约束等待响应头。
    /// </summary>
    public required TimeSpan HeaderTimeout { get; init; }

    /// <summary>
    /// 在同一 handler 上的第二个客户端，供必须看到 3xx 响应体
    /// 而不是静默重放 POST 的线路使用（对应 rotation.go 的 ErrUseLastResponse）。
    /// 共享 handler 可以让连接池保持完全一致。
    /// </summary>
    public required HttpClient NoRedirectClient { get; init; }

    /// <summary>
    /// 发送时按请求施加传输层的响应头等待上限，
    /// 这是运行时对 Transport.ResponseHeaderTimeout 的替代。
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool noRedirect = false)
    {
        var client = noRedirect ? NoRedirectClient : Client;
        if (HeaderTimeout <= TimeSpan.Zero)
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(HeaderTimeout);
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        }
        catch (Exception e)
        {
            var timedOut = linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            if (!timedOut || !HasCancellation(e))
            {
                throw;
            }
            throw new HttpRequestException("timeout awaiting response headers", new TimeoutException("timeout awaiting response headers", e));
        }
    }

    private static bool HasCancellation(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }
        return false;
    }

    public bool HealthyLoad() => Volatile.Read(ref _healthy) != 0;

    /// <summary>对应 Go 的 atomic.Bool.Swap：返回旧值。</summary>
    public bool HealthySwap(bool value) => Interlocked.Exchange(ref _healthy, value ? 1 : 0) != 0;

    public bool CheckingLoad() => Volatile.Read(ref _checking) != 0;

    /// <summary>对应 Go 的 checking.CompareAndSwap(false, true)。</summary>
    public bool ClaimChecking() => Interlocked.CompareExchange(ref _checking, 1, 0) == 0;

    public void ReleaseChecking() => Volatile.Write(ref _checking, 0);

    /// <summary>
    /// 代理池条目绝不复用隧道：Go 设置 DisableKeepAlives
    /// 并把 TLSNextProto 置空，使每次尝试都新建连接、
    /// 获得新的出口 IP。运行时的等价做法是 Connection: close
    /// 加上严格指定的 HTTP/1.1 请求版本。
    /// </summary>
    public void ApplyTo(HttpRequestMessage request)
    {
        if (!Pool)
        {
            return;
        }
        request.Headers.ConnectionClose = true;
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
    }
}

internal sealed record ProxyHealthResult(ProxyTransport Proxy, Exception? Error, bool Failed, bool WasHealthy);

/// <summary>
/// 代理传输集合，以及真实流量与定时检查器共享的
/// 健康记账（pool.go transportPool）。
/// </summary>
internal sealed class TransportPool
{
    public List<ProxyTransport> Items { get; } = [];

    public bool HasHealthy()
    {
        foreach (var proxy in Items)
        {
            if (proxy.HealthyLoad())
            {
                return true;
            }
        }
        return false;
    }

    public (int Total, int Healthy) HealthCounts()
    {
        var healthy = 0;
        foreach (var proxy in Items)
        {
            if (proxy.HealthyLoad())
            {
                healthy++;
            }
        }
        return (Items.Count, healthy);
    }

    /// <summary>
    /// 为每个配置的代理条目构造一个隔离的传输。每条线路
    /// 继承调优后的连接池上限与共享的响应头超时，
    /// 后者正是尝试守卫启用之前约束卡死出口的因素。
    /// </summary>
    public static TransportPool Create(IReadOnlyList<string> proxies, PerformanceConfig cfg, TimeSpan responseHeaderTimeout)
    {
        var pool = new TransportPool();
        foreach (var raw in proxies)
        {
            var (clean, isPool) = Proxy.ParseProxyEntry(raw);
            ProxyEndpoint? socks = null;
            WebProxy? webProxy = null;
            if (clean != "direct")
            {
                var endpoint = ProxyEndpoint.TryParse(clean);
                if (endpoint == null)
                {
                    throw new GatewayException($"parse proxy {Secrets.RedactUrl(raw)}: invalid proxy URL {clean}");
                }
                if (endpoint.IsSocks)
                {
                    // SOCKS 由连接回调代为中继，因为运行时没有
                    // 受管理的 socks 传输；代理本身绝不会出现在重定向目标中。
                    socks = endpoint;
                }
                else
                {
                    Uri proxyUri;
                    try
                    {
                        proxyUri = new Uri(clean);
                    }
                    catch (UriFormatException e)
                    {
                        throw new GatewayException($"parse proxy {Secrets.RedactUrl(raw)}: {e.Message}", e);
                    }
                    webProxy = new WebProxy(proxyUri, false);
                    if (endpoint.Username.Length > 0 || endpoint.Password != null)
                    {
                        webProxy.Credentials = new NetworkCredential(endpoint.Username, endpoint.Password);
                    }
                }
            }
            // AllowAutoRedirect 是 handler 级开关，因此轮转线路的
            // 不重放客户端需要在相同设置上另建一个副本。
            SocketsHttpHandler Build(bool allowRedirect)
            {
                var handler = new SocketsHttpHandler
                {
                    UseCookies = false,
                    AllowAutoRedirect = allowRedirect,
                    // Go 的 MaxIdleConns/MaxIdleConnsPerHost 没有运行时
                    // 等价项；每主机硬上限才是有意义的那个。
                    MaxConnectionsPerServer = cfg.MaxConnsPerHost > 0 ? cfg.MaxConnsPerHost : int.MaxValue,
                    PooledConnectionLifetime = isPool ? TimeSpan.FromTicks(1) : Timeout.InfiniteTimeSpan,
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(cfg.IdleConnTimeoutSeconds),
                    ConnectTimeout = cfg.ConnectTimeoutSeconds > 0 ? TimeSpan.FromSeconds(cfg.ConnectTimeoutSeconds) : Timeout.InfiniteTimeSpan,
                    UseProxy = webProxy != null,
                    Proxy = webProxy,
                };
                if (socks != null || clean == "direct")
                {
                    handler.ConnectCallback = (context, token) => OpenConnectionAsync(handler, context, socks, isPool, token);
                }
                return handler;
            }

            var proxy = new ProxyTransport
            {
                Index = pool.Items.Count,
                Name = raw,
                Pool = isPool,
                HeaderTimeout = responseHeaderTimeout,
                Client = new HttpClient(Build(true)),
                NoRedirectClient = new HttpClient(Build(false)),
            };
            pool.Items.Add(proxy);
        }
        return pool;
    }

    /// <summary>
    /// 为直连与 socks 线路建立源连接，只在目标协议需要时
    /// 再叠加 TLS。池条目把 ALPN 固定为
    /// http/1.1，使轮转出口绝不可能把两个请求复用进同一条隧道连接。
    /// </summary>
    private static async ValueTask<Stream> OpenConnectionAsync(
        SocketsHttpHandler handler, SocketsHttpConnectionContext context, ProxyEndpoint? socks, bool isPool, CancellationToken cancellationToken)
    {
        var trace = AttemptTraceRegistry.Find(context.InitialRequestMessage);
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var timeout = handler.ConnectTimeout > TimeSpan.Zero && handler.ConnectTimeout != Timeout.InfiniteTimeSpan
            ? handler.ConnectTimeout
            : TimeSpan.FromSeconds(30);

        Stream stream;
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            linked.CancelAfter(timeout);
            var token = linked.Token;
            if (socks != null)
            {
                stream = await Socks5.ConnectAsync(socks, host, port, timeout, cancellationToken);
            }
            else
            {
                var socket = new Socket(
                    Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork,
                    SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    socket.DualMode = true;
                }
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
                stream = new NetworkStream(socket, ownsSocket: true);
            }
            if (UsesTls(context))
            {
                var sslStream = new SslStream(stream, leaveInnerStreamOpen: false);
                var options = new SslClientAuthenticationOptions { TargetHost = host };
                if (isPool)
                {
                    options.ApplicationProtocols = [SslApplicationProtocol.Http11];
                }
                try
                {
                    await sslStream.AuthenticateAsClientAsync(options, token);
                }
                catch
                {
                    sslStream.Dispose();
                    throw;
                }
                stream = sslStream;
            }
        }
        trace?.MarkGotConn();
        return stream;
    }

    private static bool UsesTls(SocketsHttpConnectionContext context) =>
        string.Equals(context.InitialRequestMessage?.RequestUri?.Scheme, "https", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 并发地只复查已被标记为不健康的代理。健康代理在
    /// 占用检查之前就被跳过。测试 URL 返回的任何 HTTP 响应
    /// 都证明线路可达；只有超时或连接被拒
    /// 才会让代理保持不健康。
    /// </summary>
    public async Task<List<ProxyHealthResult>> CheckHealthAsync(CancellationToken cancellationToken, string target, TimeSpan timeout)
    {
        List<Task<ProxyHealthResult>> checks = [];
        foreach (var proxy in Items)
        {
            if (proxy.Pool || proxy.HealthyLoad() || !proxy.ClaimChecking())
            {
                continue;
            }
            // 在首次读取健康状态与占用本次检查之间，
            // 真实请求可能已经恢复了该代理。
            if (proxy.HealthyLoad())
            {
                proxy.ReleaseChecking();
                continue;
            }
            checks.Add(Task.Run(() => CheckClaimedProxyAsync(proxy, target, timeout, cancellationToken), CancellationToken.None));
        }
        var output = new List<ProxyHealthResult>(checks.Count);
        foreach (var check in checks)
        {
            output.Add(await check);
        }
        return output;
    }

    /// <summary>在调用方已取得检查占用权之后执行一次检查。</summary>
    public async Task<ProxyHealthResult> CheckClaimedProxyAsync(ProxyTransport proxy, string target, TimeSpan timeout, CancellationToken parentToken)
    {
        try
        {
            Exception? error = null;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken))
            {
                linked.CancelAfter(timeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgents.Current());
                proxy.ApplyTo(request);
                try
                {
                    using var response = await proxy.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                    await DrainAsync(response, linked.Token);
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or SocketException)
                {
                    error = e;
                }
            }
            var wasHealthy = proxy.HealthyLoad();
            var failed = false;
            if (error == null)
            {
                wasHealthy = proxy.HealthySwap(true);
            }
            else if (ProxyHealth.IsProxyFailure(error))
            {
                failed = true;
                wasHealthy = proxy.HealthySwap(false);
            }
            return new ProxyHealthResult(proxy, error, failed, wasHealthy);
        }
        finally
        {
            proxy.ReleaseChecking();
        }
    }

    private static async Task DrainAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[16 * 1024];
            long total = 0;
            while (total < 64L << 10)
            {
                var read = await body.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // 线路已经自证可达，响应尾部内容无关紧要。
        }
    }
}

/// <summary>
/// 判断尝试错误是否说明代理线路不可用。HTTP 响应
/// 与无关的传输/协议错误都不得剔除代理
/// （pool.go isProxyFailure）。
/// </summary>
internal static class ProxyHealth
{
    public static bool IsProxyFailure(Exception? error)
    {
        if (ExceptionSignals.Is<FirstEventTimeoutException>(error) || ExceptionSignals.UpstreamAccepted(error))
        {
            return false; // The upstream accepted HTTP; slow generation is not a broken proxy.
        }
        if (error == null)
        {
            return false;
        }
        if (ExceptionSignals.Is<DeadlineExceededException>(error) || IsConnectionRefused(error))
        {
            return true;
        }
        return IsTimeoutish(error);
    }

    /// <summary>对应 Go 的 errors.Is(err, syscall.ECONNREFUSED)。</summary>
    private static bool IsConnectionRefused(Exception? error)
    {
        for (var current = error; current != null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 对应 Go 的 net.Error.Timeout()：让拨号超时、
    /// 等待响应头超时或被取消中断的传输，在运行时表现为这几类异常。
    /// </summary>
    private static bool IsTimeoutish(Exception? error)
    {
        for (var current = error; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case TimeoutException:
                case TaskCanceledException:
                    return true;
                case SocketException socket when socket.SocketErrorCode == SocketError.TimedOut:
                    return true;
                case OperationCanceledException canceled when !canceled.CancellationToken.IsCancellationRequested:
                    return true;
            }
        }
        return false;
    }
}
