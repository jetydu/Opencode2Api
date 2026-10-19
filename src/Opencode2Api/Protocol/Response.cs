using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.Identity;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Protocol;

/// <summary>
/// 非流式响应桥接（internal/protocol/response.go）：把上游
/// 请求体解码为桥形态并为客户端协议编码，
/// 以及与流桥共享的用量与停止原因词汇表。
/// </summary>
internal static class Response
{
    /// <summary>
    /// 把已序列化的上游请求体转换为客户端协议。同协议的
    /// 请求体原样拷贝，使提供方专属字段得以保留。
    /// </summary>
    public static byte[] Convert(string from, string to, byte[] body)
    {
        if (from == to)
        {
            return (byte[])body.Clone();
        }
        var response = Decode(from, Parse(body));
        if (response.Error.Length > 0 || response.Stop == "error")
        {
            throw new ProtocolException(
                $"upstream response failed: {JsonValues.FirstString(response.Error, "upstream response failed")}");
        }
        return ToUtf8(Encode(to, response));
    }

    public static JsonObject Parse(byte[] body) =>
        JsonSerializer.Deserialize<JsonObject>(body)
            ?? throw new ProtocolException("upstream response is not a JSON object");

    public static byte[] ToUtf8(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    public static BridgeResponse Decode(string protocol, JsonObject input)
    {
        var response = new BridgeResponse
        {
            ID = JsonValues.StringAt(input, "id"),
            Model = JsonValues.StringAt(input, "model"),
            Created = JsonValues.Int64At(input, "created"),
        };
        if (response.Created == 0)
        {
            response.Created = JsonValues.Int64At(input, "created_at");
        }
        if (response.Created == 0)
        {
            response.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        switch (protocol)
        {
            case Wire.Chat:
                DecodeChat(input, response);
                break;
            case Wire.Responses:
                DecodeResponses(input, response);
                break;
            case Wire.Anthropic:
                DecodeAnthropic(input, response);
                break;
            default:
                throw new ProtocolException($"unsupported response protocol \"{protocol}\"");
        }
        return response;
    }

    private static void DecodeChat(JsonObject input, BridgeResponse response)
    {
        response.Error = JsonValues.StringAt(input, "error", "message");
        if (response.Error.Length > 0)
        {
            return;
        }
        var choices = JsonValues.SliceAt(input, "choices");
        if (choices.Count == 0)
        {
            throw new ProtocolException("chat response contains no choices");
        }
        var choice = choices[0] as JsonObject;
        var message = JsonValues.MapAt(choice, "message");
        response.Reasoning = Content.DecodeChatReasoning(message);
        var blocks = Content.DecodeOpenAIBlocksChecked(message?["content"]);
        response.Text = ResponseTextBlocks(blocks, "Chat");
        foreach (var raw in JsonValues.SliceAt(message, "tool_calls"))
        {
            var call = raw as JsonObject;
            var function = JsonValues.MapAt(call, "function");
            response.Tools.Add(new BridgeBlock
            {
                Kind = "tool_call",
                ID = JsonValues.StringAt(call, "id"),
                Name = JsonValues.StringAt(function, "name"),
                ArgumentsJSON = JsonValues.StringAt(function, "arguments"),
            });
        }
        response.Stop = CanonicalChatStop(JsonValues.StringAt(choice, "finish_reason"));
        response.Usage = DecodeOpenAIUsage(JsonValues.MapAt(input, "usage"));
    }

    private static void DecodeResponses(JsonObject input, BridgeResponse response)
    {
        foreach (var raw in JsonValues.SliceAt(input, "output"))
        {
            var item = raw as JsonObject;
            switch (JsonValues.StringAt(item, "type"))
            {
                case "reasoning":
                    response.Reasoning.AddRange(Content.DecodeResponsesReasoning(item));
                    break;
                case "message":
                {
                    var blocks = Content.DecodeOpenAIBlocksChecked(item?["content"]);
                    response.Text += ResponseTextBlocks(blocks, "Responses");
                    break;
                }
                case "function_call":
                    response.Tools.Add(new BridgeBlock
                    {
                        Kind = "tool_call",
                        ID = JsonValues.FirstString(JsonValues.StringAt(item, "call_id"), JsonValues.StringAt(item, "id")),
                        Name = JsonValues.StringAt(item, "name"),
                        ArgumentsJSON = JsonValues.StringAt(item, "arguments"),
                    });
                    break;
                default:
                    throw new ProtocolException(
                        $"Responses response has unsupported output item type \"{JsonValues.StringAt(item, "type")}\"");
            }
        }
        response.Stop = "stop";
        if (response.Tools.Count > 0)
        {
            response.Stop = "tool_calls";
        }
        var status = JsonValues.StringAt(input, "status");
        if (status == "incomplete")
        {
            response.Stop = CanonicalResponsesIncomplete(JsonValues.StringAt(input, "incomplete_details", "reason"));
        }
        else if (status == "failed")
        {
            response.Stop = "error";
            response.Error = JsonValues.FirstString(
                JsonValues.StringAt(input, "error", "message"), "upstream Responses request failed");
        }
        response.Usage = DecodeOpenAIUsage(JsonValues.MapAt(input, "usage"));
    }

    private static void DecodeAnthropic(JsonObject input, BridgeResponse response)
    {
        if (JsonValues.StringAt(input, "type") == "error")
        {
            response.Error = JsonValues.FirstString(
                JsonValues.StringAt(input, "error", "message"), "upstream Anthropic request failed");
        }
        foreach (var block in Content.DecodeAnthropicBlocksChecked(input["content"]))
        {
            switch (block.Kind)
            {
                case "reasoning":
                    response.Reasoning.Add(block);
                    break;
                case "text":
                    response.Text += block.Text;
                    break;
                case "tool_call":
                    response.Tools.Add(block);
                    break;
                default:
                    throw new ProtocolException($"Anthropic response contains unsupported {block.Kind} content block");
            }
        }
        response.Stop = CanonicalAnthropicStop(JsonValues.StringAt(input, "stop_reason"));
        response.Usage = DecodeAnthropicUsage(JsonValues.MapAt(input, "usage"));
    }

    /// <summary>
    /// 为客户端协议编码桥响应。桥响应
    /// 被视为只读：对幽灵工具停止的修补发生在本地
    /// 副本上，因此复用该值的调用方看到的仍是解码后的形态。
    /// </summary>
    public static JsonObject Encode(string protocol, BridgeResponse value)
    {
        var response = new BridgeResponse
        {
            ID = value.ID.Length > 0 ? value.ID : RequestIds.RandomId("resp", 12),
            Model = value.Model,
            Text = value.Text,
            Reasoning = value.Reasoning,
            Stop = value.Stop,
            Error = value.Error,
            Usage = value.Usage,
            Created = value.Created,
            Tools = value.Tools,
        };
        if (IsToolStop(response.Stop))
        {
            response.Tools = UsableToolBlocks(response.Tools);
            if (response.Tools.Count == 0)
            {
                // 上游把这一轮以工具调用结束，但从未组装出可用的
                // 工具块（例如缺少名称）。
                // 带有 tool_use 停止原因却没有可用工具块，会让严格的
                // 客户端结束本轮、什么都不执行也不报错，因此
                // 降级为普通 stop，让客户端把它当作文本。
                response.Stop = "stop";
            }
        }
        switch (protocol)
        {
            case Wire.Chat:
            {
                var message = new JsonObject { ["role"] = "assistant", ["content"] = response.Text };
                var (reasoningText, hasReasoning) = Content.BridgeReasoningText(response.Reasoning);
                if (hasReasoning)
                {
                    message["reasoning_content"] = reasoningText;
                }
                var signature = Content.BridgeReasoningSignature(response.Reasoning);
                if (signature.Length > 0)
                {
                    message["reasoning_signature"] = signature;
                }
                if (response.Tools.Count > 0)
                {
                    var calls = new JsonArray();
                    foreach (var tool in response.Tools)
                    {
                        calls.Add(new JsonObject
                        {
                            ["id"] = tool.ID,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = tool.Name,
                                ["arguments"] = Bridge.BridgeArgumentsJSON(tool),
                            },
                        });
                    }
                    message["tool_calls"] = calls;
                }
                return new JsonObject
                {
                    ["id"] = Bridge.AsPrefix(response.ID, "chatcmpl"),
                    ["object"] = "chat.completion",
                    ["created"] = response.Created,
                    ["model"] = response.Model,
                    ["choices"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = 0,
                            ["message"] = message,
                            ["finish_reason"] = ChatStop(response.Stop),
                        },
                    },
                    ["usage"] = OpenAIUsage(response.Usage),
                };
            }
            case Wire.Responses:
            {
                var output = new JsonArray();
                foreach (var reasoning in response.Reasoning)
                {
                    output.Add(Content.EncodeResponsesReasoning(reasoning, true));
                }
                if (response.Text.Length > 0)
                {
                    output.Add(new JsonObject
                    {
                        ["id"] = RequestIds.RandomId("msg", 12),
                        ["type"] = "message",
                        ["status"] = "completed",
                        ["role"] = "assistant",
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "output_text",
                                ["text"] = response.Text,
                                ["annotations"] = new JsonArray(),
                            },
                        },
                    });
                }
                foreach (var tool in response.Tools)
                {
                    output.Add(new JsonObject
                    {
                        ["id"] = RequestIds.RandomId("fc", 12),
                        ["type"] = "function_call",
                        ["status"] = "completed",
                        ["call_id"] = tool.ID,
                        ["name"] = tool.Name,
                        ["arguments"] = Bridge.BridgeArgumentsJSON(tool),
                    });
                }
                var status = "completed";
                JsonNode? incomplete = null;
                if (response.Stop is "length" or "content_filter")
                {
                    status = "incomplete";
                    incomplete = new JsonObject { ["reason"] = ResponsesIncompleteReason(response.Stop) };
                }
                else if (response.Stop == "error")
                {
                    status = "failed";
                }
                return new JsonObject
                {
                    ["id"] = Bridge.AsPrefix(response.ID, "resp"),
                    ["object"] = "response",
                    ["created_at"] = response.Created,
                    ["status"] = status,
                    ["model"] = response.Model,
                    ["output"] = output,
                    ["error"] = null,
                    ["incomplete_details"] = incomplete,
                    ["usage"] = new JsonObject
                    {
                        ["input_tokens"] = response.Usage.Input,
                        ["output_tokens"] = response.Usage.Output,
                        ["total_tokens"] = response.Usage.Total,
                        ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = response.Usage.Cached },
                        ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = response.Usage.Reasoning },
                    },
                };
            }
            case Wire.Anthropic:
            {
                var content = new JsonArray();
                foreach (var block in Content.EncodeAnthropicBlocks(response.Reasoning))
                {
                    content.Add(block!.DeepClone());
                }
                if (response.Text.Length > 0)
                {
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = response.Text });
                }
                foreach (var tool in response.Tools)
                {
                    var input = tool.Arguments ?? JsonValues.ParseJsonOrString(tool.ArgumentsJSON);
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = tool.ID,
                        ["name"] = tool.Name,
                        ["input"] = input?.DeepClone(),
                    });
                }
                return new JsonObject
                {
                    ["id"] = Bridge.AsPrefix(response.ID, "msg"),
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = response.Model,
                    ["content"] = content,
                    ["stop_reason"] = AnthropicStop(response.Stop),
                    ["stop_sequence"] = null,
                    ["usage"] = AnthropicUsage(response.Usage),
                };
            }
            default:
                return new JsonObject();
        }
    }

    public static Usage DecodeOpenAIUsage(JsonObject? usage)
    {
        var input = JsonValues.FirstNonZero(JsonValues.IntAt(usage, "prompt_tokens"), JsonValues.IntAt(usage, "input_tokens"));
        var output = JsonValues.FirstNonZero(JsonValues.IntAt(usage, "completion_tokens"), JsonValues.IntAt(usage, "output_tokens"));
        var total = JsonValues.IntAt(usage, "total_tokens");
        if (total == 0)
        {
            total = input + output;
        }
        return new Usage
        {
            Input = input,
            Output = output,
            Total = total,
            Cached = JsonValues.FirstNonZero(
                JsonValues.IntAt(usage, "prompt_tokens_details", "cached_tokens"),
                JsonValues.IntAt(usage, "input_tokens_details", "cached_tokens")),
            CacheCreation = JsonValues.FirstNonZero(
                JsonValues.IntAt(usage, "cache_creation_input_tokens"),
                JsonValues.IntAt(usage, "prompt_tokens_details", "cache_creation_input_tokens"),
                JsonValues.IntAt(usage, "prompt_tokens_details", "cache_write_tokens"),
                JsonValues.IntAt(usage, "input_tokens_details", "cache_creation_input_tokens")),
            Reasoning = JsonValues.FirstNonZero(
                JsonValues.IntAt(usage, "completion_tokens_details", "reasoning_tokens"),
                JsonValues.IntAt(usage, "output_tokens_details", "reasoning_tokens")),
        };
    }

    public static Usage DecodeAnthropicUsage(JsonObject? usage)
    {
        var input = JsonValues.IntAt(usage, "input_tokens");
        var cached = JsonValues.IntAt(usage, "cache_read_input_tokens");
        var cacheCreation = JsonValues.IntAt(usage, "cache_creation_input_tokens");
        var output = JsonValues.IntAt(usage, "output_tokens");
        input += cached + cacheCreation;
        return new Usage
        {
            Input = input,
            Output = output,
            Total = input + output,
            Cached = cached,
            CacheCreation = cacheCreation,
        };
    }

    public static JsonObject OpenAIUsage(Usage usage) => new()
    {
        ["prompt_tokens"] = usage.Input,
        ["completion_tokens"] = usage.Output,
        ["total_tokens"] = usage.Total,
        ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = usage.Cached },
        ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = usage.Reasoning },
    };

    public static JsonObject AnthropicUsage(Usage usage) => new()
    {
        ["input_tokens"] = Math.Max(usage.Input - usage.Cached - usage.CacheCreation, 0),
        ["output_tokens"] = usage.Output,
        ["cache_creation_input_tokens"] = usage.CacheCreation,
        ["cache_read_input_tokens"] = usage.Cached,
    };

    public static string ChatStop(string stop) => stop switch
    {
        "tool_use" or "tool_calls" => "tool_calls",
        "max_tokens" or "length" => "length",
        "content_filter" or "error" => stop,
        _ => "stop",
    };

    public static string AnthropicStop(string stop) => stop switch
    {
        "tool_use" or "tool_calls" => "tool_use",
        "max_tokens" or "length" => "max_tokens",
        "stop_sequence" or "pause_turn" or "refusal" => stop,
        _ => "end_turn",
    };

    public static string CanonicalChatStop(string stop) => stop switch
    {
        "tool_calls" or "function_call" => "tool_calls",
        "length" => "length",
        "content_filter" => "content_filter",
        "error" or "network_error" or "server_error" => "error",
        _ => "stop",
    };

    public static string CanonicalAnthropicStop(string stop) => stop switch
    {
        "tool_use" => "tool_calls",
        "max_tokens" => "length",
        "stop_sequence" or "pause_turn" or "refusal" => stop,
        "error" or "network_error" or "server_error" => "error",
        _ => "stop",
    };

    /// <summary>
    /// 判断规范的桥停止原因是否向下游承诺了工具调用。
    /// </summary>
    public static bool IsToolStop(string stop) => stop is "tool_calls" or "tool_use" or "function_call";

    /// <summary>
    /// 丢弃下游客户端无法执行的幽灵工具块。
    /// 空参数是合法的，因此工具名是这里必需的最低
    /// 信号；流式发射器使用同一条规则。
    /// </summary>
    public static List<BridgeBlock> UsableToolBlocks(List<BridgeBlock> tools)
    {
        var usable = new List<BridgeBlock>();
        foreach (var tool in tools)
        {
            if (tool.Name.Trim().Length > 0)
            {
                usable.Add(tool);
            }
        }
        return usable;
    }

    public static string CanonicalResponsesIncomplete(string reason) =>
        reason == "content_filter" ? "content_filter" : "length";

    public static string ResponsesIncompleteReason(string stop) =>
        stop == "content_filter" ? "content_filter" : "max_output_tokens";

    public static string ResponseTextBlocks(List<BridgeBlock> blocks, string protocol)
    {
        foreach (var block in blocks)
        {
            if (block.Kind != "text")
            {
                throw new ProtocolException($"{protocol} response contains unsupported {block.Kind} content block");
            }
        }
        return Bridge.BlocksText(blocks);
    }

    /// <summary>
    /// 从已序列化的请求体中提取用量，供从不解析负载
    /// 的路径做请求日志使用。
    /// </summary>
    public static (Usage Usage, bool Ok) ResponseUsage(string protocol, byte[] body)
    {
        JsonObject? payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonObject>(body);
        }
        catch (JsonException)
        {
            return (new Usage(), false);
        }
        var usage = JsonValues.MapAt(payload, "usage");
        if (usage == null || usage.Count == 0)
        {
            return (new Usage(), false);
        }
        return protocol == Wire.Anthropic
            ? (DecodeAnthropicUsage(usage), true)
            : (DecodeOpenAIUsage(usage), true);
    }

    /// <summary>
    /// 在给定协议的已序列化 JSON 响应或 SSE 事件数据对象中，
    /// 把匹配 fromName 的工具调用名改写为 toName。它用于在旧版 /zen/v1
    /// 塑形器把客户端 read 相关工具名在线路上
    /// 别名成 "read" 之后把原名还原回来。
    ///
    /// fromName 保证不会作为响应中其他文本的子串出现
    /// （工具名不会在内容里被回显），因此一次字节级
    /// 出现检查就足以跳过绝大多数帧的重新序列化。
    /// </summary>
    public static byte[] RenameTool(byte[] body, string protocol, string fromName, string toName)
    {
        if (fromName.Length == 0 || toName.Length == 0 || fromName == toName || body.Length == 0
            || !Encoding.UTF8.GetString(body).Contains(fromName, StringComparison.Ordinal))
        {
            return body;
        }
        JsonObject? payload;
        try
        {
            payload = JsonSerializer.Deserialize<JsonObject>(body);
        }
        catch (JsonException)
        {
            return body;
        }
        if (payload == null || !RenameToolInMap(payload, protocol, fromName, toName))
        {
            return body;
        }
        try
        {
            return ToUtf8(payload);
        }
        catch (JsonException)
        {
            return body;
        }
    }

    public static bool RenameToolInMap(JsonObject? payload, string protocol, string fromName, string toName)
    {
        if (payload == null)
        {
            return false;
        }
        var changed = false;
        switch (protocol)
        {
            case Wire.Chat:
                foreach (var rawChoice in JsonValues.SliceAt(payload, "choices"))
                {
                    var choice = rawChoice as JsonObject;
                    foreach (var key in new[] { "message", "delta" })
                    {
                        var container = JsonValues.MapAt(choice, key);
                        foreach (var rawCall in JsonValues.SliceAt(container, "tool_calls"))
                        {
                            if (rawCall is not JsonObject call || call["function"] is not JsonObject function)
                            {
                                continue;
                            }
                            if (JsonValues.StringAt(function, "name") == fromName)
                            {
                                function["name"] = toName;
                                changed = true;
                            }
                        }
                    }
                }
                break;
            case Wire.Responses:
                if (payload["item"] is JsonObject item
                    && JsonValues.StringAt(item, "type") == "function_call"
                    && JsonValues.StringAt(item, "name") == fromName)
                {
                    item["name"] = toName;
                    changed = true;
                }
                foreach (var rawItem in JsonValues.SliceAt(payload, "output"))
                {
                    var outputItem = rawItem as JsonObject;
                    if (JsonValues.StringAt(outputItem, "type") == "function_call"
                        && JsonValues.StringAt(outputItem, "name") == fromName)
                    {
                        outputItem!["name"] = toName;
                        changed = true;
                    }
                }
                if (payload["response"] is JsonObject response)
                {
                    foreach (var rawItem in JsonValues.SliceAt(response, "output"))
                    {
                        var outputItem = rawItem as JsonObject;
                        if (JsonValues.StringAt(outputItem, "type") == "function_call"
                            && JsonValues.StringAt(outputItem, "name") == fromName)
                        {
                            outputItem!["name"] = toName;
                            changed = true;
                        }
                    }
                }
                break;
            case Wire.Anthropic:
                if (payload["content_block"] is JsonObject block
                    && JsonValues.StringAt(block, "type") == "tool_use"
                    && JsonValues.StringAt(block, "name") == fromName)
                {
                    block["name"] = toName;
                    changed = true;
                }
                foreach (var rawBlock in JsonValues.SliceAt(payload, "content"))
                {
                    var contentBlock = rawBlock as JsonObject;
                    if (JsonValues.StringAt(contentBlock, "type") == "tool_use"
                        && JsonValues.StringAt(contentBlock, "name") == fromName)
                    {
                        contentBlock!["name"] = toName;
                        changed = true;
                    }
                }
                break;
        }
        return changed;
    }
}
