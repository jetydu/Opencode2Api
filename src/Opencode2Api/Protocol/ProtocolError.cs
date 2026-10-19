using Microsoft.AspNetCore.Http;
using Opencode2Api.Http;

namespace Opencode2Api.Protocol;

/// <summary>协议形态的错误响应（internal/protocol/errors.go）。</summary>
public static class ProtocolError
{
    public static async Task Write(HttpContext context, string protocol, int status, string message, string kind, string requestId)
    {
        if (!context.Response.HasStarted)
        {
            context.Response.ContentType = "application/json";
            if (requestId.Length > 0)
            {
                context.Response.Headers["x-request-id"] = requestId;
            }
        }
        if (protocol == Wire.Anthropic)
        {
            await HttpResponses.WriteJsonStatus(context, status, new Dictionary<string, object?>
            {
                ["type"] = "error",
                ["error"] = new Dictionary<string, object?> { ["type"] = kind, ["message"] = message },
            });
            return;
        }
        await HttpResponses.WriteJsonStatus(context, status, new Dictionary<string, object?>
        {
            ["error"] = new Dictionary<string, object?>
            {
                ["message"] = message,
                ["type"] = kind,
                ["param"] = null,
                ["code"] = null,
            },
        });
    }
}

/// <summary>桥内共享的 token 用量记账（protocol.Usage）。</summary>
public sealed class Usage
{
    // Input 是提示输入总量，包含缓存读与缓存写，
    // 与 OpenAI 的 prompt_tokens 语义一致。
    public int Input { get; set; }
    public int Output { get; set; }
    public int Total { get; set; }
    public int Cached { get; set; }
    public int CacheCreation { get; set; }
    public int Reasoning { get; set; }
}
