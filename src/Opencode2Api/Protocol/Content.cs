using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.Identity;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Protocol;

/// <summary>
/// 桥的内容块与工具选择的解码/编码
/// （internal/protocol/content.go）。
/// </summary>
internal static class Content
{
    /// <summary>解析 OpenAI 形态的 content（字符串/单对象/数组），非法块抛出 ProtocolException。</summary>
    public static List<BridgeBlock> DecodeOpenAIBlocksChecked(JsonNode? value)
    {
        switch (value)
        {
            case null:
                return [];
            case JsonValue when value.GetValueKind() == JsonValueKind.String:
            {
                var text = value.GetValue<string>();
                if (text.Length == 0)
                {
                    return [];
                }
                return [new BridgeBlock { Kind = "text", Text = text }];
            }
            case JsonObject single:
                return DecodeOpenAIBlocksChecked(new JsonArray { single });
            case JsonArray items:
            {
                var blocks = new List<BridgeBlock>();
                foreach (var raw in items)
                {
                    if (raw is not JsonObject part)
                    {
                        throw new ProtocolException("OpenAI content block must be an object");
                    }
                    switch (JsonValues.StringAt(part, "type"))
                    {
                        case "text":
                        case "input_text":
                        case "output_text":
                            blocks.Add(new BridgeBlock { Kind = "text", Text = JsonValues.StringAt(part, "text") });
                            break;
                        case "image_url":
                            blocks.Add(new BridgeBlock
                            {
                                Kind = "image",
                                URL = JsonValues.FirstString(
                                    JsonValues.StringAt(part, "image_url", "url"),
                                    JsonValues.StringAt(part, "image_url")),
                            });
                            break;
                        case "input_image":
                            blocks.Add(new BridgeBlock { Kind = "image", URL = JsonValues.StringAt(part, "image_url") });
                            break;
                        case "file":
                        case "input_file":
                        {
                            var file = JsonValues.MapAt(part, "file");
                            blocks.Add(new BridgeBlock
                            {
                                Kind = "file",
                                FileID = JsonValues.FirstString(JsonValues.StringAt(part, "file_id"), JsonValues.StringAt(file, "file_id")),
                                Filename = JsonValues.FirstString(JsonValues.StringAt(part, "filename"), JsonValues.StringAt(file, "filename")),
                                MediaType = JsonValues.FirstString(JsonValues.StringAt(part, "media_type"), JsonValues.StringAt(file, "media_type")),
                                Data = JsonValues.FirstString(JsonValues.StringAt(part, "file_data"), JsonValues.StringAt(file, "file_data")),
                            });
                            break;
                        }
                        default:
                            throw new ProtocolException($"unsupported OpenAI content block type \"{JsonValues.StringAt(part, "type")}\"");
                    }
                }
                return blocks;
            }
            case JsonValue when value.GetValueKind() == JsonValueKind.Null:
                return [];
            default:
                throw new ProtocolException("unsupported OpenAI content value");
        }
    }

    /// <summary>解析 Anthropic 形态的 content 块数组。</summary>
    public static List<BridgeBlock> DecodeAnthropicBlocksChecked(JsonNode? value)
    {
        if (value != null && value.GetValueKind() == JsonValueKind.String)
        {
            var text = value.GetValue<string>();
            return text.Length == 0 ? [] : [new BridgeBlock { Kind = "text", Text = text }];
        }
        var blocks = new List<BridgeBlock>();
        if (Jsonx.IsNull(value))
        {
            return blocks;
        }
        if (value is not JsonArray items)
        {
            throw new ProtocolException("Anthropic content block must be an object");
        }
        foreach (var raw in items)
        {
            if (raw is not JsonObject part)
            {
                throw new ProtocolException("Anthropic content block must be an object");
            }
            switch (JsonValues.StringAt(part, "type"))
            {
                case "thinking":
                    blocks.Add(new BridgeBlock
                    {
                        Kind = "reasoning",
                        Text = JsonValues.StringAt(part, "thinking"),
                        Signature = JsonValues.StringAt(part, "signature"),
                    });
                    break;
                case "redacted_thinking":
                    blocks.Add(new BridgeBlock { Kind = "reasoning", Encrypted = JsonValues.StringAt(part, "data") });
                    break;
                case "text":
                    blocks.Add(new BridgeBlock { Kind = "text", Text = JsonValues.StringAt(part, "text") });
                    break;
                case "image":
                {
                    var source = JsonValues.MapAt(part, "source");
                    if (JsonValues.StringAt(source, "type") == "url")
                    {
                        blocks.Add(new BridgeBlock { Kind = "image", URL = JsonValues.StringAt(source, "url") });
                    }
                    else
                    {
                        blocks.Add(new BridgeBlock
                        {
                            Kind = "image",
                            MediaType = JsonValues.StringAt(source, "media_type"),
                            Data = JsonValues.StringAt(source, "data"),
                        });
                    }
                    break;
                }
                case "document":
                {
                    var source = JsonValues.MapAt(part, "source");
                    if (JsonValues.StringAt(source, "type") == "text")
                    {
                        blocks.Add(new BridgeBlock
                        {
                            Kind = "text",
                            Text = JsonValues.FirstString(JsonValues.StringAt(source, "data"), JsonValues.StringAt(source, "text")),
                        });
                        continue;
                    }
                    blocks.Add(new BridgeBlock
                    {
                        Kind = "file",
                        URL = JsonValues.StringAt(source, "url"),
                        MediaType = JsonValues.StringAt(source, "media_type"),
                        Data = JsonValues.FirstString(JsonValues.StringAt(source, "data"), JsonValues.StringAt(source, "text")),
                        Filename = JsonValues.StringAt(part, "title"),
                    });
                    break;
                }
                case "tool_use":
                    blocks.Add(new BridgeBlock
                    {
                        Kind = "tool_call",
                        ID = JsonValues.StringAt(part, "id"),
                        Name = JsonValues.StringAt(part, "name"),
                        Arguments = Jsonx.Get(part, "input"),
                    });
                    break;
                case "tool_result":
                    blocks.Add(new BridgeBlock
                    {
                        Kind = "tool_result",
                        CallID = JsonValues.StringAt(part, "tool_use_id"),
                        Result = Jsonx.Get(part, "content"),
                        IsError = JsonValues.BoolAt(part, "is_error"),
                    });
                    break;
                default:
                    throw new ProtocolException($"unsupported Anthropic content block type \"{JsonValues.StringAt(part, "type")}\"");
            }
        }
        return blocks;
    }

    public static List<BridgeBlock> DecodeChatReasoning(JsonObject? message)
    {
        var value = Jsonx.Get(message, "reasoning_content") ?? Jsonx.Get(message, "reasoning");
        if (value == null || value.GetValueKind() != JsonValueKind.String)
        {
            return [];
        }
        return
        [
            new BridgeBlock
            {
                Kind = "reasoning",
                Text = value.GetValue<string>(),
                Signature = JsonValues.StringAt(message, "reasoning_signature"),
            },
        ];
    }

    public static List<BridgeBlock> DecodeResponsesReasoning(JsonObject? item)
    {
        var text = new StringBuilder();
        if (Jsonx.Get(item, "summary") is JsonArray summary)
        {
            foreach (var raw in summary)
            {
                text.Append(JsonValues.StringAt(raw as JsonObject, "text"));
            }
        }
        if (Jsonx.Get(item, "content") is JsonArray content)
        {
            foreach (var raw in content)
            {
                text.Append(JsonValues.StringAt(raw as JsonObject, "text"));
            }
        }
        var encrypted = JsonValues.StringAt(item, "encrypted_content");
        if (text.Length == 0 && encrypted.Length == 0)
        {
            return [];
        }
        return
        [
            new BridgeBlock
            {
                Kind = "reasoning",
                ID = JsonValues.StringAt(item, "id"),
                Text = text.ToString(),
                Encrypted = encrypted,
            },
        ];
    }

    /// <summary>
    /// 为 Responses 请求体渲染 reasoning 块。对既不携带服务端签发的
    /// id 也不携带加密内容的块返回 false：
    /// 用合成 id 重放它们只会被判为过期的 reasoning 引用。
    /// </summary>
    public static (JsonObject? Item, bool Ok) EncodeResponsesReasoningInput(BridgeBlock block)
    {
        if (block.ID.Length == 0 && block.Encrypted.Length == 0)
        {
            return (null, false);
        }
        return (EncodeResponsesReasoning(block, false), true);
    }

    public static JsonObject EncodeResponsesReasoning(BridgeBlock block, bool completed)
    {
        var id = block.ID.Length > 0 ? block.ID : RequestIds.RandomId("rs", 12);
        var summary = new JsonArray();
        if (block.Text.Length > 0)
        {
            summary.Add(new JsonObject { ["type"] = "summary_text", ["text"] = block.Text });
        }
        var item = new JsonObject { ["id"] = id, ["type"] = "reasoning", ["summary"] = summary };
        if (block.Encrypted.Length > 0)
        {
            item["encrypted_content"] = block.Encrypted;
        }
        if (completed)
        {
            item["status"] = "completed";
        }
        return item;
    }

    public static (string Text, bool Ok) BridgeReasoningText(List<BridgeBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return ("", false);
        }
        var text = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block.Text.Length > 0)
            {
                text.Append(block.Text);
            }
            else if (block.Encrypted.Length > 0)
            {
                text.Append(Bridge.AnthropicRedactedThinkingPlaceholder);
            }
        }
        return (text.ToString(), true);
    }

    public static string BridgeReasoningSignature(List<BridgeBlock> blocks)
    {
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i].Signature.Length > 0)
            {
                return blocks[i].Signature;
            }
        }
        return "";
    }

    public static JsonNode EncodeChatBlocks(List<BridgeBlock> blocks)
    {
        if (blocks.Count == 1 && blocks[0].Kind == "text")
        {
            return JsonValue.Create(blocks[0].Text)!;
        }
        var parts = new JsonArray();
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case "text":
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = block.Text });
                    break;
                case "image":
                {
                    var url = block.URL;
                    if (url.Length == 0 && block.Data.Length > 0)
                    {
                        url = "data:" + block.MediaType + ";base64," + block.Data;
                    }
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = url },
                    });
                    break;
                }
                case "file":
                {
                    var file = new JsonObject();
                    if (block.FileID.Length > 0)
                    {
                        file["file_id"] = block.FileID;
                    }
                    if (block.Data.Length > 0)
                    {
                        file["file_data"] = block.Data;
                    }
                    if (block.Filename.Length > 0)
                    {
                        file["filename"] = block.Filename;
                    }
                    parts.Add(new JsonObject { ["type"] = "file", ["file"] = file });
                    break;
                }
            }
        }
        return parts;
    }

    public static JsonArray EncodeAnthropicBlocks(List<BridgeBlock> blocks)
    {
        var parts = new JsonArray();
        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case "text":
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = block.Text });
                    break;
                case "image":
                    if (block.URL.Length > 0)
                    {
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject { ["type"] = "url", ["url"] = block.URL },
                        });
                    }
                    else
                    {
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = block.MediaType,
                                ["data"] = block.Data,
                            },
                        });
                    }
                    break;
                case "file":
                {
                    JsonObject? source = null;
                    if (block.URL.Length > 0)
                    {
                        source = new JsonObject { ["type"] = "url", ["url"] = block.URL };
                    }
                    else if (block.Data.Length > 0)
                    {
                        source = new JsonObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = JsonValues.FirstString(block.MediaType, "application/octet-stream"),
                            ["data"] = block.Data,
                        };
                    }
                    if (source != null)
                    {
                        var part = new JsonObject { ["type"] = "document", ["source"] = source };
                        if (block.Filename.Length > 0)
                        {
                            part["title"] = block.Filename;
                        }
                        parts.Add(part);
                    }
                    break;
                }
                case "tool_call":
                {
                    var input = block.Arguments ?? JsonValues.ParseJsonOrString(block.ArgumentsJSON);
                    parts.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = block.ID,
                        ["name"] = block.Name,
                        ["input"] = input?.DeepClone(),
                    });
                    break;
                }
                case "tool_result":
                {
                    JsonNode? content = block.Result switch
                    {
                        List<BridgeBlock> imageBlocks => EncodeAnthropicBlocks(imageBlocks),
                        JsonNode node => node.DeepClone(),
                        _ => null,
                    };
                    parts.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = block.CallID,
                        ["content"] = content,
                        ["is_error"] = block.IsError,
                    });
                    break;
                }
                case "reasoning":
                    if (block.Encrypted.Length > 0)
                    {
                        parts.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = block.Encrypted });
                        continue;
                    }
                    parts.Add(new JsonObject
                    {
                        ["type"] = "thinking",
                        ["thinking"] = block.Text,
                        ["signature"] = block.Signature,
                    });
                    break;
            }
        }
        return parts;
    }

    /// <summary>把原始工具结果展平成 Chat 工具轮次所承载的文本。</summary>
    public static string BridgeToolResultContent(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case JsonNode node when node.GetValueKind() == JsonValueKind.String:
                return node.GetValue<string>();
            case JsonArray parts:
            {
                var text = new StringBuilder();
                var allText = true;
                foreach (var raw in parts)
                {
                    if (raw is not JsonObject part || JsonValues.StringAt(part, "type") != "text")
                    {
                        allText = false;
                        break;
                    }
                    text.Append(JsonValues.StringAt(part, "text"));
                }
                if (allText)
                {
                    return text.ToString();
                }
                break;
            }
        }
        if (value is JsonNode json)
        {
            try
            {
                return json.ToJsonString();
            }
            catch (JsonException)
            {
                return "";
            }
        }
        return "";
    }

    public static BridgeToolChoice DecodeChatToolChoice(JsonNode? value)
    {
        if (value != null && value.GetValueKind() == JsonValueKind.String)
        {
            return new BridgeToolChoice(value.GetValue<string>());
        }
        if (value is JsonObject choice && JsonValues.StringAt(choice, "type") == "function")
        {
            return new BridgeToolChoice("named", JsonValues.StringAt(choice, "function", "name"));
        }
        return new BridgeToolChoice();
    }

    public static BridgeToolChoice DecodeResponsesToolChoice(JsonNode? value)
    {
        if (value != null && value.GetValueKind() == JsonValueKind.String)
        {
            return new BridgeToolChoice(value.GetValue<string>());
        }
        if (value is JsonObject choice && JsonValues.StringAt(choice, "type") == "function")
        {
            return new BridgeToolChoice("named", JsonValues.StringAt(choice, "name"));
        }
        return new BridgeToolChoice();
    }

    public static BridgeToolChoice DecodeAnthropicToolChoice(JsonNode? value)
    {
        if (value != null && value.GetValueKind() == JsonValueKind.String)
        {
            var mode = value.GetValue<string>();
            if (mode == "required")
            {
                mode = "any";
            }
            return DecodeAnthropicToolChoice(new JsonObject { ["type"] = mode });
        }
        if (value is JsonObject choice)
        {
            switch (JsonValues.StringAt(choice, "type"))
            {
                case "auto":
                    return new BridgeToolChoice("auto");
                case "none":
                    return new BridgeToolChoice("none");
                case "any":
                    return new BridgeToolChoice("required");
                case "tool":
                    return new BridgeToolChoice("named", JsonValues.StringAt(choice, "name"));
            }
        }
        return new BridgeToolChoice();
    }

    public static JsonNode? EncodeChatToolChoice(BridgeToolChoice choice) => choice.Mode switch
    {
        "named" => new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject { ["name"] = choice.Name },
        },
        "auto" or "none" or "required" => JsonValue.Create(choice.Mode),
        _ => null,
    };

    public static JsonNode? EncodeResponsesToolChoice(BridgeToolChoice choice) => choice.Mode switch
    {
        "named" => new JsonObject { ["type"] = "function", ["name"] = choice.Name },
        "auto" or "none" or "required" => JsonValue.Create(choice.Mode),
        _ => null,
    };

    public static JsonNode? EncodeAnthropicToolChoice(BridgeToolChoice choice) => choice.Mode switch
    {
        "named" => new JsonObject { ["type"] = "tool", ["name"] = choice.Name },
        "required" => new JsonObject { ["type"] = "any" },
        "auto" => new JsonObject { ["type"] = choice.Mode },
        _ => null,
    };
}
