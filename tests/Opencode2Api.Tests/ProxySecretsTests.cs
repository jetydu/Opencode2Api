using System.Security.Cryptography;
using System.Text;
using Opencode2Api.Config;
using Opencode2Api.IO;
using GatewayConfig = Opencode2Api.Config.Config;

namespace Opencode2Api.Tests;

/// <summary>
/// 代理条目解析、proxyfile 载入、密钥脱敏与崩溃安全写入
/// （对应 Go 的 internal/config/proxy.go、secrets.go 与状态存储的写入序列）。
/// </summary>
public sealed class ProxySecretsTests : IDisposable
{
    private readonly string _directory;

    public ProxySecretsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "opencode2api-proxy-" + Guid.NewGuid().ToString("N"));
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

    [Theory]
    [InlineData("direct", "direct", false)]
    [InlineData("  direct  ", "direct", false)]
    [InlineData("", "", false)]
    [InlineData("pool+socks5://127.0.0.1:1080", "socks5://127.0.0.1:1080", true)]
    [InlineData("socks5://127.0.0.1:1080#pool", "socks5://127.0.0.1:1080", true)]
    [InlineData("socks5://127.0.0.1:1080#POOL", "socks5://127.0.0.1:1080", true)]
    [InlineData("socks5://127.0.0.1:1080#other", "socks5://127.0.0.1:1080#other", false)]
    [InlineData("http://user:pass@10.0.0.1:8080", "http://user:pass@10.0.0.1:8080", false)]
    public void ParseProxyEntry_识别动态池标记(string raw, string clean, bool pool)
    {
        var actual = Proxy.ParseProxyEntry(raw);

        Assert.Equal(clean, actual.Clean);
        Assert.Equal(pool, actual.Pool);
    }

    [Fact]
    public void ResolveEffective_去重且空列表回落到直连()
    {
        Assert.Equal("direct", string.Join(",", Proxy.ResolveEffective("appsettings.json", ["direct", "  ", "direct"], "")));
        Assert.Equal("direct", string.Join(",", Proxy.ResolveEffective("appsettings.json", [], "")));
    }

    [Fact]
    public void ResolveEffective_保留配置顺序并去掉重复项()
    {
        var effective = Proxy.ResolveEffective("appsettings.json", ["http://a:8080", "direct", "http://a:8080"], "");

        Assert.Equal("http://a:8080,direct", string.Join(",", effective));
    }

    [Fact]
    public void ResolveEffective_载入相对配置目录的_proxyfile()
    {
        var configPath = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(Path.Combine(_directory, "proxies.txt"), """
            # 整行注释
            direct

            socks5://1.2.3.4:1080#pool
            http://5.6.7.8:8080   # 行尾注释
            direct
            """);

        var effective = Proxy.ResolveEffective(configPath, ["direct"], "proxies.txt");

        // 池条目与静态条目即使端点相同也并存；重复的直连被去掉。
        Assert.Equal("direct,socks5://1.2.3.4:1080#pool,http://5.6.7.8:8080", string.Join(",", effective));
    }

    [Fact]
    public void ResolveEffective_proxyfile_缺失时报配置错误()
    {
        var configPath = Path.Combine(_directory, "appsettings.json");
        Assert.Throws<ConfigException>(() =>
            Proxy.ResolveEffective(configPath, ["direct"], "missing-proxies.txt"));
    }

    [Fact]
    public void TrimList_去掉空白条目()
    {
        Assert.Equal("a,b", string.Join(",", Proxy.TrimList([" a ", "", "  ", "b"])));
    }

    [Fact]
    public void Fingerprint_是稳定的小写_Sha256_前缀()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("sk-abc")))[..10].ToLowerInvariant();

        Assert.Equal(expected, Secrets.Fingerprint("sk-abc"));
        Assert.Equal(10, Secrets.Fingerprint("sk-abc").Length);
        Assert.NotEqual(Secrets.Fingerprint("sk-abc"), Secrets.Fingerprint("sk-abd"));
    }

    [Theory]
    [InlineData("abcde", "abcde")]           // 不足 6 位时原样返回
    [InlineData("abcdefg", "cdefg")]         // 只保留最后 5 位
    [InlineData("密钥abcdefg", "cdefg")]      // 按码点而不是 UTF-16 单位计数
    public void KeyDisplayId_只展示尾部字符(string value, string expected)
    {
        Assert.Equal(expected, Secrets.KeyDisplayId(value));
    }

    [Theory]
    [InlineData("direct", "direct")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://user:pass@host:8080/path", "http://***@host:8080/path")]
    [InlineData("socks5://u:p@host:1080#pool", "socks5://***@host:1080#pool")]
    [InlineData("pool+socks5://u:p@host:1080", "socks5://***@host:1080#pool")]
    public void RedactUrl_抹掉内嵌凭据(string raw, string expected)
    {
        Assert.Equal(expected, Secrets.RedactUrl(raw));
    }

    [Fact]
    public void ParseUserInfo_解码百分号转义()
    {
        var info = Secrets.ParseUserInfo("http://a%20b:p%40ss@host:80/");

        Assert.NotNull(info);
        Assert.Equal("a b", info!.Value.Username);
        Assert.Equal("p@ss", info.Value.Password);
        Assert.Null(Secrets.ParseUserInfo("http://host:80/"));
        Assert.Null(Secrets.ParseUserInfo("不是 URL"));
    }

    [Fact]
    public void SecretRedactor_把配置里的凭据替换为星号()
    {
        var cfg = new GatewayConfig
        {
            ServerKeys = ["super-local-key"],
            ZenKeys = ["sk-zen-secret"],
            Proxies = ["http://proxy-user:proxy-pass@10.0.0.1:8080"],
            Upstream = { Zen = "https://opencode.ai/zen" },
        };
        var redactor = new SecretRedactor();
        redactor.Replace(cfg);

        Assert.Equal("key=***", redactor.String("key=super-local-key"));
        Assert.Equal("***", redactor.String("sk-zen-secret"));
        Assert.DoesNotContain("proxy-pass", redactor.String("dial http://proxy-user:proxy-pass@10.0.0.1:8080"));
        Assert.Equal("无关文本", redactor.String("无关文本"));
    }

    [Fact]
    public void AtomicText_落盘不留临时文件()
    {
        var path = Path.Combine(_directory, "state.json");

        AtomicText.Write(path, """{"a":1}""", ".state-");
        Assert.Equal("""{"a":1}""", File.ReadAllText(path));

        // 覆盖写同样成功，并且目录里只剩目标文件。
        AtomicText.Write(path, """{"a":2}""", ".state-");
        Assert.Equal("""{"a":2}""", File.ReadAllText(path));
        Assert.Equal("state.json", string.Join(",",
            Directory.GetFileSystemEntries(_directory).Select(Path.GetFileName)));
    }
}
