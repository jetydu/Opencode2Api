using System.Text;
using Microsoft.Extensions.Configuration;
using Opencode2Api.Config;
using Opencode2Api.Gateway;
using Opencode2Api.Models;
using GatewayConfig = Opencode2Api.Config.Config;

namespace Opencode2Api.Tests;

/// <summary>
/// 运行期状态文件的存放位置：配置文件定位已交给 .NET 配置系统，
/// 这里只验证 StateDir 派生出的四条状态文件路径。
/// </summary>
public sealed class StartupPathsTests : IDisposable
{
    private readonly string _root;

    public StartupPathsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "opencode2api-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    private GatewayConfig Load(string json)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        return GatewayConfig.FromConfiguration(
            configuration.GetSection(GatewayConfig.SectionName),
            Path.Combine(_root, "appsettings.json"));
    }

    [Fact]
    public void 缺省把状态文件收进_state_子目录()
    {
        var cfg = Load(MinimalConfig());

        Assert.Equal("state", cfg.StateDir);
        Assert.Equal(Path.Combine("implicit", "state", GatewayConfig.StateFilePrefix),
            cfg.ResolveStateBase(Path.Combine("implicit", "appsettings.json")));
    }

    [Theory]
    [InlineData("\"StateDir\": \"\"", "")]
    [InlineData("\"StateDir\": \"data/runtime\"", "data/runtime")]
    public void StateDir_覆盖缺省值(string raw, string expected)
    {
        var cfg = Load(MinimalConfig(raw));

        Assert.Equal(expected, cfg.StateDir);
    }

    [Fact]
    public void 空_StateDir_沿用_邻接配置文件_的目录布局()
    {
        var cfg = new GatewayConfig { StateDir = "" };

        Assert.Equal(Path.Combine("smoke", "appsettings.json"),
            cfg.ResolveStateBase(Path.Combine("smoke", "appsettings.json")));
        // 没有配置文件（空路径）表示不持久化任何状态。
        Assert.Equal("", cfg.ResolveStateBase(""));
    }

    [Fact]
    public void 绝对_StateDir_不再拼接配置目录()
    {
        var target = Path.Combine(_root, "runtime");
        var cfg = new GatewayConfig { StateDir = target };

        Assert.Equal(Path.Combine(target, GatewayConfig.StateFilePrefix),
            cfg.ResolveStateBase(Path.Combine(_root, "appsettings.json")));
    }

    [Fact]
    public void 四条状态文件共用同一前缀()
    {
        var cfg = Load(MinimalConfig());
        var basePath = cfg.ResolveStateBase(Path.Combine(_root, "appsettings.json"));

        // 轮转游标与目录快照都由基准路径拼后缀，因此它们必然落在同一目录、同一主名。
        Assert.Equal(basePath + ".rotation.json", RuntimeManager.StatePath(basePath));
        Assert.Equal(basePath + ".models.catalog.json", CatalogCache.Path(basePath));
        Assert.StartsWith(Path.Combine(_root, "state", GatewayConfig.StateFilePrefix), basePath);
    }

    private static string MinimalConfig(string extra = "\"StateDir\": \"state\"") =>
        """{"Opencode2Api":{"ServerKeys":["k"],"Anonymous":true,"Proxies":["direct"],""" + extra + "}}";
}
