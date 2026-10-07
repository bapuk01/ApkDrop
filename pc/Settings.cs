using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApkDrop;

public sealed class Device
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Android { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = DropClient.DefaultPort;
    public string? Pin { get; set; }
    public bool Checked { get; set; } = true;

    [JsonIgnore] public string Status { get; set; } = "";
}

/// <summary>Приложение из списка автообновления: источник — APK-файл или папка со сборками.</summary>
public sealed class TrackedApp
{
    public string Package { get; set; } = "";
    public string Label { get; set; } = "";
    public string Source { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>true — все телефоны, отмеченные галочкой в главном списке; false — только <see cref="DeviceIds"/>.</summary>
    public bool AllDevices { get; set; } = true;
    public List<string> DeviceIds { get; set; } = new();
    /// <summary>Ставить приложение на выбранные телефоны, где его ещё нет (иначе — только обновлять).</summary>
    public bool InstallMissing { get; set; }

    public bool Targets(Device d) => AllDevices ? d.Checked : DeviceIds.Contains(d.Id);

    /// <summary>Самая новая сборка в источнике (по versionCode, затем по дате).</summary>
    [JsonIgnore] public ApkInfo? Latest { get; set; }
    /// <summary>Все сборки этого пакета в источнике — например, debug и release с разными подписями.</summary>
    [JsonIgnore] public List<ApkInfo> Candidates { get; set; } = new();
    [JsonIgnore] public string SourceError { get; set; } = "";
    /// <summary>Состояние по телефонам: имя телефона → текст.</summary>
    [JsonIgnore] public SortedDictionary<string, string> Phones { get; } = new();
}

public sealed class AppSettings
{
    public List<Device> Devices { get; set; } = new();
    public string? LastApk { get; set; }
    public bool Watch { get; set; }
    public bool Launch { get; set; } = true;
    public List<TrackedApp> Apps { get; set; } = new();
    public bool AutoUpdate { get; set; } = true;
    /// <summary>"auto" (как в Windows), "ru" или "en".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>"auto" (как в Windows), "light" или "dark".</summary>
    public string ThemeMode { get; set; } = "auto";
    /// <summary>Искать новую версию на GitHub при запуске.</summary>
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Тег релиза, который пользователь выбрал «Пропустить версию».</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>Портативный режим: settings.json рядом с ApkDrop.exe важнее, чем в %APPDATA%.</summary>
    static string Dir => File.Exists(Path.Combine(AppContext.BaseDirectory, "settings.json"))
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApkDrop");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch
        {
            // Повреждённый файл настроек — начинаем с чистого листа.
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Не критично: настройки просто не сохранятся.
        }
    }
}
