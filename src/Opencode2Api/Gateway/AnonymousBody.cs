using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.Config;
using Opencode2Api.Http;
using Opencode2Api.Identity;
using Opencode2Api.JsonUtil;
using Opencode2Api.Models;
using Opencode2Api.Protocol;

namespace Opencode2Api.Gateway;

/// <summary>
/// 免费档请求塑形与出站请求构造
/// （upstream.go 的匿名线路辅助方法与 newUpstreamRequest）。
/// </summary>
internal static class AnonymousBody
{
    /// <summary>匿名线路用于认证的凭据字面量。</summary>
    public const string ZenKey = "public";

    /// <summary>旧版 /zen/v1 免费档门槛要求的单个工具名。</summary>
    private const string LegacyReadTool = "read";

    private const string LegacyDeprecatedReadDescription =
        "DEPRECATED: This tool is deprecated and disabled. Do NOT call or reference this tool under any circumstances; ignore it completely.";

    /// <summary>
    /// /inference 门槛在智能体形态请求上期望的工具名：缺少 shell 与 read
    /// 工具的请求会被 403
    /// FreeTierError 拒绝。自 2026-10-14 起只有名字也不够，门槛会把
    /// 每个可识别工具的描述与参数模式与官方 CLI 定义
    /// 逐字节比对，这正是内嵌
    /// 真实定义的原因。
    /// </summary>
    private static readonly string[] CoreTools = ["shell", "edit", "glob", "grep", "read"];

    private static readonly Lazy<JsonObject?> Tools = new(LoadTools);

    private static JsonObject? LoadTools()
    {
        var assembly = typeof(AnonymousBody).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(entry => entry.EndsWith("opencode_tools.json", StringComparison.OrdinalIgnoreCase));
        if (name == null)
        {
            return null;
        }
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream == null)
        {
            return null;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // 内嵌工具定义解析失败时按“没有定义”处理，不影响主流程。
        return Jsonx.TryParseObject(reader.ReadToEnd());
    }

    /// <summary>若工具目录认识某个 OpenCode 工具名，返回其真实定义。</summary>
    private static (string Description, JsonNode? Parameters)? KnownTool(string name)
    {
        if (Tools.Value == null || !Tools.Value.TryGetPropertyValue(name, out var entry) || entry is not JsonObject obj)
        {
            return null;
        }
        var description = JsonValues.StringAt(obj, "description");
        var parameters = JsonValues.MapAt(obj, "parameters");
        return (description, parameters?.DeepClone());
    }

    /// <summary>
    /// 报告基础 URL 指向的是旧版 /zen/v1 门槛，而不是
    /// 较新的 /inference 门槛。
    /// </summary>
    public static bool IsLegacyZenUpstream(string baseURL)
    {
        var trimmed = baseURL.Trim().TrimEnd('/');
        if (string.Equals(trimmed, Discovery.LegacyZenBase.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return trimmed.EndsWith("/zen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 返回为匿名免费档归一化后的请求体副本，以及在旧版线路上被
    /// 别名为 "read" 的客户端工具名，便于网关在响应中
    /// 还原它。System One 负载是决策请求，不是智能体流量，
    /// 原样转发。
    /// </summary>
    public static (byte[] Body, string Alias) Prepare(byte[] body, string protocol, bool legacy)
    {
        if (protocol == Wire.SystemOne)
        {
            return (body, "");
        }
        var payload = Jsonx.TryParseObject(body);
        if (payload == null)
        {
            return (body, "");
        }
        var changed = false;
        if (!JsonValues.BoolAt(payload, "stream"))
        {
            payload["stream"] = true;
            changed = true;
        }
        if (EnsureChatUsage(payload, protocol))
        {
            changed = true;
        }
        var alias = "";
        if (legacy)
        {
            var (toolAlias, toolChanged) = EnsureLegacyReadTool(payload, protocol);
            alias = toolAlias;
            if (toolChanged)
            {
                changed = true;
            }
        }
        else if (EnsureTools(payload, protocol))
        {
            changed = true;
        }
        if (!changed)
        {
            return (body, alias);
        }
        // 无法编码的负载保留原始字节并放弃别名：请求体从未离开网关，
        // 下游没有需要还原的内容。
        try
        {
            return (Response.ToUtf8(payload), alias);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return (body, "");
        }
    }

    /// <summary>
    /// 当非流式请求被强制改走 Chat SSE 线路时保留 token 用量：
    /// Chat 流需要 stream_options.include_usage 才会输出最后的
    /// 用量事件。
    /// </summary>
    private static bool EnsureChatUsage(JsonObject payload, string protocol)
    {
        if (protocol != Wire.Chat)
        {
            return false;
        }
        if (payload["stream_options"] is not JsonObject options)
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
            return true;
        }
        if (JsonValues.BoolAt(options, "include_usage"))
        {
            return false;
        }
        options["include_usage"] = true;
        return true;
    }

    /// <summary>
    /// 用单个 "read" 要求满足旧版 /zen/v1 门槛：名为 "read" 的工具无需处理，
    /// read 相关工具就地改名（保留其描述与参数模式）并返回原名，
    /// 供响应还原使用；其余情况补充一个已废弃的占位
    /// 桩。
    /// </summary>
    private static (string Alias, bool Changed) EnsureLegacyReadTool(JsonObject payload, string protocol)
    {
        if (!payload.ContainsKey("tools"))
        {
            var injected = new JsonArray();
            injected.Add(LegacyPlaceholderTool(protocol));
            payload["tools"] = injected;
            return ("", true);
        }
        if (payload["tools"] is not JsonArray items)
        {
            return ("", false);
        }
        var candidateIndex = -1;
        var candidateName = "";
        for (var index = 0; index < items.Count; index++)
        {
            var name = ToolName(protocol, items[index]);
            if (name.Length == 0)
            {
                continue;
            }
            if (name == LegacyReadTool)
            {
                return ("", false);
            }
            if (string.Equals(name, LegacyReadTool, StringComparison.OrdinalIgnoreCase))
            {
                candidateIndex = index;
                candidateName = name;
                break;
            }
            if (candidateIndex < 0 && IsReadToolCandidate(name))
            {
                candidateIndex = index;
                candidateName = name;
            }
        }
        if (candidateIndex >= 0)
        {
            SetToolName(protocol, items[candidateIndex], LegacyReadTool);
            RewriteToolReferences(payload, protocol, candidateName, LegacyReadTool);
            return (candidateName, true);
        }
        items.Add(LegacyPlaceholderTool(protocol));
        return ("", true);
    }

    /// <summary>
    /// 判断客户端工具名是否与 read 相关，例如 "reAd"、"read_file"、
    /// "ReadFile"、"file_read" 或 "view_file"。
    /// </summary>
    private static bool IsReadToolCandidate(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        if (lower.Length == 0)
        {
            return false;
        }
        if (lower == "read")
        {
            return true;
        }
        var normalized = lower.Replace("_", "").Replace("-", "").Replace(".", "");
        switch (normalized)
        {
            case "readfile":
            case "fileread":
            case "readfiles":
            case "readtext":
            case "readtextfile":
            case "readcontent":
            case "viewfile":
                return true;
        }
        return lower.StartsWith("read_", StringComparison.Ordinal) || lower.StartsWith("read-", StringComparison.Ordinal)
            || lower.EndsWith("_read", StringComparison.Ordinal) || lower.EndsWith("-read", StringComparison.Ordinal);
    }

    private static void SetToolName(string protocol, JsonNode? item, string name)
    {
        if (item is not JsonObject entry)
        {
            return;
        }
        if (protocol == Wire.Chat)
        {
            if (entry["function"] is JsonObject function)
            {
                function["name"] = name;
            }
            return;
        }
        entry["name"] = name;
    }

    /// <summary>
    /// 同步改写具名 tool_choice 与助手工具调用历史条目，使客户端 read 工具
    /// 被别名之后出站请求仍保持自洽。
    /// </summary>
    private static void RewriteToolReferences(JsonObject payload, string protocol, string fromName, string toName)
    {
        if (fromName.Length == 0 || fromName == toName)
        {
            return;
        }
        switch (protocol)
        {
            case Wire.Chat:
                if (JsonValues.MapAt(payload, "tool_choice") is { } choice
                    && JsonValues.MapAt(choice, "function") is { } choiceFunction
                    && JsonValues.StringAt(choiceFunction, "name") == fromName)
                {
                    choiceFunction["name"] = toName;
                }
                foreach (var rawMessage in JsonValues.SliceAt(payload, "messages"))
                {
                    if (rawMessage is not JsonObject message)
                    {
                        continue;
                    }
                    foreach (var rawCall in JsonValues.SliceAt(message, "tool_calls"))
                    {
                        if (rawCall is JsonObject call && JsonValues.MapAt(call, "function") is { } function
                            && JsonValues.StringAt(function, "name") == fromName)
                        {
                            function["name"] = toName;
                        }
                    }
                }
                break;
            case Wire.Responses:
                if (JsonValues.MapAt(payload, "tool_choice") is { } responsesChoice
                    && JsonValues.StringAt(responsesChoice, "name") == fromName)
                {
                    responsesChoice["name"] = toName;
                }
                foreach (var rawItem in JsonValues.SliceAt(payload, "input"))
                {
                    if (rawItem is JsonObject item && JsonValues.StringAt(item, "type") == "function_call"
                        && JsonValues.StringAt(item, "name") == fromName)
                    {
                        item["name"] = toName;
                    }
                }
                break;
            case Wire.Anthropic:
                if (JsonValues.MapAt(payload, "tool_choice") is { } anthropicChoice
                    && JsonValues.StringAt(anthropicChoice, "name") == fromName)
                {
                    anthropicChoice["name"] = toName;
                }
                foreach (var rawMessage in JsonValues.SliceAt(payload, "messages"))
                {
                    if (rawMessage is not JsonObject message)
                    {
                        continue;
                    }
                    foreach (var rawBlock in JsonValues.SliceAt(message, "content"))
                    {
                        if (rawBlock is JsonObject block && JsonValues.StringAt(block, "type") == "tool_use"
                            && JsonValues.StringAt(block, "name") == fromName)
                        {
                            block["name"] = toName;
                        }
                    }
                }
                break;
        }
    }

    private static JsonObject LegacyPlaceholderTool(string protocol) =>
        Tool(protocol, LegacyReadTool, LegacyDeprecatedReadDescription, EmptyParameters());

    private static JsonObject EmptyParameters() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    /// <summary>
    /// 让工具列表在 /inference 门槛看来像一个 OpenCode 2.x 智能体会话：
    /// 每个可识别工具都带上真实 CLI 定义，缺失的核心工具被追加，
    /// 无法识别的客户端工具原样保留。
    /// </summary>
    private static bool EnsureTools(JsonObject payload, string protocol)
    {
        if (!payload.ContainsKey("tools"))
        {
            payload["tools"] = Toolset(protocol, null);
            return true;
        }
        if (payload["tools"] is not JsonArray items)
        {
            return false;
        }
        var changed = false;
        var present = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            var name = ToolName(protocol, items[index]);
            if (name.Length == 0)
            {
                continue;
            }
            present.Add(name);
            if (KnownTool(name) != null)
            {
                items[index] = ToolFor(protocol, name);
                changed = true;
            }
        }
        var missing = CoreTools.Where(name => !present.Contains(name)).ToList();
        if (missing.Count > 0)
        {
            foreach (var name in missing)
            {
                // 按名称逐个构造：已带有父节点的节点不能被移动
                // 进客户端的数组。
                items.Add(ToolFor(protocol, name));
            }
            changed = true;
        }
        return changed;
    }

    private static string ToolName(string protocol, JsonNode? item)
    {
        if (item is not JsonObject entry)
        {
            return "";
        }
        return protocol == Wire.Chat ? JsonValues.StringAt(entry, "function", "name") : JsonValues.StringAt(entry, "name");
    }

    private static JsonArray Toolset(string protocol, IEnumerable<string>? names)
    {
        var tools = new JsonArray();
        foreach (var name in names ?? CoreTools)
        {
            tools.Add(ToolFor(protocol, name));
        }
        return tools;
    }

    /// <summary>
    /// 按协议期望的形态渲染单个工具，优先使用真实的
    /// OpenCode CLI 定义。System One 负载不会走到这条路径。
    /// </summary>
    private static JsonObject ToolFor(string protocol, string name)
    {
        var definition = KnownTool(name);
        return Tool(protocol, name,
            definition?.Description ?? "Agent tool " + name,
            definition?.Parameters ?? EmptyParameters());
    }

    private static JsonObject Tool(string protocol, string name, string description, JsonNode? parameters)
    {
        return protocol switch
        {
            Wire.Chat => new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = parameters,
                },
            },
            Wire.Anthropic => new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["input_schema"] = parameters,
            },
            _ => new JsonObject
            {
                ["type"] = "function",
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = parameters,
            },
        };
    }
}

/// <summary>
/// 构造出站上游请求，包括旧版 /zen/v1 与较新的 /inference
/// 两条门槛各自期望的两套请求头形态。
/// </summary>
internal static class UpstreamRequest
{
    public static HttpRequestMessage New(string baseURL, string protocol, byte[] body, RequestIds ids, string key)
    {
        var endpoint = baseURL.TrimEnd('/') + Wire.Path(protocol);
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation("x-opencode-client", "cli");
        request.Headers.TryAddWithoutValidation("x-opencode-session", ids.Session);
        request.Headers.TryAddWithoutValidation("x-session-affinity", ids.Session);
        request.Headers.TryAddWithoutValidation("X-Session-Id", ids.Session);
        if (AnonymousBody.IsLegacyZenUpstream(baseURL))
        {
            // 旧版线路沿用 v1.3.7 的 2.x 之前 OpenCode 1.18.x 请求形态：
            // 旧版 User-Agent、x-opencode-request 以及规范的
            // prj_* 项目 ID。
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgents.Legacy());
            request.Headers.TryAddWithoutValidation("x-opencode-request", ids.Request);
            request.Headers.TryAddWithoutValidation("x-opencode-project", LegacyProjectID(ids.Project));
        }
        else
        {
            // /inference 门槛按 2.x CLI 的头部集合做指纹识别：
            // "opencode/<channel>/<version>/cli" 形式的 User-Agent、两个会话头
            // 使用同一个 ID、裸 40 位十六进制项目 ID，并且会以 403
            // FreeTierError 拒绝 x-opencode-request 与规范的 prj_* ID。
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgents.Current());
            request.Headers.TryAddWithoutValidation("x-opencode-session-id", ids.Session);
            request.Headers.TryAddWithoutValidation("x-opencode-project", ClientProjectID(ids.Project));
        }
        if (ids.ParentSession.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("x-parent-session-id", ids.ParentSession);
        }
        if (protocol == Wire.Anthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            request.Headers.TryAddWithoutValidation("anthropic-beta",
                "interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14");
        }
        else
        {
            // 匿名凭据是字面量令牌 "public"；门槛拒绝无密钥请求，
            // 并且区分大小写。
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        }
        return request;
    }

    /// <summary>以 v1.3.7 的规范 prj_&lt;24hex&gt; 形态渲染项目请求头。</summary>
    internal static string LegacyProjectID(string project) =>
        project.StartsWith("prj_", StringComparison.Ordinal) ? project : RequestIds.StableId("prj", project);

    /// <summary>
    /// 按 2.x CLI 的方式渲染项目请求头：裸 40 位
    /// 小写十六进制 ID。上游只校验形态，但哈希可以让同一段
    /// 会话始终绑定同一个项目。
    /// </summary>
    internal static string ClientProjectID(string project)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes("opencode-project\0" + project));
        return Convert.ToHexStringLower(sum.AsSpan(0, 20));
    }
}
