using System.Text;
using System.Text.Json.Nodes;
using Opencode2Api.Identity;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Protocol;

/// <summary>
/// 在向下游发出的过程中累积单个工具调用
/// （protocol.bridgeStreamTool）。
/// </summary>
internal sealed class StreamTool(string key)
{
    public string Key { get; } = key;
    public string ID { get; set; } = RequestIds.RandomId("call", 12);
    public string ItemID { get; set; } = RequestIds.RandomId("fc", 12);
    public string Name { get; set; } = "";
    public StringBuilder Arguments { get; } = new();
    public int Index { get; set; }
    public int AnthropicIndex { get; set; }
    public bool Started { get; set; }
    public int EmittedArguments { get; set; }
}

/// <summary>
/// 按目标协议把桥流事件以 SSE 写出
/// （internal/protocol/stream_emitter.go）。
/// </summary>
internal sealed class StreamEmitter(Stream output, string target, string model, CancellationToken ct)
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private readonly StringBuilder _reasoningSignature = new();
    private readonly Dictionary<string, StreamTool> _tools = [];
    private readonly List<string> _order = [];

    private int _sequence;
    private int _nextOutput;
    private bool _textOpen;
    private bool _reasoningOpen;
    private bool _reasoningClosed;
    private string _reasoningItemId = "";
    private int _reasoningOutput;
    private int _reasoningIndex;
    private int _textIndex;
    private int _nextAnthropic;
    private string _textItemId = "";
    private int _textOutput;

    public Usage Usage { get; private set; } = new();
    public bool UsageReported { get; private set; }
    public string Model { get; private set; } = model;
    public string ID { get; private set; } = "";
    public long Created { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public bool Started { get; private set; }
    public bool Done { get; private set; }
    public string Stop { get; private set; } = "";
    public string ReasoningEncrypted { get; private set; } = "";

    /// <summary>
    /// 应用一个桥事件。一旦错误已带内投递，就抛出
    /// StreamUpstreamFailureException，写入错误像 Go 中那样向外传播。
    /// </summary>
    public async Task EmitAsync(StreamEvent ev)
    {
        if (ev.ResponseID.Length > 0 && ID.Length == 0)
        {
            ID = ev.ResponseID;
        }
        if (ev.Model.Length > 0)
        {
            Model = ev.Model;
        }
        if (!Started && ev.Kind is "start" or "reasoning" or "reasoning_signature" or "text" or "tool_start" or "tool_delta")
        {
            await StartAsync();
        }
        switch (ev.Kind)
        {
            case "reasoning":
            {
                var text = ev.Text;
                if (ev.Encrypted.Length > 0)
                {
                    ReasoningEncrypted = ev.Encrypted;
                    if (target != Wire.Responses && text.Length == 0)
                    {
                        text = Bridge.AnthropicRedactedThinkingPlaceholder;
                    }
                    if (target == Wire.Responses && text.Length == 0)
                    {
                        await StartReasoningAsync();
                        return;
                    }
                }
                if (text.Length == 0)
                {
                    return;
                }
                _reasoning.Append(text);
                await EmitReasoningAsync(text);
                return;
            }
            case "reasoning_signature":
                if (ev.Signature.Length == 0)
                {
                    return;
                }
                _reasoningSignature.Append(ev.Signature);
                await EmitReasoningSignatureAsync(ev.Signature);
                return;
            case "text":
                if (ev.Text.Length == 0)
                {
                    return;
                }
                _text.Append(ev.Text);
                await EmitTextAsync(ev.Text);
                return;
            case "tool_start":
            {
                var tool = Tool(ev.ToolKey);
                if (ev.ToolID.Length > 0)
                {
                    tool.ID = ev.ToolID;
                }
                if (ev.ToolName.Length > 0)
                {
                    tool.Name = ev.ToolName;
                }
                await StartToolAsync(tool);
                await EmitPendingToolArgumentsAsync(tool);
                return;
            }
            case "tool_delta":
            {
                var tool = Tool(ev.ToolKey);
                if (ev.ToolID.Length > 0)
                {
                    tool.ID = ev.ToolID;
                }
                if (ev.ToolName.Length > 0)
                {
                    tool.Name = ev.ToolName;
                }
                tool.Arguments.Append(ev.Text);
                await StartToolAsync(tool);
                await EmitPendingToolArgumentsAsync(tool);
                return;
            }
            case "usage":
                if (ev.Usage != null)
                {
                    UsageReported = true;
                    StreamUsage.Merge(Usage, ev.Usage);
                }
                return;
            case "finish":
                Stop = ev.Stop;
                return;
            case "error":
                Done = true;
                await EmitErrorAsync(
                    JsonValues.FirstString(ev.Error, "upstream stream failed"),
                    JsonValues.FirstString(ev.ErrorType, "upstream_error"));
                throw new StreamUpstreamFailureException();
            case "done":
                await FinishAsync();
                return;
        }
    }

    private async Task EmitErrorAsync(string message, string errorType)
    {
        switch (target)
        {
            case Wire.Anthropic:
                await SseAsync("error", new JsonObject
                {
                    ["type"] = "error",
                    ["error"] = new JsonObject { ["type"] = errorType, ["message"] = message },
                });
                return;
            case Wire.Responses:
            {
                var response = ResponsesBase("failed");
                response["error"] = new JsonObject { ["code"] = errorType, ["message"] = message };
                await SseAsync("response.failed", new JsonObject
                {
                    ["type"] = "response.failed",
                    ["response"] = response,
                    ["sequence_number"] = NextSequence(),
                });
                return;
            }
            default:
                await SseAsync("", new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["message"] = message,
                        ["type"] = errorType,
                        ["param"] = null,
                        ["code"] = null,
                    },
                });
                await RawSseAsync("", "[DONE]");
                return;
        }
    }

    private StreamTool Tool(string key)
    {
        if (key.Length == 0)
        {
            key = $"tool-{_order.Count}";
        }
        if (_tools.TryGetValue(key, out var current))
        {
            return current;
        }
        var tool = new StreamTool(key) { Index = _order.Count };
        _tools[key] = tool;
        _order.Add(key);
        return tool;
    }

    /// <summary>
    /// 报告是否至少有一个工具块真正向下游开启了。
    /// 卡在缺少名称状态的工具从不开始；它们的停止承诺不得
    /// 被宣称为工具停止。
    /// </summary>
    private bool AnyToolStarted()
    {
        foreach (var key in _order)
        {
            if (_tools.TryGetValue(key, out var tool) && tool.Started)
            {
                return true;
            }
        }
        return false;
    }

    private async Task StartAsync()
    {
        if (Started)
        {
            return;
        }
        Started = true;
        if (ID.Length == 0)
        {
            ID = RequestIds.RandomId("resp", 12);
        }
        switch (target)
        {
            case Wire.Chat:
                await SseAsync("", new JsonObject
                {
                    ["id"] = Bridge.AsPrefix(ID, "chatcmpl"),
                    ["object"] = "chat.completion.chunk",
                    ["created"] = Created,
                    ["model"] = Model,
                    ["choices"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = 0,
                            ["delta"] = new JsonObject { ["role"] = "assistant", ["content"] = "" },
                            ["finish_reason"] = null,
                        },
                    },
                });
                return;
            case Wire.Anthropic:
                await SseAsync("message_start", new JsonObject
                {
                    ["type"] = "message_start",
                    ["message"] = new JsonObject
                    {
                        ["id"] = Bridge.AsPrefix(ID, "msg"),
                        ["type"] = "message",
                        ["role"] = "assistant",
                        ["model"] = Model,
                        ["content"] = new JsonArray(),
                        ["stop_reason"] = null,
                        ["stop_sequence"] = null,
                        ["usage"] = Response.AnthropicUsage(new Usage()),
                    },
                });
                return;
            case Wire.Responses:
            {
                var response = ResponsesBase("in_progress");
                await SseAsync("response.created", new JsonObject
                {
                    ["type"] = "response.created",
                    ["response"] = response,
                    ["sequence_number"] = NextSequence(),
                });
                await SseAsync("response.in_progress", new JsonObject
                {
                    ["type"] = "response.in_progress",
                    ["response"] = response,
                    ["sequence_number"] = NextSequence(),
                });
                return;
            }
            default:
                throw new ProtocolException($"unsupported stream target \"{target}\"");
        }
    }

    private async Task EmitTextAsync(string delta)
    {
        if (target is Wire.Anthropic or Wire.Responses)
        {
            await FinishReasoningAsync();
        }
        switch (target)
        {
            case Wire.Chat:
                await ChatChunkAsync(new JsonObject { ["content"] = delta });
                return;
            case Wire.Anthropic:
                if (!_textOpen)
                {
                    _textOpen = true;
                    _textIndex = _nextAnthropic++;
                    await SseAsync("content_block_start", new JsonObject
                    {
                        ["type"] = "content_block_start",
                        ["index"] = _textIndex,
                        ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" },
                    });
                }
                await SseAsync("content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = _textIndex,
                    ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = delta },
                });
                return;
            case Wire.Responses:
                if (!_textOpen)
                {
                    _textOpen = true;
                    _textItemId = RequestIds.RandomId("msg", 12);
                    _textOutput = _nextOutput++;
                    await SseAsync("response.output_item.added", new JsonObject
                    {
                        ["type"] = "response.output_item.added",
                        ["output_index"] = _textOutput,
                        ["item"] = new JsonObject
                        {
                            ["id"] = _textItemId,
                            ["type"] = "message",
                            ["status"] = "in_progress",
                            ["role"] = "assistant",
                            ["content"] = new JsonArray(),
                        },
                        ["sequence_number"] = NextSequence(),
                    });
                    await SseAsync("response.content_part.added", new JsonObject
                    {
                        ["type"] = "response.content_part.added",
                        ["item_id"] = _textItemId,
                        ["output_index"] = _textOutput,
                        ["content_index"] = 0,
                        ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() },
                        ["sequence_number"] = NextSequence(),
                    });
                }
                await SseAsync("response.output_text.delta", new JsonObject
                {
                    ["type"] = "response.output_text.delta",
                    ["delta"] = delta,
                    ["item_id"] = _textItemId,
                    ["output_index"] = _textOutput,
                    ["content_index"] = 0,
                    ["sequence_number"] = NextSequence(),
                });
                return;
        }
    }

    private async Task EmitReasoningAsync(string delta)
    {
        await StartReasoningAsync();
        switch (target)
        {
            case Wire.Chat:
                await ChatChunkAsync(new JsonObject { ["reasoning_content"] = delta });
                return;
            case Wire.Anthropic:
                await SseAsync("content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = _reasoningIndex,
                    ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = delta },
                });
                return;
            case Wire.Responses:
                await SseAsync("response.reasoning_summary_text.delta", new JsonObject
                {
                    ["type"] = "response.reasoning_summary_text.delta",
                    ["delta"] = delta,
                    ["item_id"] = _reasoningItemId,
                    ["output_index"] = _reasoningOutput,
                    ["summary_index"] = 0,
                    ["sequence_number"] = NextSequence(),
                });
                return;
        }
    }

    private async Task EmitReasoningSignatureAsync(string signature)
    {
        await StartReasoningAsync();
        switch (target)
        {
            case Wire.Chat:
                await ChatChunkAsync(new JsonObject { ["reasoning_signature"] = signature });
                return;
            case Wire.Anthropic:
                await SseAsync("content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = _reasoningIndex,
                    ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = signature },
                });
                return;
        }
    }

    private async Task StartReasoningAsync()
    {
        if (_reasoningOpen || _reasoningClosed)
        {
            return;
        }
        _reasoningOpen = true;
        switch (target)
        {
            case Wire.Anthropic:
                _reasoningIndex = _nextAnthropic++;
                await SseAsync("content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = _reasoningIndex,
                    ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" },
                });
                return;
            case Wire.Responses:
                _reasoningItemId = RequestIds.RandomId("rs", 12);
                _reasoningOutput = _nextOutput++;
                await SseAsync("response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added",
                    ["output_index"] = _reasoningOutput,
                    ["item"] = new JsonObject
                    {
                        ["id"] = _reasoningItemId,
                        ["type"] = "reasoning",
                        ["status"] = "in_progress",
                        ["summary"] = new JsonArray(),
                    },
                    ["sequence_number"] = NextSequence(),
                });
                await SseAsync("response.reasoning_summary_part.added", new JsonObject
                {
                    ["type"] = "response.reasoning_summary_part.added",
                    ["item_id"] = _reasoningItemId,
                    ["output_index"] = _reasoningOutput,
                    ["summary_index"] = 0,
                    ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = "" },
                    ["sequence_number"] = NextSequence(),
                });
                return;
        }
    }

    private async Task FinishReasoningAsync()
    {
        if (!_reasoningOpen || _reasoningClosed)
        {
            return;
        }
        _reasoningClosed = true;
        var text = _reasoning.ToString();
        switch (target)
        {
            case Wire.Anthropic:
                await SseAsync("content_block_stop", new JsonObject
                {
                    ["type"] = "content_block_stop",
                    ["index"] = _reasoningIndex,
                });
                return;
            case Wire.Responses:
                await SseAsync("response.reasoning_summary_text.done", new JsonObject
                {
                    ["type"] = "response.reasoning_summary_text.done",
                    ["text"] = text,
                    ["item_id"] = _reasoningItemId,
                    ["output_index"] = _reasoningOutput,
                    ["summary_index"] = 0,
                    ["sequence_number"] = NextSequence(),
                });
                var part = new JsonObject { ["type"] = "summary_text", ["text"] = text };
                await SseAsync("response.reasoning_summary_part.done", new JsonObject
                {
                    ["type"] = "response.reasoning_summary_part.done",
                    ["item_id"] = _reasoningItemId,
                    ["output_index"] = _reasoningOutput,
                    ["summary_index"] = 0,
                    ["part"] = part,
                    ["sequence_number"] = NextSequence(),
                });
                var item = new JsonObject
                {
                    ["id"] = _reasoningItemId,
                    ["type"] = "reasoning",
                    ["status"] = "completed",
                    ["summary"] = new JsonArray { part },
                };
                if (ReasoningEncrypted.Length > 0)
                {
                    item["encrypted_content"] = ReasoningEncrypted;
                }
                await SseAsync("response.output_item.done", new JsonObject
                {
                    ["type"] = "response.output_item.done",
                    ["output_index"] = _reasoningOutput,
                    ["item"] = item,
                    ["sequence_number"] = NextSequence(),
                });
                return;
        }
    }

    private async Task StartToolAsync(StreamTool tool)
    {
        if (tool.Started || tool.Name.Length == 0)
        {
            return;
        }
        if (target is Wire.Responses or Wire.Anthropic)
        {
            await FinishReasoningAsync();
        }
        if (target == Wire.Anthropic && _textOpen)
        {
            await SseAsync("content_block_stop", new JsonObject
            {
                ["type"] = "content_block_stop",
                ["index"] = _textIndex,
            });
            _textOpen = false;
        }
        tool.Started = true;
        switch (target)
        {
            case Wire.Chat:
                await ChatChunkAsync(new JsonObject
                {
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = tool.Index,
                            ["id"] = tool.ID,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = tool.Name,
                                ["arguments"] = "",
                            },
                        },
                    },
                });
                return;
            case Wire.Responses:
                tool.Index = _nextOutput++;
                await SseAsync("response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added",
                    ["output_index"] = tool.Index,
                    ["item"] = new JsonObject
                    {
                        ["id"] = tool.ItemID,
                        ["type"] = "function_call",
                        ["status"] = "in_progress",
                        ["arguments"] = "",
                        ["call_id"] = tool.ID,
                        ["name"] = tool.Name,
                    },
                    ["sequence_number"] = NextSequence(),
                });
                return;
            case Wire.Anthropic:
                tool.AnthropicIndex = _nextAnthropic++;
                await SseAsync("content_block_start", new JsonObject
                {
                    ["type"] = "content_block_start",
                    ["index"] = tool.AnthropicIndex,
                    ["content_block"] = new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = tool.ID,
                        ["name"] = tool.Name,
                        ["input"] = new JsonObject(),
                    },
                });
                return;
        }
    }

    private async Task EmitToolDeltaAsync(StreamTool tool, string delta)
    {
        if (delta.Length == 0)
        {
            return;
        }
        switch (target)
        {
            case Wire.Chat:
                await ChatChunkAsync(new JsonObject
                {
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = tool.Index,
                            ["function"] = new JsonObject { ["arguments"] = delta },
                        },
                    },
                });
                return;
            case Wire.Responses:
                await SseAsync("response.function_call_arguments.delta", new JsonObject
                {
                    ["type"] = "response.function_call_arguments.delta",
                    ["delta"] = delta,
                    ["item_id"] = tool.ItemID,
                    ["output_index"] = tool.Index,
                    ["sequence_number"] = NextSequence(),
                });
                return;
            case Wire.Anthropic:
                await SseAsync("content_block_delta", new JsonObject
                {
                    ["type"] = "content_block_delta",
                    ["index"] = tool.AnthropicIndex,
                    ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = delta },
                });
                return;
        }
    }

    private async Task EmitPendingToolArgumentsAsync(StreamTool tool)
    {
        if (!tool.Started)
        {
            return;
        }
        var arguments = tool.Arguments.ToString();
        if (tool.EmittedArguments >= arguments.Length)
        {
            return;
        }
        var delta = arguments[tool.EmittedArguments..];
        tool.EmittedArguments = arguments.Length;
        await EmitToolDeltaAsync(tool, delta);
    }

    public async Task FinishAsync()
    {
        if (Done)
        {
            return;
        }
        Done = true;
        if (!Started)
        {
            await StartAsync();
        }
        if (Stop.Length == 0)
        {
            Stop = _order.Count > 0 ? "tool_calls" : "stop";
        }
        if (Response.IsToolStop(Stop) && !AnyToolStarted())
        {
            // 与非流式编码器相同的幽灵工具调用防护：
            // 带有 tool_use 停止原因却没有任何 tool_use 内容块，会让
            // 客户端这一轮静默结束。
            Stop = "stop";
        }
        await FinishReasoningAsync();
        switch (target)
        {
            case Wire.Chat:
                await ChatChunkAsync(new JsonObject(), Response.ChatStop(Stop));
                await SseAsync("", new JsonObject
                {
                    ["id"] = Bridge.AsPrefix(ID, "chatcmpl"),
                    ["object"] = "chat.completion.chunk",
                    ["created"] = Created,
                    ["model"] = Model,
                    ["choices"] = new JsonArray(),
                    ["usage"] = Response.OpenAIUsage(Usage),
                });
                await RawSseAsync("", "[DONE]");
                return;
            case Wire.Anthropic:
                if (_textOpen)
                {
                    await SseAsync("content_block_stop", new JsonObject
                    {
                        ["type"] = "content_block_stop",
                        ["index"] = _textIndex,
                    });
                }
                foreach (var key in _order)
                {
                    var tool = _tools[key];
                    await StartToolAsync(tool);
                    await EmitPendingToolArgumentsAsync(tool);
                    if (tool.Started)
                    {
                        await SseAsync("content_block_stop", new JsonObject
                        {
                            ["type"] = "content_block_stop",
                            ["index"] = tool.AnthropicIndex,
                        });
                    }
                }
                await SseAsync("message_delta", new JsonObject
                {
                    ["type"] = "message_delta",
                    ["delta"] = new JsonObject
                    {
                        ["stop_reason"] = Response.AnthropicStop(Stop),
                        ["stop_sequence"] = null,
                    },
                    ["usage"] = Response.AnthropicUsage(Usage),
                });
                await SseAsync("message_stop", new JsonObject { ["type"] = "message_stop" });
                return;
            case Wire.Responses:
                await FinishResponsesItemsAsync();
                var response = new BridgeResponse
                {
                    ID = ID,
                    Model = Model,
                    Text = _text.ToString(),
                    Stop = Stop,
                    Usage = Usage,
                    Created = Created,
                };
                if (_reasoning.Length > 0 || _reasoningSignature.Length > 0)
                {
                    response.Reasoning =
                    [
                        new BridgeBlock
                        {
                            Kind = "reasoning",
                            ID = _reasoningItemId,
                            Text = _reasoning.ToString(),
                            Signature = _reasoningSignature.ToString(),
                        },
                    ];
                }
                foreach (var key in _order)
                {
                    var tool = _tools[key];
                    response.Tools.Add(new BridgeBlock
                    {
                        Kind = "tool_call",
                        ID = tool.ID,
                        Name = tool.Name,
                        ArgumentsJSON = tool.Arguments.ToString(),
                    });
                }
                await SseAsync("response.completed", new JsonObject
                {
                    ["type"] = "response.completed",
                    ["response"] = Response.Encode(Wire.Responses, response),
                    ["sequence_number"] = NextSequence(),
                });
                return;
        }
    }

    private async Task FinishResponsesItemsAsync()
    {
        if (_textOpen)
        {
            var text = _text.ToString();
            await SseAsync("response.output_text.done", new JsonObject
            {
                ["type"] = "response.output_text.done",
                ["text"] = text,
                ["item_id"] = _textItemId,
                ["output_index"] = _textOutput,
                ["content_index"] = 0,
                ["sequence_number"] = NextSequence(),
            });
            var part = new JsonObject
            {
                ["type"] = "output_text",
                ["text"] = text,
                ["annotations"] = new JsonArray(),
            };
            await SseAsync("response.content_part.done", new JsonObject
            {
                ["type"] = "response.content_part.done",
                ["item_id"] = _textItemId,
                ["output_index"] = _textOutput,
                ["content_index"] = 0,
                ["part"] = part,
                ["sequence_number"] = NextSequence(),
            });
            await SseAsync("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = _textOutput,
                ["item"] = new JsonObject
                {
                    ["id"] = _textItemId,
                    ["type"] = "message",
                    ["status"] = "completed",
                    ["role"] = "assistant",
                    ["content"] = new JsonArray { part.DeepClone() },
                },
                ["sequence_number"] = NextSequence(),
            });
        }
        foreach (var key in _order)
        {
            var tool = _tools[key];
            var arguments = tool.Arguments.ToString();
            if (arguments.Length == 0)
            {
                arguments = "{}";
            }
            await StartToolAsync(tool);
            await EmitPendingToolArgumentsAsync(tool);
            await SseAsync("response.function_call_arguments.done", new JsonObject
            {
                ["type"] = "response.function_call_arguments.done",
                ["arguments"] = arguments,
                ["item_id"] = tool.ItemID,
                ["output_index"] = tool.Index,
                ["sequence_number"] = NextSequence(),
            });
            await SseAsync("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = tool.Index,
                ["item"] = new JsonObject
                {
                    ["id"] = tool.ItemID,
                    ["type"] = "function_call",
                    ["status"] = "completed",
                    ["arguments"] = arguments,
                    ["call_id"] = tool.ID,
                    ["name"] = tool.Name,
                },
                ["sequence_number"] = NextSequence(),
            });
        }
    }

    private async Task ChatChunkAsync(JsonObject delta, string? finish = null)
    {
        await SseAsync("", new JsonObject
        {
            ["id"] = Bridge.AsPrefix(ID, "chatcmpl"),
            ["object"] = "chat.completion.chunk",
            ["created"] = Created,
            ["model"] = Model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finish,
                },
            },
        });
    }

    private JsonObject ResponsesBase(string status) => new()
    {
        ["id"] = Bridge.AsPrefix(ID, "resp"),
        ["object"] = "response",
        ["created_at"] = Created,
        ["status"] = status,
        ["model"] = Model,
        ["output"] = new JsonArray(),
        ["error"] = null,
    };

    private int NextSequence()
    {
        var sequence = _sequence;
        _sequence++;
        return sequence;
    }

    private async Task SseAsync(string eventName, JsonNode value) =>
        await RawSseAsync(eventName, value.ToJsonString());

    private async Task RawSseAsync(string eventName, string data)
    {
        var payload = eventName.Length > 0 ? $"event: {eventName}\ndata: {data}\n\n" : $"data: {data}\n\n";
        try
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes(payload), ct);
            await output.FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new ClientWriteException(error);
        }
    }
}
