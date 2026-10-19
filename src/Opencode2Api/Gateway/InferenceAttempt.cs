using System.Net.Http.Headers;
using System.Text;
using Opencode2Api.Http;

namespace Opencode2Api.Gateway;

/// <summary>
/// 用独立的取消作用域约束一次上游尝试，使卡死的出口可以被
/// 放弃，同时请求预算继续计时。尝试定时器只在
/// 响应尚未提交时触发；一旦提交就彻底停止，这正是
/// 已建立的流不会被切断的原因（first_event.go）。
/// </summary>
internal sealed class AttemptGate : IDisposable
{
    private readonly object _mutex = new();
    private Timer? _attemptTimer;
    private Exception? _cause;
    private bool _committed;

    private readonly CancellationTokenSource _source;

    public AttemptGate(CancellationToken parent, TimeSpan attemptTimeout)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(parent);
        if (attemptTimeout > TimeSpan.Zero)
        {
            _attemptTimer = new Timer(_ => Cancel(new AttemptTimeoutException()), null, attemptTimeout, Timeout.InfiniteTimeSpan);
        }
    }

    public CancellationToken Token => _source.Token;

    public bool Canceled => _source.IsCancellationRequested;

    public void Cancel(Exception? cause = null)
    {
        lock (_mutex)
        {
            if (cause != null)
            {
                // Go 的尝试定时器在取消前先检查 committed：
                // 已经投递出去的响应绝不被撤销。
                if (_committed)
                {
                    return;
                }
                _cause ??= cause;
            }
        }
        _source.Cancel();
    }

    public Exception? Cause()
    {
        lock (_mutex)
        {
            return _cause;
        }
    }

    /// <summary>
    /// 针对尝试截止时间原子地判定成功：要么截止时间
    /// 已经触发（返回原因），要么响应已提交，
    /// 定时器再也无法切断已建立的流。
    /// </summary>
    public Exception? Commit()
    {
        if (_attemptTimer == null)
        {
            return Cause();
        }
        lock (_mutex)
        {
            if (_cause != null)
            {
                return _cause;
            }
            _committed = true;
        }
        StopAttemptTimer();
        return null;
    }

    /// <summary>Go 在每条返回路径上执行的 defer 块。</summary>
    public void Finish()
    {
        lock (_mutex)
        {
            _committed = true;
        }
        StopAttemptTimer();
    }

    /// <summary>在响应体关闭时释放作用域。</summary>
    public void Release()
    {
        Cancel();
        Dispose();
    }

    public void Dispose() => _source.Dispose();

    private void StopAttemptTimer()
    {
        var timer = Interlocked.Exchange(ref _attemptTimer, null);
        timer?.Dispose();
    }
}

/// <summary>
/// 一次成功的上游交接：响应消息加上必须保持打开
/// 直到网关处理完毕的正文流。关闭它会释放
/// 按尝试的取消作用域，与 Go 的 attemptBody.Close 完全一致。
/// </summary>
internal sealed class UpstreamResponse
{
    public required HttpResponseMessage Message { get; init; }
    public required Stream Body { get; init; }

    /// <summary>
    /// 同时中止正文读取与下游写入的令牌。存在尝试作用域时
    /// 就用它的令牌，因此被取消的尝试
    /// 不会留下仍在活动的流。
    /// </summary>
    public CancellationToken StreamToken { get; init; }

    public int Status => (int)Message.StatusCode;

    public string Header(string name)
    {
        if (Message.Headers.TryGetValues(name, out var values))
        {
            return First(values);
        }
        return Message.Content.Headers.TryGetValues(name, out var contentValues) ? First(contentValues) : "";
    }

    private static string First(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            return value;
        }
        return "";
    }

    public bool IsEventStream => Header("Content-Type").ToLowerInvariant().Contains("text/event-stream");

    /// <summary>
    /// 用响应正文的内存副本重述该响应。Go 换成
    /// io.NopCloser(bytes.NewReader(...))，使状态码与响应头
    /// 继续描述同一个上游答复。
    /// </summary>
    public UpstreamResponse WithBufferedBody(byte[] payload) => new()
    {
        Message = Message,
        Body = new MemoryStream(payload),
        StreamToken = StreamToken,
    };

    /// <summary>
    /// 释放网络正文，同时让响应对象仍可用于
    /// 读取状态码与响应头。
    /// </summary>
    public async Task DrainBodyAsync() => await HttpResponses.DrainAndClose(Body);

    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync();
        Message.Dispose();
    }

    public void Close()
    {
        Body.Dispose();
        Message.Dispose();
    }

    /// <summary>
    /// 把响应的连接归还给连接池：读取有限长度的尾部，然后
    /// 关闭。没有这一步，重试会让上一个流一直占着代理连接。
    /// </summary>
    public async Task DrainAndCloseAsync()
    {
        await HttpResponses.DrainAndClose(Body);
        Message.Dispose();
    }
}

/// <summary>
/// 在下游尚未提交任何内容时，先等待再把成功的 SSE 响应
/// 交回重试循环，保留每个字节以便
/// 原生转发。注释与不完整的数据帧不算首个事件。
/// (doInferenceAttempt).
///
/// attemptTimeout 约束整次尝试（拨号 + 写入请求 + 响应头 +
/// 首个事件）。传输层的响应头超时只覆盖等待响应头，
/// 因此卡死的出口否则会在写入或拨号阶段一直挂起，
/// 直到请求级预算耗尽。
/// </summary>
internal static class InferenceAttempt
{
    private const int MaxPreludeBytes = 1 << 20;

    public static async Task<(UpstreamResponse? Response, Exception? Error)> RunAsync(
        ProxyTransport proxy, HttpRequestMessage request, TimeSpan firstEventTimeout, TimeSpan attemptTimeout, RequestBudget budget, bool noRedirect = false)
    {
        if ((firstEventTimeout <= TimeSpan.Zero && attemptTimeout <= TimeSpan.Zero) ||
            request.RequestUri!.AbsolutePath.EndsWith("/systemone", StringComparison.Ordinal))
        {
            try
            {
                var plain = await proxy.SendAsync(request, budget.Token);
                return (new UpstreamResponse
                {
                    Message = plain,
                    Body = await plain.Content.ReadAsStreamAsync(),
                    StreamToken = budget.Token,
                }, null);
            }
            catch (Exception error)
            {
                return (null, Translate(error, budget, null));
            }
        }

        var trace = AttemptTraceRegistry.Attach(request);
        await RebindTracedContentAsync(request, trace);

        var gate = new AttemptGate(budget.Token, attemptTimeout);
        var handedOff = false;
        try
        {
            HttpResponseMessage message;
            try
            {
                message = await proxy.SendAsync(request, gate.Token, noRedirect);
                trace.MarkGotHeaders();
            }
            catch (Exception error)
            {
                return (null, new PhasedException(Translate(error, budget, gate.Cause()), trace.Phase()));
            }

            // 不需要等待首事件时，响应头到达即视为该次尝试完成。
            if (firstEventTimeout <= TimeSpan.Zero || (int)message.StatusCode / 100 != 2 || !EventStream(message))
            {
                var cause = gate.Commit();
                if (cause != null)
                {
                    message.Dispose();
                    return (null, new PhasedException(cause, trace.Phase()));
                }
                var body = await message.Content.ReadAsStreamAsync();
                handedOff = true;
                return (new UpstreamResponse
                {
                    Message = message,
                    Body = new PrefixReplayStream([], [], body, gate.Release),
                    StreamToken = gate.Token,
                }, null);
            }

            using var firstTimer = new Timer(_ => gate.Cancel(new FirstEventTimeoutException()), null,
                firstEventTimeout, Timeout.InfiniteTimeSpan);
            var raw = await message.Content.ReadAsStreamAsync();
            var prefix = new List<byte>();
            var carried = new List<byte>();
            Exception? readError = null;
            try
            {
                var leftover = await ReadFirstEventAsync(raw, prefix, carried, gate.Token);
                carried.Clear();
                carried.AddRange(leftover);
            }
            catch (Exception error)
            {
                readError = error;
            }
            var stopped = firstTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (!stopped && !gate.Canceled)
            {
                // Stop(false) 表示回调可能仍即将执行。
                gate.Cancel(new FirstEventTimeoutException());
            }
            if (readError != null)
            {
                var err = readError is OperationCanceledException ? gate.Cause() ?? readError : readError;
                await raw.DisposeAsync();
                message.Dispose();
                return (null, new PhasedException(err, trace.Phase()));
            }
            var deadline = gate.Commit();
            if (deadline != null)
            {
                await raw.DisposeAsync();
                message.Dispose();
                return (null, new PhasedException(deadline, trace.Phase()));
            }
            handedOff = true;
            return (new UpstreamResponse
            {
                Message = message,
                Body = new PrefixReplayStream(prefix.ToArray(), carried.ToArray(), raw, gate.Release),
                StreamToken = gate.Token,
            }, null);
        }
        finally
        {
            // Go 在每条返回路径上都会置提交标记并停止尝试定时器；
            // 只有正文已接管所有权的响应才会让作用域继续存活。
            gate.Finish();
            if (!handedOff)
            {
                gate.Cancel();
                gate.Dispose();
            }
        }
    }

    private static bool EventStream(HttpResponseMessage message)
    {
        var contentType = message.Content.Headers.TryGetValues("Content-Type", out var values)
            ? string.Join(",", values)
            : "";
        return contentType.ToLowerInvariant().Contains("text/event-stream");
    }

    /// <summary>
    /// 把缓冲正文替换为能报告写入进度的实现：没有
    /// httptrace 时，传输开始与结束序列化
    /// 负载的时刻是唯一可靠的拨号/写入边界。
    /// </summary>
    private static async Task RebindTracedContentAsync(HttpRequestMessage request, AttemptTrace trace)
    {
        if (request.Content is not ByteArrayContent bytes)
        {
            return;
        }
        var payload = await bytes.ReadAsByteArrayAsync();
        var contentType = bytes.Headers.ContentType;
        request.Content = new TracedByteArrayContent(payload, trace);
        if (contentType != null)
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType.ToString());
        }
    }

    /// <summary>
    /// 把运行时取消翻译成对应的 Go 错误，使
    /// 处理器能区分 504 与 502，也能区分代理剔除与上游缓慢。
    /// </summary>
    private static Exception Translate(Exception error, RequestBudget budget, Exception? cause)
    {
        if (cause != null)
        {
            return cause;
        }
        if (error is OperationCanceledException)
        {
            return budget.IsRequestTimeout ? new DeadlineExceededException() : error;
        }
        return error;
    }

    /// <summary>
    /// 缓冲字节直到出现第一条完整的 SSE 记录，对应 bufio.Reader 的
    /// ReadSlice('\n') 语义：读到的每个字节都加入可重放前缀，
    /// 1 MiB 的前奏是硬失败。注释与残缺帧不会结束等待。
    /// 返回值是越过记录边界之后额外读到的内容。
    /// </summary>
    private static async Task<byte[]> ReadFirstEventAsync(Stream reader, List<byte> prefix, List<byte> carried, CancellationToken cancellationToken)
    {
        var chunk = new byte[4096];
        var hasData = false;
        while (true)
        {
            var boundary = carried.IndexOf((byte)'\n');
            if (boundary < 0)
            {
                var read = await reader.ReadAsync(chunk, cancellationToken);
                if (read == 0)
                {
                    // Go：这条路径上 io.EOF 会变成 io.ErrUnexpectedEOF。
                    throw new EndOfStreamException("unexpected EOF");
                }
                if (prefix.Count + carried.Count + read > MaxPreludeBytes)
                {
                    throw new GatewayException("upstream SSE prelude exceeds 1 MiB");
                }
                for (var index = 0; index < read; index++)
                {
                    carried.Add(chunk[index]);
                }
                continue;
            }
            for (var index = 0; index <= boundary; index++)
            {
                prefix.Add(carried[index]);
            }
            var text = Encoding.UTF8.GetString(carried.ToArray(), 0, boundary).TrimEnd('\r');
            carried.RemoveRange(0, boundary + 1);
            if (text.Length == 0 && hasData)
            {
                return carried.ToArray();
            }
            if (text.StartsWith("data:", StringComparison.Ordinal) || text == "data")
            {
                hasData = true;
            }
        }
    }
}
