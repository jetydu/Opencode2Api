using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Protocol;

/// <summary>
/// 请求侧协议桥（internal/protocol/request.go）：把客户端
/// 请求体解码为中性的桥形态，编码到目标
/// 协议，以及对完成后的请求体施加目标相关的修补。
/// </summary>
internal static class RequestBridge
{
    /// <summary>
    /// 透传与转码请求共用的单条请求准备路径。同协议的请求
    /// 会被克隆，使
    /// 提供方专属字段得以保留；跨协议请求则
    /// 走桥转换。
    /// </summary>
    public static JsonObject Prepare(string from, string to, JsonObject input, string upstreamUrl)
    {
        var output = ConvertRequest(from, to, input);
        if (to == Wire.Responses)
        {
            // 桥已经为转换后的请求体设了下限；同协议的请求体
            // 是客户端请求体的克隆，也需要同样的保护。
            Jsonx.Put(output, "max_output_tokens", FloorResponsesMaxTokens(Jsonx.Get(output, "max_output_tokens")));
        }
        NormalizeToolReasoningHistory(to, JsonValues.StringAt(output, "model"), upstreamUrl, output);
        return output;
    }

    public static JsonObject ConvertRequest(string from, string to, JsonObject input)
    {
        if (from == to)
        {
            return input.DeepClone()!.AsObject();
        }
        var request = Decode(from, input);
        return Encode(to, request);
    }

    /// <summary>
    /// 把运维者配置的思考等级应用到已准备的上游
    /// 请求体。客户端显式声明的等级永远胜出：强制等级
    /// 替换的是没人真正要求过的那个，无论请求
    /// 完全没提推理，还是转换过程由 thinking.budget_tokens 推导而来。
    /// （未声明时才会被替换。）
    /// </summary>
    public static void ForcedEffort(string protocol, JsonObject? body, string effort)
    {
        if (body == null)
        {
            return;
        }
        effort = effort.Trim().ToLowerInvariant();
        if (!ValidForcedEffort(effort))
        {
            return;
        }
        var disable = effort == "none";
        switch (protocol)
        {
            case Wire.Chat:
                if (ClientEffortExplicit(protocol, body))
                {
                    return;
                }
                if (disable)
                {
                    body.Remove("reasoning_effort");
                    return;
                }
                body["reasoning_effort"] = effort;
                break;
            case Wire.Anthropic:
                if (ClientEffortExplicit(protocol, body))
                {
                    return;
                }
                ApplyAnthropicForcedEffort(body, effort, disable);
                break;
            case Wire.Responses:
                if (ClientEffortExplicit(protocol, body))
                {
                    return;
                }
                if (disable)
                {
                    body.Remove("reasoning");
                    return;
                }
                var reasoning = body["reasoning"] as JsonObject ?? NewReasoning(body);
                reasoning["effort"] = effort;
                break;
        }
    }

    private static JsonObject NewReasoning(JsonObject body)
    {
        var reasoning = new JsonObject();
        body["reasoning"] = reasoning;
        return reasoning;
    }

    /// <summary>
    /// 报告上游请求体是否已带有客户端声明的等级，
    /// 而不是网关由预算推导出的等级。这些槽位里的
    /// 其他内容是转换产生的结构值，
    /// 强制等级可以替换它们。
    /// </summary>
    public static bool ClientEffortExplicit(string protocol, JsonObject body) => protocol switch
    {
        Wire.Chat => Jsonx.Get(body, "reasoning_effort")?.GetValueKind() == JsonValueKind.String,
        Wire.Anthropic => EffortString(JsonValues.AnyAt(body, "output_config", "effort"))
                          || EffortString(Jsonx.Get(body, "effort")),
        Wire.Responses => JsonValues.AnyAt(body, "reasoning", "effort")?.GetValueKind() == JsonValueKind.String,
        _ => false,
    };

    private static bool EffortString(JsonNode? value) =>
        value != null && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Trim().Length > 0;

    /// <summary>
    /// 把强制等级写入 Anthropic 请求体，并让 thinking 块
    /// 与 max_tokens 与之保持一致：Anthropic 要求 thinking 块
    /// 带有预算，且 max_tokens 必须大于它，否则上游会拒绝该请求。
    /// 负载 —— 它刚被告知要以更高的等级运行。
    /// </summary>
    public static void ApplyAnthropicForcedEffort(JsonObject body, string effort, bool disable)
    {
        if (disable)
        {
            body.Remove("thinking");
            body.Remove("output_config");
            return;
        }
        if (body["output_config"] is JsonObject outputConfig)
        {
            outputConfig["effort"] = effort;
        }
        else
        {
            body["output_config"] = new JsonObject { ["effort"] = effort };
        }
        var budget = Bridge.BudgetForEffort(JsonValue.Create(effort));
        if (body["thinking"] is JsonObject thinking)
        {
            thinking["type"] = "enabled";
            thinking["budget_tokens"] = JsonValue.Create((double)budget);
        }
        else
        {
            body["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = JsonValue.Create((double)budget),
            };
        }
        if (JsonValues.IntAt(body, "max_tokens") > 0 && JsonValues.IntAt(body, "max_tokens") <= budget)
        {
            body["max_tokens"] = budget + 4096;
        }
    }

    /// <summary>
    /// 只接受配置能够强制的等级。"none" 是可以
    /// 再次移除 reasoning 的逃生口。
    /// </summary>
    public static bool ValidForcedEffort(string effort) =>
        effort is "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "none";

    /// <summary>
    /// 只作用于已知需要重放 reasoning 的端点，或
    /// 显式启用推理的请求。把目标形态归一化
    /// 可以让行为与客户端协议无关。
    /// </summary>
    public static bool NormalizeToolReasoningHistory(string protocol, string model, string upstreamUrl, JsonObject input)
    {
        var vendor = Bridge.IsReasoningVendorIdentifier(model) || Bridge.IsReasoningVendorIdentifier(upstreamUrl);
        var widened = vendor || RequestEnablesReasoning(input);
        switch (protocol)
        {
            case Wire.Chat:
                // Chat 的修补是附加式的：它只补充 reasoning_content，
                // 而在不认识该字段时
                // 兼容端点会忽略它，因此放宽门槛在这里是安全的。
                if (!widened)
                {
                    return false;
                }
                return NormalizeChatToolReasoningHistory(input);
            case Wire.Anthropic:
                // 以下每一项修补对原生 Anthropic
                // 端点都是破坏性的：它会删除 thinking 签名、丢弃
                // redacted_thinking 负载并覆写空的 thinking 文本，
                // 而这些正是原生端点会重新校验并
                // 逐字节重放的内容，改写它会让已缓存的提示
                // 前缀在历史中段发生变化。
                if (!vendor)
                {
                    return false;
                }
                return NormalizeAnthropicToolThinkingHistory(input);
            default:
                return false;
        }
    }

    public static bool RequestEnablesReasoning(JsonObject input)
    {
        foreach (var key in new[] { "reasoning_effort", "reasoning", "thinking", "effort" })
        {
            var value = Jsonx.Get(input, key);
            if (value == null)
            {
                continue;
            }
            switch (value.GetValueKind())
            {
                case JsonValueKind.String:
                {
                    var mode = value.GetValue<string>().Trim().ToLowerInvariant();
                    if (mode.Length > 0 && mode != "none" && mode != "disabled")
                    {
                        return true;
                    }
                    break;
                }
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.Object:
                {
                    var obj = value.AsObject();
                    var mode = JsonValues.FirstString(JsonValues.StringAt(obj, "type"), JsonValues.StringAt(obj, "effort"))
                        .Trim().ToLowerInvariant();
                    if (mode == "none" || mode == "disabled")
                    {
                        continue;
                    }
                    return true;
                }
                default:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 确保每个助手工具调用轮次都带有 reasoning_content。有些
    /// 客户端会丢弃这个非标准字段却保留 tool_calls，
    /// 这会让下一次思考模式请求变得非法。
    /// </summary>
    public static bool NormalizeChatToolReasoningHistory(JsonObject input)
    {
        if (input["messages"] is not JsonArray messages)
        {
            return false;
        }
        var changed = false;
        foreach (var rawMessage in messages)
        {
            if (rawMessage is not JsonObject message || JsonValues.StringAt(message, "role") != "assistant"
                || JsonValues.SliceAt(message, "tool_calls").Count == 0)
            {
                continue;
            }
            var reasoning = Jsonx.Get(message, "reasoning_content");
            if (reasoning != null && reasoning.GetValueKind() == JsonValueKind.String
                && reasoning.GetValue<string>().Trim().Length > 0)
            {
                continue;
            }
            var legacy = Jsonx.Get(message, "reasoning");
            var text = legacy != null && legacy.GetValueKind() == JsonValueKind.String ? legacy.GetValue<string>() : "";
            if (text.Trim().Length == 0)
            {
                text = Bridge.ToolReasoningPlaceholder;
            }
            message["reasoning_content"] = text;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// 只修补包含 tool_use 的助手轮次。DeepSeek、
    /// Kimi/Moonshot 与 MiMo 会拒绝这些轮次上带签名、被脱敏、为空或缺失的 thinking
    /// 历史，尽管 Anthropic 客户端可以合法地
    /// 发送上述每一种形态。
    /// </summary>
    public static bool NormalizeAnthropicToolThinkingHistory(JsonObject input)
    {
        if (input["messages"] is not JsonArray messages)
        {
            return false;
        }
        var changed = false;
        foreach (var rawMessage in messages)
        {
            if (rawMessage is not JsonObject message || JsonValues.StringAt(message, "role") != "assistant")
            {
                continue;
            }
            if (message["content"] is not JsonArray content || !AnthropicContentHasType(content, "tool_use"))
            {
                continue;
            }
            var hasThinking = false;
            for (var i = 0; i < content.Count; i++)
            {
                if (content[i] is not JsonObject block)
                {
                    continue;
                }
                switch (JsonValues.StringAt(block, "type"))
                {
                    case "thinking":
                        hasThinking = true;
                        if (block.ContainsKey("signature"))
                        {
                            block.Remove("signature");
                            changed = true;
                        }
                        var thinking = Jsonx.Get(block, "thinking");
                        var text = thinking != null && thinking.GetValueKind() == JsonValueKind.String
                            ? thinking.GetValue<string>() : "";
                        if (text.Trim().Length == 0)
                        {
                            block["thinking"] = Bridge.ToolReasoningPlaceholder;
                            changed = true;
                        }
                        break;
                    case "redacted_thinking":
                        hasThinking = true;
                        content[i] = new JsonObject
                        {
                            ["type"] = "thinking",
                            ["thinking"] = Bridge.AnthropicRedactedThinkingPlaceholder,
                        };
                        changed = true;
                        break;
                }
            }
            if (!hasThinking)
            {
                content.Insert(0, new JsonObject
                {
                    ["type"] = "thinking",
                    ["thinking"] = Bridge.ToolReasoningPlaceholder,
                });
                changed = true;
            }
        }
        return changed;
    }

    public static bool AnthropicContentHasType(JsonArray content, string kind)
    {
        foreach (var rawBlock in content)
        {
            if (JsonValues.StringAt(rawBlock as JsonObject, "type") == kind)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把 Anthropic Messages 请求的推理控制项解码为
    /// 单个桥级取值。客户端显式声明的
    /// 等级优先于 thinking 块中的预算。
    /// </summary>
    public static JsonNode? AnthropicReasoning(JsonObject input)
    {
        var stated = JsonValues.FirstAny(JsonValues.AnyAt(input, "output_config", "effort"), Jsonx.Get(input, "effort"));
        var (effort, ok) = Bridge.NormalizeEffortValue(stated);
        if (ok)
        {
            if (Jsonx.IsNull(effort))
            {
                return null;
            }
            if (input["thinking"] is JsonObject thinking)
            {
                var merged = new JsonObject();
                foreach (var property in thinking)
                {
                    merged[property.Key] = property.Value?.DeepClone();
                }
                merged["effort"] = effort!.DeepClone();
                return merged;
            }
            return effort;
        }
        return Jsonx.Get(input, "thinking");
    }

    public static BridgeRequest Decode(string protocol, JsonObject input)
    {
        var request = new BridgeRequest
        {
            Model = JsonValues.StringAt(input, "model"),
            Stream = JsonValues.BoolAt(input, "stream"),
            Temperature = Jsonx.Get(input, "temperature"),
            TopP = Jsonx.Get(input, "top_p"),
            Metadata = Jsonx.Get(input, "metadata"),
        };
        switch (protocol)
        {
            case Wire.Chat:
                DecodeChat(input, request);
                break;
            case Wire.Responses:
                DecodeResponses(input, request);
                break;
            case Wire.Anthropic:
                DecodeAnthropic(input, request);
                break;
            default:
                throw new ProtocolException($"unsupported input protocol \"{protocol}\"");
        }
        // 携带图像的工具结果只解码一次，时机是在每种协议
        // 都产出其桥形态之后。
        for (var i = 0; i < request.Messages.Count; i++)
        {
            var blocks = request.Messages[i].Blocks;
            for (var j = 0; j < blocks.Count; j++)
            {
                var block = blocks[j];
                if (block.Kind != "tool_result" || protocol == Wire.Chat)
                {
                    continue;
                }
                var result = ToolImages.Decode(protocol, block.Result as JsonNode);
                if (result is List<BridgeBlock> images && images.Count > 0 && block.CallID.Length == 0)
                {
                    throw new ProtocolException($"messages[{i}]: tool image result requires a call ID");
                }
                block.Result = result;
            }
        }
        return request;
    }

    private static void DecodeChat(JsonObject input, BridgeRequest request)
    {
        request.MaxTokens = JsonValues.FirstAny(Jsonx.Get(input, "max_completion_tokens"), Jsonx.Get(input, "max_tokens"));
        request.Stop = Jsonx.Get(input, "stop");
        request.Reasoning = JsonValues.FirstAny(Jsonx.Get(input, "reasoning_effort"), Jsonx.Get(input, "reasoning"));
        var messages = JsonValues.SliceAt(input, "messages");
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                throw new ProtocolException($"messages[{i}] must be an object");
            }
            var role = JsonValues.StringAt(message, "role");
            var blocks = Content.DecodeOpenAIBlocksChecked(message["content"]);
            if (role == "assistant")
            {
                blocks.InsertRange(0, Content.DecodeChatReasoning(message));
                // 网关在助手消息上带出的预算，会在
                // 等级旁边被读回，因此两个方向都能一致地
                // 得到客户端最初声明的等级。
                var budget = JsonValues.IntAt(message, "reasoning_budget_tokens");
                if (budget > 0)
                {
                    request.Reasoning = Bridge.WithReasoningBudget(request.Reasoning, budget);
                }
            }
            var calls = JsonValues.SliceAt(message, "tool_calls");
            for (var j = 0; j < calls.Count; j++)
            {
                if (calls[j] is not JsonObject call)
                {
                    throw new ProtocolException($"messages[{i}].tool_calls[{j}] must be an object");
                }
                var function = JsonValues.MapAt(call, "function");
                blocks.Add(new BridgeBlock
                {
                    Kind = "tool_call",
                    ID = JsonValues.StringAt(call, "id"),
                    Name = JsonValues.StringAt(function, "name"),
                    ArgumentsJSON = JsonValues.StringAt(function, "arguments"),
                });
            }
            switch (role)
            {
                case "system":
                    request.System.AddRange(blocks);
                    break;
                case "developer":
                    request.Developer.AddRange(blocks);
                    break;
                case "tool":
                    request.Messages.Add(new BridgeMessage
                    {
                        Role = "user",
                        Blocks =
                        [
                            new BridgeBlock
                            {
                                Kind = "tool_result",
                                CallID = JsonValues.StringAt(message, "tool_call_id"),
                                Result = message["content"],
                            },
                        ],
                    });
                    break;
                case "user":
                case "assistant":
                    request.Messages.Add(new BridgeMessage { Role = role, Blocks = blocks });
                    break;
                default:
                    throw new ProtocolException($"messages[{i}] has unsupported role \"{role}\"");
            }
        }
        var tools = JsonValues.SliceAt(input, "tools");
        for (var i = 0; i < tools.Count; i++)
        {
            if (tools[i] is not JsonObject tool || JsonValues.StringAt(tool, "type") != "function")
            {
                throw new ProtocolException($"tools[{i}] must be a function tool");
            }
            var function = JsonValues.MapAt(tool, "function");
            request.Tools.Add(new BridgeTool
            {
                Name = JsonValues.StringAt(function, "name"),
                Description = JsonValues.StringAt(function, "description"),
                Schema = Jsonx.Get(function, "parameters"),
                Strict = JsonValues.BoolAt(function, "strict"),
            });
        }
        request.ToolChoice = Content.DecodeChatToolChoice(Jsonx.Get(input, "tool_choice"));
        request.ResponseFormat = Jsonx.Get(input, "response_format");
        request.ParallelToolCalls = Jsonx.Get(input, "parallel_tool_calls");
        request.FrequencyPenalty = Jsonx.Get(input, "frequency_penalty");
        request.PresencePenalty = Jsonx.Get(input, "presence_penalty");
        request.Seed = Jsonx.Get(input, "seed");
        request.PromptCacheKey = Jsonx.Get(input, "prompt_cache_key");
        request.SafetyIdentifier = Jsonx.Get(input, "safety_identifier");
        request.ServiceTier = Jsonx.Get(input, "service_tier");
        request.Store = Jsonx.Get(input, "store");
    }

    private static void DecodeResponses(JsonObject input, BridgeRequest request)
    {
        request.MaxTokens = Jsonx.Get(input, "max_output_tokens");
        request.Stop = Jsonx.Get(input, "stop");
        request.Reasoning = JsonValues.FirstAny(Jsonx.Get(input, "reasoning"), Jsonx.Get(input, "reasoning_effort"));
        var instructions = Content.DecodeOpenAIBlocksChecked(input["instructions"]);
        request.System.AddRange(instructions);
        var inputValue = input["input"];
        if (inputValue != null && inputValue.GetValueKind() == JsonValueKind.String)
        {
            request.Messages.Add(new BridgeMessage
            {
                Role = "user",
                Blocks = [new BridgeBlock { Kind = "text", Text = inputValue.GetValue<string>() }],
            });
        }
        else if (inputValue is JsonArray items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i] is not JsonObject item)
                {
                    throw new ProtocolException($"input[{i}] must be an object");
                }
                switch (JsonValues.StringAt(item, "type"))
                {
                    case "reasoning":
                        foreach (var block in Content.DecodeResponsesReasoning(item))
                        {
                            AppendBridgeBlock(request.Messages, "assistant", block);
                        }
                        break;
                    case "function_call":
                        AppendBridgeBlock(request.Messages, "assistant", new BridgeBlock
                        {
                            Kind = "tool_call",
                            ID = JsonValues.FirstString(JsonValues.StringAt(item, "call_id"), JsonValues.StringAt(item, "id")),
                            Name = JsonValues.StringAt(item, "name"),
                            ArgumentsJSON = JsonValues.StringAt(item, "arguments"),
                        });
                        break;
                    case "function_call_output":
                        AppendBridgeBlock(request.Messages, "user", new BridgeBlock
                        {
                            Kind = "tool_result",
                            CallID = JsonValues.StringAt(item, "call_id"),
                            Result = Jsonx.Get(item, "output"),
                        });
                        break;
                    case "message":
                    case "":
                    {
                        var role = JsonValues.StringAt(item, "role");
                        var blocks = Content.DecodeOpenAIBlocksChecked(item["content"]);
                        if (role == "system")
                        {
                            request.System.AddRange(blocks);
                        }
                        else if (role == "developer")
                        {
                            request.Developer.AddRange(blocks);
                        }
                        else if (role is "user" or "assistant")
                        {
                            request.Messages.Add(new BridgeMessage { Role = role, Blocks = blocks });
                        }
                        break;
                    }
                    default:
                        throw new ProtocolException(
                            $"input[{i}] has unsupported Responses item type \"{JsonValues.StringAt(item, "type")}\"");
                }
            }
        }
        else
        {
            throw new ProtocolException("input must be a string or array");
        }
        var tools = JsonValues.SliceAt(input, "tools");
        for (var i = 0; i < tools.Count; i++)
        {
            if (tools[i] is not JsonObject tool)
            {
                throw new ProtocolException($"tools[{i}] must be an object");
            }
            // 内置 Responses 工具（web_search、file_search、code_interpreter 等）
            // 没有 Chat/Anthropic 等价物；在跨协议
            // 桥接时跳过它们，使得混合了函数工具与内置插件
            // 的请求仍能到达上游。
            if (JsonValues.StringAt(tool, "type") != "function")
            {
                continue;
            }
            request.Tools.Add(new BridgeTool
            {
                Name = JsonValues.StringAt(tool, "name"),
                Description = JsonValues.StringAt(tool, "description"),
                Schema = Jsonx.Get(tool, "parameters"),
                Strict = JsonValues.BoolAt(tool, "strict"),
            });
        }
        request.ToolChoice = Content.DecodeResponsesToolChoice(Jsonx.Get(input, "tool_choice"));
        request.ResponseFormat = JsonValues.MapAt(input, "text", "format");
        request.ParallelToolCalls = Jsonx.Get(input, "parallel_tool_calls");
        request.PromptCacheKey = Jsonx.Get(input, "prompt_cache_key");
        request.SafetyIdentifier = Jsonx.Get(input, "safety_identifier");
        request.ServiceTier = Jsonx.Get(input, "service_tier");
        request.Store = Jsonx.Get(input, "store");
    }

    private static void DecodeAnthropic(JsonObject input, BridgeRequest request)
    {
        request.MaxTokens = Jsonx.Get(input, "max_tokens");
        request.Stop = Jsonx.Get(input, "stop_sequences");
        request.Reasoning = AnthropicReasoning(input);
        request.System = Content.DecodeAnthropicBlocksChecked(input["system"]);
        var messages = JsonValues.SliceAt(input, "messages");
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message)
            {
                throw new ProtocolException($"messages[{i}] must be an object");
            }
            var role = JsonValues.StringAt(message, "role");
            // 某些客户端（例如 Claude Code）会把 system/developer 消息
            // 内联进 messages 数组，而不是使用顶层的
            // system 字段。把它们折叠进系统提示，而不是拒绝请求。
            if (role == "system")
            {
                request.System.AddRange(Content.DecodeAnthropicBlocksChecked(message["content"]));
                continue;
            }
            if (role == "developer")
            {
                request.Developer.AddRange(Content.DecodeAnthropicBlocksChecked(message["content"]));
                continue;
            }
            if (role != "user" && role != "assistant")
            {
                throw new ProtocolException($"messages[{i}] has unsupported role \"{role}\"");
            }
            request.Messages.Add(new BridgeMessage
            {
                Role = role,
                Blocks = Content.DecodeAnthropicBlocksChecked(message["content"]),
            });
        }
        var tools = JsonValues.SliceAt(input, "tools");
        for (var i = 0; i < tools.Count; i++)
        {
            if (tools[i] is not JsonObject tool)
            {
                throw new ProtocolException($"tools[{i}] must be an object");
            }
            request.Tools.Add(new BridgeTool
            {
                Name = JsonValues.StringAt(tool, "name"),
                Description = JsonValues.StringAt(tool, "description"),
                Schema = Jsonx.Get(tool, "input_schema"),
            });
        }
        request.ToolChoice = Content.DecodeAnthropicToolChoice(Jsonx.Get(input, "tool_choice"));
    }

    /// <summary>
    /// 把内容块追加到同角色的尾部纯工具轮次上，因此一串
    /// Responses 条目会收敛为一条桥消息。
    /// </summary>
    public static void AppendBridgeBlock(List<BridgeMessage> messages, string role, BridgeBlock block)
    {
        if (messages.Count > 0)
        {
            var last = messages[^1];
            if (last.Role == role && BridgeBlocksOnlyTools(last.Blocks))
            {
                last.Blocks.Add(block);
                return;
            }
        }
        messages.Add(new BridgeMessage { Role = role, Blocks = [block] });
    }

    public static bool BridgeBlocksOnlyTools(List<BridgeBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return false;
        }
        return blocks.All(block => block.Kind is "tool_call" or "tool_result");
    }

    public static JsonObject Encode(string protocol, BridgeRequest request)
    {
        Validate(protocol, request);
        return protocol switch
        {
            Wire.Chat => EncodeChatRequest(request),
            Wire.Responses => EncodeResponsesRequest(request),
            Wire.Anthropic => EncodeAnthropicRequest(request),
            _ => throw new ProtocolException($"unsupported output protocol \"{protocol}\""),
        };
    }

    public static void Validate(string protocol, BridgeRequest request)
    {
        if (protocol == Wire.Responses)
        {
            foreach (var block in request.System)
            {
                if (block.Kind == "file")
                {
                    throw new ProtocolException(
                        "file content is not valid inside Responses instructions; send it as a message input item");
                }
            }
        }
        var blocks = new List<BridgeBlock>();
        blocks.AddRange(request.System);
        blocks.AddRange(request.Developer);
        foreach (var message in request.Messages)
        {
            blocks.AddRange(message.Blocks);
        }
        foreach (var block in blocks)
        {
            if (block.Kind == "file" && protocol == Wire.Anthropic && block.FileID.Length > 0
                && block.Data.Length == 0 && block.URL.Length == 0)
            {
                throw new ProtocolException(
                    $"file \"{block.FileID}\" cannot be represented by Anthropic Messages without file data or a URL");
            }
        }
    }

    public static JsonObject EncodeChatRequest(BridgeRequest request)
    {
        var output = new JsonObject { ["model"] = request.Model, ["stream"] = request.Stream };
        Jsonx.Put(output, "temperature", request.Temperature);
        Jsonx.Put(output, "top_p", request.TopP);
        Jsonx.Put(output, "max_tokens", request.MaxTokens);
        Jsonx.Put(output, "stop", request.Stop);
        var effort = Bridge.ReasoningEffort(request.Reasoning);
        if (!Jsonx.IsNull(effort))
        {
            output["reasoning_effort"] = effort!.DeepClone();
        }
        Jsonx.Put(output, "response_format", Bridge.ChatResponseFormat(request.ResponseFormat));
        Jsonx.Put(output, "parallel_tool_calls", request.ParallelToolCalls);
        Jsonx.Put(output, "frequency_penalty", request.FrequencyPenalty);
        Jsonx.Put(output, "presence_penalty", request.PresencePenalty);
        Jsonx.Put(output, "seed", request.Seed);
        Jsonx.Put(output, "prompt_cache_key", request.PromptCacheKey);
        Jsonx.Put(output, "safety_identifier", request.SafetyIdentifier);
        Jsonx.Put(output, "service_tier", request.ServiceTier);
        Jsonx.Put(output, "store", request.Store);
        if (request.Stream)
        {
            output["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        var messages = new List<JsonObject>();
        if (request.System.Count > 0)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = Content.EncodeChatBlocks(request.System) });
        }
        if (request.Developer.Count > 0)
        {
            messages.Add(new JsonObject { ["role"] = "developer", ["content"] = Content.EncodeChatBlocks(request.Developer) });
        }
        var pending = new Dictionary<string, bool>();
        var pendingResults = new Dictionary<string, BridgeBlock>();
        var pendingOrder = new List<string>();
        var deferred = new List<List<BridgeBlock>>();
        // Anthropic 的推理控制项属于整段对话，而 Chat
        // 每个请求只带一个 reasoning_effort。客户端声明的预算
        // 只输出一次，放在第一条助手消息上，这样把
        // 这个 Chat 请求体解码回 Anthropic 时可以精确还原等级，而不是
        // 从档位重新推导（32000 过去会被还原成 8192）。
        var budgetCarried = false;
        void CarryBudget(JsonObject encoded)
        {
            if (budgetCarried)
            {
                return;
            }
            var budget = Bridge.ReasoningBudget(request.Reasoning);
            if (budget > 0)
            {
                encoded["reasoning_budget_tokens"] = budget;
                budgetCarried = true;
            }
        }
        void FlushDeferred()
        {
            foreach (var blocks in deferred)
            {
                if (blocks.Count > 0)
                {
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = Content.EncodeChatBlocks(blocks) });
                }
            }
            deferred.Clear();
        }
        // 对话可能在任何助手轮次存在之前就结束，也就没有承载
        // 预算的地方。把预算挂在一个空的助手消息上，可以让它
        // 留在线路上而不臆造可见输出；Chat 客户端会把
        // 这个空轮次当作助手预填充。
        void CarryBudgetOnly()
        {
            if (budgetCarried)
            {
                return;
            }
            if (Bridge.ReasoningBudget(request.Reasoning) <= 0)
            {
                return;
            }
            var encoded = new JsonObject { ["role"] = "assistant", ["content"] = null };
            CarryBudget(encoded);
            if (!encoded.ContainsKey("reasoning_budget_tokens"))
            {
                return;
            }
            messages.Add(encoded);
        }

        for (var i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            var content = new List<BridgeBlock>();
            var reasoning = new List<BridgeBlock>();
            var calls = new List<BridgeBlock>();
            var results = new List<BridgeBlock>();
            foreach (var block in message.Blocks)
            {
                switch (block.Kind)
                {
                    case "reasoning":
                        reasoning.Add(block);
                        break;
                    case "tool_call":
                        calls.Add(block);
                        break;
                    case "tool_result":
                        results.Add(block);
                        break;
                    default:
                        content.Add(block);
                        break;
                }
            }
            if (message.Role == "assistant")
            {
                if (pending.Count > 0)
                {
                    throw new ProtocolException(
                        $"messages[{i}]: assistant message appears before tool results for {string.Join(", ", MissingToolIDs(pendingOrder, pending))}");
                }
                if (results.Count > 0)
                {
                    throw new ProtocolException($"messages[{i}]: assistant message contains tool results");
                }
                var encoded = new JsonObject { ["role"] = "assistant", ["content"] = null };
                CarryBudget(encoded);
                var (reasoningText, hasReasoning) = Content.BridgeReasoningText(reasoning);
                if (hasReasoning)
                {
                    encoded["reasoning_content"] = reasoningText;
                }
                if (content.Count > 0)
                {
                    encoded["content"] = Content.EncodeChatBlocks(content);
                }
                if (calls.Count > 0)
                {
                    var toolCalls = new JsonArray();
                    foreach (var call in calls)
                    {
                        if (call.ID.Length == 0 || call.Name.Length == 0)
                        {
                            throw new ProtocolException($"messages[{i}]: tool call must contain id and name");
                        }
                        if (pending.TryGetValue(call.ID, out var seen) && seen)
                        {
                            throw new ProtocolException($"messages[{i}]: duplicate tool call id \"{call.ID}\"");
                        }
                        pending[call.ID] = true;
                        pendingOrder.Add(call.ID);
                        toolCalls.Add(new JsonObject
                        {
                            ["id"] = call.ID,
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = call.Name,
                                ["arguments"] = Bridge.BridgeArgumentsJSON(call),
                            },
                        });
                    }
                    encoded["tool_calls"] = toolCalls;
                }
                if (content.Count > 0 || reasoning.Count > 0 || calls.Count > 0)
                {
                    messages.Add(encoded);
                    budgetCarried = true;
                }
                continue;
            }

            if (calls.Count > 0)
            {
                throw new ProtocolException($"messages[{i}]: user message contains tool calls");
            }
            foreach (var result in results)
            {
                if (pendingResults.ContainsKey(result.CallID))
                {
                    throw new ProtocolException($"messages[{i}]: duplicate tool result \"{result.CallID}\"");
                }
                if (!pending.TryGetValue(result.CallID, out var waiting) || !waiting)
                {
                    throw new ProtocolException($"messages[{i}]: tool result \"{result.CallID}\" has no pending tool call");
                }
                pendingResults[result.CallID] = result;
                pending.Remove(result.CallID);
            }
            if (content.Count > 0)
            {
                if (pendingOrder.Count > 0 || deferred.Count > 0)
                {
                    deferred.Add(content);
                }
                else
                {
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = Content.EncodeChatBlocks(content) });
                }
            }
            if (pending.Count == 0 && pendingOrder.Count > 0)
            {
                var attachments = new List<JsonObject>();
                foreach (var id in pendingOrder)
                {
                    var (text, images) = ToolImages.ChatToolImages(pendingResults[id]);
                    if (images is { Count: > 0 })
                    {
                        attachments.Add(new JsonObject { ["role"] = "user", ["content"] = Content.EncodeChatBlocks(images) });
                    }
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["content"] = text,
                    });
                }
                foreach (var attachment in attachments)
                {
                    messages.Add(attachment);
                }
                FlushDeferred();
                pendingOrder.Clear();
                pendingResults.Clear();
            }
        }
        if (pending.Count > 0)
        {
            throw new ProtocolException($"tool calls are missing results for {string.Join(", ", MissingToolIDs(pendingOrder, pending))}");
        }
        FlushDeferred();
        CarryBudgetOnly();
        var messageArray = new JsonArray();
        foreach (var message in messages)
        {
            messageArray.Add(message);
        }
        output["messages"] = messageArray;

        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                var function = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = Bridge.SchemaOrDefault(tool.Schema),
                };
                if (tool.Strict)
                {
                    function["strict"] = true;
                }
                tools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            output["tools"] = tools;
            var choice = Content.EncodeChatToolChoice(request.ToolChoice);
            if (choice != null)
            {
                output["tool_choice"] = choice;
            }
        }
        return output;
    }

    /// <summary>
    /// 列出仍未收到结果的工具调用 id，优先保持这些调用
    /// 出现的顺序，并对剩余部分排序，使消息稳定可复现。
    /// </summary>
    public static List<string> MissingToolIDs(List<string> order, Dictionary<string, bool> pending)
    {
        var missing = new List<string>();
        foreach (var id in order)
        {
            if (pending.ContainsKey(id))
            {
                missing.Add(id);
            }
        }
        if (missing.Count == pending.Count)
        {
            return missing;
        }
        foreach (var id in pending.Keys)
        {
            if (!missing.Contains(id))
            {
                missing.Add(id);
            }
        }
        missing.Sort(StringComparer.Ordinal);
        return missing;
    }

    /// <summary>
    /// 构造 Responses 的 "reasoning" 对象。裸的等级字符串，或带有
    /// budget_tokens 却没有等级的 thinking 形态块，
    /// 会被投射到 "reasoning.effort" 上：原样拷贝这类块
    /// 曾把 {"type":"adaptive"} 转发出去并彻底丢掉等级。
    /// </summary>
    public static JsonNode? ResponsesReasoning(JsonNode? value)
    {
        if (value is JsonObject obj)
        {
            if (JsonValues.StringAt(obj, "type") == "disabled")
            {
                return obj.DeepClone();
            }
            // 这里 "effort" 是权威的，理由与
            // EncodeChatRequest 相同：它要么来自客户端原样输入，
            // 要么由推理等级从预算推导而来。
            var effort = Bridge.ReasoningEffort(obj);
            if (!Jsonx.IsNull(effort))
            {
                return effort!.GetValueKind() switch
                {
                    JsonValueKind.Object => effort.DeepClone(),
                    _ => new JsonObject { ["effort"] = effort.DeepClone() },
                };
            }
            return obj.DeepClone();
        }
        if (value != null && value.GetValueKind() == JsonValueKind.String)
        {
            var (effort, ok) = Bridge.NormalizeEffortValue(value);
            if (ok)
            {
                if (Jsonx.IsNull(effort))
                {
                    return null;
                }
                return new JsonObject { ["effort"] = effort!.DeepClone() };
            }
            return new JsonObject { ["effort"] = value.DeepClone() };
        }
        return value?.DeepClone();
    }

    /// <summary>
    /// Zen Responses 端点所能接受的最小 max_output_tokens；
    /// 更低的值会被以 400 拒绝。
    /// </summary>
    public const int MinResponsesMaxOutputTokens = 16;

    /// <summary>
    /// 避免极小的下游预算（后台标题、探测）导致
    /// Zen 响应校验失败。较大的预算原样通过。
    /// </summary>
    public static JsonNode? FloorResponsesMaxTokens(JsonNode? value)
    {
        if (value is JsonValue scalar && value.GetValueKind() == JsonValueKind.Number
            && scalar.TryGetValue(out double number) && number > 0 && number < MinResponsesMaxOutputTokens)
        {
            return JsonValue.Create((double)MinResponsesMaxOutputTokens);
        }
        return value;
    }

    public static JsonObject EncodeResponsesRequest(BridgeRequest request)
    {
        var output = new JsonObject { ["model"] = request.Model, ["stream"] = request.Stream };
        Jsonx.Put(output, "temperature", request.Temperature);
        Jsonx.Put(output, "top_p", request.TopP);
        Jsonx.Put(output, "max_output_tokens", FloorResponsesMaxTokens(request.MaxTokens));
        Jsonx.Put(output, "stop", request.Stop);
        Jsonx.Put(output, "metadata", request.Metadata);
        if (request.System.Count > 0)
        {
            output["instructions"] = Bridge.BlocksText(request.System);
        }
        if (request.Reasoning != null)
        {
            var reasoning = ResponsesReasoning(request.Reasoning);
            if (reasoning != null)
            {
                output["reasoning"] = reasoning;
            }
        }
        var format = Bridge.ResponsesTextFormat(request.ResponseFormat);
        if (format != null)
        {
            output["text"] = new JsonObject { ["format"] = format };
        }
        Jsonx.Put(output, "parallel_tool_calls", request.ParallelToolCalls);
        // 原生保真开关，缺失时省略：从不发送它们的
        // 客户端看到零行为变化；opencode 轮次则搭上缓存亲和。
        Jsonx.Put(output, "prompt_cache_key", request.PromptCacheKey);
        Jsonx.Put(output, "safety_identifier", request.SafetyIdentifier);
        Jsonx.Put(output, "service_tier", request.ServiceTier);
        Jsonx.Put(output, "store", request.Store);

        var items = new JsonArray();
        if (request.Developer.Count > 0)
        {
            items.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = "developer",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "input_text", ["text"] = Bridge.BlocksText(request.Developer) },
                },
            });
        }
        foreach (var message in request.Messages)
        {
            var content = new JsonArray();
            void FlushContent()
            {
                if (content.Count == 0)
                {
                    return;
                }
                items.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = Bridge.NormalizeResponsesRole(message.Role),
                    ["content"] = content,
                });
                // 产出的数组现在归属于该条目，因此下一轮
                // 会累加进一个新的数组，而不是就地清空它。
                content = new JsonArray();
            }
            foreach (var block in message.Blocks)
            {
                switch (block.Kind)
                {
                    case "reasoning":
                        FlushContent();
                        // 只重放上游确实签发过的 reasoning。以纯文本
                        // 到达的块（Chat/Anthropic
                        // 历史）没有服务端 id，而合成一个 id
                        // 必然会被判为过期 reasoning 引用而拒绝。
                        var (reasoningItem, ok) = Content.EncodeResponsesReasoningInput(block);
                        if (ok)
                        {
                            items.Add(reasoningItem);
                        }
                        break;
                    case "text":
                        content.Add(new JsonObject
                        {
                            ["type"] = message.Role == "assistant" ? "output_text" : "input_text",
                            ["text"] = block.Text,
                        });
                        break;
                    case "image":
                    {
                        var url = block.URL;
                        if (url.Length == 0 && block.Data.Length > 0)
                        {
                            url = "data:" + block.MediaType + ";base64," + block.Data;
                        }
                        content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = url });
                        break;
                    }
                    case "file":
                    {
                        var item = new JsonObject
                        {
                            ["type"] = "input_file",
                            ["file_id"] = block.FileID,
                            ["file_data"] = block.Data,
                            ["filename"] = block.Filename,
                        };
                        content.Add(item);
                        break;
                    }
                    case "tool_call":
                        FlushContent();
                        items.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = block.ID,
                            ["name"] = block.Name,
                            ["arguments"] = Bridge.BridgeArgumentsJSON(block),
                        });
                        break;
                    case "tool_result":
                        FlushContent();
                        items.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = block.CallID,
                            ["output"] = ToolImages.ResponsesToolContent(block.Result),
                        });
                        break;
                }
            }
            FlushContent();
        }
        output["input"] = items;
        if (request.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = Bridge.SchemaOrDefault(tool.Schema),
                    ["strict"] = tool.Strict,
                });
            }
            output["tools"] = tools;
            var choice = Content.EncodeResponsesToolChoice(request.ToolChoice);
            if (choice != null)
            {
                output["tool_choice"] = choice;
            }
        }
        return output;
    }

    public static JsonObject EncodeAnthropicRequest(BridgeRequest request)
    {
        var output = new JsonObject { ["model"] = request.Model, ["stream"] = request.Stream };
        Jsonx.Put(output, "temperature", request.Temperature);
        Jsonx.Put(output, "top_p", request.TopP);
        Jsonx.Put(output, "stop_sequences", request.Stop);
        if (Jsonx.IsNull(request.MaxTokens))
        {
            output["max_tokens"] = 4096;
        }
        else
        {
            output["max_tokens"] = request.MaxTokens!.DeepClone();
        }
        var (thinking, outputConfig) = Bridge.AnthropicThinking(request.Reasoning);
        if (thinking != null)
        {
            output["thinking"] = thinking;
            if (outputConfig != null)
            {
                output["output_config"] = outputConfig;
            }
        }
        if (request.System.Count > 0 || request.Developer.Count > 0)
        {
            var instructions = new List<BridgeBlock>();
            instructions.AddRange(request.System);
            instructions.AddRange(request.Developer);
            output["system"] = Content.EncodeAnthropicBlocks(instructions);
        }

        var messages = new JsonArray();
        foreach (var message in request.Messages)
        {
            var content = Content.EncodeAnthropicBlocks(message.Blocks);
            if (content.Count == 0)
            {
                continue;
            }
            var role = message.Role == "assistant" ? "assistant" : "user";
            if (messages.Count > 0 && messages[^1] is JsonObject last
                && JsonValues.StringAt(last, "role") == role)
            {
                if (last["content"] is JsonArray existing)
                {
                    foreach (var block in content)
                    {
                        existing.Add(block!.DeepClone());
                    }
                }
                continue;
            }
            messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
        }
        output["messages"] = messages;
        // Anthropic 没有 tool_choice 的 "none" 取值。省略 tools 是
        // 显式禁用工具集在协议上的等价表达。
        if (request.Tools.Count > 0 && request.ToolChoice.Mode != "none")
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["input_schema"] = Bridge.SchemaOrDefault(tool.Schema),
                });
            }
            output["tools"] = tools;
            var choice = Content.EncodeAnthropicToolChoice(request.ToolChoice);
            if (choice != null)
            {
                output["tool_choice"] = choice;
            }
        }
        return output;
    }
}
