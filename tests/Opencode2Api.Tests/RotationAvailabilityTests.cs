using Opencode2Api.Config;
using Opencode2Api.Models;
using Opencode2Api.Rotation;

namespace Opencode2Api.Tests;

/// <summary>
/// 轮转分组配置、粘性轮转状态与免费模型可用性存储
/// （对应 Go 的 internal/rotation/state.go 与 models/availability.go）。
/// </summary>
public sealed class RotationAvailabilityTests : IDisposable
{
    private readonly string _directory;

    public RotationAvailabilityTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "opencode2api-rotation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    private string FilePath(string name) => Path.Combine(_directory, name);

    private static RotationConfig NewConfig(int firstOutput = 30)
    {
        var cfg = new RotationConfig
        {
            Sota = new RotationGroup { Order = [.. RotationConfig.SotaModels] },
            Sweet = new RotationGroup { Order = ["sweet-a", "sweet-b"] },
            FirstOutputSeconds = firstOutput,
        };
        cfg.Defaults();
        return cfg;
    }

    [Fact]
    public void Defaults_补齐别名与首个输出窗口()
    {
        var cfg = new RotationConfig();

        cfg.Defaults();

        Assert.Equal("sota", cfg.Sota.Alias);
        Assert.Equal("sweet", cfg.Sweet.Alias);
        Assert.Equal(30, cfg.FirstOutputSeconds);
        Assert.Equal(RotationConfig.SotaModels, cfg.Sota.Order);
        Assert.Empty(cfg.Sweet.Order);
    }

    [Theory]
    [InlineData(0)]      // 默认值补齐后才允许通过
    [InlineData(400)]    // 超出 300 秒
    [InlineData(-1)]
    public void Validate_拒绝非法的首个输出窗口(int seconds)
    {
        var cfg = NewConfig();
        cfg.FirstOutputSeconds = seconds;

        Assert.Throws<ConfigException>(() => cfg.Validate(null));
    }

    [Fact]
    public void Validate_拒绝冲突与非法的别名()
    {
        var sameAlias = NewConfig();
        sameAlias.Sweet.Alias = sameAlias.Sota.Alias;
        Assert.Throws<ConfigException>(() => sameAlias.Validate(null));

        var realModelAlias = NewConfig();
        realModelAlias.Sweet.Alias = "glm-5.3";
        Assert.Throws<ConfigException>(() => realModelAlias.Validate(null));

        var illegalChars = NewConfig();
        illegalChars.Sota.Alias = "bad alias";
        Assert.Throws<ConfigException>(() => illegalChars.Validate(null));

        // 目录里真实存在的模型名也不能作为别名。
        var inCatalog = NewConfig();
        inCatalog.Sweet.Alias = "catalog-only-model";
        Assert.Throws<ConfigException>(() => inCatalog.Validate(["catalog-only-model"]));
    }

    [Fact]
    public void Validate_拒绝重复或错组的模型顺序()
    {
        var duplicated = NewConfig();
        duplicated.Sweet.Order = ["sweet-a", "sweet-a"];
        Assert.Throws<ConfigException>(() => duplicated.Validate(null));

        var wrongGroup = NewConfig();
        wrongGroup.Sweet.Order = [RotationConfig.SotaModels[0]];
        Assert.Throws<ConfigException>(() => wrongGroup.Validate(null));

        var emptyModel = NewConfig();
        emptyModel.Sweet.Order = ["", "sweet-b"];
        Assert.Throws<ConfigException>(() => emptyModel.Validate(null));
    }

    [Fact]
    public void 轮转_失败后切到下一个模型并持久化游标()
    {
        var statePath = FilePath("rotation.json");
        var manager = new RotationManager(statePath, NewConfig());

        Assert.Equal("sota", manager.GroupId("sota"));
        Assert.Equal("sweet", manager.GroupId("sweet"));
        Assert.Equal("", manager.GroupId("不存在的别名"));

        var available = RotationConfig.SotaModels.ToDictionary(model => model, _ => true);
        available["sweet-a"] = true;
        available["sweet-b"] = true;
        manager.Sync(available);

        var first = manager.Select("sota", new HashSet<string>());
        Assert.NotNull(first);
        Assert.Equal(RotationConfig.SotaModels[0], first!.Value.Model);

        // 首个输出超时：暂停当前模型并把游标推进到组内下一个。
        manager.Fail(first.Value, TimeSpan.FromMinutes(5), "first_output_timeout");
        var second = manager.Select("sota", new HashSet<string>());
        Assert.Equal(RotationConfig.SotaModels[1], second!.Value.Model);

        Assert.True(File.Exists(statePath));
        var reloaded = new RotationManager(statePath, NewConfig());
        var snapshot = reloaded.TakeSnapshot();
        Assert.Equal(RotationConfig.SotaModels[1], snapshot.Groups[0].Current);
        Assert.Equal(30, snapshot.FirstOutputSeconds);
        Assert.NotEmpty(snapshot.Groups[0].Switches);
        Assert.True(snapshot.Groups[0].Paused.ContainsKey(RotationConfig.SotaModels[0]));

        // 运维手工切换会清掉该模型的暂停标记。
        reloaded.Manual("sota", RotationConfig.SotaModels[0]);
        Assert.Equal(RotationConfig.SotaModels[0], reloaded.TakeSnapshot().Groups[0].Current);
        Assert.False(reloaded.TakeSnapshot().Groups[0].Paused.ContainsKey(RotationConfig.SotaModels[0]));
    }

    [Fact]
    public void 轮转_全部模型被访问过时不再选出模型()
    {
        var manager = new RotationManager(FilePath("rotation-empty.json"), NewConfig());
        manager.Sync(RotationConfig.SotaModels.ToDictionary(model => model, _ => true));

        var visited = new HashSet<string>(RotationConfig.SotaModels);

        Assert.Null(manager.Select("sota", visited));
        Assert.Null(manager.Select("不存在的组", new HashSet<string>()));
    }

    [Fact]
    public void 轮转_手工切换只接受当前可用的模型()
    {
        var manager = new RotationManager(FilePath("rotation-manual.json"), NewConfig());
        manager.Sync(RotationConfig.SotaModels.ToDictionary(model => model, _ => true));

        Assert.Throws<ConfigException>(() => manager.Manual("sota", "另一个模型"));
        Assert.Throws<ConfigException>(() => manager.Manual("sweet", RotationConfig.SotaModels[0]));
    }

    [Fact]
    public void 轮转_记录请求轨迹并截断历史()
    {
        var manager = new RotationManager(FilePath("rotation-attempts.json"), NewConfig());

        for (var i = 0; i < 120; i++)
        {
            manager.Record(new Attempt
            {
                Request = "req_" + i,
                Alias = "sota",
                Model = RotationConfig.SotaModels[i % RotationConfig.SotaModels.Length],
                Number = 1,
                Outcome = "ok",
            });
        }

        var snapshot = manager.TakeSnapshot();
        Assert.Equal(100, snapshot.Attempts.Count);
        Assert.Equal("req_119", snapshot.Attempts[^1].Request);
        Assert.NotEqual(default, snapshot.Attempts[^1].Time);
    }

    [Fact]
    public void 可用性_失败记录会禁用模型并延长退避()
    {
        var storePath = FilePath("availability.json");
        var store = new AvailabilityStore(storePath);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        store.Record("m-free", store.Get("m-free").Generation, false, "model_unavailable", "anonymous", now, new EffortProbe());

        var item = store.Get("m-free");
        Assert.True(item.Disabled);
        Assert.Equal(now.Add(AvailabilityStore.FailedModelCheckInterval), item.NextCheck);
        Assert.Equal("model_unavailable", item.Reason);

        // 重启后判定与退避都还在。
        var reloaded = new AvailabilityStore(storePath);
        Assert.True(reloaded.Disabled("m-free"));
        Assert.Equal(now.Add(AvailabilityStore.FailedModelCheckInterval), reloaded.Get("m-free").NextCheck);
    }

    [Fact]
    public void 可用性_瞬时失败不覆盖已有禁用证据()
    {
        var store = new AvailabilityStore("");
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        store.Record("m-free", 0, false, "model_unavailable", "anonymous", now, new EffortProbe());
        var disabled = store.Get("m-free");

        // 无定论复查（例如网络抖动）既不解除禁用，也不把退避缩到一小时。
        store.Record("m-free", disabled.Generation, false, "http_500", "anonymous",
            now.AddHours(30), new EffortProbe());

        var after = store.Get("m-free");
        Assert.True(after.Disabled);
        Assert.Equal("model_unavailable", after.Reason);
        Assert.True(after.NextCheck > now.AddHours(30));
    }

    [Fact]
    public void 可用性_手工锁定后自动探测不得覆盖()
    {
        var storePath = FilePath("manual.json");
        var store = new AvailabilityStore(storePath);
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        store.SetManual("m-free", true, now);
        store.Record("m-free", store.Get("m-free").Generation, false, "model_unavailable", "anonymous", now, new EffortProbe());

        var item = store.Get("m-free");
        Assert.True(item.Manual);
        Assert.False(item.Disabled);
        Assert.Equal("manually_enabled", item.Reason);
        Assert.Equal(now.Add(AvailabilityStore.FreeModelCheckInterval), item.NextCheck);

        // 手工模式会让 generation 前进，因此旧探测结果被丢弃。
        store.Record("m-free", 0, false, "model_unavailable", "anonymous", now, new EffortProbe());
        Assert.False(store.Get("m-free").Disabled);

        var reloaded = new AvailabilityStore(storePath);
        Assert.True(reloaded.Get("m-free").Manual);
        Assert.Equal(1, reloaded.Get("m-free").ProbeVersion);
    }

    [Fact]
    public void 可用性_成功记录写入学习到的推理等级()
    {
        var store = new AvailabilityStore("");
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        store.Record("m-free", 0, true, "probe_succeeded", "anonymous", now,
            new EffortProbe { Effort = "high", Protocol = "chat" });

        var item = store.Get("m-free");
        Assert.False(item.Disabled);
        Assert.Equal("high", item.AutoEffort.Effort);
        Assert.Equal(now.Add(AvailabilityStore.FreeModelCheckInterval), item.NextCheck);

        // Reset 请求在成功后清除学习到的等级。
        store.Record("m-free", item.Generation, true, "probe_succeeded", "anonymous", now.AddMinutes(1),
            new EffortProbe { Reset = true });
        Assert.Equal("", store.Get("m-free").AutoEffort.Effort);
        Assert.True(JsonTime.IsZero(store.Get("m-free").EffortCheckedAt));
    }

    [Fact]
    public void 可用性_快照按模型名排序()
    {
        var store = new AvailabilityStore("");
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        store.Record("b-free", 0, true, "probe_succeeded", "anonymous", now, new EffortProbe());
        store.Record("a-free", 0, false, "model_unavailable", "anonymous", now, new EffortProbe());

        Assert.Equal("a-free,b-free", string.Join(",", store.Snapshot().Select(item => item.Model)));
    }
}
