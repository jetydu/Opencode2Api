using System.Text.RegularExpressions;
using System.Text.Json;
using Opencode2Api.Config;
using Opencode2Api.Http;
using Opencode2Api.JsonUtil;
using Opencode2Api.Protocol;
using System.Text.Json.Nodes;

namespace Opencode2Api.Models;

internal static class TierMaps
{
    public static Dictionary<string, T> New<T>() => new() { [Tier.Zen] = default!, [Tier.Go] = default! };
}

/// <summary>
/// 由发现辅助方法抛出。状态码让刷新循环可以区分
/// 被吊销的密钥（401/403）与瞬时上游故障
/// （models/discovery.go 的错误返回）。
/// </summary>
public sealed class DiscoveryException(string message, int statusCode = 0) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>上游目录的机器可读协议/元数据视图。</summary>
public sealed class Capabilities
{
    public Dictionary<string, Dictionary<string, string>> Protocols { get; } =
        new() { [Tier.Zen] = [], [Tier.Go] = [] };
    public Dictionary<string, Dictionary<string, bool>> Unsupported { get; } =
        new() { [Tier.Zen] = [], [Tier.Go] = [] };
    public Dictionary<string, Dictionary<string, Metadata>> Metadata { get; } =
        new() { [Tier.Zen] = [], [Tier.Go] = [] };
}

/// <summary>
/// 上游能力发现（models/discovery.go）。本项目不保留
/// 任何模型 ID：每个条目都从 OpenCode 自己的端点学习。
/// </summary>
public static class Discovery
{
    public const string CapabilitiesURL = "https://models.opencode.ai/api.json";
    public const string ZenDocsURL = "https://raw.githubusercontent.com/anomalyco/opencode/dev/packages/web/src/content/docs/zen.mdx";
    public const string GoDocsURL = "https://raw.githubusercontent.com/anomalyco/opencode/dev/packages/web/src/content/docs/go.mdx";

    // LegacyZenBase 是旧的 /zen 控制台源（服务 /zen/v1/*），
    // 它与较新的 /inference 后端并存，仍提供匿名推理
    // 与模型列表。
    public const string LegacyZenBase = "https://opencode.ai/zen";

    private static readonly Regex ProtocolDocEndpointPattern = new(
        @"\|[^|]+\|\s*`?([^|`\s]+)`?\s*\|\s*`[^`]+/v1/(chat/completions|responses|messages|systemone)`",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> DocProtocolByName = new()
    {
        ["chat/completions"] = Wire.Chat,
        ["responses"] = Wire.Responses,
        ["messages"] = Wire.Anthropic,
        ["systemone"] = Wire.SystemOne,
    };

    /// <summary>
    /// 读取 OpenCode 的机器可读提供方目录。与 /v1/models 不同，这个
    /// 来源包含每个模型所选的 SDK，即上游对
    /// 协议的声明。
    /// </summary>
    public static async Task<Capabilities> FetchCapabilitiesAsync(HttpClient client, string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgents.Current());
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode / 100 != 2)
        {
            throw new DiscoveryException($"OpenCode capability endpoint returned HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        }
        var data = await ReadLimitedAsync(response, 64L << 20, cancellationToken);
        var root = JsonNode.Parse(data)?.AsObject()
            ?? throw new DiscoveryException("decode OpenCode capabilities: expected a JSON object");

        var result = new Capabilities();
        foreach (var providerProperty in root)
        {
            if (providerProperty.Value is not JsonObject provider)
            {
                continue;
            }
            var tier = CapabilityTier(providerProperty.Key, JsonValues.StringAt(provider, "api"));
            if (tier == null)
            {
                continue;
            }
            var providerNpm = JsonValues.StringAt(provider, "npm");
            if (JsonValues.MapAt(provider, "models") is not JsonObject models)
            {
                continue;
            }
            foreach (var modelProperty in models)
            {
                if (modelProperty.Value is not JsonObject model)
                {
                    continue;
                }
                var modelId = JsonValues.FirstString(JsonValues.StringAt(model, "id"));
                if (modelId.Length == 0)
                {
                    modelId = modelProperty.Key;
                }
                var npm = providerNpm;
                var overrideNpm = JsonValues.StringAt(JsonValues.MapAt(model, "provider")!, "npm");
                if (overrideNpm.Length > 0)
                {
                    npm = overrideNpm;
                }
                var protocol = ProtocolForSdk(npm);
                if (protocol != null)
                {
                    result.Protocols[tier][modelId] = protocol;
                }
                else
                {
                    result.Unsupported[tier][modelId] = true;
                }
                result.Metadata[tier][modelId] = ModelMetadata(model);
            }
        }

        // 机器目录是主要来源。上游端点表是补充来源，
        // 用于那些提供方继承了默认 SDK、
        // 但已发布端点更具体的模型（例如一条
        // Messages 线路）。这仍然是数据驱动的：不内嵌任何模型 ID。
        foreach (var (tier, url) in new[] { (Tier.Zen, ZenDocsURL), (Tier.Go, GoDocsURL) })
        {
            Dictionary<string, string> protocols;
            try
            {
                protocols = await FetchProtocolDocsAsync(client, url, cancellationToken);
            }
            catch (Exception e) when (e is DiscoveryException or HttpRequestException or OperationCanceledException or JsonException)
            {
                continue;
            }
            foreach (var (modelId, protocol) in protocols)
            {
                result.Protocols[tier][modelId] = protocol;
                result.Unsupported[tier].Remove(modelId);
            }
        }
        if (result.Protocols[Tier.Zen].Count == 0 && result.Protocols[Tier.Go].Count == 0 &&
            result.Unsupported[Tier.Zen].Count == 0 && result.Unsupported[Tier.Go].Count == 0)
        {
            throw new DiscoveryException("OpenCode capability endpoint returned no Zen or Go models");
        }
        return result;
    }

    public static async Task<Dictionary<string, string>> FetchProtocolDocsAsync(HttpClient client, string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("Accept", "text/plain, text/markdown, */*");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgents.Current());
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode / 100 != 2)
        {
            throw new DiscoveryException($"OpenCode endpoint documentation returned HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        }
        var body = System.Text.Encoding.UTF8.GetString(await ReadLimitedAsync(response, 8L << 20, cancellationToken));
        var result = new Dictionary<string, string>();
        foreach (var line in body.Split('\n'))
        {
            var match = ProtocolDocEndpointPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }
            var modelId = match.Groups[1].Value.Trim();
            if (modelId.Length == 0 || modelId.IndexOfAny([' ', '`', '|']) >= 0)
            {
                continue;
            }
            if (DocProtocolByName.TryGetValue(match.Groups[2].Value, out var protocol))
            {
                result[modelId] = protocol;
            }
        }
        if (result.Count == 0)
        {
            throw new DiscoveryException("OpenCode endpoint documentation returned no protocol rows");
        }
        return result;
    }

    /// <summary>列出某条线路的 /v1/models 所发布的模型 ID。</summary>
    public static async Task<List<string>> FetchModelsAsync(HttpClient client, string baseURL, string key, CancellationToken cancellationToken)
    {
        var trimmed = baseURL.Trim().TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, trimmed + "/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        request.Headers.TryAddWithoutValidation("User-Agent",
            string.Equals(trimmed, LegacyZenBase.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith("/zen", StringComparison.OrdinalIgnoreCase)
                ? UserAgents.Legacy()
                : UserAgents.Current());
        request.Headers.TryAddWithoutValidation("x-opencode-client", "cli");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode / 100 != 2)
        {
            throw new DiscoveryException($"models endpoint returned HTTP {(int)response.StatusCode}", (int)response.StatusCode);
        }
        var data = await ReadLimitedAsync(response, 8L << 20, cancellationToken);
        var root = JsonNode.Parse(data)?.AsObject();
        var models = new List<string>();
        if (JsonValues.ArrayAt(root, "data") is JsonArray items)
        {
            foreach (var item in items)
            {
                var id = item is JsonObject obj ? JsonValues.StringAt(obj, "id") : "";
                if (id.Length > 0)
                {
                    models.Add(id);
                }
            }
        }
        if (models.Count == 0)
        {
            throw new DiscoveryException("models endpoint returned an empty list", (int)response.StatusCode);
        }
        return models;
    }

    public static string? CapabilityTier(string providerID, string api)
    {
        var value = (providerID + " " + api).Trim().ToLowerInvariant();
        if (value.Contains("opencode-go") || value.Contains("/go/"))
        {
            return Tier.Go;
        }
        if (value.Contains("opencode") || value.Contains("/zen/"))
        {
            return Tier.Zen;
        }
        return null;
    }

    public static string? ProtocolForSdk(string npm)
    {
        var value = npm.Trim().ToLowerInvariant();
        if (value.Contains("anthropic"))
        {
            return Wire.Anthropic;
        }
        if (value == "@ai-sdk/openai" || value.EndsWith("/openai"))
        {
            return Wire.Responses;
        }
        if (value.Contains("openai-compatible"))
        {
            return Wire.Chat;
        }
        return null;
    }

    private static Metadata ModelMetadata(JsonObject model)
    {
        var md = new Metadata
        {
            Reasoning = JsonValues.BoolAt(model, "reasoning"),
            ToolCall = JsonValues.BoolAt(model, "tool_call"),
            StructuredOutput = JsonValues.BoolAt(model, "structured_output"),
        };
        if (JsonValues.MapAt(model, "modalities") is JsonObject modalities)
        {
            md.InputModalities = Metadata.StringList(modalities, "input");
            md.OutputModalities = Metadata.StringList(modalities, "output");
        }
        if (JsonValues.MapAt(model, "limit") is JsonObject limit)
        {
            md.ContextWindow = JsonValues.IntAt(limit, "context");
            md.MaxInput = JsonValues.IntAt(limit, "input");
            md.MaxOutput = JsonValues.IntAt(limit, "output");
        }
        return md;
    }

    /// <summary>最多读取 <paramref name="limit"/> 字节，对应 Go 的 LimitReader。</summary>
    internal static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while (buffer.Length < limit && (read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length)), cancellationToken)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }
}
