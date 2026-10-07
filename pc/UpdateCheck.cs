using static ApkDrop.L;

namespace ApkDrop;

/// <summary>Проверка обновлений на GitHub: при запуске (тихо) и по команде из меню.</summary>
public sealed partial class MainForm
{
    bool updateCheckRunning;

    /// <param name="manual">true — по команде из меню: сообщает «у вас последняя версия» и показывает пропущенную версию.</param>
    async Task CheckForUpdatesAsync(bool manual)
    {
        // Окно открыли ради отправки конкретного APK — не отвлекаем.
        if (!manual && (!settings.CheckUpdates || startupApk != null)) return;
        if (updateCheckRunning) return;
        updateCheckRunning = true;
        try
        {
            UpdateInfo? info;
            try
            {
                info = await UpdateChecker.CheckAsync();
            }
            catch (Exception ex)
            {
                // При запуске отсутствие сети — не повод что-либо показывать.
                if (manual) Log(T("Не удалось проверить обновления: ", "Could not check for updates: ") + ex.GetBaseException().Message, ErrColor);
                return;
            }

            if (info == null || info.Version <= UpdateChecker.Current)
            {
                if (manual) Log(T($"Установлена последняя версия ({UpdateChecker.Current.ToString(2)}).", $"You have the latest version ({UpdateChecker.Current.ToString(2)})."), OkColor);
                return;
            }
            if (!manual && settings.SkippedVersion == info.Tag) return;

            using var dialog = new UpdateDialog(info);
            switch (dialog.ShowDialog(this))
            {
                case DialogResult.OK:
                    await InstallUpdateAsync(dialog.DownloadedFile!);
                    break;
                case DialogResult.Ignore:
                    settings.SkippedVersion = info.Tag;
                    settings.Save();
                    Log(T($"Версия {info.Version.ToString(2)} пропущена. Новее — предложим снова.", $"Version {info.Version.ToString(2)} skipped. A newer one will be offered again."));
                    break;
            }
        }
        finally
        {
            updateCheckRunning = false;
        }
    }

    async Task InstallUpdateAsync(string newFile)
    {
        // Перезапуск оборвал бы идущую передачу — ждём её конца.
        if (busy)
        {
            Log(T("Обновление скачано. Жду окончания текущей отправки…", "Update downloaded. Waiting for the current transfer to finish…"), WarnColor);
            while (busy) await Task.Delay(500);
        }
        try
        {
            settings.Save();
            UpdateChecker.ReplaceSelfAndStart(newFile);
            Close();
        }
        catch (Exception ex)
        {
            try { File.Delete(newFile); } catch { }
            Log(T("Не удалось установить обновление: ", "Could not install the update: ") + ex.GetBaseException().Message, ErrColor);
        }
    }
}
