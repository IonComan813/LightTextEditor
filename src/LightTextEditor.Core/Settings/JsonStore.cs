using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LightTextEditor.Core.Text;

namespace LightTextEditor.Core.Settings;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(EncodingMemoryData))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>读写 JSON 配置文件。文件损坏时返回默认值，不让程序崩溃。</summary>
public static class JsonStore
{
    public static AppSettings LoadSettings(string? path = null)
    {
        var settings = Load(path ?? AppPaths.SettingsFile, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        settings.Migrate();
        return settings;
    }

    public static void SaveSettings(AppSettings settings, string? path = null) =>
        Save(path ?? AppPaths.SettingsFile, settings, SettingsJsonContext.Default.AppSettings);


    public static EncodingMemoryData LoadEncodingMemory(string? path = null) =>
        Load(path ?? AppPaths.EncodingMemoryFile, SettingsJsonContext.Default.EncodingMemoryData) ?? new EncodingMemoryData();

    public static void SaveEncodingMemory(EncodingMemoryData data, string? path = null) =>
        Save(path ?? AppPaths.EncodingMemoryFile, data, SettingsJsonContext.Default.EncodingMemoryData);

    private static T? Load<T>(string path, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(value, typeInfo);
        TextFileIO.WriteAllBytesAtomic(path, Encoding.UTF8.GetBytes(json));
    }
}
