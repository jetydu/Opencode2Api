namespace Opencode2Api.Protocol;

/// <summary>
/// 线路协议标识符。Chat、Responses 与 Anthropic 的负载会被
/// 桥接；System One 的决策负载不透明地透传。
/// （Go：internal/protocol/protocol.go，在那里以 "wire" 导入。）
/// </summary>
public static class Wire
{
    public const string Chat = "chat";
    public const string Responses = "responses";
    public const string Anthropic = "anthropic";
    public const string SystemOne = "systemone";

    public static bool IsValid(string protocol) =>
        protocol is Chat or Responses or Anthropic or SystemOne;

    public static string Path(string protocol) => protocol switch
    {
        Responses => "/v1/responses",
        Anthropic => "/v1/messages",
        SystemOne => "/v1/systemone",
        _ => "/v1/chat/completions",
    };
}
