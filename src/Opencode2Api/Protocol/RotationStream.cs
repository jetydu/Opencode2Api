namespace Opencode2Api.Protocol;

/// <summary>
/// 轮转分组的流式路径。角色、开始与用量事件
/// 不携带任何模型可见内容，因此它们会被按住，直到第一个
/// 有效事件证明当前模型确实产出输出。Ready 会
/// 提交所选模型并停止首输出定时器；一旦提交，
/// 失败就带内输出且绝不重放
/// （internal/protocol/rotation_stream.go）。
/// </summary>
internal static class RotationStream
{
    private const int MaxPendingEvents = 256;

    public static async Task<StreamOutcome> ForwardAsync(
        Stream input, Stream output, string from, string to, string alias, Func<Task> ready, CancellationToken ct)
    {
        var parser = new StreamParser(from);
        var emitter = new StreamEmitter(output, to, alias, ct);
        var pending = new List<StreamEvent>();
        var committed = false;
        Exception? readErr = null;

        // 发射器的模型在整个响应中都是请求所指定的别名，
        // 因此按事件的模型提示不得覆盖它。
        async Task EmitEventAsync(StreamEvent ev)
        {
            ev.Model = "";
            try
            {
                await emitter.EmitAsync(ev);
            }
            catch (StreamUpstreamFailureException)
            {
                throw;
            }
            catch (ClientWriteException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ClientWriteException(error);
            }
        }

        try
        {
            await SseReader.ReadSseAsync(input, async (eventName, data) =>
            {
                List<StreamEvent> events;
                try
                {
                    events = parser.Parse(eventName, data);
                }
                catch (Exception)
                {
                    throw new StreamFailureException("parse_error");
                }
                foreach (var ev in events)
                {
                    if (ev.Kind == "error")
                    {
                        throw new StreamFailureException(ev.ErrorType, ev.Error);
                    }
                    if (!committed)
                    {
                        var effective = ev.Kind is "text" or "reasoning" or "reasoning_signature"
                            or "tool_start" or "tool_delta" or "done";
                        if (!effective)
                        {
                            if (pending.Count >= MaxPendingEvents)
                            {
                                throw new StreamFailureException("invalid_prelude");
                            }
                            pending.Add(ev);
                            continue;
                        }
                        await ready();
                        committed = true;
                        foreach (var held in pending)
                        {
                            await EmitEventAsync(held);
                        }
                        pending.Clear();
                    }
                    await EmitEventAsync(ev);
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
        var usage = emitter.Usage;
        var reported = emitter.UsageReported;
        if (readErr is StreamNormalTerminationException)
        {
            return Result(usage, reported, committed, null);
        }
        readErr ??= new StreamFailureException("unexpected_eof");
        // 只有已提交的流才可以描述自身的失败：提交之前
        // 网关仍能切换到另一个模型，而客户端还没有
        // 看到任何内容。写入错误意味着客户端已经离开。
        if (committed && !ct.IsCancellationRequested && readErr is not ClientWriteException)
        {
            try
            {
                await EmitEventAsync(new StreamEvent
                {
                    Kind = "error",
                    Error = "当前上游模型的输出中断；本次响应已结束，请重新发起请求。",
                    ErrorType = "upstream_error",
                });
            }
            catch (Exception)
            {
                // 流已经在失败；合成的错误不会让它
                // 更糟，而网关上报的仍是原始原因。
            }
        }
        return Result(usage, reported, committed, readErr);
    }

    private static StreamOutcome Result(Usage usage, bool reported, bool committed, Exception? error) => new()
    {
        Usage = usage,
        Reported = reported,
        Committed = committed,
        Error = error,
    };
}
