namespace Opencode2Api.Config;

/// <summary>上游线路标识（"zen" 或 "go"）。</summary>
public static class Tier
{
    public const string Zen = "zen";
    public const string Go = "go";

    /// <summary>全部可宣告的档位，按默认优先顺序排列。</summary>
    public static readonly string[] All = [Zen, Go];

    public static bool IsValid(string tier) => tier is Zen or Go;
}

/// <summary>
/// 元数据以既有密钥指纹为键；密钥明文仍留在
/// server_keys 中，以兼容既有配置与客户端。
/// </summary>
public sealed class ServerKeyMetadata
{
    public string Name { get; set; } = "";
    public bool Disabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
