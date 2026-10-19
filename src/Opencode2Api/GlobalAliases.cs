// 网关以路由模型请求的语义表述，而 ASP.NET Core
// 把其中一些名字保留给了自己的管道。集中做一次别名，
// 可以让所有网关文件都不产生歧义（Go 包不存在这种冲突）。
global using Route = Opencode2Api.Models.Route;
global using Monitor = Opencode2Api.Telemetry.Monitor;
