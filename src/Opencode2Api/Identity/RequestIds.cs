using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Identity;

/// <summary>
/// 推导稳定的会话标识（internal/identity/request.go）。
///
/// Zen 免费档门槛会校验一个倒序时间戳的会话 ID 加上
/// 客户端 User-Agent，并对携带旧版 x-opencode-request 的请求
/// 返回 403。Session 是对话亲和键，保持
/// CanonicalSessionId 生成的倒序布局。
/// </summary>
public sealed class RequestIds
{
    public string Session { get; init; } = "";
    public string Request { get; init; } = "";
    public string Project { get; init; } = "";
    public string ParentSession { get; init; } = "";

    public static RequestIds Derive(HttpContext httpContext, JsonObject body)
    {
        var headers = httpContext.Request.Headers;
        var signal = JsonValues.FirstString(
            headers.TryGetValue("x-opencode-session", out var v1) ? v1.ToString() : "",
            headers.TryGetValue("x-opencode-session-id", out var v2) ? v2.ToString() : "",
            headers.TryGetValue("x-session-affinity", out var v3) ? v3.ToString() : "",
            headers.TryGetValue("X-Session-Id", out var v4) ? v4.ToString() : "",
            headers.TryGetValue("x-session-id", out var v5) ? v5.ToString() : "",
            headers.TryGetValue("conversation-id", out var v6) ? v6.ToString() : "",
            JsonValues.StringAt(body, "conversation_id"),
            JsonValues.StringAt(body, "metadata", "session_id"));
        if (signal.Length == 0)
        {
            // 使用第一轮用户消息可以让多轮对话保持稳定。
            signal = ConversationSeed(body);
        }
        if (signal.Length == 0)
        {
            signal = JsonValues.StringAt(body, "previous_response_id");
        }
        if (signal.Length == 0 || signal == "{}")
        {
            signal = RandomId("fallback", 16);
        }
        var session = CanonicalSessionId(signal);
        // 上游免费档会以 403 拒绝规范的 prj_* 项目 ID，
        // 但接受字面量 "global"。项目请求头只作为
        // 本地关联提示使用，不再向上游转发。
        var projectSignal = JsonValues.FirstString(
            headers.TryGetValue("x-opencode-project", out var project) ? project.ToString() : "",
            JsonValues.StringAt(body, "metadata", "project_id"));
        if (projectSignal.Length == 0)
        {
            projectSignal = "opencode2api:default-project";
        }
        var parentSession = JsonValues.FirstString(
            headers.TryGetValue("x-parent-session-id", out var parent) ? parent.ToString() : "",
            JsonValues.StringAt(body, "metadata", "parent_session_id"));
        return new RequestIds
        {
            Session = session,
            Request = RandomId("req", 16),
            Project = projectSignal,
            ParentSession = parentSession,
        };
    }

    public static string ConversationSeed(JsonObject body)
    {
        if (body["input"] is JsonValue inputValue && inputValue.GetValueKind() == JsonValueKind.String)
        {
            var text = inputValue.GetValue<string>();
            if (text.Length > 0)
            {
                return text;
            }
        }
        foreach (var field in new[] { "messages", "input" })
        {
            foreach (var raw in JsonValues.ArrayAt(body, field) ?? new JsonArray())
            {
                if (raw is not JsonObject item || JsonValues.StringAt(item, "role") != "user")
                {
                    continue;
                }
                var encoded = item["content"]?.ToJsonString();
                if (!string.IsNullOrEmpty(encoded) && encoded != "null")
                {
                    return encoded;
                }
            }
        }
        return "";
    }

    public static string StableId(string prefix, string value)
    {
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(prefix + "\0" + value));
        return prefix + "_" + Convert.ToHexString(sum[..12]).ToLowerInvariant();
    }

    private static readonly Regex CanonicalSessionPattern =
        new(@"^ses_[0-9a-f]{12}[0-9A-Za-z]{14}$", RegexOptions.Compiled);

    private static readonly Regex DescendingTimePartPattern =
        new(@"^[0-9a-f]{9}f[c-f][0-9a-f]$", RegexOptions.Compiled);

    private const string Base62Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// 当标识已经是官方 OpenCode 会话时原样返回（
    /// 保留上游提示缓存亲和）。其他任何标识都会被确定性地
    /// 重新编码为该精确布局 —— 取反的 48 位毫秒
    /// 时间戳，计数器为 1。
    /// </summary>
    public static string CanonicalSessionId(string signal)
    {
        if (CanonicalSessionPattern.IsMatch(signal))
        {
            if (DescendingTimePartPattern.IsMatch(signal[4..16]))
            {
                return signal;
            }
            // 形状正确但编码来自外部：继续往下并重新编码。
        }
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes("ses\0" + signal));
        var timePart = DescendingTimePart(sum[..6]);
        var randomPart = Base62Fixed(new BigInteger(sum[6..16].ToArray(), isUnsigned: true, isBigEndian: true), 14);
        return "ses_" + timePart + randomPart;
    }

    private static string DescendingTimePart(byte[] hash)
    {
        ulong current = ((ulong)hash[0] << 28 | (ulong)hash[1] << 20 | (ulong)hash[2] << 12 |
                         (ulong)hash[3] << 4 | (ulong)hash[4] >> 4) << 12;
        current |= 1;
        var inverted = ~current & 0xFFFFFFFFFFFF;
        return Convert.ToString((long)inverted, 16).PadLeft(12, '0');
    }

    private static string Base62Fixed(BigInteger n, int width)
    {
        var output = new char[width];
        var base62 = new BigInteger(62);
        for (var i = width - 1; i >= 0; i--)
        {
            n = BigInteger.DivRem(n, base62, out var remainder);
            output[i] = Base62Alphabet[(int)remainder];
        }
        return new string(output);
    }

    public static string RandomId(string prefix, int size)
    {
        var buffer = RandomNumberGenerator.GetBytes(size);
        return prefix + "_" + Convert.ToHexString(buffer).ToLowerInvariant();
    }
}
