using System.Text.Json.Nodes;
using Opencode2Api;
using Opencode2Api.Config;
using Opencode2Api.JsonUtil;
using Opencode2Api.Protocol;

namespace Opencode2Api.Tests;

/// <summary>
/// JSON 宽容取值与协议常量
/// （对应 Go 的 internal/jsonutil 与 internal/protocol/protocol.go）。
/// 配置文件的注释剥离与严格绑定已随 IConfiguration 一起移除，
/// 相关用例转移到 ConfigLoadTests。
/// </summary>
public sealed class JsonHelperTests
{
    private const string Sample = """
        {
          "model": "gpt-x",
          "nested": { "count": 7, "flag": true, "list": [1, 2] },
          "document": "{\"a\":1}",
          "note": null
        }
        """;

    private static JsonObject Root() => (JsonNode.Parse(Sample)!.AsObject());

    [Theory]
    [InlineData("model", "gpt-x")]
    [InlineData("missing", "")]
    [InlineData("note", "")]
    public void StringAt_缺失与非字符串都返回空(string key, string expected)
    {
        Assert.Equal(expected, JsonValues.StringAt(Root(), key));
    }

    [Fact]
    public void 取值器按路径下钻并做空安全处理()
    {
        var root = Root();

        Assert.Equal(7, JsonValues.IntAt(root, "nested", "count"));
        Assert.Equal(7L, JsonValues.Int64At(root, "nested", "count"));
        Assert.True(JsonValues.BoolAt(root, "nested", "flag"));
        Assert.False(JsonValues.BoolAt(root, "nested", "missing"));
        Assert.Equal(2, JsonValues.SliceAt(root, "nested", "list").Count);
        // 非数组一律当空切片，调用方可以无脑遍历。
        Assert.Empty(JsonValues.SliceAt(root, "nested", "count"));
        Assert.Null(JsonValues.ArrayAt(root, "nested", "flag"));
        Assert.NotNull(JsonValues.MapAt(root, "nested"));
        Assert.Equal(7.0, JsonValues.OptionalNumber(root, "nested", "count"));
        Assert.Null(JsonValues.OptionalNumber(root, "nested", "flag"));
        Assert.Null(JsonValues.AnyAt(root, "nested", "nope"));
        Assert.Null(JsonValues.AnyAt(root, "nope", "deeper"));
    }

    [Fact]
    public void First_系列辅助方法跳过空值()
    {
        Assert.Equal("b", JsonValues.FirstString("", "  b  ", "c"));
        Assert.Equal("", JsonValues.FirstString("", "   "));
        Assert.Equal(5, JsonValues.FirstNonZero(0, 0, 5, 9));
        Assert.Equal(0, JsonValues.FirstNonZero(0, 0));

        var first = JsonValue.Create(1);
        Assert.Same(first, JsonValues.FirstAny(null, first));
        Assert.Null(JsonValues.FirstAny(null, null));
    }

    [Fact]
    public void ParseJsonOrString_解析失败时回退原始字符串()
    {
        Assert.IsType<JsonObject>(JsonValues.ParseJsonOrString("""{"a":1}"""));
        Assert.Equal(2, JsonValues.ParseJsonOrString("[1,2]")!.AsArray().Count);
        Assert.Equal("不是 JSON", JsonValues.ParseJsonOrString("不是 JSON")!.GetValue<string>());
        Assert.IsType<JsonObject>(JsonValues.ParseJsonOrString(""));
    }

    [Fact]
    public void Jsonx_宽容解析对象并区分缺失与_null()
    {
        Assert.NotNull(Jsonx.TryParseObject("""{"a":1}"""));
        Assert.Null(Jsonx.TryParseObject("[1]"));
        Assert.Null(Jsonx.TryParseObject("坏了"));
        Assert.Null(Jsonx.TryParseObject((string?)null));
        Assert.NotNull(Jsonx.TryParseObject(System.Text.Encoding.UTF8.GetBytes("""{"a":1}""")));

        var error = Jsonx.ParseObject("坏了", out var caught);
        Assert.Null(error);
        Assert.NotNull(caught);

        var root = JsonNode.Parse(@"{""a"":null,""b"":2}")!.AsObject();
        Assert.True(Jsonx.IsNull(root["a"]));
        Assert.False(Jsonx.IsNull(root["b"]));
        // 与 Go 的 jsonutil.Put 一致：null 值不写键。
        Jsonx.Put(root, "a", null);
        Assert.True(root.ContainsKey("a"));
        Jsonx.Put(root, "c", JsonValue.Create(3));
        Assert.Equal(3, Jsonx.Get(root, "c")!.GetValue<int>());
        Assert.Null(Jsonx.Get(root, "a"));
        Assert.Null(Jsonx.Get(null, "x"));
    }

    [Fact]
    public void Durations_补齐_BCL_缺少的二参重载()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), Durations.Max(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(1), Durations.Min(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(Wire.Chat, "/v1/chat/completions")]
    [InlineData(Wire.Responses, "/v1/responses")]
    [InlineData(Wire.Anthropic, "/v1/messages")]
    [InlineData(Wire.SystemOne, "/v1/systemone")]
    [InlineData("未知协议", "/v1/chat/completions")]
    public void Wire_协议常量与路径映射(string protocol, string path)
    {
        Assert.Equal(path, Wire.Path(protocol));
    }

    [Fact]
    public void Wire_与_Tier_的取值校验()
    {
        Assert.True(Wire.IsValid(Wire.Anthropic));
        Assert.False(Wire.IsValid("messages"));
        Assert.True(Tier.IsValid(Tier.Zen));
        Assert.False(Tier.IsValid("anonymous"));
        Assert.Equal("zen,go", string.Join(",", Tier.All));
    }
}
