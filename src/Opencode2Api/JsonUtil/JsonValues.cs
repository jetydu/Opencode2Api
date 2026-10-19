using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Opencode2Api.JsonUtil;

/// <summary>
/// 已解码 JSON 取值的访问器（internal/jsonutil）。所有辅助方法
/// 都做空安全处理，对应 Go 宽容的类型断言。
/// </summary>
public static class JsonValues
{
    public static int FirstNonZero(params int[] values)
    {
        foreach (var value in values)
        {
            if (value != 0)
            {
                return value;
            }
        }
        return 0;
    }

    /// <summary>把 JSON 文档字符串解析为节点，失败时回退为原始字符串值。</summary>
    public static JsonNode? ParseJsonOrString(string value)
    {
        if (value.Length == 0)
        {
            return new JsonObject();
        }
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }

    public static JsonNode? FirstAny(params JsonNode?[] values)
    {
        foreach (var value in values)
        {
            if (value != null)
            {
                return value;
            }
        }
        return null;
    }

    /// <summary>对应 Go 的 SliceAt：缺失或非数组的值表现为空切片，因此返回的数组总能用于迭代与计数。</summary>
    public static JsonArray SliceAt(JsonObject? obj, params string[] path) => AnyAt(obj, path) as JsonArray ?? new JsonArray();

    public static JsonArray? ArrayAt(JsonObject? obj, params string[] path) => AnyAt(obj, path) as JsonArray;

    public static JsonObject? MapAt(JsonObject? obj, params string[] path) => AnyAt(obj, path) as JsonObject;

    public static string StringAt(JsonObject? obj, params string[] path) => AnyAt(obj, path)?.GetValueKind() == JsonValueKind.String ? AnyAt(obj, path)!.GetValue<string>() : "";

    public static bool BoolAt(JsonObject? obj, params string[] path)
    {
        var node = AnyAt(obj, path);
        return node?.GetValueKind() is JsonValueKind.True or JsonValueKind.False && node!.GetValue<bool>();
    }

    public static int IntAt(JsonObject? obj, params string[] path) => (int)(NumberAt(obj, path) ?? 0);

    public static long Int64At(JsonObject? obj, params string[] path) => (long)(NumberAt(obj, path) ?? 0);

    /// <summary>可选数字：缺失或不是数字时返回 null（价格类字段需要区分“未配置”与“配置为 0”）。</summary>
    public static double? OptionalNumber(JsonObject? obj, params string[] path) => NumberAt(obj, path);

    /// <summary>数字取值的唯一实现：非数字或缺失一律返回 null（对齐 Go 的宽容类型断言）。</summary>
    private static double? NumberAt(JsonObject? obj, string[] path)
    {
        var node = AnyAt(obj, path);
        if (node != null && node.GetValueKind() == JsonValueKind.Number
            && double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
        return null;
    }

    public static JsonNode? AnyAt(JsonObject? obj, params string[] path)
    {
        JsonNode? current = obj;
        foreach (var key in path)
        {
            if (current is not JsonObject next)
            {
                return null;
            }
            current = next[key];
        }
        return current;
    }

    public static string FirstString(params string[] values)
    {
        foreach (var value in values)
        {
            var trimmed = value.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }
        return "";
    }
}
