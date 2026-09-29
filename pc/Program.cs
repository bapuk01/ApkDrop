namespace ApkDrop;

static class Program
{
    /// <summary>ApkDrop.exe [путь\к\файлу.apk] — если APK передан, он сразу отправляется на отмеченные телефоны.</summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Язык и тема нужны до создания окна: из них строятся все подписи и цвета.
        var settings = AppSettings.Load();
        L.Init(settings.Language);
        Theme.Init(settings.ThemeMode);
        DropClient.SetLanguage(L.Code);

        var apk = args.FirstOrDefault(a => a.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(settings, apk));
    }
}
