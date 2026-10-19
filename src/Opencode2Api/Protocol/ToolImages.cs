using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Protocol;

/// <summary>
/// 携带图像的工具结果（internal/protocol/tool_images.go）。只有
/// 带图像的结果走这条路径；旧的文本与不透明结果
/// 原样返回。
/// </summary>
internal static class ToolImages
{
    /// <summary>
    /// 校验并解码工具结果请求体。返回值要么
    /// 是原节点（无图像、无需改写），要么是桥内容块列表，
    /// 其中的图像 data URI 已拆成媒体类型与负载。
    /// </summary>
    public static object? Decode(string from, JsonNode? value)
    {
        if (value is not JsonArray parts)
        {
            return value;
        }
        var imageType = "input_image";
        var textType = "input_text";
        if (from == Wire.Anthropic)
        {
            imageType = "image";
            textType = "text";
        }
        var hasImage = false;
        foreach (var raw in parts)
        {
            hasImage = hasImage || JsonValues.StringAt(raw as JsonObject, "type") == imageType;
        }
        if (!hasImage)
        {
            return value;
        }
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] is not JsonObject part)
            {
                throw new ProtocolException($"content[{i}] must be an object");
            }
            switch (JsonValues.StringAt(part, "type"))
            {
                case var kind when kind == textType:
                    if (Jsonx.Get(part, "text")?.GetValueKind() != JsonValueKind.String)
                    {
                        throw new ProtocolException($"content[{i}] requires text");
                    }
                    break;
                case var kind when kind == imageType:
                    if (from == Wire.Anthropic)
                    {
                        var source = JsonValues.MapAt(part, "source");
                        var valid = JsonValues.StringAt(source, "type") switch
                        {
                            "url" => JsonValues.StringAt(source, "url").Length > 0
                                && Jsonx.IsNull(source?["data"]) && Jsonx.IsNull(source?["file_id"]),
                            "base64" => JsonValues.StringAt(source, "data").Length > 0
                                && JsonValues.StringAt(source, "media_type").StartsWith("image/", StringComparison.Ordinal)
                                && Jsonx.IsNull(source?["url"]) && Jsonx.IsNull(source?["file_id"]),
                            _ => false,
                        };
                        if (!valid)
                        {
                            throw new ProtocolException($"content[{i}] has an unsupported or conflicting image source");
                        }
                    }
                    else if (JsonValues.StringAt(part, "image_url").Length == 0 || !Jsonx.IsNull(part["file_id"]))
                    {
                        throw new ProtocolException($"content[{i}] requires an image URL without a file reference");
                    }
                    break;
                default:
                    throw new ProtocolException($"content[{i}] contains an unsupported block alongside tool images");
            }
        }
        if (from == Wire.Anthropic)
        {
            return Content.DecodeAnthropicBlocksChecked(value);
        }
        var blocks = Content.DecodeOpenAIBlocksChecked(value);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.Kind != "image" || !block.URL.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            var rest = block.URL["data:".Length..];
            var comma = rest.IndexOf(',');
            if (comma < 0)
            {
                throw new ProtocolException($"content[{i}] has an invalid image data URI");
            }
            var header = rest[..comma];
            var data = rest[(comma + 1)..];
            var base64 = header.EndsWith(";base64", StringComparison.Ordinal);
            var media = base64 ? header[..^";base64".Length] : header;
            if (!base64 || !media.StartsWith("image/", StringComparison.Ordinal) || data.Length == 0)
            {
                throw new ProtocolException($"content[{i}] has an invalid image data URI");
            }
            block.URL = "";
            block.MediaType = media;
            block.Data = data;
        }
        return blocks;
    }

    /// <summary>
    /// 把一个 Chat 工具轮次拆成文本部分，加上作为后续 user 内容
    /// 追加的图像块，每块都由一个稳定标记引出，使模型
    /// 能把图像归属到产生它的那次调用。
    /// </summary>
    public static (string Text, List<BridgeBlock>? Images) ChatToolImages(BridgeBlock result)
    {
        if (result.Result is not List<BridgeBlock> parts)
        {
            return (Content.BridgeToolResultContent(result.Result), null);
        }
        var text = new StringBuilder();
        var images = new List<BridgeBlock>();
        var n = 0;
        foreach (var part in parts)
        {
            if (part.Kind == "text")
            {
                text.Append(part.Text);
                continue;
            }
            n++;
            var marker = $"[tool_image call_id={Bridge.BridgeArgumentsJSON(new BridgeBlock { Arguments = JsonValue.Create(result.CallID) })} index={n}]";
            text.Append('\n').Append(marker).Append('\n');
            images.Add(new BridgeBlock { Kind = "text", Text = marker });
            images.Add(part);
        }
        return (text.ToString(), images);
    }

    /// <summary>把工具结果请求体渲染为 Responses 输入内容。</summary>
    public static JsonNode ResponsesToolContent(object? value)
    {
        if (value is not List<BridgeBlock> parts)
        {
            return JsonValue.Create(Content.BridgeToolResultContent(value))!;
        }
        var content = new JsonArray();
        foreach (var part in parts)
        {
            if (part.Kind == "text")
            {
                content.Add(new JsonObject { ["type"] = "input_text", ["text"] = part.Text });
            }
            else
            {
                var url = part.URL;
                if (url.Length == 0)
                {
                    url = "data:" + part.MediaType + ";base64," + part.Data;
                }
                content.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = url });
            }
        }
        return content;
    }
}
