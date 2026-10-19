using System.Text.Json;
using System.Text.Json.Nodes;

namespace Opencode2Api.Protocol;

/// <summary>
/// 协议桥的 JSON 树拆装。Go 只能通过赋值风格区分
/// 缺失的 map 值与显式 JSON null（Put 跳过 nil，
/// 直接赋值会存储它），而 JsonNode 不能挂到第二个父节点上，
/// 因此每次转移都要克隆。
/// </summary>
internal static class Jsonx
{
    /// <summary>
    /// JSON null 以 null 节点引用承载：JsonObject 与 JsonArray
    /// 会把它序列化为 JSON null，同时仍报告键或槽位存在，
    /// 这正好对应 Go 区分 nil 与缺失 map 项的方式。
    /// </summary>
    public static bool IsNull(JsonNode? node) => node == null || node.GetValueKind() == JsonValueKind.Null;

    /// <summary>对应 jsonutil.Put：值不承载任何东西时省略该键。</summary>
    public static void Put(JsonObject target, string key, JsonNode? value)
    {
        if (!IsNull(value))
        {
            target[key] = value!.DeepClone();
        }
    }

    /// <summary>按 Go 读取 map 键的方式读取取值：缺失与 null 都视为 nil。</summary>
    public static JsonNode? Get(JsonObject? source, string key)
    {
        if (source == null)
        {
            return null;
        }
        var value = source[key];
        return IsNull(value) ? null : value;
    }

    /// <summary>
    /// 宽容解析 JSON 文本：解析失败或顶层不是对象时返回 null。
    /// 对应 Go 中忽略 json.Unmarshal 错误、只把“没拿到对象”当作无效处理的调用点。
    /// </summary>
    public static JsonObject? TryParseObject(string? text) => ParseObject(text ?? "", out _);

    /// <summary>TryParseObject 的字节序列重载。</summary>
    public static JsonObject? TryParseObject(ReadOnlySpan<byte> body) => ParseObject(body, out _);

    /// <summary>
    /// 解析 JSON 文本为对象，失败时通过 out 参数返回异常（对应 Go 的 v, err := json.Unmarshal 形态），
    /// 让调用方能区分“报文不可解析”与“根本不是对象”。
    /// </summary>
    public static JsonObject? ParseObject(string text, out Exception? error)
    {
        error = null;
        try
        {
            return JsonNode.Parse(text)?.AsObject();
        }
        catch (Exception parseError) when (parseError is JsonException or InvalidOperationException)
        {
            error = parseError;
            return null;
        }
    }

    /// <summary>ParseObject 的字节序列重载。</summary>
    public static JsonObject? ParseObject(ReadOnlySpan<byte> body, out Exception? error)
    {
        error = null;
        try
        {
            return JsonNode.Parse(body)?.AsObject();
        }
        catch (Exception parseError) when (parseError is JsonException or InvalidOperationException)
        {
            error = parseError;
            return null;
        }
    }
}
