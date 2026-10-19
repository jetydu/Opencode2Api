using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Protocol;

/// <summary>
/// 桥内的一条归一化流式事件
/// （protocol.bridgeStreamEvent）。
/// </summary>
internal sealed class StreamEvent
{
    public string Kind { get; set; } = "";
    public string ResponseID { get; set; } = "";
    public string Model { get; set; } = "";
    public string Text { get; set; } = "";
    public string Signature { get; set; } = "";
    public string ToolKey { get; set; } = "";
    public string ToolID { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string Stop { get; set; } = "";
    public string Error { get; set; } = "";
    public string ErrorType { get; set; } = "";
    public string Encrypted { get; set; } = "";
    public Usage? Usage { get; set; }
}

/// <summary>
/// 把上游 SSE 帧转换为桥流事件
/// （internal/protocol/stream_parser.go）。
/// </summary>
internal sealed class StreamParser(string protocol)
{
    private readonly Dictionary<string, bool> _tools = [];
    private readonly Dictionary<string, string> _toolIds = [];
    private readonly Dictionary<string, string> _toolNames = [];
    private readonly List<string> _toolOrder = [];
    private readonly Dictionary<string, bool> _responseArgs = [];
    private readonly Dictionary<string, bool> _responseReasoning = [];
    private bool _started;

    public string Protocol { get; } = protocol;

    public IReadOnlyList<string> ToolOrder => _toolOrder;

    public List<StreamEvent> Parse(string eventName, string data)
    {
        if (data == "[DONE]")
        {
            if (Protocol != Wire.Chat)
            {
                throw new ProtocolException($"unexpected [DONE] terminator for {Protocol} stream");
            }
            return [new StreamEvent { Kind = "done" }];
        }
        JsonObject value;
        try
        {
            value = JsonSerializer.Deserialize<JsonObject>(data)
                ?? throw new JsonException("stream frame is not a JSON object");
        }
        catch (JsonException error)
        {
            throw new ProtocolException($"invalid upstream SSE JSON: {error.Message}");
        }
        switch (Protocol)
        {
            case Wire.Chat:
                return ParseChatEvent(eventName, value);
            case Wire.Anthropic:
                return ParseAnthropicEvent(eventName, value);
            case Wire.Responses:
                return ParseResponses(eventName, value);
            default:
                throw new ProtocolException($"unsupported stream protocol \"{Protocol}\"");
        }
    }

    private List<StreamEvent> ParseChatEvent(string eventName, JsonObject value)
    {
        var events = new List<StreamEvent>();
        if (eventName == "error")
        {
            return [new StreamEvent
            {
                Kind = "error",
                Error = StreamErrorMessage(value, "upstream Chat stream error"),
                ErrorType = StreamErrorType(value),
            }];
        }
        if (!Jsonx.IsNull(value["error"]))
        {
            return [new StreamEvent
            {
                Kind = "error",
                Error = StreamErrorMessage(value, "upstream Chat stream error"),
                ErrorType = StreamErrorType(value),
            }];
        }
        if (!_started)
        {
            var id = JsonValues.StringAt(value, "id");
            if (id.Length > 0)
            {
                _started = true;
                events.Add(new StreamEvent
                {
                    Kind = "start",
                    ResponseID = id,
                    Model = JsonValues.StringAt(value, "model"),
                });
            }
        }
        var usageMap = JsonValues.MapAt(value, "usage");
        if (usageMap != null && usageMap.Count > 0)
        {
            events.Add(new StreamEvent { Kind = "usage", Usage = Response.DecodeOpenAIUsage(usageMap) });
        }
        foreach (var raw in JsonValues.SliceAt(value, "choices"))
        {
            var choice = raw as JsonObject;
            var delta = JsonValues.MapAt(choice, "delta");
            var reasoning = JsonValues.FirstString(
                JsonValues.StringAt(delta, "reasoning_content"), JsonValues.StringAt(delta, "reasoning"));
            if (reasoning.Length > 0)
            {
                events.Add(new StreamEvent { Kind = "reasoning", Text = reasoning });
            }
            var text = JsonValues.StringAt(delta, "content");
            if (text.Length > 0)
            {
                events.Add(new StreamEvent { Kind = "text", Text = text });
            }
            foreach (var rawCall in JsonValues.SliceAt(delta, "tool_calls"))
            {
                var call = rawCall as JsonObject;
                var key = Sprint(JsonValues.FirstAny(Jsonx.Get(call, "index"), JsonValue.Create(JsonValues.StringAt(call, "id"))));
                var function = JsonValues.MapAt(call, "function");
                var id = JsonValues.StringAt(call, "id");
                if (!_tools.ContainsKey(key))
                {
                    _tools[key] = false;
                    _toolOrder.Add(key);
                }
                if (id.Length > 0)
                {
                    _toolIds[key] = id;
                }
                var name = JsonValues.StringAt(function, "name");
                if (name.Length > 0)
                {
                    _toolNames[key] = MergeToolName(_toolNames.TryGetValue(key, out var current) ? current : "", name);
                }
                var arguments = JsonValues.StringAt(function, "arguments");
                if (arguments.Length > 0)
                {
                    var startedTool = _tools.TryGetValue(key, out var started2) && started2;
                    var toolName = _toolNames.TryGetValue(key, out var known) ? known : "";
                    if (!startedTool && toolName.Length > 0)
                    {
                        _tools[key] = true;
                        events.Add(new StreamEvent
                        {
                            Kind = "tool_start",
                            ToolKey = key,
                            ToolID = _toolIds.TryGetValue(key, out var toolId) ? toolId : "",
                            ToolName = toolName,
                        });
                    }
                    events.Add(new StreamEvent
                    {
                        Kind = "tool_delta",
                        ToolKey = key,
                        ToolID = _toolIds.TryGetValue(key, out var deltaId) ? deltaId : "",
                        ToolName = toolName,
                        Text = arguments,
                    });
                }
            }
            var stop = JsonValues.StringAt(choice, "finish_reason");
            if (stop.Length > 0)
            {
                if (IsStreamErrorFinish(stop))
                {
                    events.Add(new StreamEvent
                    {
                        Kind = "error",
                        Error = JsonValues.FirstString(JsonValues.StringAt(choice, "error", "message"), "upstream Chat stream failed"),
                        ErrorType = "upstream_error",
                    });
                    continue;
                }
                foreach (var key in _toolOrder)
                {
                    var startedTool = _tools.TryGetValue(key, out var seen) && seen;
                    var toolName = _toolNames.TryGetValue(key, out var known) ? known : "";
                    if (!startedTool && toolName.Length > 0)
                    {
                        _tools[key] = true;
                        events.Add(new StreamEvent
                        {
                            Kind = "tool_start",
                            ToolKey = key,
                            ToolID = _toolIds.TryGetValue(key, out var toolId) ? toolId : "",
                            ToolName = toolName,
                        });
                    }
                }
                events.Add(new StreamEvent { Kind = "finish", Stop = stop });
            }
        }
        return events;
    }

    private List<StreamEvent> ParseAnthropicEvent(string eventName, JsonObject value)
    {
        var typeName = JsonValues.StringAt(value, "type");
        if (typeName.Length == 0)
        {
            typeName = eventName;
        }
        switch (typeName)
        {
            case "message_start":
            {
                var message = JsonValues.MapAt(value, "message");
                var events = new List<StreamEvent>
                {
                    new()
                    {
                        Kind = "start",
                        ResponseID = JsonValues.StringAt(message, "id"),
                        Model = JsonValues.StringAt(message, "model"),
                    },
                };
                var usageMap = JsonValues.MapAt(message, "usage");
                if (usageMap != null && usageMap.Count > 0)
                {
                    events.Add(new StreamEvent { Kind = "usage", Usage = Response.DecodeAnthropicUsage(usageMap) });
                }
                return events;
            }
            case "content_block_start":
            {
                var block = JsonValues.MapAt(value, "content_block");
                var key = Sprint(Jsonx.Get(value, "index"));
                switch (JsonValues.StringAt(block, "type"))
                {
                    case "thinking":
                    {
                        var events = new List<StreamEvent>();
                        var thinking = JsonValues.StringAt(block, "thinking");
                        if (thinking.Length > 0)
                        {
                            events.Add(new StreamEvent { Kind = "reasoning", Text = thinking });
                        }
                        var signature = JsonValues.StringAt(block, "signature");
                        if (signature.Length > 0)
                        {
                            events.Add(new StreamEvent { Kind = "reasoning_signature", Signature = signature });
                        }
                        return events;
                    }
                    case "redacted_thinking":
                        return [new StreamEvent { Kind = "reasoning", Encrypted = JsonValues.StringAt(block, "data") }];
                    case "tool_use":
                        _tools[key] = true;
                        return [new StreamEvent
                        {
                            Kind = "tool_start",
                            ToolKey = key,
                            ToolID = JsonValues.StringAt(block, "id"),
                            ToolName = JsonValues.StringAt(block, "name"),
                        }];
                    case "text":
                    {
                        var text = JsonValues.StringAt(block, "text");
                        if (text.Length > 0)
                        {
                            return [new StreamEvent { Kind = "text", Text = text }];
                        }
                        break;
                    }
                }
                break;
            }
            case "content_block_delta":
            {
                var delta = JsonValues.MapAt(value, "delta");
                var key = Sprint(Jsonx.Get(value, "index"));
                switch (JsonValues.StringAt(delta, "type"))
                {
                    case "thinking_delta":
                        return [new StreamEvent { Kind = "reasoning", Text = JsonValues.StringAt(delta, "thinking") }];
                    case "signature_delta":
                        return [new StreamEvent { Kind = "reasoning_signature", Signature = JsonValues.StringAt(delta, "signature") }];
                    case "text_delta":
                        return [new StreamEvent { Kind = "text", Text = JsonValues.StringAt(delta, "text") }];
                    case "input_json_delta":
                        return [new StreamEvent
                        {
                            Kind = "tool_delta",
                            ToolKey = key,
                            Text = JsonValues.StringAt(delta, "partial_json"),
                        }];
                }
                break;
            }
            case "message_delta":
            {
                var events = new List<StreamEvent>();
                var usageMap = JsonValues.MapAt(value, "usage");
                if (usageMap != null && usageMap.Count > 0)
                {
                    events.Add(new StreamEvent { Kind = "usage", Usage = Response.DecodeAnthropicUsage(usageMap) });
                }
                var stop = JsonValues.StringAt(value, "delta", "stop_reason");
                if (stop.Length > 0)
                {
                    if (IsStreamErrorFinish(stop))
                    {
                        events.Add(new StreamEvent
                        {
                            Kind = "error",
                            Error = "upstream Anthropic stream failed",
                            ErrorType = "upstream_error",
                        });
                        return events;
                    }
                    events.Add(new StreamEvent { Kind = "finish", Stop = stop });
                }
                return events;
            }
            case "message_stop":
                return [new StreamEvent { Kind = "done" }];
            case "error":
                return [new StreamEvent
                {
                    Kind = "error",
                    Error = JsonValues.FirstString(JsonValues.StringAt(value, "error", "message"), "upstream Anthropic stream error"),
                    ErrorType = JsonValues.FirstString(JsonValues.StringAt(value, "error", "type"), "upstream_error"),
                }];
        }
        return [];
    }

    public List<StreamEvent> ParseResponses(string eventName, JsonObject value)
    {
        var typeName = JsonValues.StringAt(value, "type");
        if (typeName.Length == 0)
        {
            typeName = eventName;
        }
        switch (typeName)
        {
            case "error":
                return [new StreamEvent
                {
                    Kind = "error",
                    Error = StreamErrorMessage(value, "upstream Responses stream error"),
                    ErrorType = StreamErrorType(value),
                }];
            case "response.created":
            {
                var response = JsonValues.MapAt(value, "response");
                return [new StreamEvent
                {
                    Kind = "start",
                    ResponseID = JsonValues.StringAt(response, "id"),
                    Model = JsonValues.StringAt(response, "model"),
                }];
            }
            case "response.output_text.delta":
                return [new StreamEvent { Kind = "text", Text = JsonValues.StringAt(value, "delta") }];
            case "response.reasoning_summary_text.delta":
                _responseReasoning[ResponseToolKey(value, null)] = true;
                return [new StreamEvent { Kind = "reasoning", Text = JsonValues.StringAt(value, "delta") }];
            case "response.output_item.added":
            {
                var item = JsonValues.MapAt(value, "item");
                if (JsonValues.StringAt(item, "type") == "function_call")
                {
                    var key = ResponseToolKey(value, item);
                    RememberTool(key, JsonValues.StringAt(item, "call_id"), JsonValues.StringAt(item, "name"));
                    if (_toolNames.TryGetValue(key, out var name) && name.Length > 0)
                    {
                        _tools[key] = true;
                        return [new StreamEvent
                        {
                            Kind = "tool_start",
                            ToolKey = key,
                            ToolID = _toolIds.TryGetValue(key, out var id) ? id : "",
                            ToolName = name,
                        }];
                    }
                }
                break;
            }
            case "response.function_call_arguments.delta":
            {
                var key = ResponseToolKey(value, null);
                _responseArgs[key] = true;
                return [new StreamEvent
                {
                    Kind = "tool_delta",
                    ToolKey = key,
                    ToolID = _toolIds.TryGetValue(key, out var id) ? id : "",
                    ToolName = _toolNames.TryGetValue(key, out var name) ? name : "",
                    Text = JsonValues.StringAt(value, "delta"),
                }];
            }
            case "response.function_call_arguments.done":
            {
                var key = ResponseToolKey(value, null);
                if (!_responseArgs.TryGetValue(key, out var seen) || !seen)
                {
                    _responseArgs[key] = true;
                    return [new StreamEvent
                    {
                        Kind = "tool_delta",
                        ToolKey = key,
                        ToolID = _toolIds.TryGetValue(key, out var id) ? id : "",
                        ToolName = _toolNames.TryGetValue(key, out var name) ? name : "",
                        Text = JsonValues.StringAt(value, "arguments"),
                    }];
                }
                break;
            }
            case "response.output_item.done":
            {
                var item = JsonValues.MapAt(value, "item");
                var itemType = JsonValues.StringAt(item, "type");
                if (itemType == "reasoning")
                {
                    var key = ResponseToolKey(value, item);
                    if (_responseReasoning.TryGetValue(key, out var streamed) && streamed)
                    {
                        return [];
                    }
                    var blocks = Content.DecodeResponsesReasoning(item);
                    if (blocks.Count > 0)
                    {
                        return [new StreamEvent
                        {
                            Kind = "reasoning",
                            Text = blocks[0].Text,
                            Encrypted = blocks[0].Encrypted,
                        }];
                    }
                    return [];
                }
                if (itemType == "function_call")
                {
                    var key = ResponseToolKey(value, item);
                    RememberTool(key, JsonValues.StringAt(item, "call_id"), JsonValues.StringAt(item, "name"));
                    var events = new List<StreamEvent>();
                    if (!_tools.TryGetValue(key, out var started) || !started)
                    {
                        _tools[key] = true;
                        events.Add(new StreamEvent
                        {
                            Kind = "tool_start",
                            ToolKey = key,
                            ToolID = _toolIds.TryGetValue(key, out var id) ? id : "",
                            ToolName = _toolNames.TryGetValue(key, out var name) ? name : "",
                        });
                    }
                    if (!_responseArgs.TryGetValue(key, out var args) || !args)
                    {
                        var arguments = JsonValues.StringAt(item, "arguments");
                        if (arguments.Length > 0)
                        {
                            events.Add(new StreamEvent { Kind = "tool_delta", ToolKey = key, Text = arguments });
                        }
                    }
                    return events;
                }
                break;
            }
            case "response.completed":
            case "response.incomplete":
            case "response.failed":
            case "response.done":
            {
                var response = JsonValues.MapAt(value, "response");
                var usageMap = JsonValues.MapAt(response, "usage");
                var hasUsage = usageMap != null && usageMap.Count > 0;
                if (typeName == "response.failed" || JsonValues.StringAt(response, "status") == "failed")
                {
                    var events = new List<StreamEvent>
                    {
                        new()
                        {
                            Kind = "error",
                            Error = JsonValues.FirstString(
                                JsonValues.StringAt(response, "error", "message"),
                                JsonValues.StringAt(value, "error", "message"),
                                "upstream Responses request failed"),
                            ErrorType = JsonValues.FirstString(JsonValues.StringAt(response, "error", "code"), "upstream_error"),
                        },
                    };
                    if (hasUsage)
                    {
                        events.Insert(0, new StreamEvent { Kind = "usage", Usage = Response.DecodeOpenAIUsage(usageMap) });
                    }
                    return events;
                }
                var stop = typeName == "response.incomplete"
                    ? Response.CanonicalResponsesIncomplete(JsonValues.StringAt(response, "incomplete_details", "reason"))
                    : ResponsesCompletedStop(response);
                var finished = new List<StreamEvent>();
                if (hasUsage)
                {
                    finished.Add(new StreamEvent { Kind = "usage", Usage = Response.DecodeOpenAIUsage(usageMap) });
                }
                finished.Add(new StreamEvent { Kind = "finish", Stop = stop });
                finished.Add(new StreamEvent { Kind = "done" });
                return finished;
            }
        }
        return [];
    }

    /// <summary>
    /// 决定已完成的 Responses 一轮如何向下游报告。
    /// completed 事件不携带停止原因，因此其输出中的 function_call 条目
    /// ——或此前在流上观察到的工具调用——是唯一
    /// 表明该轮在等待工具结果处结束的信号。
    /// </summary>
    private string ResponsesCompletedStop(JsonObject? response)
    {
        foreach (var raw in JsonValues.SliceAt(response, "output"))
        {
            if (JsonValues.StringAt(raw as JsonObject, "type") == "function_call")
            {
                return "tool_calls";
            }
        }
        return _toolOrder.Count > 0 ? "tool_calls" : "stop";
    }

    public void RememberTool(string key, string id, string name)
    {
        if (!_tools.ContainsKey(key))
        {
            _tools[key] = false;
            _toolOrder.Add(key);
        }
        if (id.Length > 0)
        {
            _toolIds[key] = id;
        }
        if (name.Length > 0)
        {
            _toolNames[key] = name;
        }
    }

    public static string StreamErrorMessage(JsonObject value, string fallback)
    {
        var message = JsonValues.StringAt(value, "error", "message");
        if (message.Length > 0)
        {
            return message;
        }
        message = JsonValues.StringAt(value, "message");
        if (message.Length > 0)
        {
            return message;
        }
        if (Jsonx.Get(value, "error") is JsonValue error && error.GetValueKind() == JsonValueKind.String
            && error.GetValue<string>().Length > 0)
        {
            return error.GetValue<string>();
        }
        return fallback;
    }

    public static string StreamErrorType(JsonObject value) => JsonValues.FirstString(
        JsonValues.StringAt(value, "error", "type"),
        JsonValues.StringAt(value, "error", "code"),
        "upstream_error");

    public static bool IsStreamErrorFinish(string stop) =>
        stop.Trim().ToLowerInvariant() is "error" or "network_error" or "server_error";

    public static string MergeToolName(string current, string fragment)
    {
        if (current.Length == 0 || fragment == current)
        {
            return fragment;
        }
        if (fragment.StartsWith(current, StringComparison.Ordinal))
        {
            return fragment;
        }
        return current + fragment;
    }

    public static string ResponseToolKey(JsonObject? frame, JsonObject? item)
    {
        if (item != null)
        {
            var id = JsonValues.StringAt(item, "id");
            if (id.Length > 0)
            {
                return id;
            }
        }
        var itemId = JsonValues.StringAt(frame, "item_id");
        if (itemId.Length > 0)
        {
            return itemId;
        }
        return Sprint(Jsonx.Get(frame, "output_index"));
    }

    /// <summary>
    /// Go 的 fmt.Sprint 作用于解码后的 JSON 值：数字以其自然
    /// 形式输出，字符串原样通过。
    /// </summary>
    internal static string Sprint(JsonNode? value)
    {
        if (value == null)
        {
            return "";
        }
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            _ => value.ToString(),
        };
    }
}
