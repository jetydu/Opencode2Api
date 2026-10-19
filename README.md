# opencode2api（.NET 10 版）

本项目是 Go 版 [opencode2api](../opencode2api) 的 .NET 移植，把 OpenCode 的 **Zen / Go / 匿名（/inference）** 三条上游线路
统一暴露成 OpenAI 与 Anthropic 兼容的 HTTP 网关：

- OpenAI Chat Completions：`POST /v1/chat/completions`
- OpenAI Responses：`POST /v1/responses`
- Anthropic Messages：`POST /v1/messages`
- System One 决策：`POST /v1/systemone`
- 模型发现：`GET /v1/models`
- 健康检查：`GET /healthz`

网关自身不做任何模型推理，只负责：**鉴权 → 路由选路 → 协议转换 → 上游重试与代理轮换 → 流式回传**。

---

## 1. 与 Go 版的差异

| 能力 | Go 版 | .NET 版 |
| --- | --- | --- |
| 核心网关（上面 6 个端点） | ✅ | ✅ 逐字段兼容 |
| WebUI 管理后台（`webui/`、`internal/admin/`） | ✅ | ❌ 未移植，`webui` 配置段已随之删除（写了会当未知键报错） |
| API Key 增删改查、模型配额、调试面板等管理端点 | ✅ | ❌ 未移植 |
| 配置热更新（运行期 `Apply/Reload`） | ✅ | ❌ 运行期只构建一次 `RuntimeManager`，改配置需重启 |
| 配置来源 | `config.json` + 环境变量覆盖 | .NET 配置系统：`appsettings.json` 的 `Opencode2Api` 节 + `appsettings.{Environment}.json` + 环境变量 + 命令行（见 §3、§4） |
| 监听地址 | `config.json` 的 `listen`，由服务自己 `net.Listen` | 不再占一个业务配置项，直接用 ASP.NET Core 标配的 `urls`（见 §3） |
| 日志 | `log/slog` TextHandler | NLog（`nlog.config`），结构化字段由 `StructuredLogger` 拼装 |
| HTTP 服务器 | `net/http` + 自定义 Transport | ASP.NET Core Minimal API + Kestrel（超时/保活对齐 Go） |
| SOCKS5 代理 | `golang.org/x/net/proxy` | 自实现 `Gateway/Socks5.cs` |
| 池亲和哈希 | `hash/fnv` | 自实现 64 位 FNV-1a（`Gateway/Hashing.cs`） |
| 密钥指纹 | `sha256` 前缀 | 同为 SHA-256 十六进制前 10 位（`Config/Secrets.cs`） |

除以上差异外，请求/响应的 JSON 结构、SSE 事件序列、错误体形状都与 Go 版保持一致，
既有的看板与客户端可以直接对接。不同的是配置文件形式：Go 版是一份独立的
`config.json`（snake_case），.NET 版把同样这些设置合并进了标准的 `appsettings.json`，
键名改成 .NET 习惯的 PascalCase，从而免费得到分环境文件与环境变量覆盖能力。
运行期状态文件默认收进 `state/` 子目录（见 §8），把 `StateDir` 设为 `""` 即可回到
Go 版「紧贴配置文件」的布局。

---

## 2. 环境要求与构建

- .NET SDK 10.0（开发时使用 10.0.13）
- Windows / Linux / macOS 均可（无平台专属代码）

```powershell
# 还原 + 构建（解决方案为 Opencode2Api.slnx）
dotnet build opencode2api-net/src/Opencode2Api/Opencode2Api.csproj -c Release

# 运行（配置就在 src/Opencode2Api/appsettings.json，无需复制、也不依赖当前目录）
dotnet run --project opencode2api-net/src/Opencode2Api

# 单元测试
dotnet test opencode2api-net/tests/Opencode2Api.Tests/Opencode2Api.Tests.csproj

# 自包含发布（单文件可执行）
dotnet publish opencode2api-net/src/Opencode2Api/Opencode2Api.csproj -c Release -r win-x64 --self-contained false
```

`src/Opencode2Api/appsettings.json` 预置了一份可直接跑的配置：只开匿名免费线路
（`Anonymous: true`）、本地密钥为 `change-this-local-key`、监听 `http://localhost:8080`。
拿去接真实客户端时改顶层的 `Urls` 与 `ServerKeys` 即可；文件里同时列出了全部可选项，
不写的字段会走代码内置默认值（见 §4 的默认值列）。

---

## 3. 命令行参数与环境覆盖

监听地址完全走 ASP.NET Core 标配的 `urls`，不再是一个业务配置项：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `"Urls": "http://localhost:8080"` | — | 写在 `appsettings.json` **顶层**（不在 `Opencode2Api` 节里），多个地址用 `;` 分隔 |
| `ASPNETCORE_URLS` | — | 环境变量形式的监听地址，优先级高于 `appsettings.json` |
| `--urls <地址>` / `--urls=<地址>` | — | 命令行监听地址，优先级最高，例如 `--urls "http://0.0.0.0:8080"` |
| `--config <path>` / `--config=<path>` | 不启用 | 追加一份额外的 JSON 配置（内含同样的 `Opencode2Api` 节），优先级高于 `appsettings.json`；用于多实例共用一个二进制 |
| `--Opencode2Api:<Key> <value>` | — | .NET 标配的命令行覆盖，例如 `--Opencode2Api:Prefer zen`、`--Opencode2Api:Logging:Level debug` |

监听地址的优先级：`--urls` > `ASPNETCORE_URLS` > `appsettings.json` 的 `Urls` >
内置兜底值 `http://127.0.0.1:8080`（三者都没写时生效）。

业务配置的读取优先级（后者覆盖前者，全部按单个键生效，不是整节替换）：

1. `appsettings.json`
2. `appsettings.{ASPNETCORE_ENVIRONMENT}.json`（`dotnet run` 默认 Development，预置的那份只把日志调成 `debug`）
3. 环境变量：`Opencode2Api__Prefer`、`Opencode2Api__ServerKeys__0`、`Opencode2Api__Retry__MaxAttempts`……
4. `--config` 指定的额外文件
5. 命令行 `--Opencode2Api:Key`

> 内容根固定为可执行文件所在目录（`AppContext.BaseDirectory`），`appsettings.json` 与
> `nlog.config` 都随构建/发布拷到那里，因此从任意工作目录启动读到的都是同一份配置，
> `ProxyFile` / `StateDir` 这类相对路径也以该目录为基准。

配置校验失败时进程返回退出码 `1`，并把 `configuration error: ...` 打到 stderr。

---

## 4. 配置文件

配置位于 `src/Opencode2Api/appsettings.json` 的 `Opencode2Api` 节（监听地址除外，它在同一文件的
顶层 `Urls`，见 §3），键名用 .NET 习惯的 PascalCase
（绑定不区分大小写，但**不认下划线**：写成 `server_keys` 会被当成未知键而报错）。
JSON 由 `Microsoft.Extensions.Configuration` 的 provider 解析，因此**允许 `//` 行注释、
`/* */` 块注释与尾逗号**；绑定又开了 `ErrorOnUnknownConfiguration`，所以**拼错的键同样启动失败**，
不会静默忽略。

最小可用配置：

```jsonc
{
  "Urls": "http://localhost:8080",              // 顶层，宿主管的监听地址
  "Opencode2Api": {
    "ServerKeys": ["change-this-local-key"], // 本地调用必须带的 key
    "ZenKeys": ["sk-..."],                   // Zen 线路凭据
    "GoKeys": [],
    "Anonymous": true                        // 允许回落到免费匿名线路
  }
}
```

> 字典型配置（`Models.Protocols`、`Reasoning.EffortByModel`、`ServerKeyMetadata`）的键要走配置路径，
> 因此键名里不能出现 `:`（会被当层级分隔符）；现有模型 ID 都不带冒号，若自定义 ID 含冒号就换个名字。

### 4.1 顶层字段

| 字段 | 类型 | 默认值 | 校验规则 / 说明 |
| --- | --- | --- | --- |
| `ServerKeys` | string[] | — | 至少一个非空密钥；调用方以 `x-api-key` 或 `Authorization: Bearer` 携带其中之一 |
| `ServerKeyMetadata` | object | `{}` | 以密钥指纹（SHA-256 十六进制前 10 位）为键，值为 `{Name, Disabled, CreatedAt}`；用于在不删除密钥的情况下停用 |
| `ZenKeys` | string[] | `[]` | Zen（`/zen`）线路的上游密钥池 |
| `GoKeys` | string[] | `[]` | Go（`/zen/go`）线路的上游密钥池 |
| `Anonymous` | bool | `false` | 是否允许走公共凭据的 `/inference` 免费线路。`false` 且两个密钥池都空 → 启动失败 |
| `Proxies` | string[] | `["direct"]` | 代理条目，见 §7 |
| `ProxyFile` | string | `""` | 额外代理列表文件（每行一条，`#` 开头为注释）；相对路径按配置文件所在目录解析 |
| `StateDir` | string | `"state"` | 运行期状态文件的存放目录；相对路径按配置文件所在目录解析，置 `""` 则紧贴配置文件（Go 版布局），目录不存在时自动创建 |
| `Prefer` | string | `"go"` | 只能是 `zen` 或 `go`，决定同时可用时的首选档位 |
| `Upstream.Zen` | string | `https://opencode.ai/zen` | 必须是 http/https URL |
| `Upstream.Go` | string | `https://opencode.ai/zen/go` | 同上 |
| `Upstream.Anonymous` | string | `https://opencode.ai/inference` | 空字符串时自动回填默认值 |

### 4.2 `Retry` / `Models` / `Performance` / `Logging` / `Reasoning` / `Rotation`

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `Retry.MaxAttempts` | `3` | 单次请求跨节点/跨档位的最大尝试次数，≥1 |
| `Retry.TimeoutSeconds` | `300` | 整个请求的总预算，≥1 |
| `Models.RefreshSeconds` | `300` | 模型目录刷新周期，≥1 |
| `Models.Protocols` | `{}` | 按模型强制协议：`{"model-id": "chat" \| "responses" \| "messages" \| "systemone"}` |
| `Models.FreeOnly` | `false` | 只在发现与路由中暴露免费模型 |
| `Performance.MaxConnsPerHost` | `0` | 每主机连接上限，`0` 表示不限 |
| `Performance.IdleConnTimeoutSeconds` | `120` | 空闲连接存活时间，≥1 |
| `Performance.ConnectTimeoutSeconds` | `5` | 拨号（含代理握手）超时，≥1 |
| `Performance.FailureCooldownSeconds` | `15` | 节点/代理失败后的冷却时间，≥1 |
| `Performance.AttemptTimeoutSeconds` | `0` | 单次上游尝试等待响应头的上限；`0` 表示沿用 `Retry.TimeoutSeconds`，且不会超过请求级预算 |
| `Performance.FirstEventTimeoutSeconds` | `0` | SSE 首个事件的等待上限，`0` 关闭；允许 0–86400 |
| `Logging.Level` | `"info"` | `debug` / `info` / `warn` / `error`（与 NLog 规则叠加） |
| `Logging.DumpRequestBodies` | `false` | 是否把请求体写进日志（含敏感内容，仅调试用） |
| `Reasoning.Effort` | `""` | 为未显式声明思考等级的请求强制等级：`auto/minimal/low/medium/high/xhigh/max/none/空`。客户端显式传的等级永远优先 |
| `Reasoning.EffortByModel` | `null` | 按模型覆盖 `Reasoning.Effort` |
| `Rotation.Sota.Alias` | `"sota"` | 别名需匹配 `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` |
| `Rotation.Sota.Order` | 内置 5 个模型 | SOTA 分组的轮转顺序 |
| `Rotation.Sweet.Alias` / `.Order` | `"sweet"` / `[]` | 自定义分组，行为与 sota 相同 |
| `Rotation.FirstOutputSeconds` | `30` | 别名请求判定“首个输出是否到达”的时间窗，超时即切下一个模型 |

跟 Go 版比，以下几项因 .NET 里没有对应行为而删除，写了会直接报错：
`webui.*`（未移植管理界面）、`Logging.RingSize`（Go 的内存日志环形缓冲属于管理端）、
`Performance.MaxIdleConns` / `MaxIdleConnsPerHost`（.NET 连接池没有等价项，空闲回
收由 `PooledConnectionIdleTimeout` 接管，并发上限由 `MaxConnsPerHost` 承担）。

另有一个键是**搬家**而不是废弃：Go 版的 `listen` 在 .NET 版改成了顶层的 `Urls`
（见 §3），继续写在 `Opencode2Api` 节里会被当成未知键而启动失败。

---

## 5. 鉴权与端点

### 5.1 鉴权

除 `GET /healthz` 外的所有端点都要求本地 API Key，两种携带方式等价：

```bash
curl -H "x-api-key: change-this-local-key" http://127.0.0.1:8080/v1/models
curl -H "Authorization: Bearer change-this-local-key" http://127.0.0.1:8080/v1/models
```

缺失或错误返回协议形态的 `401`（OpenAI 用 `error` 对象，Anthropic 用 `type/error` 结构）。
`ServerKeyMetadata[指纹].Disabled = true` 可临时停用某个密钥而不改动 `ServerKeys`。

### 5.2 端点行为

| 端点 | 说明 |
| --- | --- |
| `GET /healthz` | 免鉴权。返回 `status`（`ok`/`degraded`/`starting`）、`ready`、`version`，以及 `models`（总数/暴露数/zen/go/最近刷新/是否过期）、`keys`（zen/go/总数/是否匿名）、`proxies`（总数/健康/不健康）、`issues` 数组。目录未就绪或没有健康代理时返回 `503` |
| `GET /v1/models` | OpenAI 形状的模型列表，`owned_by` 为 `opencode`；轮转别名会额外插入 `owned_by = opencode-go-rotation` 的条目，方便只认模型名的客户端直接使用 `sota`/`sweet` |
| `POST /v1/chat/completions` | OpenAI Chat 协议，支持 `stream: true` 的 SSE；也接受 responses/messages 形态请求并做桥接转换 |
| `POST /v1/responses` | OpenAI Responses 协议（含 `reasoning` 条目、工具调用），可流式 |
| `POST /v1/messages` | Anthropic Messages 协议（含 `thinking` 块、`tool_use`/`tool_result`），可流式 |
| `POST /v1/systemone` | System One 决策协议。请求体形如 `{"model": "...", "state": {...}, "questions": [...]}`，负载**原样中继、永不翻译**；只有上游协议为 systemone 的模型能在此服务 |

补充约束：

- 请求体上限 `32 MiB`（`32 << 20`），超限返回 `400`，行为对齐 Go 的 `http.MaxBytesReader`。
- 单条 SSE 行上限 `16 MiB`。
- System One 模型如果被发到 `/v1/chat/completions` 或 `/v1/responses`：若请求体已带 `state` + 非空 `questions`，网关原样中继；否则返回 `400` 并提示改用 `/v1/systemone`。
- Kestrel 侧：请求头读取超时 `15s`、keep-alive 空闲 `120s`、优雅停机窗口 `15s`，与 Go `http.Server` 一致。

### 5.3 流式与协议折叠

- 同协议请求直接透传上游 SSE，不做二次编解码。
- 跨协议（如客户端用 Chat、上游只有 Messages）由 `Protocol/Bridge.cs` + `StreamEmitter/StreamParser` 逐事件转换，工具名按 `Protocol/StreamRename.cs` 还原。
- 客户端要 JSON、上游只给了流（或反之）时，由 `Protocol/Collapse.cs` 双向折叠。
- 上游返回“过期 reasoning 引用”类 400 时，`Gateway.Upstream.cs` 会剥离 stale reasoning 后重放一次完整路由。

---

## 6. 路由、重试与轮转

**档位（tier）**：`zen`、`go`、`anonymous`。`Catalog.Route()` 按下列顺序构造回退计划：

1. `Prefer` 指定的档位优先；
2. 已配置密钥且目录宣告支持该模型的档位依次加入（`KeyTiers` 为有序回退列表）；
3. `Anonymous: true` 时追加公共凭据的 `/inference` 线路（每个免费模型都从这里开始兜底）。

Zen 与 Go **有意不共用全局协议**：同一模型在 Zen 上可能是 Chat、在 Go 上是 Messages，
因此重试跨档位时会重新编码请求体。

**重试与冷却**：每次尝试按 `Performance.AttemptTimeoutSeconds`/`Performance.FirstEventTimeoutSeconds` 限时，
失败节点进入 `Performance.FailureCooldownSeconds` 冷却；匿名线路遇到 `Retry-After` 时按最多 8 倍退避。
整体仍受 `Retry.TimeoutSeconds` 约束。

**别名轮转**（`Rotation/RotationManager.cs`，对应 `internal/rotation/state.go`）：
`sota`/`sweet` 这类别名按 `Rotation.<组>.Order` 粘性轮转——某个模型在 `Rotation.FirstOutputSeconds` 内没吐出首个输出，
就切到组内下一个，并把游标持久化到状态文件，重启后继续。轮转流的判定与折叠在
`Protocol/RotationStream.cs`。

---

## 7. 代理

`Proxies` 与 `ProxyFile` 支持的条目形式：

```
direct                                     # 直连
http://user:pass@127.0.0.1:8080
https://proxy.example.com:8443
socks5://127.0.0.1:1080                    # 本地解析域名
socks5h://127.0.0.1:1080                   # 由代理解析域名
pool+socks5://a.example:1080               # 动态池条目（前缀形式）
socks5://b.example:1080#pool               # 动态池条目（片段形式）
```

- `pool+` / `#pool` 标记的条目进入**动态轮转池**：同一代理族内多节点轮换，失败节点被摘除并在冷却后回池。
- 每个代理都有后台健康检查循环，`/healthz` 的 `proxies.healthy` 即来自这里；不健康代理不参与选路。
- `ProxyFile` 与 `Proxies` 合并去重后得到“生效列表”；`Config.Proxies` 只保留 `appsettings.json` 里书写的那份，
  不会把 `ProxyFile` 载入的条目混进来。
- SOCKS5 客户端为自实现（`Gateway/Socks5.cs`），支持 USER/PASS 认证与域名/IPv4/IPv6 目标。

---

## 8. 后台任务与状态文件

`RuntimeManager` 启动 4 个后台循环（统一由 `Telemetry/BackgroundLoop.cs` 承载，主机停机时一起取消）：

| 循环 | 周期 | 作用 |
| --- | --- | --- |
| 代理健康检查 | 15 分钟（启动不立即跑） | 访问 `https://cloudflare.com/cdn-cgi/trace`（超时 10s）探测每个代理传输，维护冷却与摘除 |
| 模型目录刷新 | `Models.RefreshSeconds`（默认 300s，启动即跑） | 并发拉取三个数据来源，全部返回后一起生效并写入磁盘快照（单次拉取上限 30s） |
| models.dev 定价刷新 | 24 小时（启动即跑） | 用健康的代理传输抓取价格与限制值（单次 30s、响应体 ≤ 32 MiB），填充目录缺失字段 |
| 免费模型可用性探测 | 每分钟一次，启动即跑一次 | 对免费模型做真实探测：成功 1 小时后再探，确认不可用（`model_unavailable`）24 小时后再探；运维手工锁定的模型不参与 |

状态文件由 `RuntimeManager` 统一从配置的 `StateDir` 派生（主名固定为 `opencode2api`，
不再跟随配置文件名），全部由 `IO/AtomicText.cs`（临时文件 + 重命名）崩溃安全写入：

```
<StateDir>/opencode2api.rotation.json                # 别名轮转：每组的游标、暂停表、可用性同步结果、切换与请求轨迹
<StateDir>/opencode2api.<指纹>.availability.json     # 免费模型可用性判定 + 运维手工锁定（指纹为 zen 上游的 SHA-256 前 10 位）
<StateDir>/opencode2api.models.catalog.json          # 模型能力目录快照（首次刷新失败时靠它冷启动）
<StateDir>/opencode2api.models.dev.json              # models.dev 派生的价格与限制值
```

各文件的作用与“能不能不生成”：

| 文件 | 丢了会怎样 |
| --- | --- |
| `*.rotation.json` | 别名轮转游标回到组首，正在被暂停的模型立刻恢复；不影响正确性，只影响粘性 |
| `*.availability.json` | 已判定不可用的免费模型会被重新探测；`manual` 手工锁定标记也会丢失 |
| `*.models.catalog.json` | 首次目录刷新前无快照可用，刷新失败时网关会 `model_catalog_pending` |
| `*.models.dev.json` | 价格与上下文/输出上限退回目录自带值，下次 24h 刷新后补齐 |

也就是说这四个文件都是“跨重启缓存”，不能真正关掉写入（关掉就等于每次冷启动），
但可以放到任何你想放的位置：

```jsonc
"StateDir": "state"                 // 默认：配置目录下的 state/ —— state/opencode2api.rotation.json
"StateDir": "D:/opencode2api-state" // 绝对路径：直接写到这里
"StateDir": ""                      // Go 版布局：状态文件紧贴配置文件（appsettings.json.rotation.json）
```

主名不再取配置文件名，因此把配置文件改名或换目录不会再新建一份状态；
Go 版产生的这些文件内容可以被 .NET 版直接读取（字段命名一致），只需把它们改名成
`<StateDir>/opencode2api.` 前缀，或把 `StateDir` 置空。

---

## 9. 日志

- 由 NLog 接管（`src/Opencode2Api/nlog.config`）：控制台彩色输出 + `logs/opencode2api.log` 按天滚动、保留 14 天。
- `Microsoft.*` 与 `System.Net.Http.*` 的框架日志被丢弃，避免刷屏。
- 业务日志走 `StructuredLogger`，形如 `event=... component=... model=... tier=...` 的键值串，对齐 Go 的 slog 文本格式。
- 所有密钥、代理 URL 中的凭据都经 `Config/Secrets.cs`（SHA-256 指纹 + URL 脱敏）处理后才可能进入日志。
- `Logging.Level` 在运行期由 `StructuredLogger.SetLevel` 映射到 NLog 规则；根层的
  `Logging:LogLevel` 不再使用（日志已经全部交给 NLog，nlog.config 里丢弃了框架噪声）。

---

## 10. 代码结构与 Go 源文件映射

```
opencode2api-net/
├── Opencode2Api.slnx
├── src/Opencode2Api/
│   ├── Program.cs                 cmd/opencode2api/main.go
│   ├── BuildInfo.cs               internal/buildinfo/version.go
│   ├── Durations.cs / GlobalAliases.cs / JsonUtil/JsonValues.cs
│   │                              internal/jsonutil/{decode,value}.go 等公共小工具
│   ├── Config/                    internal/config/
│   │   ├── Config.cs              config.go（绑定默认值、全部校验 + StateDir）
│   │   ├── ConfigException.cs     配置错误类型（启动阶段统一成 configuration error）
│   │   ├── Proxy.cs               proxy.go
│   │   ├── RotationConfig.cs      rotation.go
│   │   ├── Secrets.cs             secrets.go（指纹、脱敏）
│   │   └── Tier.cs                tier.go
│   ├── Gateway/                   internal/gateway/
│   │   ├── Gateway.cs             gateway.go（端点、鉴权、请求读取）
│   │   ├── Gateway.Health.cs      health.go（/healthz、/v1/models）
│   │   ├── Gateway.Upstream.cs    upstream.go（路由执行、stale reasoning 重放）
│   │   ├── Gateway.Rotation.cs    rotation.go（别名轮转选路）
│   │   ├── Gateway.Refresh.cs     refresh.go（目录刷新）
│   │   ├── Gateway.Probe.cs       probe.go（免费模型探测）
│   │   ├── Gateway.Runtime.cs     runtime.go + availability.go（运行期与后台循环，状态文件基准路径也在这里解析）
│   │   ├── NodePools.cs           pool.go（密钥池 / 代理池 / 匿名池）
│   │   ├── Transports.cs          pool.go（代理传输与健康状态）
│   │   ├── HttpAttempt.cs         first_event.go（首事件/尝试超时计时）
│   │   ├── InferenceAttempt.cs    请求级预算与重放
│   │   ├── AnonymousBody.cs       匿名 /inference 线路的工具门槛与请求整形
│   │   ├── Socks5.cs              自实现 SOCKS5 客户端
│   │   ├── Hashing.cs             自实现 64 位 FNV-1a + 代理端点解析
│   │   └── opencode_tools.json    官方 CLI 工具定义（嵌入资源，同 //go:embed）
│   ├── Models/                    internal/models/
│   │   ├── Catalog.cs             catalog.go（发现、路由、能力）
│   │   ├── CatalogCache.cs        cache.go（磁盘快照）
│   │   ├── Discovery.cs           discovery.go（models.dev 元数据）
│   │   ├── Pricing.cs             pricing.go（价格与匿名决策）
│   │   ├── Availability.cs        availability.go
│   │   ├── Metadata.cs / JsonTime.cs
│   ├── Protocol/                  internal/protocol/
│   │   ├── Wire.cs                protocol.go（协议常量与判定）
│   │   ├── Request.cs             request.go（请求解析与转换）
│   │   ├── Response.cs            response.go（非流式响应改写）
│   │   ├── Bridge.cs              bridge.go（跨协议对象互转）
│   │   ├── Collapse.cs            collapse.go（流↔JSON 折叠）
│   │   ├── Content.cs             content.go（内容块与思考块判定）
│   │   ├── ToolImages.cs          tool_images.go（工具结果图片）
│   │   ├── Stream.cs              stream.go（SSE 读写与终止信号）
│   │   ├── StreamParser.cs        stream_parser.go
│   │   ├── StreamEmitter.cs       stream_emitter.go
│   │   ├── StreamRename.cs / StreamSignals.cs
│   │   ├── RotationStream.cs      rotation_stream.go（轮转流缓冲与切换）
│   │   ├── ProtocolError.cs       errors.go（协议形态错误体）
│   │   └── Jsonx.cs               JSON 解析辅助
│   ├── Rotation/RotationManager.cs  internal/rotation/state.go
│   ├── Http/HttpResponses.cs        internal/httpx/
│   ├── Identity/RequestIds.cs       internal/identity/request.go（会话/请求 ID 重编码）
│   ├── IO/AtomicText.cs             崩溃安全写入（Go: os.CreateTemp+Sync+Rename）
│   └── Telemetry/                   internal/telemetry/
│       ├── Middlewares.cs           请求日志 + panic 恢复
│       ├── StructuredLogger.cs      slog TextHandler 等价输出
│       ├── RequestMeta.cs           请求上下文元数据
│       └── BackgroundLoop.cs        统一后台循环（含 panic 隔离）
└── tests/Opencode2Api.Tests/      单元测试（配置、代理、JSON 取值、轮转、原子写入等纯逻辑）
```

---

## 11. 常用调用示例

```powershell
$Key = "change-this-local-key"
$Base = "http://127.0.0.1:8080"

# 健康检查（免鉴权）
Invoke-RestMethod "$Base/healthz"

# 模型列表
Invoke-RestMethod "$Base/v1/models" -Headers @{ "x-api-key" = $Key }

# Chat 非流式
$body = @{ model = "sota"; messages = @(@{ role = "user"; content = "你好" }) } | ConvertTo-Json -Depth 6
Invoke-RestMethod "$Base/v1/chat/completions" -Method Post -ContentType "application/json" `
  -Headers @{ "x-api-key" = $Key } -Body $body

# Anthropic Messages 流式（curl 更直观）
curl -N -H "x-api-key: $Key" -H "content-type: application/json" `
  -d '{\"model\":\"claude-sonnet-4\",\"max_tokens\":64,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}' `
  http://127.0.0.1:8080/v1/messages
```

---

## 12. 排障要点

| 现象 | 排查方向 |
| --- | --- |
| 启动即退出 `configuration error` | 配置里有未知键/拼错的键（注意 .NET 版用 PascalCase，不认 `server_keys` 这种下划线键）；`Prefer`、`Logging.Level`、`Reasoning.Effort` 取值受限；`ServerKeys` 为空 |
| 报 `Listen` 是未知键 | 监听地址已搬到 `appsettings.json` 顶层的 `Urls`，不再属于 `Opencode2Api` 节（见 §3） |
| Kestrel 报 `Failed to bind ... address already in use` | 端口被占或 `Urls` 写错；改顶层 `Urls`，或用 `ASPNETCORE_URLS` / `--urls` 换一个 |
| `appsettings.json 里没有 "Opencode2Api" 配置节` | 配置被误删或改到了根层；所有网关设置都必须写在该节下（见 §4） |
| `/healthz` 返回 503 `model_catalog_pending` | 目录还没刷新成功；看日志 `component=models`，或确认代理能访问 `opencode.ai` |
| 全部请求 4xx `no healthy proxies` | 代理健康检查全灭；先用 `"Proxies": ["direct"]` 验证网络 |
| 免费模型时有 403 `FreeTierError` | 匿名线路要求请求带官方工具定义，网关会自动补齐；自建工具名可能被判不合规 |
| 别名总落在同一个模型 | `<StateDir>/opencode2api.rotation.json` 里保存了粘性游标，删除该文件即可回到组首 |
| 某模型被长期跳过 | 可用性存储把它判为不可用（24h 退避）；删 `<StateDir>/opencode2api.*.availability.json` 或在其中手工设置 `manual/disabled` |
| 状态文件散落得到处都是 | 统一由 `StateDir` 控制（默认 `state/`，相对路径基于可执行文件目录），见 §8 |
| 首 token 很慢就断流 | 调大 `Performance.FirstEventTimeoutSeconds`（默认 0 = 不额外限制） |
