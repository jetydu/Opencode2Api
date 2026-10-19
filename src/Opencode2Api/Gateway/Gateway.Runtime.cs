using System.Text.Json;
using Opencode2Api.Config;
using Opencode2Api.Models;
using Opencode2Api.Rotation;
using Opencode2Api.Telemetry;

namespace Opencode2Api.Gateway;

/// <summary>
/// 拥有唯一一份网关运行期：轮转状态、模型目录缓存、价格与可用性存储，以及后台循环
/// （Go: internal/gateway/runtime.go）。
/// 核心网关不做配置热更新，因此这里没有 Apply/Update/Reload/Restart 路径——
/// 运行期在启动时构建一次，随宿主一起关闭。
/// </summary>
internal sealed class RuntimeManager : IAsyncDisposable
{
    private readonly Config.Config _cfg;
    private readonly StructuredLogger _logger;
    private readonly SecretRedactor _redactor;
    private readonly RotationManager _rotation;
    private readonly PricingStore _pricing;
    private readonly AvailabilityStore _availability;
    private readonly ApiGateway _gateway;
    private readonly CancellationTokenSource _runtime = new();
    private readonly CancellationTokenSource _availabilityScope = new();

    /// <summary>运行期状态文件的存放目录，供启动日志与诊断使用。</summary>
    public string StateDirectory { get; }

    public ApiGateway Gateway => _gateway;

    public RotationManager Rotation => _rotation;

    public static RuntimeManager Create(
        string configPath, Config.Config cfg, StructuredLogger logger, Monitor monitor, SecretRedactor redactor)
    {
        var manager = new RuntimeManager(configPath, cfg, logger, monitor, redactor);
        manager.Start();
        return manager;
    }

    private RuntimeManager(
        string configPath, Config.Config cfg, StructuredLogger logger, Monitor monitor, SecretRedactor redactor)
    {
        _cfg = cfg;
        _logger = logger;
        _redactor = redactor;

        // 运行期状态文件的基准路径：后面的轮转游标、可用性判定、
        // 目录缓存与价格缓存都在它上拼后缀。
        var stateBase = cfg.ResolveStateBase(configPath);
        StateDirectory = Path.GetDirectoryName(stateBase) ?? "";
        EnsureStateDirectory(stateBase, cfg.StateDir);

        // models.dev 刷新使用当前运行时的健康代理传输，
        // 没有可用代理时回退到存储的直连客户端。
        _pricing = new PricingStore(stateBase, logger);

        cfg.Rotation.Defaults();
        _rotation = new RotationManager(StatePath(stateBase), cfg.Rotation);
        _gateway = ApiGateway.Create(cfg, logger, monitor);
        _gateway.Rotation = _rotation;
        _pricing.SetClientProvider(() =>
        {
            var clients = new List<HttpClient>();
            foreach (var proxy in _gateway.Transports.Items)
            {
                if (proxy.HealthyLoad())
                {
                    clients.Add(proxy.Client);
                }
            }
            return clients;
        });

        var availabilityPath = stateBase.Length == 0
            ? ""
            : stateBase + "." + Secrets.Fingerprint(cfg.Upstream.Zen) + ".availability.json";
        try
        {
            _availability = new AvailabilityStore(availabilityPath);
        }
        catch (Exception error) when (error is ConfigException or IOException or UnauthorizedAccessException)
        {
            throw new GatewayException($"load model availability: {error.Message}", error);
        }
        _gateway.Availability = _availability;
        _gateway.Catalog.SetPricingStore(_pricing);
        _gateway.Catalog.SetAvailabilityStore(_availability);
        _gateway.Catalog.SetCachePath(CatalogCache.Path(stateBase));

        try
        {
            _gateway.Catalog.LoadCache(CatalogCache.Path(stateBase));
        }
        catch (FileNotFoundException)
        {
        }
        catch (Exception error) when (error is ConfigException or IOException or JsonException
            or UnauthorizedAccessException)
        {
            _logger.Warn("model catalog cache ignored",
                ("component", "models"), ("event", "catalog_cache_load_failed"),
                ("path", CatalogCache.Path(stateBase)), ("error", error));
        }

        _cfg.Rotation.Validate(_gateway.Catalog.List());
        _gateway.SyncRotation();
        _redactor.Replace(_cfg);
        _logger.SetLevel(_cfg.Logging.Level);
    }

    public static string StatePath(string stateBase) => stateBase + ".rotation.json";

    /// <summary>
    /// state_dir 指定的目录不存在时就地创建；Go 版把状态文件写在配置文件旁边，
    /// 不需要这一步。
    /// </summary>
    private static void EnsureStateDirectory(string stateBase, string stateDir)
    {
        if (stateDir.Length == 0 || stateBase.Length == 0)
        {
            return;
        }
        var directory = Path.GetDirectoryName(stateBase);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new GatewayException($"create state directory {directory}: {error.Message}", error);
        }
    }

    private void Start()
    {
        _gateway.StartProxyHealthChecks(_runtime.Token);
        _gateway.StartModelRefresh(_runtime.Token);
        _pricing.Start(_runtime.Token);
        StartAvailabilityChecks();
    }

    /// <summary>
    /// 每分钟探测一次免费模型，启动时先跑一次，避免首批请求落在空的可用性表上
    /// （Go: internal/gateway/availability.go）。
    /// </summary>
    private void StartAvailabilityChecks() => BackgroundLoop.Start(
        "availability", _logger, TimeSpan.FromMinutes(1), true,
        CheckFreeModelsAsync, _availabilityScope.Token);

    private async Task CheckFreeModelsAsync(CancellationToken cancellationToken)
    {
        foreach (var item in AvailabilitySnapshot())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            var current = _availability.Get(item.Model);
            if (current.Manual)
            {
                continue;
            }
            if (DateTime.UtcNow < current.NextCheck)
            {
                continue;
            }
            var result = await _gateway.ProbeFreeModel(cancellationToken, current.Model);
            if (!result.Attempted || cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            try
            {
                _availability.Record(current.Model, current.Generation, result.Success,
                    result.Reason, result.Channel, DateTime.UtcNow, result.Effort);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                _logger.Warn("model availability state save failed", ("error", error));
            }
            _logger.Info("free model availability checked",
                ("component", "models"), ("event", "availability_checked"), ("model", current.Model),
                ("available", result.Success), ("reason", result.Reason), ("channel", result.Channel));
        }
    }

    /// <summary>当前纳入自动探测的免费模型可用性记录。</summary>
    private List<Availability> AvailabilitySnapshot()
    {
        var items = new List<Availability>();
        foreach (var model in _gateway.Catalog.List())
        {
            if (_gateway.Catalog.IsFreeModel(model))
            {
                items.Add(_availability.Get(model));
            }
        }
        return items;
    }

    public async ValueTask DisposeAsync()
    {
        _availabilityScope.Cancel();
        _runtime.Cancel();
        _availabilityScope.Dispose();
        _runtime.Dispose();
        await Task.CompletedTask;
    }
}
