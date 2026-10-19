using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using Opencode2Api.Config;

namespace Opencode2Api.Gateway;

/// <summary>
/// 对应 Go 的 context.DeadlineExceeded：是预算耗尽而不是调用方
/// 离开。handleInference 把它映射为 504，因此该类型必须
/// 贯穿整个尝试链。
/// </summary>
internal class DeadlineExceededException(string message = "context deadline exceeded") : Exception(message);

/// <summary>errAttemptTimeout：单次尝试预算过期（first_event.go）。</summary>
internal sealed class AttemptTimeoutException() : DeadlineExceededException("upstream attempt timed out: context deadline exceeded");

/// <summary>errFirstEventTimeout：响应头已到达，但 SSE 事件未及时到达。</summary>
internal sealed class FirstEventTimeoutException() : DeadlineExceededException("upstream first SSE event timed out: context deadline exceeded");

/// <summary>errProxyUnavailable：尝试从未到达上游。</summary>
internal sealed class ProxyUnavailableSentinel() : Exception("proxy unavailable");

/// <summary>
/// 对应 Go 的 proxyUnavailableError：它可以展开为 errProxyUnavailable
/// 与传输错误两者，因此询问任意一种的调用方都能得到真实答案。
/// </summary>
internal sealed class ProxyUnavailableRouteException(string proxy, Exception inner)
    : Exception($"proxy {Secrets.RedactUrl(proxy)} unavailable: {inner.Message}", inner)
{
    public IReadOnlyList<Exception> Branches { get; } = [new ProxyUnavailableSentinel(), inner];
}

/// <summary>
/// 对应 Go 的 fmt.Errorf("no healthy anonymous proxies available: %w", errProxyUnavailable)：
/// 消息点名匿名线路，而原因仍然标记所有代理
/// 不可达，因此客户端得到 503 而不是 502。
/// </summary>
internal sealed class ProxyBudgetExhaustedException() : Exception(
    "no healthy anonymous proxies available: proxy unavailable", new ProxyUnavailableSentinel());

/// <summary>
/// 携带尝试失败所处的阶段。展开时保留原始错误，
/// 使既有的类型检查继续有效（phasedError）。
/// </summary>
internal sealed class PhasedException(Exception inner, string phase) : Exception($"{inner.Message} [phase={phase}]", inner)
{
    public string Phase { get; } = phase;

    public static PhasedException? Find(Exception? error)
    {
        foreach (var candidate in ExceptionSignals.Walk(error))
        {
            if (candidate is PhasedException phased)
            {
                return phased;
            }
        }
        return null;
    }
}

/// <summary>
/// 错误检查辅助方法，代替 Go 的 errors.Is/errors.As，
/// 包括 proxyUnavailableError 依赖的多错误展开。
/// </summary>
internal static class ExceptionSignals
{
    public static IEnumerable<Exception> Walk(Exception? error)
    {
        if (error == null)
        {
            return [];
        }
        var seen = new List<Exception>();
        var pending = new Stack<Exception>();
        pending.Push(error);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            seen.Add(current);
            if (current is ProxyUnavailableRouteException route)
            {
                foreach (var branch in route.Branches)
                {
                    pending.Push(branch);
                }
            }
            if (current.InnerException != null)
            {
                pending.Push(current.InnerException);
            }
        }
        return seen;
    }

    public static bool Is<T>(Exception? error) => Walk(error).OfType<T>().Any();

    /// <summary>
    /// 判断尝试是否在上游已经用响应头答复之后才失败。
    /// 这类超时是生成缓慢，不是代理故障，
    /// 因此不得剔除该出口。
    /// </summary>
    public static bool UpstreamAccepted(Exception? error) => PhasedException.Find(error)?.Phase == "first_event";

    /// <summary>
    /// 为尚未建立上游连接的尝试错误打标记。经过真实代理时，
    /// 该阶段涵盖代理拨号、CONNECT/SOCKS 握手以及
    /// 在其之上隧道化的 TLS 握手，因此故障属于代理
    /// 而非上游。直连传输不做改动：那里的拨号失败
    /// 是上游可达性问题。
    /// </summary>
    public static Exception AsProxyUnavailable(ProxyTransport? proxy, Exception? error)
    {
        if (error == null || proxy == null || proxy.Name == "direct")
        {
            return error!;
        }
        var phase = PhasedException.Find(error)?.Phase;
        if (phase == "dial" || IsSocksRouteFailure(error))
        {
            return new ProxyUnavailableRouteException(proxy.Name, error);
        }
        return error;
    }

    /// <summary>对应 Go 的 op.Op == "proxyconnect" || strings.HasPrefix(op.Op, "socks")。</summary>
    private static bool IsSocksRouteFailure(Exception? error)
    {
        foreach (var candidate in Walk(error))
        {
            if (candidate is GatewayException gateway && gateway.Message.StartsWith("socks5", StringComparison.Ordinal))
            {
                return true;
            }
            if (candidate is SocketException socket &&
                socket.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 为请求日志渲染一段标记。尝试超时与传输两类情况会附上阶段名，
    /// 使卡死的出口可以被归因，
    /// 无需再去翻日志（classifyAttemptError）。
    /// </summary>
    public static string ClassifyAttemptError(Exception? error)
    {
        var phase = PhasedException.Find(error)?.Phase;
        var suffix = phase == null ? "" : "_" + phase;
        if (Is<AttemptTimeoutException>(error))
        {
            return "attempt_timeout" + suffix;
        }
        if (Is<FirstEventTimeoutException>(error))
        {
            return "first_event";
        }
        if (Is<DeadlineExceededException>(error))
        {
            return "timeout" + suffix;
        }
        return "transport" + suffix;
    }
}

/// <summary>
/// 请求级预算：重试超时加上客户端自身断开，
/// 这与 Go 派生 context 的行为一致。
/// </summary>
internal sealed class RequestBudget : IDisposable
{
    private readonly CancellationTokenSource _source;

    private RequestBudget(CancellationTokenSource source, CancellationToken client)
    {
        _source = source;
        Client = client;
    }

    public static RequestBudget Start(CancellationToken client, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(client);
        if (timeout > TimeSpan.Zero)
        {
            source.CancelAfter(timeout);
        }
        return new RequestBudget(source, client);
    }

    public CancellationToken Token => _source.Token;

    /// <summary>下游传输令牌（Go: r.Context()）。</summary>
    public CancellationToken Client { get; }

    public bool Canceled => _source.IsCancellationRequested;

    /// <summary>预算是自行到期而非客户端退出导致时为真。</summary>
    public bool IsRequestTimeout => _source.IsCancellationRequested && !Client.IsCancellationRequested;

    public void Dispose() => _source.Dispose();
}

/// <summary>
/// 记录单次尝试失败前走到了哪一步。共享的
/// 响应头超时只覆盖等待响应头；这个探针把它看不见的
/// 阶段（拨号、写入请求）与能看见的区分开。
/// (attemptTrace).
/// </summary>
internal sealed class AttemptTrace
{
    private int _gotConn;
    private int _wroteRequest;
    private int _gotHeaders;

    public void MarkGotConn() => Volatile.Write(ref _gotConn, 1);

    public void MarkWroteRequest() => Volatile.Write(ref _wroteRequest, 1);

    public void MarkGotHeaders() => Volatile.Write(ref _gotHeaders, 1);

    public string Phase()
    {
        if (Volatile.Read(ref _gotConn) == 0)
        {
            return "dial";
        }
        if (Volatile.Read(ref _wroteRequest) == 0)
        {
            return "write";
        }
        if (Volatile.Read(ref _gotHeaders) == 0)
        {
            return "headers";
        }
        return "first_event";
    }
}

/// <summary>
/// 把进行中的请求映射到其跟踪对象，使连接回调与内容
/// 序列化器可以报告进度，而不必把跟踪层层传递
/// （httptrace.WithClientTrace）。
/// </summary>
internal static class AttemptTraceRegistry
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpRequestMessage, AttemptTrace> Table = new();

    public static AttemptTrace Attach(HttpRequestMessage request)
    {
        var trace = new AttemptTrace();
        Table.Add(request, trace);
        return trace;
    }

    public static AttemptTrace? Find(HttpRequestMessage? request)
    {
        if (request == null)
        {
            return null;
        }
        return Table.TryGetValue(request, out var trace) ? trace : null;
    }
}

/// <summary>
/// 一个能报告传输开始与结束写入字节的内存请求体，
/// 在没有 httptrace 时，这是代理连接自证的唯一位置。
/// </summary>
internal sealed class TracedByteArrayContent(byte[] content, AttemptTrace trace) : ByteArrayContent(content)
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        trace.MarkGotConn();
        try
        {
            await base.SerializeToStreamAsync(stream, context, cancellationToken);
        }
        finally
        {
            trace.MarkWroteRequest();
        }
    }
}

/// <summary>
/// 先提供首事件守卫已经读到的字节，再接上活动连接。
/// Go 用 io.MultiReader 串起 bufio 的剩余内容实现同样的组合。
/// (attemptBody).
/// </summary>
internal sealed class PrefixReplayStream : Stream
{
    private readonly byte[] _prefix;
    private readonly byte[] _carried;
    private readonly Stream _inner;
    private readonly Action _onClose;
    private int _position;
    private int _carriedPosition;
    private int _closed;

    public PrefixReplayStream(byte[] prefix, byte[] carried, Stream inner, Action onClose)
    {
        _prefix = prefix;
        _carried = carried;
        _inner = inner;
        _onClose = onClose;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// 关闭响应体才会释放按尝试的取消作用域，因此
    /// 即使调用方重复释放，它也必须恰好触发一次。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _inner.Dispose();
            _onClose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            await _inner.DisposeAsync();
            _onClose();
        }
        await base.DisposeAsync();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_position < _prefix.Length)
        {
            var take = Math.Min(destination.Length, _prefix.Length - _position);
            _prefix.AsMemory(_position, take).CopyTo(destination);
            _position += take;
            return take;
        }
        if (_carriedPosition < _carried.Length)
        {
            var take = Math.Min(destination.Length, _carried.Length - _carriedPosition);
            _carried.AsMemory(_carriedPosition, take).CopyTo(destination);
            _carriedPosition += take;
            return take;
        }
        return await _inner.ReadAsync(destination, cancellationToken);
    }
}
