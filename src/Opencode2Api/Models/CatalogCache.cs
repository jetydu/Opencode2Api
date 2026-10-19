using System.Text.Json;
using System.Text.Json.Nodes;
using Opencode2Api.Config;
using Opencode2Api.IO;
using Opencode2Api.JsonUtil;
using Opencode2Api.Protocol;

namespace Opencode2Api.Models;

/// <summary>
/// 公开模型能力目录的磁盘快照（models/cache.go
/// modelCatalogCache）。凭据与代理配置不属于其中，
/// 绝不会写入这个文件。
/// </summary>
internal sealed class ModelCatalogCache
{
    public const int SchemaVersion = 3;

    public DateTime UpdatedAt { get; set; }
    public List<string> Zen { get; set; } = [];
    public List<string> Go { get; set; } = [];
    public Dictionary<string, Dictionary<string, string>> NativeProtocols { get; } = NewProtocolMaps();
    public Dictionary<string, Dictionary<string, bool>> Unsupported { get; } = NewBoolMaps();
    public Dictionary<string, Dictionary<string, Metadata>> Metadata { get; } = NewMetaMaps();

    public static Dictionary<string, Dictionary<string, string>> NewProtocolMaps() =>
        new() { [Tier.Zen] = [], [Tier.Go] = [] };

    public static Dictionary<string, Dictionary<string, bool>> NewBoolMaps() =>
        new() { [Tier.Zen] = [], [Tier.Go] = [] };

    public static Dictionary<string, Dictionary<string, Metadata>> NewMetaMaps() =>
        new() { [Tier.Zen] = [], [Tier.Go] = [] };
}

/// <summary>模型目录的磁盘缓存（models/cache.go）。</summary>
internal static class CatalogCache
{
    private static readonly object WriteMutex = new();

    public static string Path(string configPath) =>
        configPath.Length == 0 ? "" : configPath + ".models.catalog.json";

    public static ModelCatalogCache Load(string path)
    {
        if (path.Length == 0)
        {
            throw new FileNotFoundException("no model catalog cache path configured", path);
        }
        var text = File.ReadAllText(path);
        var root = JsonNode.Parse(text)?.AsObject() ?? throw new ConfigException("model catalog cache is not a JSON object");
        var cache = new ModelCatalogCache();
        var schemaVersion = JsonValues.IntAt(root, "schema_version");
        if (schemaVersion != ModelCatalogCache.SchemaVersion)
        {
            throw new ConfigException($"unsupported model catalog cache schema version {schemaVersion}");
        }
        cache.UpdatedAt = JsonTime.Parse(JsonValues.StringAt(root, "updated_at"));
        if (JsonTime.IsZero(cache.UpdatedAt))
        {
            throw new ConfigException("model catalog cache is missing updated_at");
        }
        cache.Zen = NormalizeModelIDs(StringArray(root, "zen"));
        cache.Go = NormalizeModelIDs(StringArray(root, "go"));
        if (cache.Zen.Count == 0 && cache.Go.Count == 0)
        {
            throw new ConfigException("model catalog cache is empty");
        }
        if (JsonValues.MapAt(root, "native_protocols") is JsonObject native)
        {
            foreach (var property in native)
            {
                if (property.Value is not JsonObject protocols)
                {
                    continue;
                }
                foreach (var modelProperty in protocols)
                {
                    if (modelProperty.Value?.GetValueKind() == JsonValueKind.String)
                    {
                        EnsureTierMap(cache.NativeProtocols, property.Key)[modelProperty.Key] = modelProperty.Value.GetValue<string>();
                    }
                }
            }
        }
        if (JsonValues.MapAt(root, "unsupported") is JsonObject unsupported)
        {
            foreach (var property in unsupported)
            {
                if (property.Value is not JsonObject models)
                {
                    continue;
                }
                var target = EnsureTierMap(cache.Unsupported, property.Key);
                foreach (var modelProperty in models)
                {
                    target[modelProperty.Key] = JsonValues.BoolAt(models, modelProperty.Key);
                }
            }
        }
        if (JsonValues.MapAt(root, "metadata") is JsonObject metadata)
        {
            foreach (var property in metadata)
            {
                if (property.Value is not JsonObject models)
                {
                    continue;
                }
                var target = EnsureTierMap(cache.Metadata, property.Key);
                foreach (var modelProperty in models)
                {
                    target[modelProperty.Key] = Metadata.FromJson(modelProperty.Value as JsonObject);
                }
            }
        }
        ValidateCatalogCapabilities(cache.NativeProtocols, cache.Unsupported);
        cache.UpdatedAt = cache.UpdatedAt.ToUniversalTime();
        return cache;
    }

    public static void Save(string path, ModelCatalogCache cache)
    {
        if (path.Length == 0)
        {
            return;
        }
        lock (WriteMutex)
        {
            var root = new JsonObject
            {
                ["schema_version"] = ModelCatalogCache.SchemaVersion,
                ["updated_at"] = JsonTime.Format(cache.UpdatedAt),
                ["zen"] = ToStringArray(cache.Zen),
                ["go"] = ToStringArray(cache.Go),
                ["native_protocols"] = SerializeProtocols(cache.NativeProtocols),
                ["unsupported"] = SerializeBools(cache.Unsupported),
            };
            var hasMetadata = cache.Metadata.Values.Any(map => map.Count > 0);
            if (hasMetadata)
            {
                root["metadata"] = SerializeMetadata(cache.Metadata);
            }
            AtomicText.Write(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ".models-catalog-");
        }
    }

    private static List<string> StringArray(JsonObject? root, string key)
    {
        var result = new List<string>();
        if (root == null || JsonValues.ArrayAt(root, key) is not JsonArray array)
        {
            return result;
        }
        foreach (var item in array)
        {
            if (item?.GetValueKind() == JsonValueKind.String)
            {
                result.Add(item.GetValue<string>());
            }
        }
        return result;
    }

    private static JsonArray ToStringArray(List<string> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(JsonValue.Create(item));
        }
        return array;
    }

    private static T EnsureTierMap<T>(Dictionary<string, T> maps, string tier) where T : new()
    {
        if (maps.TryGetValue(tier, out var map) && map != null)
        {
            return map;
        }
        var created = new T();
        maps[tier] = created;
        return created;
    }

    private static void ValidateCatalogCapabilities(
        Dictionary<string, Dictionary<string, string>> native,
        Dictionary<string, Dictionary<string, bool>> unsupported)
    {
        foreach (var (tier, protocols) in native)
        {
            if (!Tier.IsValid(tier))
            {
                throw new ConfigException($"model catalog cache contains unknown tier \"{tier}\"");
            }
            foreach (var (model, protocol) in protocols)
            {
                if (model.Length == 0 || !Wire.IsValid(protocol))
                {
                    throw new ConfigException($"model catalog cache contains invalid protocol for \"{model}\"");
                }
            }
        }
        foreach (var (tier, models) in unsupported)
        {
            if (!Tier.IsValid(tier))
            {
                throw new ConfigException($"model catalog cache contains unknown tier \"{tier}\"");
            }
            foreach (var model in models.Keys)
            {
                if (model.Trim().Length == 0)
                {
                    throw new ConfigException("model catalog cache contains an empty unsupported model");
                }
            }
        }
    }

    private static List<string> NormalizeModelIDs(List<string> items)
    {
        var result = new List<string>();
        var seen = new HashSet<string>();
        foreach (var item in items)
        {
            var value = item.Trim();
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }
            result.Add(value);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static JsonObject SerializeProtocols(Dictionary<string, Dictionary<string, string>> source)
    {
        var root = new JsonObject();
        foreach (var (tier, protocols) in source.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var map = new JsonObject();
            foreach (var (model, protocol) in protocols.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                map[model] = protocol;
            }
            root[tier] = map;
        }
        return root;
    }

    private static JsonObject SerializeBools(Dictionary<string, Dictionary<string, bool>> source)
    {
        var root = new JsonObject();
        foreach (var (tier, models) in source.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var map = new JsonObject();
            foreach (var (model, value) in models.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                map[model] = value;
            }
            root[tier] = map;
        }
        return root;
    }

    private static JsonObject SerializeMetadata(Dictionary<string, Dictionary<string, Metadata>> source)
    {
        var root = new JsonObject();
        foreach (var (tier, models) in source.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var map = new JsonObject();
            foreach (var (model, metadata) in models.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                map[model] = metadata.ToJson();
            }
            root[tier] = map;
        }
        return root;
    }
}
