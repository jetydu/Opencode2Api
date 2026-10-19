namespace Opencode2Api.Protocol;

/// <summary>
/// 用于展开 SSE 读取循环的信号，对应 Go 的哨兵错误
/// （errStreamNormalTermination、errStreamUpstreamFailure、errSSEUnexpectedEOF）。
/// </summary>
internal sealed class StreamNormalTerminationException : Exception
{
    public StreamNormalTerminationException() : base("upstream stream terminated normally") { }
}

/// <summary>上游失败已经带内投递给了客户端。</summary>
internal sealed class StreamUpstreamFailureException : Exception
{
    public StreamUpstreamFailureException() : base("upstream stream failure delivered") { }
}

/// <summary>上游结束却没有终止事件。</summary>
internal sealed class SseUnexpectedEofException : Exception
{
    public SseUnexpectedEofException() : base("unexpected end of SSE stream") { }
}

/// <summary>客户端连接在写入中途消失。</summary>
internal sealed class ClientWriteException(Exception inner) : Exception("downstream write failed", inner);

/// <summary>
/// 携带上游分类结果，但不把原始文本暴露给调用方
/// 或诊断信息。在提交轮转流之前可以先检查它。
/// </summary>
internal sealed class StreamFailureException(string type, string detail = "") : Exception("upstream stream failed")
{
    public string Type { get; } = type;
    public string Detail { get; } = detail;
}

/// <summary>流辅助函数最终得到了什么，供网关记录日志。</summary>
internal readonly struct StreamOutcome
{
    public required Usage Usage { get; init; }
    public bool Reported { get; init; }
    public bool Committed { get; init; }

    /// <summary>流干净结束时为 null。</summary>
    public Exception? Error { get; init; }
}

internal static class StreamUsage
{
    /// <summary>
    /// 把新观察到的用量折叠进累计用量。只有非零字段
    /// 会覆盖，且总量绝不会收缩到 input+output 以下。
    /// </summary>
    public static void Merge(Usage destination, Usage source)
    {
        if (source.Input != 0)
        {
            destination.Input = source.Input;
        }
        if (source.Output != 0)
        {
            destination.Output = source.Output;
        }
        if (source.Total != 0)
        {
            destination.Total = Math.Max(destination.Total, source.Total);
        }
        if (source.Cached != 0)
        {
            destination.Cached = source.Cached;
        }
        if (source.CacheCreation != 0)
        {
            destination.CacheCreation = source.CacheCreation;
        }
        if (source.Reasoning != 0)
        {
            destination.Reasoning = source.Reasoning;
        }
        destination.Total = Math.Max(destination.Total, destination.Input + destination.Output);
    }
}
