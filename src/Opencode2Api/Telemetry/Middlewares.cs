using System.Diagnostics;

namespace Opencode2Api.Telemetry;

/// <summary>
/// 最外层 HTTP 中间件：挂载 RequestMeta、跟踪在途请求，
/// 并记录 routed/completed 事件（telemetry.Middleware）。
/// </summary>
public sealed class RequestLoggingMiddleware(Monitor monitor, StructuredLogger logger) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path == "/healthz")
        {
            await next(context);
            return;
        }
        var started = Stopwatch.GetTimestamp();
        var meta = RequestMeta.FromContext(context);
        if (meta == null)
        {
            meta = new RequestMeta();
            RequestMeta.IntoContext(context, meta);
        }
        var counter = new BodyCounter();
        context.Response.Body = new CountingStream(context.Response.Body, counter);
        monitor.Enter();
        try
        {
            await next(context);
        }
        finally
        {
            monitor.Leave();
            var duration = Stopwatch.GetElapsedTime(started);
            var status = context.Response.StatusCode;
            monitor.Record(context.Request.Path, status, duration, meta);
            var outcome = meta.Outcome.Length > 0 ? meta.Outcome : Monitor.RequestOutcome(status, meta.Channel);
            if (meta.Channel.Length > 0)
            {
                logger.Info("request routed",
                    ("component", "http"), ("event", "request_routed"), ("method", context.Request.Method),
                    ("path", context.Request.Path.ToString()), ("status", status), ("duration_ms", (long)duration.TotalMilliseconds),
                    ("request_id", meta.Request), ("model", meta.Model), ("model_alias", meta.ModelAlias),
                    ("session", meta.Session), ("server_key_id", meta.ServerKeyId), ("tier", meta.Tier), ("key_id", meta.KeyId),
                    ("channel", meta.Channel), ("anonymous", meta.Anonymous), ("attempts", meta.Attempts),
                    ("legs", string.Join(' ', meta.Legs)), ("stream", meta.Stream), ("outcome", outcome),
                    ("stop", meta.Stop), ("in", meta.Usage.Input), ("out", meta.Usage.Output),
                    ("reasoning_tokens", meta.Usage.Reasoning), ("cached", meta.Usage.Cached), ("tail", meta.Tail));
            }
            logger.Debug("request completed",
                ("component", "http"), ("event", "request_complete"), ("method", context.Request.Method),
                ("path", context.Request.Path.ToString()), ("status", status), ("duration_ms", (long)duration.TotalMilliseconds),
                ("bytes", counter.Bytes), ("request_id", meta.Request), ("model", meta.Model), ("model_alias", meta.ModelAlias),
                ("tier", meta.Tier), ("key_id", meta.KeyId), ("channel", meta.Channel), ("anonymous", meta.Anonymous),
                ("attempts", meta.Attempts), ("stream", meta.Stream), ("outcome", outcome));
        }
    }
}

/// <summary>把处理程序异常转换为协议形态的 500 响应（telemetry.Recover）。</summary>
public sealed class RecoveryMiddleware(StructuredLogger logger) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception e)
        {
            logger.Error("request handler panicked", ("component", "http"), ("event", "request_panic"), ("error", e));
            try
            {
                await Protocol.ProtocolError.Write(context, Protocol.Wire.Chat, 500, "internal server error", "server_error", "");
            }
            catch (Exception)
            {
                // 响应已经开始发送，无法再写入任何内容。
            }
        }
    }
}

internal sealed class BodyCounter
{
    public long Bytes;
}

/// <summary>为完成日志行统计响应体写入的字节数。</summary>
internal sealed class CountingStream(Stream inner, BodyCounter counter) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        counter.Bytes += count;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer, offset, count, cancellationToken);
        counter.Bytes += count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken);
        counter.Bytes += buffer.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
