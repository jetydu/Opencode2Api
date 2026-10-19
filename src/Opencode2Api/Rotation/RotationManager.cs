using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.Config;

namespace Opencode2Api.Rotation;

public sealed class Attempt
{
    public DateTime Time { get; set; }
    public string Request { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Model { get; set; } = "";
    public int Number { get; set; }
    public string Outcome { get; set; } = "";
}

public sealed class Switch
{
    public DateTime Time { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class Group
{
    public string Id { get; set; } = "";
    public string Alias { get; set; } = "";
    public List<string> Order { get; set; } = [];
    public string Current { get; set; } = "";
    public ulong Generation { get; set; }
    public Dictionary<string, DateTime> Paused { get; set; } = [];
    public Dictionary<string, bool> Available { get; set; } = [];
    public List<Switch> Switches { get; set; } = [];
}

public sealed class Snapshot
{
    public List<Group> Groups { get; } = [];
    public List<Attempt> Attempts { get; } = [];
    public string PersistenceError { get; set; } = "";
    public int FirstOutputSeconds { get; set; }

    public Snapshot DeepClone()
    {
        var copy = new Snapshot { PersistenceError = PersistenceError, FirstOutputSeconds = FirstOutputSeconds };
        foreach (var group in Groups)
        {
            copy.Groups.Add(new Group
            {
                Id = group.Id,
                Alias = group.Alias,
                Order = [.. group.Order],
                Current = group.Current,
                Generation = group.Generation,
                Paused = new Dictionary<string, DateTime>(group.Paused),
                Available = new Dictionary<string, bool>(group.Available),
                Switches = group.Switches.Select(s => new Switch { Time = s.Time, From = s.From, To = s.To, Reason = s.Reason }).ToList(),
            });
        }
        foreach (var attempt in Attempts)
        {
            copy.Attempts.Add(new Attempt
            {
                Time = attempt.Time,
                Request = attempt.Request,
                Alias = attempt.Alias,
                Model = attempt.Model,
                Number = attempt.Number,
                Outcome = attempt.Outcome,
            });
        }
        return copy;
    }
}

public readonly record struct Token(string Group, string Model, ulong Generation);

/// <summary>
/// 负责全局粘性的模型选择（internal/rotation/state.go）。
/// 它的锁内不执行任何网络调用。世代号可防止迟到的请求
/// 撤销一次切换。
/// </summary>
public sealed class RotationManager
{
    private readonly object _mutex = new();
    private readonly string _path;
    private Snapshot _state = new();
    private RotationConfig _cfg = new();

    public RotationManager(string path, RotationConfig cfg)
    {
        _path = path;
        if (path.Length > 0 && File.Exists(path))
        {
            string data;
            try
            {
                data = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new ConfigException($"read rotation state: {e.Message}");
            }
            try
            {
                _state = ParseSnapshot(data);
            }
            catch (Exception e) when (e is JsonException or ConfigException)
            {
                throw new ConfigException($"invalid rotation state: {e.Message}");
            }
            if (_state.Groups.Count != 0 &&
                (_state.Groups.Count != 2 || _state.Groups[0].Id != "sota" || _state.Groups[1].Id != "sweet"))
            {
                throw new ConfigException("invalid rotation groups");
            }
        }
        Configure(cfg);
        if (_state.PersistenceError.Length > 0)
        {
            throw new ConfigException("cannot persist rotation state");
        }
    }

    public void Configure(RotationConfig cfg)
    {
        lock (_mutex)
        {
            cfg.Defaults();
            var configs = new[] { cfg.Sota, cfg.Sweet };
            var ids = new[] { "sota", "sweet" };
            for (var i = 0; i < configs.Length; i++)
            {
                var c = configs[i];
                if (_state.Groups.Count <= i)
                {
                    _state.Groups.Add(new Group { Id = ids[i] });
                }
                var g = _state.Groups[i];
                var previous = i == 0 ? _cfg.Sota : _cfg.Sweet;
                var order = new List<string>(g.Order);
                if (order.Count == 0 || !previous.Order.SequenceEqual(c.Order))
                {
                    order = new List<string>(c.Order);
                    foreach (var value in g.Order)
                    {
                        if (!order.Contains(value))
                        {
                            order.Add(value);
                        }
                    }
                }
                if (!order.SequenceEqual(g.Order) || g.Alias != c.Alias)
                {
                    g.Generation++;
                }
                g.Order = order;
                g.Alias = c.Alias;
                g.Paused ??= [];
                if (order.Count > 0 && !g.Order.Contains(g.Current))
                {
                    g.Current = g.Order[0];
                    g.Generation++;
                }
            }
            _cfg = cfg;
            _state.FirstOutputSeconds = cfg.FirstOutputSeconds;
            SaveLocked();
        }
    }

    public void Sync(IReadOnlyDictionary<string, bool> available)
    {
        lock (_mutex)
        {
            var changed = false;
            foreach (var g in _state.Groups)
            {
                g.Available = [];
                foreach (var model in available.Keys)
                {
                    if (RotationConfig.IsSota(model) == (g.Id == "sota"))
                    {
                        g.Available[model] = true;
                    }
                }
                var added = new List<string>();
                foreach (var model in g.Available.Keys)
                {
                    if (!g.Order.Contains(model))
                    {
                        added.Add(model);
                    }
                }
                added.Sort(StringComparer.Ordinal);
                if (g.Order.Count == 0 && added.Contains("glm-5.3-flash"))
                {
                    added.Remove("glm-5.3-flash");
                    added.Insert(0, "glm-5.3-flash");
                }
                if (added.Count > 0)
                {
                    g.Order.AddRange(added);
                    changed = true;
                }
                if (g.Current.Length == 0 && g.Order.Count > 0)
                {
                    g.Current = g.Order[0];
                    g.Generation++;
                    changed = true;
                }
            }
            if (changed)
            {
                SaveLocked();
            }
        }
    }

    public string GroupId(string alias)
    {
        lock (_mutex)
        {
            foreach (var g in _state.Groups)
            {
                if (g.Alias == alias)
                {
                    return g.Id;
                }
            }
            return "";
        }
    }

    public Snapshot TakeSnapshot()
    {
        lock (_mutex)
        {
            return _state.DeepClone();
        }
    }

    private Group? FindGroup(string id)
    {
        foreach (var g in _state.Groups)
        {
            if (g.Id == id)
            {
                return g;
            }
        }
        return null;
    }

    private static bool Usable(Group g, string model) =>
        g.Available.TryGetValue(model, out var ok) && ok && (!g.Paused.TryGetValue(model, out var until) || DateTime.UtcNow >= until);

    public Token? Select(string id, HashSet<string> visited)
    {
        lock (_mutex)
        {
            var g = FindGroup(id);
            if (g == null || g.Order.Count == 0)
            {
                return null;
            }
            var index = g.Order.IndexOf(g.Current);
            var start = Math.Max(index, 0);
            for (var n = 0; n < g.Order.Count; n++)
            {
                var model = g.Order[(start + n) % g.Order.Count];
                if (visited.Contains(model) || !Usable(g, model))
                {
                    continue;
                }
                // 请求局部的跳过不得改动更新且健康的整体选择。
                if (!Usable(g, g.Current))
                {
                    SwitchLocked(g, model, "当前模型暂停或已下架");
                    SaveLocked();
                }
                return new Token(g.Id, model, g.Generation);
            }
            return null;
        }
    }

    private void SwitchLocked(Group g, string to, string reason)
    {
        var from = g.Current;
        g.Current = to;
        g.Generation++;
        g.Switches.Add(new Switch { Time = DateTime.UtcNow, From = from, To = to, Reason = reason });
        if (g.Switches.Count > 30)
        {
            g.Switches.RemoveRange(0, g.Switches.Count - 30);
        }
    }

    public void Fail(Token token, TimeSpan pause, string reason)
    {
        lock (_mutex)
        {
            var g = FindGroup(token.Group);
            if (g == null)
            {
                return;
            }
            var until = DateTime.UtcNow.Add(pause);
            if (!g.Paused.TryGetValue(token.Model, out var existing) || until > existing)
            {
                g.Paused[token.Model] = until;
            }
            if (g.Current == token.Model && g.Generation == token.Generation)
            {
                var start = g.Order.IndexOf(token.Model);
                var next = token.Model;
                for (var n = 1; n < g.Order.Count; n++)
                {
                    var candidate = g.Order[(start + n) % g.Order.Count];
                    if (Usable(g, candidate))
                    {
                        next = candidate;
                        break;
                    }
                }
                SwitchLocked(g, next, reason);
            }
            SaveLocked();
        }
    }

    public void Manual(string id, string model)
    {
        lock (_mutex)
        {
            var g = FindGroup(id);
            if (g == null || !g.Order.Contains(model) || !g.Available.TryGetValue(model, out var available) || !available)
            {
                throw new ConfigException("请选择该组当前可用的 Go 模型");
            }
            g.Paused.Remove(model);
            SwitchLocked(g, model, "管理员手动切换");
            SaveLocked();
            if (_state.PersistenceError.Length > 0)
            {
                throw new ConfigException("轮转状态保存失败");
            }
        }
    }

    public void Record(Attempt attempt)
    {
        lock (_mutex)
        {
            attempt.Time = DateTime.UtcNow;
            _state.Attempts.Add(attempt);
            if (_state.Attempts.Count > 100)
            {
                _state.Attempts.RemoveRange(0, _state.Attempts.Count - 100);
            }
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        if (_path.Length == 0)
        {
            return;
        }
        _state.PersistenceError = "";
        try
        {
            WriteLocked();
        }
        catch (Exception)
        {
            // 持久化失败沿用 Go 的做法：写进状态文件供诊断读取，而不是抛出，
            // 因此请求处理仍能继续工作。
            _state.PersistenceError = "轮转状态写入失败，请检查磁盘空间和文件权限";
        }
    }

    private void WriteLocked()
    {
        var json = RenderSnapshot(_state);
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temp = Path.Combine(directory ?? ".", $".rotation-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, json);
        try
        {
            File.Move(temp, _path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";

    private static DateTime ParseTime(string? value) =>
        value != null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.UtcDateTime
            : DateTime.MinValue;

    internal static string RenderSnapshot(Snapshot state)
    {
        var root = new JsonObject
        {
            ["groups"] = new JsonArray([.. state.Groups.Select(g => (JsonNode)new JsonObject
            {
                ["id"] = g.Id,
                ["alias"] = g.Alias,
                ["order"] = new JsonArray([.. g.Order.Select(m => (JsonNode)JsonValue.Create(m))]),
                ["current"] = g.Current,
                ["generation"] = g.Generation,
                ["paused"] = new JsonObject(g.Paused.ToDictionary(kv => kv.Key, kv => (JsonNode?)JsonValue.Create(kv.Value.ToString(TimeFormat, CultureInfo.InvariantCulture)))),
                ["available"] = new JsonObject(g.Available.ToDictionary(kv => kv.Key, kv => (JsonNode?)JsonValue.Create(kv.Value))),
                ["switches"] = new JsonArray([.. g.Switches.Select(s => (JsonNode)new JsonObject
                {
                    ["time"] = s.Time.ToString(TimeFormat, CultureInfo.InvariantCulture),
                    ["from"] = s.From,
                    ["to"] = s.To,
                    ["reason"] = s.Reason,
                })]),
            })]),
            ["attempts"] = new JsonArray([.. state.Attempts.Select(a => (JsonNode)new JsonObject
            {
                ["time"] = a.Time.ToString(TimeFormat, CultureInfo.InvariantCulture),
                ["request"] = a.Request,
                ["alias"] = a.Alias,
                ["model"] = a.Model,
                ["number"] = a.Number,
                ["outcome"] = a.Outcome,
            })]),
            ["first_output_seconds"] = state.FirstOutputSeconds,
        };
        if (state.PersistenceError.Length > 0)
        {
            root["persistence_error"] = state.PersistenceError;
        }
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    internal static Snapshot ParseSnapshot(string data)
    {
        var root = JsonNode.Parse(data)?.AsObject() ?? throw new ConfigException("expected a JSON object");
        var state = new Snapshot();
        if (root["persistence_error"]?.GetValueKind() == System.Text.Json.JsonValueKind.String)
        {
            state.PersistenceError = root["persistence_error"]!.GetValue<string>();
        }
        if (root["first_output_seconds"] is JsonNode firstOutput)
        {
            state.FirstOutputSeconds = (int)firstOutput.GetValue<double>();
        }
        if (root["groups"] is JsonArray groups)
        {
            foreach (var node in groups)
            {
                if (node is not JsonObject g)
                {
                    throw new ConfigException("invalid rotation group");
                }
                var group = new Group
                {
                    Id = g["id"]?.GetValue<string>() ?? "",
                    Alias = g["alias"]?.GetValue<string>() ?? "",
                    Current = g["current"]?.GetValue<string>() ?? "",
                    Generation = g["generation"] != null ? (ulong)g["generation"]!.GetValue<double>() : 0,
                };
                if (g["order"] is JsonArray order)
                {
                    group.Order.AddRange(order.Select(item => item?.GetValue<string>() ?? ""));
                }
                if (g["paused"] is JsonObject paused)
                {
                    foreach (var property in paused)
                    {
                        group.Paused[property.Key] = ParseTime(property.Value?.GetValue<string>());
                    }
                }
                if (g["available"] is JsonObject available)
                {
                    foreach (var property in available)
                    {
                        group.Available[property.Key] = property.Value?.GetValue<bool>() ?? false;
                    }
                }
                if (g["switches"] is JsonArray switches)
                {
                    foreach (var switchNode in switches)
                    {
                        if (switchNode is JsonObject s)
                        {
                            group.Switches.Add(new Switch
                            {
                                Time = ParseTime(s["time"]?.GetValue<string>()),
                                From = s["from"]?.GetValue<string>() ?? "",
                                To = s["to"]?.GetValue<string>() ?? "",
                                Reason = s["reason"]?.GetValue<string>() ?? "",
                            });
                        }
                    }
                }
                state.Groups.Add(group);
            }
        }
        if (root["attempts"] is JsonArray attempts)
        {
            foreach (var node in attempts)
            {
                if (node is JsonObject a)
                {
                    state.Attempts.Add(new Attempt
                    {
                        Time = ParseTime(a["time"]?.GetValue<string>()),
                        Request = a["request"]?.GetValue<string>() ?? "",
                        Alias = a["alias"]?.GetValue<string>() ?? "",
                        Model = a["model"]?.GetValue<string>() ?? "",
                        Number = a["number"] != null ? (int)a["number"]!.GetValue<double>() : 0,
                        Outcome = a["outcome"]?.GetValue<string>() ?? "",
                    });
                }
            }
        }
        return state;
    }
}
