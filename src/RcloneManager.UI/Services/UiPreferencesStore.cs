using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RcloneManager.UI.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CloseWindowBehavior
{
    AskEveryTime,
    MinimizeToTray,
    CloseKeepService
}

public sealed class UiPreferences
{
    public CloseWindowBehavior CloseBehavior { get; set; } = CloseWindowBehavior.AskEveryTime;
    public bool AutoUpdateInBackground { get; set; } = true;
}

public static class UiPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static string GetFilePath(string workDir) =>
        Path.Combine(workDir, ".rclone-manager-runtime", "ui_preferences.json");

    public static UiPreferences Load(string workDir)
    {
        try
        {
            var path = GetFilePath(workDir);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<UiPreferences>(json, JsonOptions);
                if (loaded != null)
                    return loaded;
            }
        }
        catch
        {
            // Usa o padrão caso o arquivo ainda não exista ou esteja inválido
        }

        return new UiPreferences();
    }

    public static void Save(string workDir, UiPreferences preferences)
    {
        try
        {
            var path = GetFilePath(workDir);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(preferences, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Ignora falha de gravação não crítica de preferência visual
        }
    }

    public static void Reset(string workDir)
    {
        try
        {
            var path = GetFilePath(workDir);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
