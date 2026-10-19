using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.IO;
using Opencode2Api.JsonUtil;

namespace Opencode2Api.Models;

/// <summary>
/// 附加在可用性记录上的推理强度探测结果。Reset 请求
/// 会清除先前学习到的等级（models/availability.go）。
/// </summary>
public sealed record EffortProbe
{
    public string Effort { get; init; } = "";
    public string Protocol { get; init; } = "";

    /// <summary>不持久化：成功时清除已存储的 auto effort。</summary>
    public bool Reset { get; init; }
}

/// <summary>单个模型的可用性判定及其重探测计划。</summary>
public sealed class Availability
{
    public string Model { get; set; } = "";
    public bool Disabled { get; set; }
    public DateTime CheckedAt { get; set; }
    public DateTime NextCheck { get; set; }
    public string Reason { get; set; } = "";
    public string Channel { get; set; } = "";
    public bool Manual { get; set; }

    /// <summary>使已在运行的探测结果失效。</summary>
    public ulong Generation { get; set; }

    public int ProbeVersion { get; set; }
    public EffortProbe AutoEffort { get; set; } = new();
    public DateTime EffortCheckedAt { get; set; }

    public Availability Clone() => new()
    {
        Model = Model,
        Disabled = Disabled,
        CheckedAt = CheckedAt,
        NextCheck = NextCheck,
        Reason = Reason,
        Channel = Channel,
        Manual = Manual,
        Generation = Generation,
        ProbeVersion = ProbeVersion,
        AutoEffort = AutoEffort,
        EffortCheckedAt = EffortCheckedAt,
    };

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["model"] = Model,
            ["disabled"] = Disabled,
            ["checked_at"] = JsonTime.Format(CheckedAt),
            ["next_check"] = JsonTime.Format(NextCheck),
            ["auto_effort"] = new JsonObject(),
        };
        if (Reason.Length > 0)
        {
            obj["reason"] = Reason;
        }
        if (Channel.Length > 0)
        {
            obj["channel"] = Channel;
        }
        if (Manual)
        {
            obj["manual"] = true;
        }
        if (ProbeVersion != 0)
        {
            obj["probe_version"] = ProbeVersion;
        }
        if (AutoEffort.Effort.Length > 0)
        {
            obj["auto_effort"]!["effort"] = AutoEffort.Effort;
        }
        if (AutoEffort.Protocol.Length > 0)
        {
            obj["auto_effort"]!["protocol"] = AutoEffort.Protocol;
        }
        if (!JsonTime.IsZero(EffortCheckedAt))
        {
            obj["effort_checked_at"] = JsonTime.Format(EffortCheckedAt);
        }
        return obj;
    }

    public static Availability FromJson(string model, JsonObject obj)
    {
        var effort = JsonValues.MapAt(obj, "auto_effort") ?? [];
        return new Availability
        {
            Model = JsonValues.FirstString(JsonValues.StringAt(obj, "model")).Length > 0 ? JsonValues.StringAt(obj, "model") : model,
            Disabled = JsonValues.BoolAt(obj, "disabled"),
            CheckedAt = JsonTime.Parse(JsonValues.StringAt(obj, "checked_at")),
            NextCheck = JsonTime.Parse(JsonValues.StringAt(obj, "next_check")),
            Reason = JsonValues.StringAt(obj, "reason"),
            Channel = JsonValues.StringAt(obj, "channel"),
            Manual = JsonValues.BoolAt(obj, "manual"),
            ProbeVersion = JsonValues.IntAt(obj, "probe_version"),
            AutoEffort = new EffortProbe
            {
                Effort = JsonValues.StringAt(effort, "effort"),
                Protocol = JsonValues.StringAt(effort, "protocol"),
            },
            EffortCheckedAt = JsonTime.Parse(JsonValues.StringAt(obj, "effort_checked_at")),
        };
    }
}

/// <summary>
/// 跟踪哪些免费模型确实可达，并在重启之间保留
/// 运维者的手工覆盖（models/availability.go AvailabilityStore）。
/// </summary>
public sealed class AvailabilityStore
{
    public static readonly TimeSpan FreeModelCheckInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan FailedModelCheckInterval = TimeSpan.FromHours(24);

    private readonly object _mutex = new();
    private readonly string _path;
    private readonly Dictionary<string, Availability> _items = [];

    public AvailabilityStore(string path)
    {
        _path = path;
        if (path.Length > 0 && File.Exists(path))
        {
            var text = File.ReadAllText(path);
            if (JsonNode.Parse(text)?.AsObject() is JsonObject root)
            {
                foreach (var property in root)
                {
                    if (property.Value is JsonObject item)
                    {
                        _items[property.Key] = Availability.FromJson(property.Key, item);
                    }
                }
            }
        }
        // 旧版探测使用了非法会话 ID，并把临时错误当作
        // 永久失败。这些记录不能确立模型
        // 不可用。迁移只保存在内存中，每次加载都会重新
        // 推导；下一次显式写入才会持久化。
        foreach (var (model, item) in _items.ToList())
        {
            if (item.ProbeVersion == 0 && item.Disabled)
            {
                item.Disabled = false;
                item.NextCheck = default;
                item.Reason = "legacy_probe_recheck";
            }
            // 手工恢复的记录早于手工锁定标记。保留
            // 运维者的意图：仍处于 manually_enabled 的模型
            // 继续由手工控制，而不落回自动探测之下。
            if (!item.Disabled && !item.Manual && item.Reason == "manually_enabled")
            {
                item.Manual = true;
            }
            _items[model] = item;
        }
    }

    public Availability Get(string model)
    {
        lock (_mutex)
        {
            var item = _items.TryGetValue(model, out var found) ? found.Clone() : new Availability();
            item.Model = model;
            return item;
        }
    }

    public bool Disabled(string model) => Get(model).Disabled;

    public List<Availability> Snapshot()
    {
        lock (_mutex)
        {
            return _items.Keys.OrderBy(key => key, StringComparer.Ordinal).Select(key => Get(key)).ToList();
        }
    }

    /// <summary>
    /// 在手工与自动控制之间切换某个模型。手工模式让模型
    /// 保持启用并排除在自动探测之外；切回
    /// 自动会在下一次检查时安排一次探测。
    /// </summary>
    public void SetManual(string model, bool manual, DateTime now)
    {
        lock (_mutex)
        {
            var existed = _items.TryGetValue(model, out var current);
            var item = current?.Clone() ?? new Availability();
            // 被重试的请求不得清除禁用证据或重置计划。
            if (item.Manual == manual)
            {
                return;
            }
            var previous = item.Clone();
            item.Model = model;
            item.Manual = manual;
            item.Generation++;
            item.ProbeVersion = 1;
            if (manual)
            {
                item.Disabled = false;
                item.Reason = "manually_enabled";
                item.NextCheck = now.Add(FreeModelCheckInterval);
            }
            else
            {
                item.Reason = "auto_probe";
                item.NextCheck = default;
            }
            _items[model] = item;
            try
            {
                SaveLocked();
            }
            catch
            {
                if (existed)
                {
                    _items[model] = previous;
                }
                else
                {
                    _items.Remove(model);
                }
                throw;
            }
        }
    }

    public void Record(string model, ulong generation, bool success, string reason, string channel, DateTime now, EffortProbe effort)
    {
        lock (_mutex)
        {
            var item = _items.TryGetValue(model, out var current) ? current.Clone() : new Availability();
            if (item.Generation != generation)
            {
                return;
            }
            // 手工控制会冻结记录：自动探测绝不可
            // 覆盖 manually_enabled 状态（既不写 Disabled 也不写 Reason）。
            if (item.Manual)
            {
                return;
            }
            item.Model = model;
            if (success)
            {
                item.Disabled = false;
            }
            else if (reason == "model_unavailable")
            {
                item.Disabled = true;
            }
            else if (item.Disabled)
            {
                // 对仍处于禁用状态的模型做无定论的复查不带来新证据。
                // 保留此前的禁用原因与退避，而不是
                // 重置为按小时重试并记为瞬时错误。
                item.CheckedAt = now;
                item.ProbeVersion = 1;
                // 已排定的复查已经到达 NextCheck。把它向前推进，
                // 避免无定论结果后每个调度刻度都重复探测。
                if (item.NextCheck <= now)
                {
                    item.NextCheck = now.Add(FailedModelCheckInterval);
                }
                _items[model] = item;
                SaveLocked();
                return;
            }
            item.CheckedAt = now;
            item.Reason = reason;
            item.Channel = channel;
            item.ProbeVersion = 1;
            if (success && effort.Effort.Length > 0)
            {
                item.AutoEffort = effort;
                item.EffortCheckedAt = now;
            }
            else if (success && effort.Reset)
            {
                item.AutoEffort = new EffortProbe();
                item.EffortCheckedAt = default;
            }
            var interval = FreeModelCheckInterval;
            if (!success && reason == "model_unavailable")
            {
                interval = FailedModelCheckInterval;
            }
            item.NextCheck = now.Add(interval);
            _items[model] = item;
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        if (_path.Length == 0)
        {
            return;
        }
        var root = new JsonObject();
        foreach (var (model, item) in _items.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            root[model] = item.ToJson();
        }
        AtomicText.Write(_path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ".availability-");
    }
}
