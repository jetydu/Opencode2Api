using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Protocol;

/// <summary>
/// 包装一个 SSE 流读取器，把匹配 fromName 的工具调用名
/// 改写回 toName，早于下游解析器或写入器消费该流。
/// 当 fromName 或 toName 为空（或相同）时，原样返回读取器
/// （protocol.RenameStreamReader）。
///
/// 它是拉取式的转换而非任务加通道，因此中途
/// 断开的客户端不会把一个被阻塞的写入器困住。
/// </summary>
internal sealed class RenameStreamReader : Stream
{
    private readonly Stream _inner;
    private readonly StreamToolRenamer _renamer;
    private readonly byte[] _chunk = new byte[64 * 1024];
    private readonly MemoryStream _partial = new();
    private byte[] _pending = [];
    private int _pendingStart;
    private bool _eof;

    private RenameStreamReader(Stream reader, string protocol, string fromName, string toName)
    {
        _inner = reader;
        _renamer = new StreamToolRenamer(protocol, fromName, toName);
    }

    public static Stream Wrap(Stream reader, string protocol, string fromName, string toName) =>
        fromName.Length == 0 || toName.Length == 0 || fromName == toName
            ? reader
            : new RenameStreamReader(reader, protocol, fromName, toName);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (Remaining == 0)
        {
            if (_eof)
            {
                return 0;
            }
            // ReadLine 要么返回一行完整内容且无错误，要么连同流结束
            // 一起返回尾块，因此这个循环只可能在
            // 仍有一个被改写的块正在排空时重复。
            var line = await ReadLineAsync(cancellationToken);
            if (line.Found)
            {
                SetPending(RewriteLine(line.Text, line.Terminator));
            }
            if (line.End)
            {
                // 最后一个块会先排空再报告结束，因此即使流被截断，
                // 上游发出的每一个字节仍会送达。
                _eof = true;
                if (Remaining == 0)
                {
                    return 0;
                }
            }
        }
        var copy = Math.Min(Remaining, destination.Length);
        _pending.AsMemory(_pendingStart, copy).CopyTo(destination);
        _pendingStart += copy;
        return copy;
    }

    private int Remaining => _pending.Length - _pendingStart;

    private void SetPending(string value)
    {
        _pending = Encoding.UTF8.GetBytes(value);
        _pendingStart = 0;
    }

    private async Task<(bool Found, string Text, string Terminator, bool End)> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var read = await _inner.ReadAsync(_chunk, ct);
            if (read == 0)
            {
                if (_partial.Length == 0)
                {
                    return (false, "", "", true);
                }
                var tail = DecodePartial();
                // 没有换行的尾部字节原样保留，包括结尾的 CR，
                // 与 bufio.ReadMessage 的报告方式完全一致。
                return (true, tail, "", true);
            }
            for (var i = 0; i < read; i++)
            {
                _partial.WriteByte(_chunk[i]);
                if (_chunk[i] != (byte)'\n')
                {
                    continue;
                }
                var line = DecodePartial();
                var text = line;
                var terminator = "";
                if (text.EndsWith("\n", StringComparison.Ordinal))
                {
                    terminator = "\n";
                    text = text[..^1];
                    if (text.EndsWith("\r", StringComparison.Ordinal))
                    {
                        terminator = "\r\n";
                        text = text[..^1];
                    }
                }
                return (true, text, terminator, false);
            }
        }
    }

    private string DecodePartial()
    {
        var text = Encoding.UTF8.GetString(_partial.GetBuffer(), 0, (int)_partial.Length);
        _partial.SetLength(0);
        return text;
    }

    /// <summary>
    /// 转换一条完整的 SSE 行，在无需改写时保留其原始字节与
    /// 终止符。
    /// </summary>
    private string RewriteLine(string text, string terminator)
    {
        if (!text.StartsWith("data:", StringComparison.Ordinal))
        {
            return text + terminator;
        }
        var raw = text["data:".Length..].Trim();
        if (raw.Length == 0 || raw == "[DONE]")
        {
            return text + terminator;
        }
        var rewritten = _renamer.RewriteFrame(raw);
        if (rewritten == raw)
        {
            return text + terminator;
        }
        return "data: " + rewritten + terminator;
    }
}

/// <summary>
/// 在流被改写期间跟踪单个工具调用名的解析过程。
/// Chat 增量可能重复不断增长的名称前缀或追加片段，因此一个仍是
/// fromName 前缀的值会被按住，直到它要么完成
/// 匹配要么与之分叉。
/// </summary>
internal sealed class StreamToolRenameState
{
    public bool Decided { get; set; }
    public bool Renamed { get; set; }
    public string Pending { get; set; } = "";
}

internal sealed class StreamToolRenamer(string protocol, string fromName, string toName)
{
    private readonly Dictionary<string, StreamToolRenameState> _tools = [];

    /// <summary>改写一个已解码的 SSE 数据对象。</summary>
    public string RewriteFrame(string raw)
    {
        if (protocol != Wire.Chat)
        {
            // Anthropic 与 Responses 的流总是在
            // 单个事件里携带完整工具名，因此无状态的对象改写是精确的。
            if (!raw.Contains(fromName, StringComparison.Ordinal))
            {
                return raw;
            }
            return Encoding.UTF8.GetString(Response.RenameTool(Encoding.UTF8.GetBytes(raw), protocol, fromName, toName));
        }
        if (!raw.Contains("\"tool_calls\"", StringComparison.Ordinal))
        {
            return raw;
        }
        return RewriteChatFrame(raw);
    }

    private string RewriteChatFrame(string raw)
    {
        JsonObject payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonObject>(raw)
                ?? throw new JsonException("stream frame is not a JSON object");
        }
        catch (JsonException)
        {
            return raw;
        }
        var changed = false;
        foreach (var rawChoice in JsonValues.SliceAt(payload, "choices"))
        {
            var choice = rawChoice as JsonObject;
            foreach (var container in new[] { "delta", "message" })
            {
                var message = JsonValues.MapAt(choice, container);
                if (message == null)
                {
                    continue;
                }
                var calls = JsonValues.SliceAt(message, "tool_calls");
                for (var callIndex = 0; callIndex < calls.Count; callIndex++)
                {
                    var call = calls[callIndex] as JsonObject;
                    var function = JsonValues.MapAt(call, "function");
                    if (function == null)
                    {
                        continue;
                    }
                    var state = ToolState(StreamParser.Sprint(JsonValues.FirstAny(
                        Jsonx.Get(call, "index"), JsonValue.Create(JsonValues.StringAt(call, "id")))));
                    var name = JsonValues.StringAt(function, "name");
                    if (name.Length > 0 && ApplyNameFragment(state, function, name))
                    {
                        changed = true;
                    }
                    if (function.ContainsKey("arguments") && !state.Decided)
                    {
                        // 参数只有在名称完整后才能开始：把
                        // 此前按住的字节原样刷出。
                        if (state.Pending.Length > 0)
                        {
                            function["name"] = state.Pending;
                            changed = true;
                        }
                        state.Decided = true;
                    }
                }
            }
        }
        if (!changed)
        {
            return raw;
        }
        return payload.ToJsonString();
    }

    /// <summary>
    /// 把一个名称片段折叠进工具的重命名状态，并报告该帧
    /// 是否发生变化。
    /// </summary>
    private bool ApplyNameFragment(StreamToolRenameState state, JsonObject function, string fragment)
    {
        if (state.Decided)
        {
            if (!state.Renamed)
            {
                return false;
            }
            // 已解析的别名不再接受后续片段：任何续写
            // 都属于客户端从未声明过的更长名称。
            if (!function.ContainsKey("name"))
            {
                return false;
            }
            function.Remove("name");
            return true;
        }
        state.Pending = StreamParser.MergeToolName(state.Pending, fragment);
        if (state.Pending == fromName)
        {
            function["name"] = toName;
            state.Decided = true;
            state.Renamed = true;
            return true;
        }
        if (fromName.StartsWith(state.Pending, StringComparison.Ordinal))
        {
            // 仍是别名的前缀：按住该片段，直到后续某一帧
            // 完成匹配或与之分叉。
            function.Remove("name");
            return true;
        }
        function["name"] = state.Pending;
        state.Decided = true;
        return true;
    }

    private StreamToolRenameState ToolState(string key)
    {
        if (_tools.TryGetValue(key, out var state))
        {
            return state;
        }
        var created = new StreamToolRenameState();
        _tools[key] = created;
        return created;
    }
}
