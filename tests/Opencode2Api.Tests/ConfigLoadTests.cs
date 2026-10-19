using System.Text;
using Microsoft.Extensions.Configuration;
using Opencode2Api.Config;
using GatewayConfig = Opencode2Api.Config.Config;

namespace Opencode2Api.Tests;

/// <summary>
/// 配置绑定、默认值与校验规则：测试走真实的 .NET 配置管线
/// （appsettings 的 JSON 源 → 绑定 Opencode2Api 节 → Normalize 校验）。
/// </summary>
public sealed class ConfigLoadTests : IDisposable
{
    private readonly string _directory;

    public ConfigLoadTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "opencode2api-config-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>把 JSON 交给配置系统，再绑定成最终 Config。</summary>
    private GatewayConfig Load(string json)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        return GatewayConfig.FromConfiguration(
            configuration.GetSection(GatewayConfig.SectionName),
            Path.Combine(_directory, "appsettings.json"));
    }

    /// <summary>在最小可用配置上追加片段，片段自身以逗号开头。</summary>
    private static string With(string extra = "") =>
        """{"Opencode2Api":{"ServerKeys":["k"],"ZenKeys":["sk-zen"]""" + extra + "}}";

    [Fact]
    public void 绑定_应用与_Go_一致的默认值()
    {
        var cfg = Load(With());

        Assert.Equal("https://opencode.ai/zen", cfg.Upstream.Zen);
        Assert.Equal("https://opencode.ai/zen/go", cfg.Upstream.Go);
        Assert.Equal("https://opencode.ai/inference", cfg.Upstream.Anonymous);
        Assert.Equal(3, cfg.Retry.MaxAttempts);
        Assert.Equal(300, cfg.Retry.TimeoutSeconds);
        Assert.Equal(300, cfg.Models.RefreshSeconds);
        Assert.False(cfg.Models.FreeOnly);
        Assert.Equal(0, cfg.Performance.MaxConnsPerHost);
        Assert.Equal(120, cfg.Performance.IdleConnTimeoutSeconds);
        Assert.Equal(5, cfg.Performance.ConnectTimeoutSeconds);
        Assert.Equal(15, cfg.Performance.FailureCooldownSeconds);
        Assert.Equal(0, cfg.Performance.AttemptTimeoutSeconds);
        Assert.Equal("info", cfg.Logging.Level);
        Assert.False(cfg.Logging.DumpRequestBodies);
        Assert.Equal(Tier.Go, cfg.Prefer);
        Assert.False(cfg.Anonymous);
        Assert.Equal("state", cfg.StateDir);
        Assert.Equal("direct", Assert.Single(cfg.RuntimeProxies()));

        // 轮转分组在 Normalize 里补齐默认值。
        Assert.Equal("sota", cfg.Rotation.Sota.Alias);
        Assert.Equal("sweet", cfg.Rotation.Sweet.Alias);
        Assert.Equal(30, cfg.Rotation.FirstOutputSeconds);
        Assert.Equal(RotationConfig.SotaModels, cfg.Rotation.Sota.Order);
    }

    [Fact]
    public void 绑定_允许注释与尾逗号()
    {
        // .NET 的 JSON 配置源本身就跳过注释，字符串里的 // 不受影响。
        var json = """
        {
          // 行注释，里有 https://example.com/keep 也不该被动
          "Opencode2Api": {
            "ServerKeys": ["k"], /* 块
               注释 */
            "ZenKeys": ["https://opencode.ai/x"],
          }
        }
        """;

        var cfg = Load(json);

        Assert.Equal("https://opencode.ai/x", Assert.Single(cfg.ZenKeys));
    }

    [Fact]
    public void 绑定_拒绝未知键与空的配置节()
    {
        // 拼错的键不会被静默忽略；Go 版的下划线键名同样会被拒。
        Assert.Throws<ConfigException>(() => Load(With(",\"Nope\":1")));
        Assert.Throws<ConfigException>(() => Load(With(",\"zen_keys\":[\"z\"]")));
        Assert.Throws<ConfigException>(() => Load("""{"Opencode2Api":{}}"""));
        var error = Assert.Throws<ConfigException>(() => Load("""{"Other":{"ServerKeys":["k"]}}"""));
        Assert.Contains(GatewayConfig.SectionName, error.Message);
    }

    [Fact]
    public void 绑定_环境变量优先于_json()
    {
        // 覆盖语法是 .NET 配置系统的标准形态：节名__属性名。
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(With())))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GatewayConfig.SectionName}:Prefer"] = Tier.Zen,
            })
            .Build();

        var cfg = GatewayConfig.FromConfiguration(
            configuration.GetSection(GatewayConfig.SectionName), Path.Combine(_directory, "appsettings.json"));

        Assert.Equal(Tier.Zen, cfg.Prefer);
    }

    [Theory]
    [InlineData(",\"Prefer\":\"both\"")]      // 只允许 zen / go
    [InlineData(",\"Retry\":{\"MaxAttempts\":0}")]
    [InlineData(",\"Retry\":{\"TimeoutSeconds\":0}")]
    [InlineData(",\"Models\":{\"RefreshSeconds\":0}")]
    [InlineData(",\"Logging\":{\"Level\":\"trace\"}")]
    [InlineData(",\"Performance\":{\"MaxConnsPerHost\":-1}")]
    [InlineData(",\"Performance\":{\"AttemptTimeoutSeconds\":-1}")]
    [InlineData(",\"Performance\":{\"FirstEventTimeoutSeconds\":90000}")]
    [InlineData(",\"Models\":{\"Protocols\":{\"m\":\"bogus\"}}")]
    [InlineData(",\"Reasoning\":{\"Effort\":\"ultra\"}")]
    [InlineData(",\"Reasoning\":{\"EffortByModel\":{\"\":\"low\"}}")]
    [InlineData(",\"Proxies\":[\"ftp://127.0.0.1:21\"]")]
    [InlineData(",\"Rotation\":{\"FirstOutputSeconds\":400}")]
    [InlineData(",\"Rotation\":{\"Sota\":{\"Order\":[\"glm-5.3\"]}}")]
    [InlineData(",\"Rotation\":{\"Sota\":{\"Alias\":\"x\"},\"Sweet\":{\"Alias\":\"x\"}}")]
    public void Normalize_拒绝非法取值(string extra)
    {
        // 每条片段都只违反一项校验，最终都必须以 ConfigException 结束。
        Assert.Throws<ConfigException>(() => Load(With(extra)));
    }

    [Fact]
    public void Normalize_要求至少一个可用上游密钥()
    {
        Assert.Throws<ConfigException>(() => Load(
            """{"Opencode2Api":{"ServerKeys":["k"],"Anonymous":false}}"""));
    }

    /// <summary>拼一份带密钥元数据的配置（指纹作为对象键）。</summary>
    private static string WithDisabledKey(string fingerprint, string extra, params string[] keys)
    {
        var joined = string.Join(",", keys.Select(key => "\"" + key + "\""));
        return "{\"Opencode2Api\":{\"ServerKeys\":[" + joined +
            "],\"ZenKeys\":[\"z\"],\"ServerKeyMetadata\":{\"" + fingerprint +
            "\":{\"Disabled\":true}}" + extra + "}}";
    }

    [Fact]
    public void Normalize_拒绝全部密钥被停用()
    {
        Assert.Throws<ConfigException>(() =>
            Load(WithDisabledKey(Secrets.Fingerprint("k"), "", "k")));
    }

    [Fact]
    public void 绑定_停用密钥后取第一个启用的密钥()
    {
        // 叠上推理强度覆盖，验证按模型覆盖优先于全局强制等级。
        var json = WithDisabledKey(Secrets.Fingerprint("first"),
            ",\"Reasoning\":{\"Effort\":\"high\",\"EffortByModel\":{\"m1\":\"low\"}}", "first", "second");

        var cfg = Load(json);

        Assert.Equal("second", cfg.FirstEnabledServerKey());
        Assert.True(cfg.ServerKeyEnabled("second"));
        Assert.False(cfg.ServerKeyEnabled("first"));
        Assert.Equal("low", cfg.ForcedEffort("m1"));
        Assert.Equal("high", cfg.ForcedEffort("m2"));
    }

    [Fact]
    public void 绑定_匿名线路为空时回填默认地址()
    {
        var json = """{"Opencode2Api":{"ServerKeys":["k"],"Anonymous":true,"Upstream":{"Anonymous":""}}}""";

        var cfg = Load(json);

        Assert.Equal("https://opencode.ai/inference", cfg.Upstream.Anonymous);
    }

    [Fact]
    public void Clone_隔离嵌套集合()
    {
        var cfg = Load(With(",\"Reasoning\":{\"EffortByModel\":{\"m1\":\"low\"}}"));
        var clone = GatewayConfig.Clone(cfg);

        cfg.ServerKeys.Add("extra");
        cfg.Models.Protocols["m"] = "chat";
        cfg.Rotation.Sota.Order.Clear();
        cfg.Reasoning.EffortByModel!["m1"] = "max";

        Assert.Single(clone.ServerKeys);
        Assert.Empty(clone.Models.Protocols);
        Assert.Equal(RotationConfig.SotaModels.Length, clone.Rotation.Sota.Order.Count);
        Assert.Equal("low", clone.Reasoning.EffortByModel!["m1"]);
    }

    [Fact]
    public void AttemptTimeout_不超过请求级预算()
    {
        var cfg = Load(With(",\"Performance\":{\"AttemptTimeoutSeconds\":600}"));
        var budget = TimeSpan.FromSeconds(120);

        Assert.Equal(budget, cfg.Performance.AttemptTimeout(budget));

        var fast = Load(With(",\"Performance\":{\"AttemptTimeoutSeconds\":5}"));
        Assert.Equal(TimeSpan.FromSeconds(5), fast.Performance.AttemptTimeout(budget));
        // 没有请求级预算（Zero）时，单次尝试上限就是配置值。
        Assert.Equal(TimeSpan.FromSeconds(5), fast.Performance.AttemptTimeout(TimeSpan.Zero));
        // 未配置时沿用请求级预算。
        var legacy = Load(With());
        Assert.Equal(budget, legacy.Performance.AttemptTimeout(budget));
    }
}
