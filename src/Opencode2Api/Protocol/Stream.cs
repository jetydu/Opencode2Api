using System.Text;

namespace Opencode2Api.Protocol;

/// <summary>
/// 转码流的结束方式，在读取器运行期间跟踪
/// （protocol.streamTermination）。
/// </summary>
internal enum StreamTermination
{
    Open,
    Normal,
    Error,
}

/// <summary>
/// 从字节流中逐行读取 SSE 记录
/// （protocol.readSSE）。只有当记录的
/// 空行分隔符到达时才把记录交给处理器，因此未结束的尾部
/// 会被报告为被截断的流，而不是当作完整帧解析。
/// </summary>
internal static class SseReader
{
    private const int ChunkSize = 64 * 1024;
    private const int MaxLineBytes = 16 * 1024 * 1024;

    /// <summary>
    /// 对每条完整记录执行处理器。处理器通过抛出
    /// StreamSignals 中的哨兵异常来发出停止信号，这些异常
    /// 从本方法向外传播，正如 Go 中哨兵错误的传播。
    /// </summary>
    public static async Task ReadSseAsync(Stream reader, Func<string, string, Task> handler, CancellationToken ct)
    {
        var chunk = new byte[ChunkSize];
        var current = new MemoryStream();
        var dataLines = new List<string>();
        var eventName = "";

        async Task FlushAsync()
        {
            if (dataLines.Count == 0)
            {
                eventName = "";
                return;
            }
            await handler(eventName, string.Join("\n", dataLines));
            eventName = "";
            dataLines.Clear();
        }

        async Task HandleLineAsync(MemoryStream line)
        {
            // bufio.ScanLines 已经丢弃 CRLF 终止符的 CR；这里
            // 额外的 Trim 对应 Go 中防御性的 TrimSuffix。
            var text = DecodeLine(line);
            if (text.EndsWith("\r", StringComparison.Ordinal))
            {
                text = text[..^1];
            }
            if (text.Length == 0)
            {
                await FlushAsync();
                return;
            }
            if (text[0] == ':')
            {
                return;
            }
            if (text.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = text["event:".Length..].Trim();
                return;
            }
            if (text.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(text["data:".Length..].Trim());
            }
        }

        while (true)
        {
            var read = await reader.ReadAsync(chunk, ct);
            if (read == 0)
            {
                break;
            }
            for (var i = 0; i < read; i++)
            {
                if (chunk[i] != (byte)'\n')
                {
                    current.WriteByte(chunk[i]);
                    if (current.Length > MaxLineBytes)
                    {
                        // 与 Go 的 bufio 扫描器使用同样的措辞，使面向客户端的
                        // 消息在两种实现之间保持完全一致。
                        throw new IOException("bufio.Scanner: token too long");
                    }
                    continue;
                }
                await HandleLineAsync(current);
                current.SetLength(0);
            }
        }
        if (current.Length > 0)
        {
            // 上游在行中间切断；Scanner.Text() 仍会把它呈现出来。
            await HandleLineAsync(current);
        }
        if (dataLines.Count > 0)
        {
            // 空行是 SSE 记录分隔符。不要把最后一条
            // 未终止的记录当作完整帧解析；没有终止事件的
            // EOF 由调用方按被截断的流处理。
            throw new SseUnexpectedEofException();
        }
    }

    /// <summary>
    /// 切分已经在内存中的 SSE 记录集合。用量观察者用它，
    /// 那里的帧总是带着分隔符，也不会漏出错误。
    /// </summary>
    public static List<(string EventName, string Data)> ParseRecords(string text)
    {
        var records = new List<(string, string)>();
        var dataLines = new List<string>();
        var eventName = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith("\r", StringComparison.Ordinal) ? raw[..^1] : raw;
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    records.Add((eventName, string.Join("\n", dataLines)));
                }
                eventName = "";
                dataLines.Clear();
                continue;
            }
            if (line[0] == ':')
            {
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                dataLines.Add(line["data:".Length..].Trim());
            }
        }
        return records;
    }

    private static string DecodeLine(MemoryStream line) =>
        // UTF-8 后续字节都 >= 0x80，因此多字节字符
        // 永远不会跨越我们切分时依据的 0x0A 边界。
        Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
}

/// <summary>
/// 网关共享的流式管线：协议之间的转码、
/// 同协议转发，以及为它提供支撑的用量观察者
/// （internal/protocol/stream.go）。
/// </summary>
internal static class StreamBridge
{
    /// <summary>
    /// 已取消的客户端不应在其连接
    /// 消失之后收到合成的上游错误。
    /// </summary>
    public static bool ClientCanceled(CancellationToken ct, Exception? error) =>
        error is OperationCanceledException || ct.IsCancellationRequested;

    /// <summary>
    /// 对于会送达客户端的消息沿用 Go 的错误文本；取消则按
    /// context.Canceled 自身的格式化方式上报。
    /// </summary>
    public static string ErrorText(Exception error) => error switch
    {
        OperationCanceledException => "context canceled",
        _ => error.Message,
    };

    /// <summary>
    /// 在 <paramref name="from"/> 上读取一路流并写出到
    /// <paramref name="to"/>.
    /// </summary>
    public static async Task<StreamOutcome> TranscodeAsync(
        Stream input, Stream output, string from, string to, string model, CancellationToken ct)
    {
        var parser = new StreamParser(from);
        var emitter = new StreamEmitter(output, to, model, ct);
        var termination = StreamTermination.Open;
        Exception? readErr = null;
        try
        {
            await SseReader.ReadSseAsync(input, async (eventName, data) =>
            {
                List<StreamEvent> events;
                try
                {
                    events = parser.Parse(eventName, data);
                }
                catch (Exception error)
                {
                    termination = StreamTermination.Error;
                    await emitter.EmitAsync(new StreamEvent
                    {
                        Kind = "error",
                        Error = ErrorText(error),
                        ErrorType = "upstream_error",
                    });
                    // EmitAsync 在带内错误写出之后
                    // 自己抛出失败哨兵，与 Go 的返回路径一致。
                    throw new StreamUpstreamFailureException();
                }
                foreach (var ev in events)
                {
                    switch (ev.Kind)
                    {
                        case "done":
                            termination = StreamTermination.Normal;
                            break;
                        case "error":
                            termination = StreamTermination.Error;
                            break;
                    }
                    await emitter.EmitAsync(ev);
                    if (ev.Kind == "done")
                    {
                        throw new StreamNormalTerminationException();
                    }
                }
            }, ct);
        }
        catch (Exception error)
        {
            readErr = error;
        }
        if (readErr != null)
        {
            if (readErr is StreamNormalTerminationException)
            {
                return Outcome(emitter, null);
            }
            if (ClientCanceled(ct, readErr))
            {
                return Outcome(emitter, readErr);
            }
            if (termination == StreamTermination.Open)
            {
                var emitErr = await EmitUnexpectedStreamErrorAsync(emitter, readErr);
                if (emitErr != null)
                {
                    return Outcome(emitter, emitErr);
                }
            }
            return Outcome(emitter, readErr);
        }
        if (termination == StreamTermination.Normal)
        {
            return Outcome(emitter, null);
        }
        if (termination == StreamTermination.Error)
        {
            return Outcome(emitter, new StreamUpstreamFailureException());
        }
        if (ClientCanceled(ct, null))
        {
            return Outcome(emitter, new OperationCanceledException(ct));
        }
        var unexpected = await EmitUnexpectedStreamErrorAsync(emitter, new SseUnexpectedEofException());
        if (unexpected != null)
        {
            return Outcome(emitter, unexpected);
        }
        return Outcome(emitter, new SseUnexpectedEofException());
    }

    /// <summary>
    /// 把上游原始 SSE 字节转成客户端流，不重新
    /// 解析协议。每次成功写入都会 flush，让同协议的流保持
    /// 活跃：io.Copy 可以自行选择较大的写入块，因此逐次写入刷新是
    /// 唯一能让流持续流动的可靠位置。
    /// </summary>
    public static async Task<StreamOutcome> ForwardAsync(
        Stream input, Stream output, string protocol, string model, CancellationToken ct)
    {
        var observer = new StreamUsageObserver(protocol);
        Exception? copyErr = null;
        var chunk = new byte[ChunkSize];
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(chunk, ct);
                if (read == 0)
                {
                    break;
                }
                // io.TeeReader 的顺序：观察者先看到字节
                // 再写给客户端，因此中途消失的客户端仍会计入
                // 上游已经发出的用量。
                observer.Write(chunk.AsSpan(0, read));
                await output.WriteAsync(chunk.AsMemory(0, read), ct);
                await output.FlushAsync(ct);
            }
        }
        catch (Exception error)
        {
            copyErr = error;
        }
        var usage = observer.Finish();
        if (observer.ErrorTermination)
        {
            return Outcome(usage, observer, copyErr ?? new StreamUpstreamFailureException());
        }
        if (observer.NormalTermination && observer.ParseError == null)
        {
            return Outcome(usage, observer, copyErr);
        }
        if (ClientCanceled(ct, copyErr))
        {
            return Outcome(usage, observer, copyErr ?? new OperationCanceledException(ct));
        }
        Exception cause = copyErr ?? observer.ParseError ?? new SseUnexpectedEofException();
        var emitter = new StreamEmitter(output, protocol, model, ct);
        var emitErr = await EmitUnexpectedStreamErrorAsync(emitter, cause);
        if (emitErr != null)
        {
            return Outcome(usage, observer, emitErr);
        }
        return Outcome(usage, observer, cause);
    }

    private const int ChunkSize = 64 * 1024;

    /// <summary>
    /// 发出被截断流所需要的合成错误，除非发射器
    /// 已经带内投递过上游的失败。
    /// </summary>
    private static async Task<Exception?> EmitUnexpectedStreamErrorAsync(StreamEmitter emitter, Exception? cause)
    {
        var message = "upstream SSE stream ended before a terminal event";
        if (cause != null && cause is not SseUnexpectedEofException)
        {
            message = $"upstream SSE stream failed: {ErrorText(cause)}";
        }
        try
        {
            await emitter.EmitAsync(new StreamEvent { Kind = "error", Error = message, ErrorType = "upstream_error" });
        }
        catch (StreamUpstreamFailureException)
        {
            return null;
        }
        return null;
    }

    private static StreamOutcome Outcome(StreamEmitter emitter, Exception? error) => new()
    {
        Usage = emitter.Usage,
        Reported = emitter.UsageReported,
        Committed = true,
        Error = error,
    };

    private static StreamOutcome Outcome(Usage usage, StreamUsageObserver observer, Exception? error) => new()
    {
        Usage = usage,
        Reported = observer.Reported,
        Committed = true,
        Error = error,
    };
}

/// <summary>
/// 监视直通流中重要的帧：终止事件、
/// 流内错误以及任何用量对象（protocol.streamUsageObserver）。
/// </summary>
internal sealed class StreamUsageObserver(string protocol)
{
    private readonly StreamParser _parser = new(protocol);
    private readonly List<byte> _buffer = [];

    public Usage Usage { get; } = new();
    public bool Reported { get; private set; }
    public bool NormalTermination { get; private set; }
    public bool ErrorTermination { get; private set; }
    public Exception? ParseError { get; private set; }

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            _buffer.Add(value);
        }
        while (true)
        {
            var (index, width) = NextSSEBoundary(_buffer);
            if (index < 0)
            {
                break;
            }
            var frame = _buffer.GetRange(0, index + width);
            _buffer.RemoveRange(0, index + width);
            Consume(frame);
        }
    }

    /// <summary>
    /// 未终止的最后一行不是一条 SSE 帧。特别地，不要
    /// 从在 EOF 处被截断的响应里统计用量对象。
    /// </summary>
    public Usage Finish()
    {
        _buffer.Clear();
        return Usage;
    }

    private void Consume(List<byte> frame)
    {
        foreach (var (eventName, data) in SseReader.ParseRecords(Encoding.UTF8.GetString(frame.ToArray())))
        {
            List<StreamEvent> events;
            try
            {
                events = _parser.Parse(eventName, data);
            }
            catch (Exception error)
            {
                ParseError ??= error;
                continue;
            }
            foreach (var ev in events)
            {
                switch (ev.Kind)
                {
                    case "done":
                        NormalTermination = true;
                        break;
                    case "error":
                        ErrorTermination = true;
                        break;
                }
                if (ev.Usage != null)
                {
                    Reported = true;
                    StreamUsage.Merge(Usage, ev.Usage);
                }
            }
        }
    }

    /// <summary>
    /// 最早出现的记录分隔符（CRLF 或 LF）的位置及其宽度。
    /// </summary>
    private static (int Index, int Width) NextSSEBoundary(List<byte> data)
    {
        for (var i = 0; i + 1 < data.Count; i++)
        {
            if (data[i] == (byte)'\n' && data[i + 1] == (byte)'\n')
            {
                return (i, 2);
            }
            if (data[i] == (byte)'\r' && i + 3 < data.Count
                && data[i + 1] == (byte)'\n' && data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
            {
                return (i, 4);
            }
        }
        return (-1, 0);
    }
}
