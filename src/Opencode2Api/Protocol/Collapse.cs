namespace Opencode2Api.Protocol;

/// <summary>
/// 消费一整条上游 SSE 流，并以同一协议
/// 合成等价的单个（非流式）响应文档
/// （internal/protocol/collapse.go）。
///
/// 它为只提供服务端流式响应的上游线路而存在：网关在线路上
/// 强制 stream:true 并把事件折叠回来，因此
/// 要求普通 JSON 回复的下游客户端仍能正常工作。
/// </summary>
internal static class Collapse
{
    public static async Task<byte[]> CollapseAsync(Stream reader, string protocol, string model)
    {
        var parser = new StreamParser(protocol);
        var accumulator = new CollapseAccumulator();
        accumulator.Response.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        accumulator.Response.Model = model;
        var terminated = false;
        Exception? streamErr = null;
        Exception? readErr = null;
        try
        {
            await SseReader.ReadSseAsync(reader, async (eventName, data) =>
            {
                List<StreamEvent> events;
                try
                {
                    events = parser.Parse(eventName, data);
                }
                catch (Exception error)
                {
                    streamErr = error;
                    throw new StreamUpstreamFailureException();
                }
                foreach (var ev in events)
                {
                    switch (ev.Kind)
                    {
                        case "done":
                            terminated = true;
                            throw new StreamNormalTerminationException();
                        case "error":
                            streamErr = new ProtocolException(FirstNonEmpty(ev.Error, "upstream stream failed"));
                            throw new StreamUpstreamFailureException();
                        default:
                            accumulator.Apply(ev);
                            break;
                    }
                }
            }, CancellationToken.None);
        }
        catch (Exception error)
        {
            readErr = error;
        }
        if (readErr != null && readErr is not StreamNormalTerminationException)
        {
            throw streamErr ?? readErr;
        }
        if (!terminated)
        {
            if (streamErr != null)
            {
                throw streamErr;
            }
            throw new SseUnexpectedEofException();
        }
        return Response.ToUtf8(Response.Encode(protocol, accumulator.Response));
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (value.Length > 0)
            {
                return value;
            }
        }
        return "";
    }
}

/// <summary>
/// 把桥级流事件折叠为一个响应文档。推理增量
/// 累加到发射器最近打开的块中，而已经带有加密内容的
/// 块会另起一个新块，因为它在线路上
/// 是另一个条目。
/// </summary>
internal sealed class CollapseAccumulator
{
    private readonly Dictionary<string, int> _tools = [];
    private BridgeBlock? _reason;

    public BridgeResponse Response { get; } = new();

    public void Apply(StreamEvent ev)
    {
        switch (ev.Kind)
        {
            case "start":
                if (ev.ResponseID.Length > 0)
                {
                    Response.ID = ev.ResponseID;
                }
                if (ev.Model.Length > 0)
                {
                    Response.Model = ev.Model;
                }
                break;
            case "text":
                Response.Text += ev.Text;
                break;
            case "reasoning":
                if (ev.Text.Length == 0 && ev.Encrypted.Length == 0 && ev.Signature.Length == 0)
                {
                    break;
                }
                ReasoningBlock().Text += ev.Text;
                if (ev.Encrypted.Length > 0)
                {
                    _reason = null;
                    var encrypted = ReasoningBlock();
                    encrypted.Encrypted += ev.Encrypted;
                    _reason = null;
                }
                break;
            case "reasoning_signature":
                ReasoningBlock().Signature += ev.Signature;
                break;
            case "tool_start":
                ToolBlock(ev.ToolKey, ev.ToolID, ev.ToolName);
                break;
            case "tool_delta":
                ToolBlock(ev.ToolKey, ev.ToolID, ev.ToolName).ArgumentsJSON += ev.Text;
                break;
            case "usage":
                if (ev.Usage != null)
                {
                    StreamUsage.Merge(Response.Usage, ev.Usage);
                }
                break;
            case "finish":
                Response.Stop = ev.Stop;
                break;
        }
    }

    private BridgeBlock ReasoningBlock()
    {
        _reason ??= OpenReasoningBlock();
        return _reason;
    }

    private BridgeBlock OpenReasoningBlock()
    {
        var block = new BridgeBlock { Kind = "reasoning" };
        Response.Reasoning.Add(block);
        return block;
    }

    private BridgeBlock ToolBlock(string key, string id, string name)
    {
        if (_tools.TryGetValue(key, out var index))
        {
            var block = Response.Tools[index];
            if (block.ID.Length == 0)
            {
                block.ID = id;
            }
            if (block.Name.Length == 0)
            {
                block.Name = name;
            }
            return block;
        }
        var created = new BridgeBlock { Kind = "tool_call", ID = id, Name = name };
        Response.Tools.Add(created);
        _tools[key] = Response.Tools.Count - 1;
        return created;
    }
}
