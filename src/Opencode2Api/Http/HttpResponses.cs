using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Opencode2Api.Http;

/// <summary>共享的 HTTP 响应与传输辅助方法（internal/httpx）。</summary>
public static class HttpResponses
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteJson(HttpContext context, int status, object? value)
    {
        context.Response.ContentType = "application/json";
        await WriteJsonStatus(context, status, value);
    }

    public static async Task WriteJsonStatus(HttpContext context, int status, object? value)
    {
        context.Response.StatusCode = status;
        var payload = value switch
        {
            JsonNode node => node.ToJsonString(SerializerOptions),
            _ => JsonSerializer.Serialize(value, SerializerOptions),
        };
        await context.Response.WriteAsync(payload, Utf8NoBom);
    }

    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

    public static async Task<bool> WriteSse(HttpContext context, string? eventName, ulong id, object? value)
    {
        var data = value switch
        {
            JsonNode node => node.ToJsonString(),
            null => "null",
            _ => JsonSerializer.Serialize(value, SerializerOptions),
        };
        try
        {
            if (id > 0)
            {
                await context.Response.WriteAsync($"id: {id}\n");
            }
            if (!string.IsNullOrEmpty(eventName))
            {
                await context.Response.WriteAsync($"event: {eventName}\n");
            }
            await context.Response.WriteAsync($"data: {data}\n\n");
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>排空并释放上游正文，使连接归还给连接池。</summary>
    public static async Task DrainAndClose(Stream? body)
    {
        if (body == null)
        {
            return;
        }
        await using (body)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                long total = 0;
                int read;
                while (total < 64L * 1024 && (read = await body.ReadAsync(buffer.AsMemory()[..(int)Math.Min(buffer.Length, 64 * 1024 - total)])) > 0)
                {
                    total += read;
                }
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

/// <summary>上游线路的 User-Agent 形态（httpx/client.go）。</summary>
public static class UserAgents
{
    // clientVersion 是网关在
    // 只在被拒绝时上调。
    private const string ClientVersion = "2.0.29";
    private const string LegacyClientVersion = "1.18.31";

    public static string Current() => "opencode/latest/" + ClientVersion + "/cli";

    public static string Legacy() =>
        $"opencode/{LegacyClientVersion} ({RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}; .NET {Environment.Version})";
}
