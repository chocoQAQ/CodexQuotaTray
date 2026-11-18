using System.Text.Json;
using System.Text.Json.Serialization;
using CodexQuotaTray.Core;

namespace CodexQuotaTray.Infrastructure;

public sealed record AppSettings(QuotaSourceKind Source = QuotaSourceKind.AuthFile);

/// <summary>仅保存用户偏好，不复制或保存任何 Codex 凭据。</summary>
public sealed class SettingsStore(string? path = null)
{
    private readonly string path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaTray", "settings.json");
    private static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true, Converters = { new JsonStringEnumConverter<QuotaSourceKind>() }
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return new();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
            return settings is not null && Enum.IsDefined(settings.Source) ? settings : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
