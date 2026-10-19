using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Models;

/// <summary>
/// 模型每百万 token 的公开价格。字段为 null 表示
/// 元数据未给出（models/discovery.go Cost）。
/// </summary>
public sealed class Cost
{
    public double? Input { get; set; }
    public double? Output { get; set; }
    public double? CacheRead { get; set; }
    public double? CacheWrite { get; set; }

    public Cost Clone() => new()
    {
        Input = Input,
        Output = Output,
        CacheRead = CacheRead,
        CacheWrite = CacheWrite,
    };

    public JsonObject ToJson()
    {
        var obj = new JsonObject();
        if (Input != null)
        {
            obj["input"] = JsonValue.Create(Input.Value);
        }
        if (Output != null)
        {
            obj["output"] = JsonValue.Create(Output.Value);
        }
        if (CacheRead != null)
        {
            obj["cache_read"] = JsonValue.Create(CacheRead.Value);
        }
        if (CacheWrite != null)
        {
            obj["cache_write"] = JsonValue.Create(CacheWrite.Value);
        }
        return obj;
    }

    public static Cost? FromJson(JsonObject? obj)
    {
        if (obj == null)
        {
            return null;
        }
        var cost = new Cost
        {
            Input = JsonValues.OptionalNumber(obj, "input"),
            Output = JsonValues.OptionalNumber(obj, "output"),
            CacheRead = JsonValues.OptionalNumber(obj, "cache_read"),
            CacheWrite = JsonValues.OptionalNumber(obj, "cache_write"),
        };
        return cost.Input == null && cost.Output == null && cost.CacheRead == null && cost.CacheWrite == null ? null : cost;
    }
}

/// <summary>
/// 通过 /v1/models 暴露的按模型能力字段，使调用方从目录
/// 拿到真实的上下文窗口与能力标记，而不是猜测。
/// 纯附加信息：路由不依赖其中任何字段
/// （models/discovery.go Metadata）。
/// </summary>
public sealed class Metadata
{
    public int ContextWindow { get; set; }
    public int MaxInput { get; set; }
    public int MaxOutput { get; set; }
    public bool Reasoning { get; set; }
    public bool ToolCall { get; set; }
    public bool StructuredOutput { get; set; }
    public List<string> InputModalities { get; set; } = [];
    public List<string> OutputModalities { get; set; } = [];

    /// <summary>读取时由 models.dev 填充，单位为美元/百万 token。</summary>
    public Cost? Cost { get; set; }

    public Metadata Clone() => new()
    {
        ContextWindow = ContextWindow,
        MaxInput = MaxInput,
        MaxOutput = MaxOutput,
        Reasoning = Reasoning,
        ToolCall = ToolCall,
        StructuredOutput = StructuredOutput,
        InputModalities = [.. InputModalities],
        OutputModalities = [.. OutputModalities],
        Cost = Cost?.Clone(),
    };

    public JsonObject ToJson()
    {
        var obj = new JsonObject();
        if (ContextWindow != 0)
        {
            obj["context_window"] = ContextWindow;
        }
        if (MaxInput != 0)
        {
            obj["max_input"] = MaxInput;
        }
        if (MaxOutput != 0)
        {
            obj["max_output"] = MaxOutput;
        }
        if (Reasoning)
        {
            obj["reasoning"] = true;
        }
        if (ToolCall)
        {
            obj["tool_call"] = true;
        }
        if (StructuredOutput)
        {
            obj["structured_output"] = true;
        }
        if (InputModalities.Count > 0)
        {
            obj["input_modalities"] = new JsonArray([.. InputModalities.Select(m => (JsonNode?)JsonValue.Create(m))]);
        }
        if (OutputModalities.Count > 0)
        {
            obj["output_modalities"] = new JsonArray([.. OutputModalities.Select(m => (JsonNode?)JsonValue.Create(m))]);
        }
        if (Cost != null)
        {
            obj["cost"] = Cost.ToJson();
        }
        return obj;
    }

    public static Metadata FromJson(JsonObject? obj)
    {
        if (obj == null)
        {
            return new Metadata();
        }
        return new Metadata
        {
            ContextWindow = JsonValues.IntAt(obj, "context_window"),
            MaxInput = JsonValues.IntAt(obj, "max_input"),
            MaxOutput = JsonValues.IntAt(obj, "max_output"),
            Reasoning = JsonValues.BoolAt(obj, "reasoning"),
            ToolCall = JsonValues.BoolAt(obj, "tool_call"),
            StructuredOutput = JsonValues.BoolAt(obj, "structured_output"),
            InputModalities = StringList(obj, "input_modalities"),
            OutputModalities = StringList(obj, "output_modalities"),
            Cost = Cost.FromJson(JsonValues.MapAt(obj, "cost")),
        };
    }

    internal static List<string> StringList(JsonObject obj, params string[] path)
    {
        var result = new List<string>();
        if (JsonValues.ArrayAt(obj, path) is not JsonArray array)
        {
            return result;
        }
        foreach (var item in array)
        {
            if (item?.GetValueKind() == JsonValueKind.String)
            {
                result.Add(item.GetValue<string>());
            }
        }
        return result;
    }
}
