namespace Opencode2Api.Config;

/// <summary>
/// 配置相关的错误：绑定失败、未知键、取值越界都归到这里，
/// 启动阶段统一打印成 configuration error。
/// </summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message)
    {
    }

    public ConfigException(string message, Exception inner) : base(message, inner)
    {
    }
}
