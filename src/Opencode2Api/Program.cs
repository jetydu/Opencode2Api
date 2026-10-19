using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using NLog.Web;
using Opencode2Api.Config;
using Opencode2Api.Gateway;
using Opencode2Api.Protocol;
using Opencode2Api.Telemetry;

// 网关启动入口（对应 Go 版 cmd/opencode2api/main.go）。
// 与 Go 版的差异：监听地址改用 ASP.NET Core 标配的 urls，网关业务配置
// 集中在 appsettings.json 的 Opencode2Api 节，可被 appsettings.{Environment}.json、
// 环境变量（Opencode2Api__Prefer）与命令行（--Opencode2Api:Prefer）逐层覆盖；
// 不启动 WebUI/管理端，也不做配置热更新。

var (extraConfigFile, hostArgs) = TakeConfigArgument(args);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = hostArgs,
    // 以可执行文件所在目录为内容根：appsettings.json 会随构建/发布拷到那里，
    // 于是从任意工作目录启动都能读到同一份配置与状态目录。
    ContentRootPath = AppContext.BaseDirectory,
});

// --config / OPENCODE2API_CONFIG 指定额外的 JSON 文件，在默认源之后追加，
// 因此它的同名键优先级高于 appsettings.json。
var configFile = extraConfigFile.Length > 0
    ? extraConfigFile
    : (Environment.GetEnvironmentVariable(Config.ExtraFileEnvVar) ?? "").Trim();
var configPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.json");
if (configFile.Length > 0)
{
    var full = Path.GetFullPath(configFile);
    if (!File.Exists(full))
    {
        Console.Error.WriteLine($"configuration error: 找不到配置文件：{full}");
        return 1;
    }
    builder.Configuration.AddJsonFile(
        new PhysicalFileProvider(Path.GetDirectoryName(full)!),
        Path.GetFileName(full),
        optional: false,
        reloadOnChange: false);
    configPath = full;
}

// 日志完全交给 NLog（nlog.config），移除默认 provider 避免出现两套输出。
builder.Logging.ClearProviders();
builder.Host.UseNLog();

Config cfg;
try
{
    cfg = Config.FromConfiguration(builder.Configuration.GetSection(Config.SectionName), configPath);
}
catch (Exception error) when (error is ConfigException or InvalidOperationException)
{
    Console.Error.WriteLine($"configuration error: {error.Message}");
    return 1;
}

var redactor = new SecretRedactor();
redactor.Replace(cfg);
var logger = new StructuredLogger(redactor, cfg.Logging.Level);
var monitor = new Monitor();

builder.Services.AddSingleton(monitor);
builder.Services.AddSingleton(logger);
// 两个中间件都实现了 IMiddleware，需要注册为服务实例。
builder.Services.AddSingleton<RequestLoggingMiddleware>();
builder.Services.AddSingleton<RecoveryMiddleware>();

// 监听地址走 .NET 标配的 urls（--urls > ASPNETCORE_URLS > appsettings.json 的 Urls），
// 不再由网关自己解析自定义的 Listen 字段去调 options.Listen。
var listenUrls = ResolveListenUrls(hostArgs, builder.Configuration);
builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, listenUrls);
builder.Services.Configure<KestrelServerOptions>(options =>
{
    // 对齐 Go http.Server：ReadHeaderTimeout 15s 对应请求头读取超时，
    // IdleTimeout 120s 对应 keep-alive 连接的空闲存活时间。
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
});
// 对齐 Go 的 15s 优雅停机窗口。
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));

RuntimeManager runtime;
try
{
    runtime = RuntimeManager.Create(configPath, cfg, logger, monitor, redactor);
}
catch (Exception error)
{
    logger.Error("failed to initialize runtime",
        ("component", "runtime"), ("event", "runtime_initialization_failed"), ("error", error));
    return 1;
}

var app = builder.Build();
// 顺序与 Go 一致：外层记录请求指标与日志，内层把处理器异常转成协议形态的 500。
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<RecoveryMiddleware>();

var gateway = runtime.Gateway;
app.MapGet("/healthz", gateway.HandleHealth);
app.MapGet("/v1/models", Auth(gateway, gateway.HandleModels));
app.MapPost("/v1/chat/completions", Auth(gateway, context => gateway.HandleInference(context, Wire.Chat)));
app.MapPost("/v1/responses", Auth(gateway, context => gateway.HandleInference(context, Wire.Responses)));
app.MapPost("/v1/messages", Auth(gateway, context => gateway.HandleInference(context, Wire.Anthropic)));
app.MapPost("/v1/systemone", Auth(gateway, gateway.HandleSystemOne));

// 主机停机时取消后台循环（代理健康检查、模型目录刷新、价格与可用性探测）。
app.Lifetime.ApplicationStopping.Register(() => runtime.DisposeAsync().AsTask().GetAwaiter().GetResult());

logger.Info("server listening",
    ("component", "api"), ("event", "server_started"), ("address", listenUrls),
    ("config", configPath), ("state_dir", runtime.StateDirectory), ("version", Opencode2Api.BuildInfo.Version));
await app.RunAsync();
return 0;

// 把处理器包一层本地 API Key 校验（Go: mux.HandleFunc 上的 g.authenticate）。
static RequestDelegate Auth(ApiGateway gateway, Func<HttpContext, Task> next) => new(gateway.Authenticate(next));

// 从命令行里摘出 --config（支持 --config=path 与 --config path 两种写法），
// 剩下的参数再交给主机解析，避开 --config 被当成一个配置键。
static (string ConfigFile, string[] HostArgs) TakeConfigArgument(string[] args)
{
    var file = "";
    var remaining = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--config=", StringComparison.Ordinal))
        {
            file = args[i]["--config=".Length..];
        }
        else if (args[i] == "--config" && i + 1 < args.Length)
        {
            file = args[++i];
        }
        else
        {
            remaining.Add(args[i]);
        }
    }
    return (file.Trim(), [.. remaining]);
}

// 按命令行 --urls > 环境变量 ASPNETCORE_URLS > appsettings.json 顶层 Urls 的顺序
// 定出监听地址（多个地址用 ; 分隔）；三者都没写时沿用 Go 版的默认监听。
static string ResolveListenUrls(string[] hostArgs, IConfiguration configuration)
{
    var fromArgs = new ConfigurationBuilder().AddCommandLine(hostArgs).Build()["urls"];
    var fromEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    var fromFile = configuration["Urls"];
    foreach (var candidate in new[] { fromArgs, fromEnvironment, fromFile })
    {
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate.Trim();
        }
    }
    return "http://127.0.0.1:8080";
}
