using System.Text.RegularExpressions;

namespace Opencode2Api.Config;

public sealed class RotationGroup
{
    /// <summary>对外暴露的分组别名（可当作模型 ID 调用）。</summary>
    public string Alias { get; set; } = "";

    /// <summary>组内真实模型 ID 的轮转顺序。</summary>
    public List<string> Order { get; set; } = [];
}

/// <summary>粘性模型分组轮转配置（internal/config/rotation.go）。</summary>
public sealed class RotationConfig
{
    /// <summary>内置 SOTA 分组包含的真实模型 ID。</summary>
    public static readonly string[] SotaModels =
    [
        "deepseek-v4.1-flash", "glm-5.3", "grok-4.6", "kimi-k3", "qwen3.8-max",
    ];

    /// <summary>别名合法性校验，进程内只构造一次。</summary>
    private static readonly Regex AliasPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    public RotationGroup Sota { get; set; } = new();
    public RotationGroup Sweet { get; set; } = new();
    public int FirstOutputSeconds { get; set; }

    public static bool IsSota(string model) => SotaModels.Contains(model);

    public void Defaults()
    {
        if (Sota.Alias.Length == 0)
        {
            Sota.Alias = "sota";
        }
        if (Sweet.Alias.Length == 0)
        {
            Sweet.Alias = "sweet";
        }
        if (Sota.Order.Count == 0)
        {
            Sota.Order = [.. SotaModels];
        }
        if (FirstOutputSeconds == 0)
        {
            FirstOutputSeconds = 30;
        }
    }

    public void Validate(IReadOnlyList<string>? realModels)
    {
        if (FirstOutputSeconds < 1 || FirstOutputSeconds > 300)
        {
            throw new ConfigException("首个有效输出超时需为 1–300 秒");
        }
        if (Sota.Alias == Sweet.Alias)
        {
            throw new ConfigException("两个分组的别名不能相同");
        }
        var groups = new[] { Sota, Sweet };
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            if (!AliasPattern.IsMatch(group.Alias))
            {
                throw new ConfigException("别名需为 1–64 位英文字母、数字、点、短横线或下划线");
            }
            if ((realModels != null && realModels.Contains(group.Alias)) || IsSota(group.Alias) || Sweet.Order.Contains(group.Alias))
            {
                throw new ConfigException("别名不能与真实模型 ID 相同");
            }
            var seen = new HashSet<string>();
            foreach (var model in group.Order)
            {
                if (model.Length == 0 || seen.Contains(model) || IsSota(model) != (i == 0))
                {
                    throw new ConfigException("模型顺序包含重复、空值或不属于该分组的模型");
                }
                seen.Add(model);
            }
            if (i == 0 && group.Order.Count != SotaModels.Length)
            {
                throw new ConfigException("SOTA 组必须包含指定的五个模型");
            }
        }
    }
}
