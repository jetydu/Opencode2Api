using Opencode2Api.Protocol;

namespace Opencode2Api.Telemetry;

/// <summary>
/// 跨 HTTP 与网关层共享的请求局部跟踪信息
/// （telemetry.RequestMeta）。存放在 HttpContext.Items 上。
/// </summary>
public sealed class RequestMeta
{
    public const string ItemKey = "opencode2api.request_meta";

    public string ModelAlias { get; set; } = "";
    public string Model { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Request { get; set; } = "";
    public string Session { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string Channel { get; set; } = "";
    public bool Anonymous { get; set; }
    public string Proxy { get; set; } = "";
    public int Attempts { get; set; }
    public List<string> Legs { get; } = [];
    public string Stop { get; set; } = "";
    public string Tail { get; set; } = "";
    public bool Stream { get; set; }
    public string ServerKeyId { get; set; } = "";
    public bool Shaped { get; set; }
    public string ToolAlias { get; set; } = "";
    public Usage Usage { get; set; } = new();
    public bool UsageReported { get; set; }
    public string AttemptOutcome { get; set; } = "";
    public string Outcome { get; set; } = "";

    public static RequestMeta? FromContext(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) ? value as RequestMeta : null;

    public static void IntoContext(HttpContext context, RequestMeta meta) => context.Items[ItemKey] = meta;
}

/// <summary>轻量级请求计数器（telemetry.Monitor，admin 切片已移除）。</summary>
public sealed class Monitor
{
    private long _active;
    private long _activeStreams;
    private ulong _total;
    private ulong _success;
    private ulong _errors;

    public DateTime StartedAt { get; } = DateTime.UtcNow;

    public long Active => Interlocked.Read(ref _active);

    public void BeginStream() => Interlocked.Increment(ref _activeStreams);

    public void EndStream() => Interlocked.Decrement(ref _activeStreams);

    public long ActiveStreams => Interlocked.Read(ref _activeStreams);

    public void Enter() => Interlocked.Increment(ref _active);

    public void Leave() => Interlocked.Decrement(ref _active);

    public void Record(string endpoint, int status, TimeSpan duration, RequestMeta? meta)
    {
        var succeeded = RequestSucceeded(status, meta);
        Interlocked.Increment(ref _total);
        if (succeeded)
        {
            Interlocked.Increment(ref _success);
        }
        else
        {
            Interlocked.Increment(ref _errors);
        }
    }

    public static bool RequestSucceeded(int status, RequestMeta? meta) =>
        status >= 200 && status < 400 && (meta == null || meta.Outcome.Length == 0);

    public static string RequestOutcome(int status, string channel)
    {
        if (channel.Length == 0 || channel == "not_routed")
        {
            return "not_routed";
        }
        if (status >= 200 && status < 400)
        {
            return "success";
        }
        if (status >= 400 && status < 500)
        {
            return "client_error";
        }
        if (status >= 500)
        {
            return "server_error";
        }
        return "unknown";
    }
}
