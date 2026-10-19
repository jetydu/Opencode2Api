using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Protocol;

/// <summary>协议桥接时抛出；网关会以 400 应答。</summary>
public sealed class ProtocolException(string message) : Exception(message);

/// <summary>桥内一个归一化的内容块（protocol.bridgeBlock）。</summary>
internal sealed class BridgeBlock
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Encrypted { get; set; } = "";
    public string URL { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string Data { get; set; } = "";
    public string FileID { get; set; } = "";
    public string Filename { get; set; } = "";
    public string ID { get; set; } = "";
    public string Name { get; set; } = "";
    public JsonNode? Arguments { get; set; }
    public string ArgumentsJSON { get; set; } = "";
    public string CallID { get; set; } = "";

    /// <summary>要么是 JsonNode（提供方原始值），要么是工具图像的 List&lt;BridgeBlock&gt;。</summary>
    public object? Result { get; set; }

    public bool IsError { get; set; }
}

internal sealed class BridgeMessage
{
    public string Role { get; set; } = "";
    public List<BridgeBlock> Blocks { get; set; } = [];
}

internal sealed class BridgeTool
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public JsonNode? Schema { get; set; }
    public bool Strict { get; set; }
}

internal readonly struct BridgeToolChoice
{
    public BridgeToolChoice(string mode = "", string name = "")
    {
        Mode = mode;
        Name = name;
    }

    public string Mode { get; }
    public string Name { get; }
}

internal sealed class BridgeRequest
{
    public string Model { get; set; } = "";
    public List<BridgeBlock> System { get; set; } = [];
    public List<BridgeBlock> Developer { get; set; } = [];
    public List<BridgeMessage> Messages { get; set; } = [];
    public List<BridgeTool> Tools { get; set; } = [];
    public BridgeToolChoice ToolChoice { get; set; }
    public bool Stream { get; set; }
    public JsonNode? Temperature { get; set; }
    public JsonNode? TopP { get; set; }
    public JsonNode? MaxTokens { get; set; }
    public JsonNode? Stop { get; set; }
    public JsonNode? Reasoning { get; set; }
    public JsonNode? Metadata { get; set; }

    // 采样与响应形态类开关在桥中以不透明值传递。
    // 每种协议接受的形态不同，因此无法原生表达的
    // 值会被省略而不是拒绝请求：静默丢弃开关
    // 是旧行为，它会让客户端误以为（例如）
    // JSON 模式已经被 honored。
    public JsonNode? ResponseFormat { get; set; }
    public JsonNode? ParallelToolCalls { get; set; }
    public JsonNode? FrequencyPenalty { get; set; }
    public JsonNode? PresencePenalty { get; set; }
    public JsonNode? Seed { get; set; }

    // 从客户端请求体解码并重新发给上游的原生保真开关。
    // 最重要的是 prompt_cache_key：原生 opencode 在这里发送
    // 按会话切分的 ID 以享受提示缓存亲和；缺少它时，每个
    // 被代理的轮次都要对全部历史付出完整注意力开销。
    public JsonNode? PromptCacheKey { get; set; }
    public JsonNode? SafetyIdentifier { get; set; }
    public JsonNode? ServiceTier { get; set; }
    public JsonNode? Store { get; set; }
}

internal sealed class BridgeResponse
{
    public string ID { get; set; } = "";
    public string Model { get; set; } = "";
    public string Text { get; set; } = "";
    public List<BridgeBlock> Reasoning { get; set; } = [];
    public List<BridgeBlock> Tools { get; set; } = [];
    public string Stop { get; set; } = "";
    public string Error { get; set; } = "";
    public Usage Usage { get; set; } = new();
    public long Created { get; set; }
}

/// <summary>
/// 共享的桥接词汇表：推理等级、响应格式与
/// ID 前缀（internal/protocol/bridge.go）。
/// </summary>
internal static class Bridge
{
    public const string ToolReasoningPlaceholder = "tool call";
    public const string AnthropicRedactedThinkingPlaceholder = "[redacted thinking]";

    private static readonly string[] ReasoningVendorHints = ["moonshot", "kimi", "deepseek", "mimo", "xiaomimimo"];

    public static string NormalizeResponsesRole(string role) => role == "assistant" ? "assistant" : "user";

    /// <summary>
    /// 工具携带的 JSON Schema，缺省为空对象模式。取值会被
    /// 克隆，因为桥中持有指向已解码客户端请求体的引用，
    /// 而一个 JSON 节点不能同时属于两棵树。
    /// </summary>
    public static JsonNode SchemaOrDefault(JsonNode? value) =>
        value?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

    /// <summary>
    /// 把桥级响应格式转换为 OpenAI Chat Completions 形态。
    /// Chat 与 Responses 对格式类型名一致，但
    /// json_schema 的嵌套不同。没有 Chat 等价形式的值（"text"）会被省略
    /// 而不是让请求失败。
    /// </summary>
    public static JsonNode? ChatResponseFormat(JsonNode? format)
    {
        if (format is not JsonObject obj)
        {
            return null;
        }
        switch (JsonValues.StringAt(obj, "type"))
        {
            case "json_object":
                return new JsonObject { ["type"] = "json_object" };
            case "json_schema":
                if (obj["json_schema"] is JsonObject nested)
                {
                    return new JsonObject { ["type"] = "json_schema", ["json_schema"] = nested.DeepClone() };
                }
                var synthesized = new JsonObject();
                foreach (var key in new[] { "name", "description", "schema", "strict" })
                {
                    if (obj.ContainsKey(key))
                    {
                        synthesized[key] = obj[key]!.DeepClone();
                    }
                }
                if (synthesized.Count == 0)
                {
                    return null;
                }
                return new JsonObject { ["type"] = "json_schema", ["json_schema"] = synthesized };
            default:
                return null;
        }
    }

    /// <summary>
    /// 把桥级响应格式转换为 Responses 的 "text.format" 字段所接受的
    /// 对象。Chat 风格的嵌套 json_schema 会被
    /// 展平成 Responses 期望的内联形态。
    /// </summary>
    public static JsonNode? ResponsesTextFormat(JsonNode? format)
    {
        if (format is not JsonObject obj)
        {
            return null;
        }
        switch (JsonValues.StringAt(obj, "type"))
        {
            case "text":
            case "json_object":
                return obj.DeepClone();
            case "json_schema":
                if (obj["json_schema"] is JsonObject nested)
                {
                    var flattened = new JsonObject();
                    foreach (var property in nested)
                    {
                        flattened[property.Key] = property.Value?.DeepClone();
                    }
                    flattened["type"] = "json_schema";
                    return flattened;
                }
                return obj.DeepClone();
            default:
                return null;
        }
    }

    /// <summary>
    /// 把桥级 reasoning 值投射到 OpenAI Chat 的
    /// "reasoning_effort" 字段。优先级很重要：客户端显式声明的
    /// 等级优先于任何由思考预算推导出的值。
    /// </summary>
    public static JsonNode? ReasoningEffort(JsonNode? value)
    {
        if (value is JsonObject obj)
        {
            var (explicitEffort, stated) = ExplicitEffort(obj);
            if (stated)
            {
                return explicitEffort;
            }
            switch (JsonValues.StringAt(obj, "type"))
            {
                case "disabled":
                    return null;
                case "enabled":
                case "adaptive":
                    return JsonValue.Create(EffortForThinkingBudget(JsonValues.IntAt(obj, "budget_tokens")));
                default:
                    return Jsonx.Get(obj, "type");
            }
        }
        var (normalized, ok) = NormalizeEffortValue(value);
        if (ok)
        {
            return normalized;
        }
        return value;
    }

    /// <summary>
    /// 报告客户端在 reasoning 对象中原样声明的等级，
    /// 以及该对象是否真的带有等级。声明过的等级优先于预算推导。
    /// null 值配合 ok=true 表示客户端写了 "none"/"disabled"，
    /// 任何推导出的等级都必须被清除。
    /// </summary>
    public static (JsonNode? Effort, bool Stated) ExplicitEffort(JsonObject obj)
    {
        foreach (var key in new[] { "effort", "reasoning_effort" })
        {
            if (obj.ContainsKey(key))
            {
                var (effort, ok) = NormalizeEffortValue(obj[key]);
                if (ok)
                {
                    return (effort, true);
                }
            }
        }
        return (null, false);
    }

    /// <summary>
    /// 校验客户端显式声明的等级。非空字符串原样
    /// 通过，因此不被支持的等级不会被静默改写；只有
    /// "none"/"disabled" 表示“不做推理”并清除该值。
    /// </summary>
    public static (JsonNode? Effort, bool Ok) NormalizeEffortValue(JsonNode? value)
    {
        if (value == null || value.GetValueKind() != JsonValueKind.String)
        {
            return (null, false);
        }
        var text = value.GetValue<string>();
        switch (text.Trim().ToLowerInvariant())
        {
            case "none":
            case "disabled":
                return (null, true);
            case "":
                return (null, false);
        }
        return (value, true);
    }

    /// <summary>
    /// 把桥级 reasoning 值映射回 Anthropic 的 "thinking" 块，
    /// 并附上原生承载等级的 "output_config"。客户端原本
    /// 声明的预算会原样恢复，而不是重新推导。
    /// </summary>
    public static (JsonNode? Thinking, JsonObject? OutputConfig) AnthropicThinking(JsonNode? value)
    {
        var budget = 0;
        if (Jsonx.IsNull(value))
        {
            return (null, null);
        }
        if (value is JsonObject obj)
        {
            var kind = JsonValues.StringAt(obj, "type");
            // 预算是无损往返的关键，因此
            // 在等级之前读取它：从等级重新推导预算会把 32000
            // 折回 16384 这一档。
            budget = JsonValues.IntAt(obj, "budget_tokens");
            var (statedEffort, stated) = ExplicitEffort(obj);
            if (stated)
            {
                // "none"/"disabled" 会彻底清除推理配置。
                if (Jsonx.IsNull(statedEffort))
                {
                    return (null, null);
                }
                return (ThinkingFor(statedEffort, budget), new JsonObject { ["effort"] = statedEffort!.DeepClone() });
            }
            if (kind == "disabled")
            {
                return (null, null);
            }
            // 写在 "type" 中的等级（Chat 的 {"reasoning":{"type":"high"}}），
            // 或只有一侧信息的 {"type":"enabled"}，其唯一控制项是预算。
            var (typeEffort, typeStated) = NormalizeEffortValue(Jsonx.Get(obj, "type"));
            if (typeStated)
            {
                if (Jsonx.IsNull(typeEffort))
                {
                    return (null, null);
                }
                return (ThinkingFor(typeEffort, budget), new JsonObject { ["effort"] = typeEffort!.DeepClone() });
            }
            if (budget > 0)
            {
                var derived = JsonValue.Create(EffortForThinkingBudget(budget))!;
                return (ThinkingFor(derived, budget), new JsonObject { ["effort"] = derived.DeepClone() });
            }
            value = Jsonx.Get(obj, "type");
        }
        var (effort, ok) = NormalizeEffortValue(value);
        if (!ok)
        {
            if (NumericValue(value, out var number))
            {
                // 只携带预算的 reasoning 值：保留该预算
                // 并发布它映射到的等级，因此不臆造档位。
                if (number <= 0)
                {
                    return (null, null);
                }
                var derived = JsonValue.Create(EffortForThinkingBudget(number))!;
                return (ThinkingFor(derived, number), new JsonObject { ["effort"] = derived.DeepClone() });
            }
            // 不是等级字符串（例如布尔值）：关于推理
            // 无话可说，因此保持沉默而不臆造等级。
            return (null, null);
        }
        if (Jsonx.IsNull(effort))
        {
            // "none"/"disabled"。
            return (null, null);
        }
        return (ThinkingFor(effort, budget), new JsonObject { ["effort"] = effort!.DeepClone() });
    }

    /// <summary>
    /// 为某个等级构造 Anthropic 的 "thinking" 块，客户端声明过预算时
    /// 复用它。数字以浮点形式写出，使
    /// 转换后的负载与同协议负载无法区分。
    /// </summary>
    public static JsonObject ThinkingFor(JsonNode? effort, int budget)
    {
        if (budget <= 0)
        {
            budget = BudgetForEffort(effort);
        }
        return new JsonObject
        {
            ["type"] = "enabled",
            ["budget_tokens"] = JsonValue.Create((double)budget),
        };
    }

    /// <summary>
    /// 报告桥级 reasoning 值声明的思考预算，没有则返回 0。
    /// 当该值不携带预算时返回 0。
    /// </summary>
    public static int ReasoningBudget(JsonNode? value)
    {
        if (value is not JsonObject obj)
        {
            return 0;
        }
        var budget = JsonValues.IntAt(obj, "budget_tokens");
        if (budget > 0)
        {
            return budget;
        }
        var (_, stated) = ExplicitEffort(obj);
        if (!stated)
        {
            return 0;
        }
        // 等级声明的是一个档位，不是预算。只发送
        // {"reasoning_effort":"high"} 的 Chat 客户端没有预算可承载。
        return NumericValueOrZero(Jsonx.Get(obj, "reasoning_budget_tokens"));
    }

    /// <summary>
    /// 给桥级 reasoning 值附上预算，并保留
    /// 其旁已有的等级。
    /// </summary>
    public static JsonNode? WithReasoningBudget(JsonNode? value, int budget)
    {
        if (budget <= 0)
        {
            return value;
        }
        if (value is JsonObject obj)
        {
            var merged = new JsonObject();
            foreach (var property in obj)
            {
                merged[property.Key] = property.Value?.DeepClone();
            }
            merged["budget_tokens"] = budget;
            return merged;
        }
        return new JsonObject { ["budget_tokens"] = budget, ["effort"] = value?.DeepClone() };
    }

    public static bool NumericValue(JsonNode? value, out int number)
    {
        number = 0;
        if (value is not JsonValue scalar || value.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }
        if (scalar.TryGetValue(out number))
        {
            return true;
        }
        if (scalar.TryGetValue(out double real))
        {
            number = (int)real;
            return true;
        }
        return false;
    }

    public static int NumericValueOrZero(JsonNode? value) => NumericValue(value, out var number) ? number : 0;

    /// <summary>
    /// EffortForThinkingBudget 的逆运算：能映射回同一档位
    /// 的最小下界预算。
    /// </summary>
    public static int BudgetForEffort(JsonNode? value)
    {
        var text = value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : "";
        return text.Trim().ToLowerInvariant() switch
        {
            "minimal" or "low" => 1024,
            "medium" => 4096,
            "high" => 8192,
            "xhigh" => 16384,
            "max" => 32768,
            _ => 4096,
        };
    }

    /// <summary>
    /// 把 Anthropic 的思考预算归入某个 OpenAI 推理等级档位。
    /// 这些档位有序且两两不同，因此客户端
    /// 特意选择的预算仍可被区分。预算缺失时保持 "high"。
    /// </summary>
    public static string EffortForThinkingBudget(int budget)
    {
        if (budget <= 0)
        {
            return "high";
        }
        if (budget >= 32768)
        {
            return "max";
        }
        if (budget >= 16384)
        {
            return "xhigh";
        }
        // 8193..16383 位于旧 "high" 边界之上；把它折入
        // "high" 会把更大的预算报告成同一个等级，且该
        // 阈值本身保留其历史归属档位。
        if (budget >= 8192)
        {
            return "high";
        }
        return budget > 2048 ? "medium" : "low";
    }

    public static string BlocksText(IEnumerable<BridgeBlock> blocks)
    {
        var text = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block.Kind == "text")
            {
                text.Append(block.Text);
            }
        }
        return text.ToString();
    }

    public static bool IsReasoningVendorIdentifier(string value)
    {
        var lower = value.ToLowerInvariant();
        return ReasoningVendorHints.Any(lower.Contains);
    }

    /// <summary>重写 ID，使其带上目标协议的前缀。</summary>
    public static string AsPrefix(string id, string prefix)
    {
        if (id.StartsWith(prefix + "_", StringComparison.Ordinal))
        {
            return id;
        }
        foreach (var current in new[] { "chatcmpl_", "resp_", "msg_" })
        {
            if (id.StartsWith(current, StringComparison.Ordinal))
            {
                id = id[current.Length..];
            }
        }
        return prefix + "_" + id;
    }

    public static string BridgeArgumentsJSON(BridgeBlock block)
    {
        if (block.ArgumentsJSON.Length > 0)
        {
            return block.ArgumentsJSON;
        }
        if (block.Arguments == null)
        {
            return "{}";
        }
        try
        {
            return block.Arguments.ToJsonString();
        }
        catch (JsonException)
        {
            return "{}";
        }
    }
}
