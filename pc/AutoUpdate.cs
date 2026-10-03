using System.Diagnostics;
using static ApkDrop.L;

namespace ApkDrop;

/// <summary>
/// Вкладка «Автообновление»: список приложений с источниками (APK-файл или папка сборок).
/// Когда в источнике появляется APK с бо́льшим versionCode, он рассылается на телефоны приложения
/// (все отмеченные или выбранные для него), где оно установлено в более старой версии,
/// а при включённом «Устанавливать» — и туда, где его ещё нет.
/// </summary>
public sealed partial class MainForm
{
    const int AutoCheckMinutes = 2;

    readonly ListView appsList = new()
    {
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        Dock = DockStyle.Fill,
    };
    readonly Button addAppFileButton = Theme.Secondary(T("Добавить APK…", "Add APK…"));
    readonly Button addAppFolderButton = Theme.Secondary(T("Добавить папку…", "Add folder…"));
    readonly Button targetsButton = Theme.Secondary(T("Телефоны…", "Phones…"));
    readonly Button removeAppButton = Theme.Secondary(T("Удалить", "Remove"));
    readonly Button checkNowButton = Theme.Primary(T("Проверить сейчас", "Check now"));
    readonly CheckBox autoBox = Theme.Style(new CheckBox
    {
        Text = T("Автоматически рассылать новые версии", "Send new versions automatically"),
        AutoSize = true,
        Margin = new Padding(0, 2, 0, 0),
    });

    readonly System.Windows.Forms.Timer autoTimer = new() { Interval = AutoCheckMinutes * 60_000 };
    readonly System.Windows.Forms.Timer autoDebounce = new() { Interval = 3000 };
    readonly System.Windows.Forms.Timer recheckTimer = new() { Interval = 15_000 };
    readonly List<FileSystemWatcher> appWatchers = new();

    /// <summary>Версии, которые телефон отклонил: не предлагаем их снова, пока не нажата «Проверить сейчас».</summary>
    readonly HashSet<string> rejected = new();
    readonly HashSet<string> warnedOutdated = new();
    readonly HashSet<string> warnedSignature = new();
    readonly Dictionary<string, (long Length, DateTime Modified, ApkInfo? Info)> apkCache = new();
    bool autoQueued, refreshingApps;

    Control BuildAutoUpdateTab()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(14, 12, 14, 10), BackColor = Theme.Surface };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        Theme.Style(appsList);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(12, 0, 0, 0) };
        removeAppButton.ForeColor = Theme.Danger;
        buttons.Controls.AddRange(new Control[] { checkNowButton, addAppFileButton, addAppFolderButton, targetsButton, removeAppButton });

        var bottom = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
        var hint = Theme.Hint(T($"«Телефоны…» — для каких телефонов ведётся приложение. Проверка — при изменении файлов и каждые {AutoCheckMinutes} мин.",
            $"“Phones…” — which phones the app is kept on. Checked when files change and every {AutoCheckMinutes} min."));
        hint.Margin = new Padding(12, 4, 0, 0);
        bottom.Controls.Add(autoBox);
        bottom.Controls.Add(hint);

        grid.Controls.Add(Theme.Framed(appsList), 0, 0);
        grid.Controls.Add(buttons, 1, 0);
        grid.Controls.Add(bottom, 0, 1);
        grid.SetColumnSpan(bottom, 2);
        return grid;
    }

    void InitAutoUpdate()
    {
        appsList.Columns.Add(T("Приложение", "App"), 115);
        appsList.Columns.Add(T("Версия в источнике", "Source version"), 150);
        appsList.Columns.Add(T("Для телефонов", "For phones"), 130);
        appsList.Columns.Add(T("Источник", "Source"), 110);
        appsList.Columns.Add(T("На телефонах", "On phones"), 250);
        appsList.ShowItemToolTips = true;
        Theme.FillLastColumn(appsList, 120);
        appsList.ItemChecked += (_, e) =>
        {
            if (refreshingApps || e.Item.Tag is not TrackedApp app) return;
            app.Enabled = e.Item.Checked;
            settings.Save();
            SetupAppWatchers();
        };
        appsList.DoubleClick += async (_, _) => await EditTargetsAsync();
        var menu = new ContextMenuStrip();
        menu.Items.Add(T("Телефоны…", "Phones…"), null, async (_, _) => await EditTargetsAsync());
        var source = new ToolStripMenuItem(T("Изменить адрес APK", "Change APK location"));
        source.DropDownItems.Add(T("APK-файл…", "APK file…"), null, async (_, _) => await ChangeSourceAsync(folder: false));
        source.DropDownItems.Add(T("Папка со сборками…", "Builds folder…"), null, async (_, _) => await ChangeSourceAsync(folder: true));
        menu.Items.Add(source);
        menu.Items.Add(T("Показать APK в Проводнике", "Show APK in Explorer"), null, (_, _) =>
        {
            if (SelectedApps().FirstOrDefault() is not { } app) return;
            var target = app.Latest?.Path ?? app.Source;
            try { Process.Start("explorer.exe", File.Exists(target) ? $"/select,\"{target}\"" : $"\"{target}\""); } catch { }
        });
        Theme.Style(menu);
        appsList.ContextMenuStrip = menu;
        targetsButton.Click += async (_, _) => await EditTargetsAsync();

        addAppFileButton.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "Android APK (*.apk)|*.apk", Title = T("APK для автообновления", "APK to auto-update"), Multiselect = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            foreach (var f in dialog.FileNames) await AddTrackedAsync(f);
        };
        addAppFolderButton.Click += async (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = T("Папка, куда попадают сборки (например app\\build\\outputs\\apk\\release)",
                    "Folder where builds are placed (for example app\\build\\outputs\\apk\\release)"),
                UseDescriptionForTitle = true,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) await AddTrackedAsync(dialog.SelectedPath);
        };
        removeAppButton.Click += (_, _) =>
        {
            foreach (ListViewItem item in appsList.SelectedItems)
                if (item.Tag is TrackedApp app) settings.Apps.Remove(app);
            settings.Save();
            RefreshApps();
            SetupAppWatchers();
        };
        checkNowButton.Click += async (_, _) => await AutoCheckAsync(manual: true);

        autoBox.Checked = settings.AutoUpdate;
        autoBox.CheckedChanged += (_, _) =>
        {
            settings.AutoUpdate = autoBox.Checked;
            settings.Save();
            SetupAppWatchers();
        };

        autoTimer.Tick += async (_, _) => await AutoCheckAsync(manual: false);
        autoTimer.Start();
        autoDebounce.Tick += async (_, _) =>
        {
            autoDebounce.Stop();
            await AutoCheckAsync(manual: false);
        };
        recheckTimer.Tick += async (_, _) =>
        {
            recheckTimer.Stop();
            await AutoCheckAsync(manual: false);
        };

        RefreshApps();
        SetupAppWatchers();
    }

    // ---------- Список ----------

    async Task AddTrackedAsync(string source)
    {
        var found = await Task.Run(() => ScanSource(source));
        if (found.Count == 0)
        {
            Log(T($"В «{source}» не найдено ни одного APK.", $"No APK found in “{source}”."), ErrColor);
            return;
        }
        foreach (var info in found.GroupBy(i => i.Package).Select(Newest))
        {
            var app = settings.Apps.FirstOrDefault(a => a.Package == info.Package);
            if (app == null)
            {
                app = new TrackedApp { Package = info.Package };
                settings.Apps.Add(app);
                Log(T($"Автообновление: добавлено {info.Label} {info.Version} — {source}",
                    $"Auto-update: added {info.Label} {info.Version} — {source}"), OkColor);
            }
            else
            {
                Log(T($"Автообновление: у {info.Label} источник заменён на {source}",
                    $"Auto-update: source of {info.Label} changed to {source}"));
            }
            app.Label = info.Label;
            app.Source = source;
            app.Enabled = true;
            app.Latest = info;
        }
        settings.Save();
        RefreshApps();
        SetupAppWatchers();
        await AutoCheckAsync(manual: false);
    }

    List<TrackedApp> SelectedApps() =>
        appsList.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<TrackedApp>().ToList();

    /// <summary>
    /// Меняет APK-файл или папку со сборками у выделенных приложений — например, после переноса проекта.
    /// Новый адрес принимается, только если в нём есть APK именно этого приложения (тот же package).
    /// </summary>
    async Task ChangeSourceAsync(bool folder)
    {
        var apps = SelectedApps();
        if (apps.Count == 0)
        {
            Log(T("Выделите приложение в списке автообновления.", "Select an app in the auto-update list."), WarnColor);
            return;
        }

        // Диалог открываем там, где лежит текущий источник: после переноса проекта это ближайшая существующая папка.
        var current = apps[0].Source;
        var start = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
        while (!string.IsNullOrEmpty(start) && !Directory.Exists(start)) start = Path.GetDirectoryName(start);

        string path;
        if (folder)
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = T("Папка, куда попадают сборки (например app\\build\\outputs\\apk\\release)",
                    "Folder where builds are placed (for example app\\build\\outputs\\apk\\release)"),
                UseDescriptionForTitle = true,
                InitialDirectory = start ?? "",
                SelectedPath = start ?? "",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            path = dialog.SelectedPath;
        }
        else
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Android APK (*.apk)|*.apk",
                Title = T("Новый APK для автообновления", "New APK to auto-update"),
                InitialDirectory = start ?? "",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            path = dialog.FileName;
        }

        var found = await Task.Run(() => ScanSource(path));
        var changed = 0;
        foreach (var app in apps)
        {
            var builds = found.Where(i => i.Package == app.Package).ToList();
            if (builds.Count == 0)
            {
                Log(T($"{app.Label}: в «{path}» нет APK этого приложения ({app.Package}) — адрес не изменён.",
                    $"{app.Label}: “{path}” has no APK of this app ({app.Package}) — location not changed."), ErrColor);
                continue;
            }
            app.Source = path;
            app.Latest = Newest(builds);
            app.Label = app.Latest.Label;
            app.SourceError = "";
            Log(T($"{app.Label}: адрес APK изменён на {path}", $"{app.Label}: APK location changed to {path}"), OkColor);
            changed++;
        }
        if (changed == 0) return;

        settings.Save();
        RefreshApps();
        SetupAppWatchers();
        await AutoCheckAsync(manual: false);
    }

    /// <summary>Выбор телефонов для выделенных приложений (для нескольких — одна настройка на все).</summary>
    async Task EditTargetsAsync()
    {
        var apps = SelectedApps();
        if (apps.Count == 0)
        {
            Log(T("Выделите приложение в списке автообновления.", "Select an app in the auto-update list."), WarnColor);
            return;
        }
        if (settings.Devices.Count == 0)
        {
            Log(T("Список телефонов пуст — сначала найдите или добавьте телефоны.", "The phone list is empty — find or add phones first."), WarnColor);
            return;
        }
        var title = apps.Count == 1 ? apps[0].Label : T($"{apps.Count} приложений", $"{apps.Count} apps");
        var result = AppTargetsDialog.Show(this, title, settings.Devices, apps[0]);
        if (result == null) return;
        foreach (var app in apps)
        {
            app.AllDevices = result.AllDevices;
            app.DeviceIds = result.DeviceIds.ToList();
            app.InstallMissing = result.InstallMissing;
        }
        settings.Save();
        RefreshApps();
        Log($"{title}: {TargetsText(apps[0])}");
        await AutoCheckAsync(manual: false);
    }

    string TargetsText(TrackedApp app)
    {
        string where;
        if (app.AllDevices)
        {
            where = T("все отмеченные", "all checked");
        }
        else
        {
            var names = settings.Devices.Where(d => app.DeviceIds.Contains(d.Id)).Select(d => d.Name).ToList();
            where = names.Count == 0 ? T("ни одного телефона", "no phones") : string.Join(", ", names);
        }
        return app.InstallMissing ? where + T(" + установка", " + install") : where;
    }

    void RefreshApps()
    {
        refreshingApps = true;
        appsList.BeginUpdate();
        appsList.Items.Clear();
        foreach (var app in settings.Apps)
        {
            // Дата сборки важна: при одинаковом versionCode только по ней видно, что сборка свежая.
            var version = app.Latest is { } l
                ? $"{l.Version}, {l.Modified:dd.MM HH:mm}" + (app.Candidates.Count > 1 ? T($" (сборок: {app.Candidates.Count})", $" ({app.Candidates.Count} builds)") : "")
                : app.SourceError != "" ? app.SourceError : "—";
            var phones = app.Phones.Count == 0 ? "" : string.Join("; ", app.Phones.Select(p => $"{p.Key}: {p.Value}"));
            var targets = TargetsText(app);
            var item = new ListViewItem(new[] { app.Label, version, targets, app.Source, phones })
            {
                Tag = app,
                Checked = app.Enabled,
                ToolTipText = $"{app.Package}\n{T("Телефоны", "Phones")}: {targets}\n{app.Latest?.Path ?? app.Source}\n{phones.Replace("; ", "\n")}",
                UseItemStyleForSubItems = false,
            };
            if (app.SourceError != "") item.SubItems[1].ForeColor = ErrColor;
            if (!app.AllDevices && !settings.Devices.Any(app.Targets)) item.SubItems[2].ForeColor = ErrColor;
            appsList.Items.Add(item);
        }
        appsList.EndUpdate();
        refreshingApps = false;
    }

    // ---------- Источники ----------

    static ApkInfo Newest(IEnumerable<ApkInfo> infos) =>
        infos.OrderByDescending(i => i.VersionCode).ThenByDescending(i => i.Modified).First();

    /// <summary>Все читаемые APK из файла или папки (рекурсивно, без промежуточных сборок Gradle).</summary>
    List<ApkInfo> ScanSource(string source)
    {
        IEnumerable<string> files;
        if (File.Exists(source))
        {
            files = new[] { source };
        }
        else if (Directory.Exists(source))
        {
            try
            {
                files = Directory.EnumerateFiles(source, "*.apk", SearchOption.AllDirectories)
                    // В intermediates лежат testOnly-сборки из кнопки Run — PackageInstaller их не примет.
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}intermediates{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    .Take(500)
                    .ToList();
            }
            catch
            {
                return new();
            }
        }
        else
        {
            return new();
        }

        var result = new List<ApkInfo>();
        foreach (var f in files)
        {
            var info = ReadCached(f);
            if (info != null) result.Add(info);
        }
        return result;
    }

    ApkInfo? ReadCached(string path)
    {
        FileInfo fi;
        try
        {
            fi = new FileInfo(path);
            if (!fi.Exists || !IsFileReady(path)) return null; // сборка ещё пишет файл
        }
        catch
        {
            return null;
        }
        lock (apkCache)
        {
            if (apkCache.TryGetValue(path, out var c) && c.Length == fi.Length && c.Modified == fi.LastWriteTimeUtc)
                return c.Info;
        }
        ApkInfo? info;
        try { info = ApkReader.Read(path); }
        catch { info = null; }
        lock (apkCache) apkCache[path] = (fi.Length, fi.LastWriteTimeUtc, info);
        return info;
    }

    List<ApkInfo> CandidatesFor(TrackedApp app) =>
        ScanSource(app.Source).Where(i => i.Package == app.Package).ToList();

    void SetupAppWatchers()
    {
        foreach (var w in appWatchers) w.Dispose();
        appWatchers.Clear();
        if (!settings.AutoUpdate) return;

        foreach (var source in settings.Apps.Where(a => a.Enabled).Select(a => a.Source).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                FileSystemWatcher w;
                if (Directory.Exists(source))
                {
                    w = new FileSystemWatcher(source, "*.apk") { IncludeSubdirectories = true };
                }
                else
                {
                    var dir = Path.GetDirectoryName(source);
                    if (dir == null || !Directory.Exists(dir)) continue;
                    w = new FileSystemWatcher(dir, Path.GetFileName(source));
                }
                w.SynchronizingObject = this;
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime;
                w.Changed += (_, _) => RestartAutoDebounce();
                w.Created += (_, _) => RestartAutoDebounce();
                w.Renamed += (_, _) => RestartAutoDebounce();
                w.EnableRaisingEvents = true;
                appWatchers.Add(w);
            }
            catch
            {
                // Папка недоступна — остаётся периодическая проверка.
            }
        }
    }

    void RestartAutoDebounce()
    {
        autoDebounce.Stop();
        autoDebounce.Start();
    }

    // ---------- Проверка и рассылка ----------

    async Task AutoCheckAsync(bool manual)
    {
        if (!manual && !settings.AutoUpdate) return;
        var apps = settings.Apps.Where(a => a.Enabled).ToList();
        if (apps.Count == 0)
        {
            if (manual) Log(T("Список автообновления пуст — добавьте APK или папку со сборками.",
                "The auto-update list is empty — add an APK or a builds folder."), WarnColor);
            return;
        }
        if (busy)
        {
            autoQueued = true;
            return;
        }

        if (manual)
        {
            rejected.Clear();
            warnedOutdated.Clear();
            warnedSignature.Clear();
            Log(T("Проверяю новые версии…", "Checking for new versions…"));
        }
        SetBusy(true);
        checkNowButton.Enabled = false;
        var sent = 0;
        try
        {
            foreach (var app in apps)
            {
                app.Candidates = await Task.Run(() => CandidatesFor(app));
                app.Latest = app.Candidates.Count == 0 ? null : Newest(app.Candidates);
                app.SourceError = app.Latest == null ? T("APK не найден", "APK not found") : "";
                if (app.Latest != null) app.Label = app.Latest.Label;
                app.Phones.Clear();
            }
            RefreshApps();

            // ApkDrop обновляем последним: после его обновления телефон на время перезапускает сервер.
            var ready = apps.Where(a => a.Latest != null).OrderBy(a => a.Package == OwnPackage).ToList();
            if (ready.Count == 0) return;

            foreach (var d in settings.Devices.ToList())
            {
                var forPhone = ready.Where(a => a.Targets(d)).ToList();
                if (forPhone.Count > 0) sent += await UpdatePhoneAsync(d, forPhone, manual);
            }
        }
        catch (Exception ex)
        {
            Log(T("Автообновление: ", "Auto-update: ") + ex.Message, ErrColor);
        }
        finally
        {
            settings.Save();
            RefreshApps();
            checkNowButton.Enabled = true;
            SetBusy(false);
        }

        if (manual && sent == 0) Log(T("Проверка завершена: рассылать нечего.", "Check complete: nothing to send."), OkColor);

        if (resendQueued)
        {
            resendQueued = false;
            await SendAsync();
        }
        else if (autoQueued)
        {
            autoQueued = false;
            await AutoCheckAsync(manual: false);
        }
    }

    /// <summary>Возвращает число отправленных на телефон обновлений.</summary>
    async Task<int> UpdatePhoneAsync(Device d, List<TrackedApp> apps, bool manual)
    {
        void MarkAll(string text)
        {
            foreach (var app in apps) app.Phones[d.Name] = text;
            RefreshApps();
        }

        if (!await EnsureReadyAsync(d, interactive: false))
        {
            MarkAll(d.Status.TrimStart('✖', ' '));
            return 0;
        }

        // Старый ApkDrop на телефоне не сообщает хеш установленных APK — без этого не отличить
        // новую сборку с тем же versionCode. Обновляем его сами, а приложения — в следующую проверку.
        var phone = await TryInfoAsync(d);
        if (phone != null && phone.Protocol < RequiredPhoneProtocol)
        {
            var updated = await UpdatePhoneAppAsync(d, phone);
            MarkAll(updated
                ? T("обновлён ApkDrop на телефоне, проверю снова", "ApkDrop updated on the phone, checking again")
                : T($"старый ApkDrop на телефоне (протокол {phone.Protocol})", $"old ApkDrop on the phone (protocol {phone.Protocol})"));
            if (updated) recheckTimer.Start(); // телефон перезапускает ApkDrop — проверим приложения чуть позже
            if (updated || phone.Protocol < 2) return 0;
        }

        Dictionary<string, InstalledApp?> installed;
        try
        {
            installed = await DropClient.PackagesAsync(d.Host, d.Port, d.Pin!, apps.Select(a => a.Package));
        }
        catch (OutdatedPhoneException)
        {
            MarkAll(T("обновите ApkDrop на телефоне", "update ApkDrop on the phone"));
            return 0;
        }
        catch (Exception)
        {
            MarkAll(T("нет связи", "no connection"));
            return 0;
        }

        var sent = 0;
        foreach (var app in apps)
        {
            var have = installed.GetValueOrDefault(app.Package);
            if (have == null && !app.InstallMissing)
            {
                app.Phones[d.Name] = T("не установлено", "not installed");
                continue;
            }

            var latest = PickBuild(d, app, have);
            if (latest == null) continue; // подходящей по подписи сборки нет — статус уже выставлен

            // Самая свежая сборка в источнике подписана другим ключом (например, release при установленной debug) —
            // поверх она не встанет, поэтому работаем с подходящей по подписи и честно об этом сообщаем.
            var skipped = app.Latest != null && app.Latest != latest && app.Latest.CertSha256 != latest.CertSha256
                && (app.Latest.VersionCode > latest.VersionCode || app.Latest.Modified > latest.Modified)
                    ? app.Latest
                    : null;
            if (skipped != null && warnedSignature.Add($"{d.Id}|{app.Package}|{skipped.Sha256}"))
            {
                var skippedFile = $"{Path.GetFileName(skipped.Path)} {T("от", "from")} {skipped.Modified:dd.MM HH:mm}";
                var usedFile = $"{Path.GetFileName(latest.Path)} {T("от", "from")} {latest.Modified:dd.MM HH:mm}";
                Log(T($"{d.Name}: самая свежая сборка {app.Label} ({skippedFile}) подписана другим ключом, чем установленная, — поверх она не встанет. " +
                      $"Беру сборку с той же подписью ({usedFile}). Чтобы перейти на новую, удалите приложение на телефоне" +
                      (app.InstallMissing ? " — ApkDrop поставит её сам." : " и установите заново."),
                      $"{d.Name}: the newest {app.Label} build ({skippedFile}) is signed with a different key than the installed one — it cannot update over it. " +
                      $"Using the build with the same signature ({usedFile}). To switch to the new one, uninstall the app on the phone" +
                      (app.InstallMissing ? " — ApkDrop will install it by itself." : " and install it again.")), WarnColor);
            }
            var skippedNote = skipped != null ? $"; {Path.GetFileName(skipped.Path)} — {T("другая подпись", "different signature")}" : "";

            if (have != null)
            {
                // Тот же versionCode: новой считаем сборку с другим содержимым (телефон сообщает хеш, ApkDrop 1.3+),
                // которая собрана позже последней установки на телефоне — чтобы не откатить то,
                // что поставили, например, из Android Studio кнопкой Run.
                var newerBuild = have.Sha256 != null && have.Sha256 != latest.Sha256
                    && (have.LastUpdate == null || latest.Modified > have.LastUpdate);
                var upToDate = latest.VersionCode < have.VersionCode
                    || latest.VersionCode == have.VersionCode && !newerBuild;
                if (upToDate)
                {
                    app.Phones[d.Name] = have.VersionName + (
                        latest.VersionCode < have.VersionCode ? T(" ✔ (на телефоне новее)", " ✔ (newer on the phone)")
                        : have.LastUpdate == null ? T(" ✔? (сравнить сборки нельзя — обновите ApkDrop на телефоне до 1.3)",
                            " ✔? (cannot compare builds — update ApkDrop on the phone to 1.3)")
                        : have.Sha256 == null ? T(" ✔ (split APK — сравниваются только versionCode)", " ✔ (split APK — only versionCode is compared)")
                        : have.Sha256 != latest.Sha256 ? T(" ✔ (на телефоне установка свежее этой сборки)", " ✔ (the phone install is newer than this build)")
                        : " ✔") + skippedNote;
                    continue;
                }
            }

            var sameVersion = have != null && have.VersionCode == latest.VersionCode;
            var from = have?.VersionName ?? T("нет", "none");
            var to = sameVersion ? $"{latest.VersionName} ({T("сборка", "build")} {latest.Modified:dd.MM HH:mm})" : latest.VersionName;
            var rejectedText = $"{from} → {to} {T("отклонено", "rejected")}";
            var key = $"{d.Id}|{app.Package}|{latest.Sha256}";
            if (rejected.Contains(key))
            {
                app.Phones[d.Name] = rejectedText;
                continue;
            }

            app.Phones[d.Name] = $"{from} → {to}…";
            RefreshApps();
            Log(have == null
                ? T($"Автоустановка: {app.Label} {latest.Version} на «{d.Name}»", $"Auto-install: {app.Label} {latest.Version} on “{d.Name}”")
                : sameVersion
                    ? T($"Автообновление: {app.Label} {latest.Version} — новая сборка от {latest.Modified:dd.MM HH:mm} (versionCode тот же) на «{d.Name}»",
                        $"Auto-update: {app.Label} {latest.Version} — new build from {latest.Modified:dd.MM HH:mm} (same versionCode) on “{d.Name}”")
                    : T($"Автообновление: {app.Label} {have!.VersionName} → {latest.Version} на «{d.Name}»",
                        $"Auto-update: {app.Label} {have!.VersionName} → {latest.Version} on “{d.Name}”"));

            string? snapshot = null;
            try
            {
                snapshot = await SnapshotAsync(latest.Path);
                var result = await InstallToAsync(d, snapshot, Path.GetFileName(latest.Path), launch: false);
                switch (result)
                {
                    case SendResult.Ok:
                        app.Phones[d.Name] = $"{latest.VersionName} ✔ " + (have == null ? T("установлено", "installed") : T("обновлено", "updated"));
                        sent++;
                        break;
                    case SendResult.Rejected:
                        // Не спамим телефон повторными запросами на ту же версию.
                        rejected.Add(key);
                        app.Phones[d.Name] = rejectedText;
                        Log(T($"  Эта версия больше не будет предлагаться «{d.Name}» автоматически — нажмите «Проверить сейчас», чтобы повторить.",
                            $"  This version will not be offered to “{d.Name}” automatically again — click “Check now” to retry."), WarnColor);
                        break;
                    default:
                        app.Phones[d.Name] = T("нет связи, повторю позже", "no connection, will retry later");
                        break;
                }
            }
            catch (Exception ex)
            {
                app.Phones[d.Name] = T("ошибка: ", "error: ") + ex.Message;
            }
            finally
            {
                if (snapshot != null) try { File.Delete(snapshot); } catch { }
            }
            RefreshApps();
        }
        return sent;
    }

    /// <summary>Протокол 3 (ApkDrop 1.3): телефон сообщает хеш, подпись и время установки приложений.</summary>
    const int RequiredPhoneProtocol = 3;
    /// <summary>versionCode ApkDrop 1.3 — первой версии с протоколом 3.</summary>
    const long RequiredPhoneAppVersionCode = 4;

    async Task<PhoneInfo?> TryInfoAsync(Device d)
    {
        try { return await DropClient.InfoAsync(d.Host, d.Port); }
        catch { return null; }
    }

    /// <summary>
    /// Свежая сборка самого ApkDrop для телефона: из списка автообновления (если ApkDrop там есть)
    /// или любой APK самого ApkDrop рядом с ApkDrop.exe (так лежит в папке dist; имя файла не важно).
    /// </summary>
    ApkInfo? PhoneAppBuild()
    {
        var builds = new List<ApkInfo>();
        foreach (var app in settings.Apps.Where(a => a.Package == OwnPackage))
            builds.AddRange(CandidatesFor(app));
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.apk", SearchOption.TopDirectoryOnly))
                if (ReadCached(file) is { Package: OwnPackage } b) builds.Add(b);
        }
        catch
        {
            // Папка с exe недоступна — остаются сборки из списка автообновления.
        }
        builds.RemoveAll(b => b.VersionCode < RequiredPhoneAppVersionCode);
        return builds.Count == 0 ? null : Newest(builds);
    }

    /// <summary>Обновляет ApkDrop на телефоне (только обновление — он там уже стоит). true — обновление встало.</summary>
    async Task<bool> UpdatePhoneAppAsync(Device d, PhoneInfo phone)
    {
        var apk = await Task.Run(PhoneAppBuild);
        if (apk == null)
        {
            if (warnedOutdated.Add(d.Id))
            {
                Log(T($"{d.Name}: на телефоне старый ApkDrop (протокол {phone.Protocol}) — новые сборки с тем же versionCode " +
                      "он не различает. Обновите его: отправьте APK приложения ApkDrop на вкладке «Отправка APK» " +
                      "(или положите его рядом с ApkDrop.exe — тогда ПК обновит телефон сам).",
                      $"{d.Name}: the phone has an old ApkDrop (protocol {phone.Protocol}) — it cannot tell apart builds with the same versionCode. " +
                      "Update it: send the ApkDrop app APK on the “Send APK” tab " +
                      "(or put it next to ApkDrop.exe — then the PC updates the phone by itself)."), WarnColor);
            }
            return false;
        }

        var key = $"phoneapp|{d.Id}|{apk.Sha256}";
        if (rejected.Contains(key)) return false;

        Log(T($"{d.Name}: на телефоне старый ApkDrop (протокол {phone.Protocol}) — обновляю его до {apk.VersionName}, " +
              "иначе не видно новых сборок с тем же versionCode.",
              $"{d.Name}: the phone has an old ApkDrop (protocol {phone.Protocol}) — updating it to {apk.VersionName}, " +
              "otherwise new builds with the same versionCode are invisible."));
        string? snapshot = null;
        try
        {
            snapshot = await SnapshotAsync(apk.Path);
            var result = await InstallToAsync(d, snapshot, Path.GetFileName(apk.Path), launch: false);
            if (result == SendResult.Rejected) rejected.Add(key);
            return result == SendResult.Ok;
        }
        catch (Exception ex)
        {
            Log(T($"{d.Name}: не удалось обновить ApkDrop — ", $"{d.Name}: could not update ApkDrop — ") + ex.Message, ErrColor);
            return false;
        }
        finally
        {
            if (snapshot != null) try { File.Delete(snapshot); } catch { }
        }
    }

    /// <summary>
    /// Сборка для конкретного телефона: самая новая среди тех, что подписаны тем же ключом,
    /// что и установленное приложение (иначе Android не примет обновление). Например, если в папке
    /// лежат debug и release, на телефон с debug-версией пойдёт debug.
    /// </summary>
    ApkInfo? PickBuild(Device d, TrackedApp app, InstalledApp? have)
    {
        if (have == null || have.Certs.Count == 0) return app.Latest;
        var matching = app.Candidates.Where(c => c.CertSha256 == null || have.Certs.Contains(c.CertSha256)).ToList();
        if (matching.Count > 0) return Newest(matching);

        app.Phones[d.Name] = $"{have.VersionName}: " + T("подпись не совпадает ни с одной сборкой в источнике", "signature matches no build in the source");
        if (warnedSignature.Add($"{d.Id}|{app.Package}"))
        {
            Log(T($"{d.Name}: {app.Label} установлен с другой подписью (например, debug вместо release), а в источнике " +
                  "нет сборки с такой подписью. Android не даст обновить поверх — удалите приложение на телефоне" +
                  (app.InstallMissing ? ", и ApkDrop поставит новую сборку сам." : " и установите заново."),
                  $"{d.Name}: {app.Label} is installed with a different signature (for example debug instead of release), and the source " +
                  "has no build with that signature. Android will not update over it — uninstall the app on the phone" +
                  (app.InstallMissing ? ", and ApkDrop will install the new build by itself." : " and install it again.")), WarnColor);
        }
        return null;
    }
}
