using System.Globalization;
using Opencode2Api.Config;

namespace Opencode2Api.Gateway;

/// <summary>
/// 让密钥保持稳定，同时允许其代理绑定在代理变得不可用时
/// 原子地改变（pool.go upstreamNode）。
/// </summary>
internal sealed class UpstreamNode
{
    private long _proxyIndex;
    private long _failures;
    private long _cooldownUntil;

    public required string Key { get; init; }
    public required int Index { get; init; }
    public required int PreferredProxy { get; init; }

    public int ProxyIndex
    {
        get => (int)Interlocked.Read(ref _proxyIndex);
        set => Interlocked.Exchange(ref _proxyIndex, value);
    }

    public long FailureCount => Interlocked.Read(ref _failures);

    public long IncrementFailures() => Interlocked.Increment(ref _failures);

    public void ResetFailures() => Interlocked.Exchange(ref _failures, 0);

    /// <summary>UTC tick 表示的绝对截止时间；零表示“就绪”。</summary>
    public long CooldownUntil
    {
        get => Interlocked.Read(ref _cooldownUntil);
        set => Interlocked.Exchange(ref _cooldownUntil, value);
    }
}

/// <summary>
/// 按轮询顺序把密钥分布到各代理上。当密钥数量
/// 少于代理时，多余的代理保持空闲，一旦活动代理失败
/// 即可立即接管某个密钥（pool.go nodePool）。
/// </summary>
internal sealed class NodePool
{
    private readonly object _bindingsMutex = new();
    private long _next;

    public List<UpstreamNode> Nodes { get; } = [];
    public TransportPool Transports { get; }
    public TimeSpan Cooldown { get; }

    private readonly int[] _bindingCount;

    private NodePool(TransportPool transports, TimeSpan cooldown)
    {
        Transports = transports;
        Cooldown = cooldown;
        _bindingCount = new int[transports.Items.Count];
    }

    /// <summary>对应 Go 的 newNodePool；没有可用线路的传输池是致命错误。</summary>
    public static NodePool Create(IReadOnlyList<string> keys, TransportPool? transports, TimeSpan cooldown)
    {
        if (transports == null || transports.Items.Count == 0)
        {
            throw new GatewayException("at least one proxy transport is required");
        }
        var pool = new NodePool(transports, cooldown);
        for (var i = 0; i < keys.Count; i++)
        {
            var proxyIndex = i % pool.Transports.Items.Count;
            var node = new UpstreamNode
            {
                Key = keys[i],
                Index = i,
                PreferredProxy = proxyIndex,
                ProxyIndex = proxyIndex,
            };
            pool.Nodes.Add(node);
            pool._bindingCount[proxyIndex]++;
        }
        return pool;
    }

    /// <summary>
    /// 把最初分配给已恢复代理的密钥迁回该代理。
    /// 没有原始密钥的代理只是重新成为健康的故障转移候选。
    /// </summary>
    public int RestoreProxy(int recoveredProxy)
    {
        if (recoveredProxy < 0 || recoveredProxy >= Transports.Items.Count)
        {
            return 0;
        }
        lock (_bindingsMutex)
        {
            var moved = 0;
            foreach (var node in Nodes)
            {
                var current = node.ProxyIndex;
                if (node.PreferredProxy != recoveredProxy || current == recoveredProxy)
                {
                    continue;
                }
                if (current >= 0 && current < _bindingCount.Length)
                {
                    _bindingCount[current]--;
                }
                _bindingCount[recoveredProxy]++;
                node.ProxyIndex = recoveredProxy;
                node.ResetFailures();
                node.CooldownUntil = 0;
                moved++;
            }
            return moved;
        }
    }

    public ProxyTransport? ProxyOf(UpstreamNode? node)
    {
        if (node == null)
        {
            return null;
        }
        var index = node.ProxyIndex;
        return index < 0 || index >= Transports.Items.Count ? null : Transports.Items[index];
    }

    /// <summary>
    /// 把当前使用失败代理的所有密钥迁到负载最小的
    /// 健康代理。空代理按配置顺序选择；否则
    /// 在负载最小的代理中随机选一个。若所有候选都不健康，
    /// 仍然会尝试负载最小的那个。
    /// </summary>
    public int RebindProxy(int failedProxy)
    {
        if (Transports.Items.Count < 2 || failedProxy < 0 || failedProxy >= Transports.Items.Count)
        {
            return 0;
        }
        lock (_bindingsMutex)
        {
            var moved = 0;
            foreach (var node in Nodes)
            {
                if (node.ProxyIndex != failedProxy)
                {
                    continue;
                }
                var target = ReplacementLocked(failedProxy, true);
                if (target < 0)
                {
                    target = ReplacementLocked(failedProxy, false);
                }
                if (target < 0)
                {
                    continue;
                }
                _bindingCount[failedProxy]--;
                _bindingCount[target]++;
                node.ProxyIndex = target;
                node.ResetFailures();
                node.CooldownUntil = 0;
                moved++;
            }
            return moved;
        }
    }

    private int ReplacementLocked(int failedProxy, bool healthyOnly)
    {
        var minimum = int.MaxValue;
        var candidates = new List<int>();
        for (var i = 0; i < Transports.Items.Count; i++)
        {
            if (i == failedProxy || (healthyOnly && !Transports.Items[i].HealthyLoad()))
            {
                continue;
            }
            var count = _bindingCount[i];
            if (count == 0)
            {
                return i;
            }
            if (count < minimum)
            {
                minimum = count;
                candidates.Clear();
                candidates.Add(i);
            }
            else if (count == minimum)
            {
                candidates.Add(i);
            }
        }
        return candidates.Count == 0 ? -1 : candidates[Random.Shared.Next(candidates.Count)];
    }

    /// <summary>
    /// 为每个并发请求预留不同的起始节点。选择会推迟到
    /// 游标推进时才确定，因此上一次尝试标记为失败的节点
    /// 会被立刻跳过。
    /// </summary>
    public NodeCursor Cursor()
    {
        if (Nodes.Count == 0)
        {
            return new NodeCursor(this, 0);
        }
        return new NodeCursor(this, (int)(Interlocked.Increment(ref _next) - 1) % Nodes.Count);
    }

    /// <summary>
    /// 首个选择对给定亲和密钥保持稳定的游标，
    /// 这让同一段对话的每一轮都落在同一个上游密钥上，
    /// 同时保留感知冷却的故障转移。空亲和退化为轮询，
    /// 后台任务用的就是轮询。
    /// </summary>
    public NodeCursor CursorFor(string affinity)
    {
        if (affinity.Length == 0 || Nodes.Count == 0)
        {
            return Cursor();
        }
        return new NodeCursor(this, (int)(Fnv1a.Hash64(affinity) % (ulong)Nodes.Count));
    }

    public void MarkSuccess(UpstreamNode? node)
    {
        if (node == null)
        {
            return;
        }
        node.ResetFailures();
        node.CooldownUntil = 0;
    }

    public void MarkFailure(UpstreamNode? node, Exception? error, UpstreamResponse? response)
    {
        if (node == null)
        {
            return;
        }
        if (ShouldSkipFailure(error, response))
        {
            return;
        }
        var failures = node.IncrementFailures();
        var multiplier = 1 << (int)Math.Min(failures - 1, 3);
        var delay = Cooldown * multiplier;
        if (response != null)
        {
            var retryAfter = RetryAfter.Parse(response.Header("Retry-After"));
            if (retryAfter > delay)
            {
                delay = retryAfter;
            }
        }
        node.CooldownUntil = DateTime.UtcNow.Add(delay).Ticks;
    }

    /// <summary>
    /// 普通的 2xx/3xx/4xx 答复不能说明节点的任何情况；只有传输
    /// 错误或鉴权/配额/服务端故障才会启动冷却。
    /// </summary>
    internal static bool ShouldSkipFailure(Exception? error, UpstreamResponse? response)
    {
        if (error != null)
        {
            return false;
        }
        if (response == null)
        {
            return false;
        }
        var status = response.Status;
        return status != 401 && status != 403 && status != 429 && status / 100 != 5;
    }
}

/// <summary>感知冷却的密钥池遍历（pool.go nodeCursor）。</summary>
internal sealed class NodeCursor(NodePool? pool, int next)
{
    private NodePool? Pool { get; } = pool;
    private int Next { get; set; } = next;

    public UpstreamNode? Advance()
    {
        if (Pool == null || Pool.Nodes.Count == 0)
        {
            return null;
        }
        var now = DateTime.UtcNow.Ticks;
        var choice = -1;
        var earliest = 0L;
        for (var offset = 0; offset < Pool.Nodes.Count; offset++)
        {
            var i = (Next + offset) % Pool.Nodes.Count;
            var until = Pool.Nodes[i].CooldownUntil;
            if (until <= now)
            {
                choice = i;
                break;
            }
            if (choice == -1 || until < earliest)
            {
                choice = i;
                earliest = until;
            }
        }
        if (choice < 0)
        {
            return null;
        }
        Next = (choice + 1) % Pool.Nodes.Count;
        return Pool.Nodes[choice];
    }
}

/// <summary>
/// 匿名线路上的一个代理。节点绝不重新绑定：换代理就是
/// 故障转移机制，因为 Zen 按 IP 限制共享的公共凭据。
/// </summary>
internal sealed class AnonymousNode(ProxyTransport proxy)
{
    private long _failures;
    private long _cooldownUntil;

    public ProxyTransport Proxy { get; } = proxy;

    public long FailureCount => Interlocked.Read(ref _failures);

    public long IncrementFailures() => Interlocked.Increment(ref _failures);

    public void ResetFailures() => Interlocked.Exchange(ref _failures, 0);

    public long CooldownUntil
    {
        get => Interlocked.Read(ref _cooldownUntil);
        set => Interlocked.Exchange(ref _cooldownUntil, value);
    }
}

/// <summary>
/// 为共享的 OpenCode "public" 凭据按代理维护独立的冷却期
/// （pool.go anonymousPool）。
/// </summary>
internal sealed class AnonymousPool
{
    public List<AnonymousNode> Nodes { get; } = [];
    public TimeSpan Cooldown { get; init; }
    public bool HasPool { get; private set; }

    private long _next;

    public static AnonymousPool Create(bool enabled, TransportPool? transports, TimeSpan cooldown)
    {
        var pool = new AnonymousPool { Cooldown = cooldown };
        if (!enabled || transports == null)
        {
            return pool;
        }
        foreach (var proxy in transports.Items)
        {
            if (proxy.Pool)
            {
                pool.HasPool = true;
            }
            pool.Nodes.Add(new AnonymousNode(proxy));
        }
        return pool;
    }

    public AnonymousCursor CursorFor(string affinity)
    {
        if (Nodes.Count == 0)
        {
            return new AnonymousCursor(this, 0);
        }
        if (affinity.Length == 0)
        {
            return new AnonymousCursor(this, (int)(Interlocked.Increment(ref _next) - 1) % Nodes.Count);
        }
        return new AnonymousCursor(this, (int)(Fnv1a.Hash64(affinity) % (ulong)Nodes.Count));
    }

    /// <summary>
    /// 清除绑定到已恢复代理的匿名节点的冷却。没有这一步，
    /// 故障期留下的冷却会在代理恢复后继续存在，
    /// 即使线路重新健康，匿名线路仍会被跳过。
    /// </summary>
    public int RestoreProxy(ProxyTransport? proxy)
    {
        if (proxy == null)
        {
            return 0;
        }
        var cleared = 0;
        foreach (var node in Nodes)
        {
            if (!ReferenceEquals(node.Proxy, proxy))
            {
                continue;
            }
            if (node.CooldownUntil == 0 && node.FailureCount == 0)
            {
                continue;
            }
            node.ResetFailures();
            node.CooldownUntil = 0;
            cleared++;
        }
        return cleared;
    }

    public void MarkSuccess(AnonymousNode? node)
    {
        if (node == null)
        {
            return;
        }
        node.ResetFailures();
        node.CooldownUntil = 0;
    }

    /// <summary>
    /// 把上游 Retry-After 限制在与指数退避相同的 8 倍上限内：
    /// 该线路按出口 IP 限流，且与密钥线路不同，
    /// 没有最早到期时间的回退，因此要求等待数小时的配额响应
    /// 会在网络切换给出新额度之后很久仍然跳过该出口。
    /// </summary>
    public void MarkFailure(AnonymousNode? node, Exception? error, UpstreamResponse? response)
    {
        if (node == null || node.Proxy.Pool)
        {
            return;
        }
        if (NodePool.ShouldSkipFailure(error, response))
        {
            return;
        }
        var failures = node.IncrementFailures();
        var delay = Cooldown * (1 << (int)Math.Min(failures - 1, 3));
        if (response != null)
        {
            var retryAfter = RetryAfter.Parse(response.Header("Retry-After"));
            if (retryAfter > delay)
            {
                delay = Durations.Min(retryAfter, Cooldown * MaxAnonymousRetryAfterMultiplier);
            }
        }
        node.CooldownUntil = DateTime.UtcNow.Add(delay).Ticks;
    }

    private const int MaxAnonymousRetryAfterMultiplier = 8;

}

/// <summary>
/// 每个游标内最多访问一次健康且未冷却的静态代理。当池中
/// 含轮转代理池条目时，第一轮之后继续在其上循环，
/// 使每次重试都建立新的上游连接，从而获得新的
/// 出口 IP。若所有静态代理都在冷却或不健康，游标回退到
/// 尚未尝试过的、到期最早的节点。
/// </summary>
internal sealed class AnonymousCursor(AnonymousPool? pool, int start)
{
    private AnonymousPool? Pool { get; } = pool;
    private int Start { get; } = start;
    private int Offset { get; set; }
    private bool[]? Used { get; set; }

    public AnonymousNode? Advance()
    {
        if (Pool == null || Pool.Nodes.Count == 0)
        {
            return null;
        }
        var count = Pool.Nodes.Count;
        Used ??= new bool[count];
        var now = DateTime.UtcNow.Ticks;
        while (Offset < count)
        {
            var index = (Start + Offset) % count;
            Offset++;
            var node = Pool.Nodes[index];
            if (node.Proxy.Pool || (!Used[index] && node.Proxy.HealthyLoad() && node.CooldownUntil <= now))
            {
                Used[index] = true;
                return node;
            }
        }
        if (Pool.HasPool)
        {
            for (var step = 0; step < count; step++)
            {
                var index = (Start + Offset) % count;
                Offset++;
                var node = Pool.Nodes[index];
                if (node.Proxy.Pool)
                {
                    return node;
                }
            }
        }
        var choice = -1;
        var earliest = 0L;
        var choiceHealthy = false;
        for (var step = 0; step < count; step++)
        {
            var index = (Start + step) % count;
            if (Used[index])
            {
                continue;
            }
            var node = Pool.Nodes[index];
            var healthy = node.Proxy.HealthyLoad();
            var until = node.CooldownUntil;
            if (choice < 0 || (healthy && !choiceHealthy) || (healthy == choiceHealthy && until < earliest))
            {
                choice = index;
                earliest = until;
                choiceHealthy = healthy;
            }
        }
        if (choice < 0)
        {
            return null;
        }
        Used[choice] = true;
        return Pool.Nodes[choice];
    }
}

/// <summary>上游可能发送的两种形式之一的 Retry-After。</summary>
internal static class RetryAfter
{
    public static TimeSpan Parse(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return TimeSpan.Zero;
        }
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }
        if (DateTime.TryParseExact(text, "r", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
        {
            var remaining = when - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return TimeSpan.Zero;
    }

    /// <summary>Go: http.ParseTime(value) 成功，即 HTTP-date 形式。</summary>
    public static bool IsHttpDate(string? value) =>
        DateTime.TryParseExact((value ?? "").Trim(), "r", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _);
}
