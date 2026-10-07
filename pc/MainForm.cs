using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using static ApkDrop.L;

namespace ApkDrop;

public sealed partial class MainForm : Form
{
    const string OwnPackage = "com.apkdrop";

    static Color OkColor => Theme.Ok;
    static Color ErrColor => Theme.Err;
    static Color WarnColor => Theme.Warn;

    readonly AppSettings settings;
    readonly string? startupApk;

    readonly ListView devices = new()
    {
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        Dock = DockStyle.Fill,
    };
    readonly Button findButton = Theme.Secondary(T("Найти телефоны", "Find phones"));
    readonly Button addButton = Theme.Secondary(T("Добавить по IP…", "Add by IP…"));
    readonly Button pinButton = Theme.Secondary(T("Ввести PIN…", "Enter PIN…"));
    readonly Button removeButton = Theme.Secondary(T("Удалить", "Remove"));

    readonly TextBox apkBox = new()
    {
        ReadOnly = true,
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        Font = Theme.Body,
        PlaceholderText = T("Файл не выбран — перетащите .apk в окно или нажмите «Выбрать APK…»",
            "No file selected — drop an .apk onto the window or click “Choose APK…”"),
        Margin = new Padding(0, 5, 10, 0),
    };
    readonly Button browseButton = Theme.Secondary(T("Выбрать APK…", "Choose APK…"));
    readonly CheckBox watchBox = Theme.Style(new CheckBox
    {
        Text = T("Следить за файлом: каждая новая сборка сразу ставится на телефон", "Watch the file: every new build is installed on the phone right away"),
        AutoSize = true,
        Margin = new Padding(0, 10, 0, 2),
    });
    readonly CheckBox launchBox = Theme.Style(new CheckBox
    {
        Text = T("Запускать приложение после установки", "Launch the app after installing"),
        AutoSize = true,
        Margin = new Padding(0, 4, 0, 2),
    });
    readonly Button sendButton = Theme.Primary(T("Установить на телефон", "Install on phone"), 220);
    readonly TabStrip tabs = new();
    readonly ThinProgress progress = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0) };
    readonly RichTextBox log = new()
    {
        ReadOnly = true,
        Dock = DockStyle.Fill,
        Font = Theme.Mono,
        BorderStyle = BorderStyle.None,
        DetectUrls = false,
    };

    readonly FileSystemWatcher watcher = new();
    readonly System.Windows.Forms.Timer debounce = new() { Interval = 1500 };
    string? lastSentHash;
    bool busy, resendQueued, refreshingList;

    public MainForm(AppSettings settings, string? startupApk)
    {
        this.settings = settings;
        this.startupApk = startupApk;
        // Версия и дата сборки в заголовке — чтобы сразу было видно, если запущен устаревший exe.
        var version = typeof(MainForm).Assembly.GetName().Version;
        var built = File.GetLastWriteTime(Environment.ProcessPath ?? AppContext.BaseDirectory);
        versionText = $"ApkDrop {version?.Major}.{version?.Minor} · {T("сборка", "build")} {built:dd.MM.yyyy}";
        Text = $"ApkDrop {version?.Major}.{version?.Minor} ({T("сборка", "build")} {built:dd.MM.yyyy}) — " +
               T("установка APK на телефон по Wi-Fi", "install APKs on phones over Wi-Fi");
        Theme.Apply(this);
        BackColor = Theme.Background;
        // На ноутбуках с 768 px по высоте окно не должно вылезать за экран — журнал просто станет короче.
        var screen = Screen.PrimaryScreen?.WorkingArea.Height ?? 900;
        ClientSize = new Size(980, Math.Min(900, screen - 60));
        MinimumSize = new Size(760, Math.Min(760, screen - 20));
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }

        Theme.Style(apkBox);
        Theme.Style(log);
        BuildLayout();
        EnableDrop(this);

        devices.Columns.Add(T("Телефон", "Phone"), 140);
        devices.Columns.Add(T("Модель", "Model"), 130);
        devices.Columns.Add("Android", 60);
        devices.Columns.Add(T("Адрес", "Address"), 125);
        devices.Columns.Add(T("Статус", "Status"), 200);
        Theme.FillLastColumn(devices, 150);
        devices.ItemChecked += (_, e) =>
        {
            if (refreshingList || e.Item.Tag is not Device d) return;
            d.Checked = e.Item.Checked;
            settings.Save();
        };

        findButton.Click += async (_, _) => await DiscoverAsync();
        addButton.Click += async (_, _) => await AddByIpAsync();
        pinButton.Click += (_, _) => EnterPin();
        removeButton.Click += (_, _) => RemoveSelected();
        browseButton.Click += (_, _) => Browse();
        sendButton.Click += async (_, _) => await SendAsync();

        launchBox.Checked = settings.Launch;
        launchBox.CheckedChanged += (_, _) => { settings.Launch = launchBox.Checked; settings.Save(); };
        watchBox.Checked = settings.Watch;
        watchBox.CheckedChanged += (_, _) => { settings.Watch = watchBox.Checked; settings.Save(); UpdateWatcher(); };

        watcher.SynchronizingObject = this;
        watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime;
        watcher.Changed += (_, _) => RestartDebounce();
        watcher.Created += (_, _) => RestartDebounce();
        watcher.Renamed += (_, _) => RestartDebounce();
        debounce.Tick += async (_, _) => await OnWatchedFileChangedAsync();

        apkBox.Text = startupApk ?? settings.LastApk ?? "";
        RefreshDevices();
        UpdateWatcher();

        Log(T("Перетащите .apk в это окно — он сразу установится на отмеченные телефоны.",
            "Drop an .apk onto this window — it will be installed on the checked phones right away."));
        Log(T("Для Android Studio: выберите app\\build\\outputs\\apk\\debug\\app-debug.apk и включите «Следить за файлом».",
            "For Android Studio: choose app\\build\\outputs\\apk\\debug\\app-debug.apk and turn on “Watch the file”."));

        InitAutoUpdate();

        tabs.SelectedIndexChanged += (_, _) => Theme.FitLastColumn(appsList);
        Shown += async (_, _) =>
        {
            Theme.FitLastColumn(devices);
            Theme.FitLastColumn(appsList);
            _ = CheckForUpdatesAsync(manual: false); // в фоне: медленная сеть не задерживает поиск телефонов
            await DiscoverAsync();
            if (startupApk != null) await SendAsync();
            await AutoCheckAsync(manual: false);
        };
        FormClosing += (_, _) => settings.Save();
    }

    readonly string versionText;

    void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16, 14, 16, 4),
            BackColor = Theme.Background,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 240));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 336));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Телефоны
        Theme.Style(devices);
        var phonesGrid = new TableLayoutPanel { ColumnCount = 2, BackColor = Theme.Surface };
        phonesGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        phonesGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var phoneButtons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(12, 0, 0, 0) };
        removeButton.ForeColor = Theme.Danger;
        phoneButtons.Controls.AddRange(new Control[] { findButton, addButton, pinButton, removeButton });
        phonesGrid.Controls.Add(Theme.Framed(devices), 0, 0);
        phonesGrid.Controls.Add(phoneButtons, 1, 0);
        var phones = new Card(T("Телефоны", "Phones"),
            T("галочка — куда устанавливать и рассылать обновления", "checked — where to install and send updates"), phonesGrid);

        // Вкладка «Отправка APK»
        var apkGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(14, 14, 14, 10), BackColor = Theme.Surface };
        apkGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        apkGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        browseButton.Margin = new Padding(0);
        apkGrid.Controls.Add(apkBox, 0, 0);
        apkGrid.Controls.Add(browseButton, 1, 0);
        var dropHint = Theme.Hint(T("Проще всего — перетащить .apk прямо в это окно: он сразу уйдёт на отмеченные телефоны.",
            "The easiest way is to drop an .apk right onto this window: it goes to the checked phones immediately."));
        apkGrid.Controls.Add(dropHint, 0, 1);
        apkGrid.SetColumnSpan(dropHint, 2);
        apkGrid.Controls.Add(watchBox, 0, 2);
        apkGrid.SetColumnSpan(watchBox, 2);
        apkGrid.Controls.Add(launchBox, 0, 3);
        apkGrid.SetColumnSpan(launchBox, 2);
        sendButton.MinimumSize = new Size(240, 38);
        sendButton.Margin = new Padding(0, 14, 0, 0);
        apkGrid.Controls.Add(sendButton, 0, 4);
        apkGrid.SetColumnSpan(sendButton, 2);

        tabs.AddPage(T("Отправка APK", "Send APK"), apkGrid);
        tabs.AddPage(T("Автообновление", "Auto-update"), BuildAutoUpdateTab());

        // Журнал с тонким индикатором передачи сверху
        var activity = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface };
        activity.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
        activity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        activity.Controls.Add(progress, 0, 0);
        activity.Controls.Add(Theme.Framed(log), 0, 1);
        var journal = new Card(T("Журнал", "Log"), null, activity) { Margin = new Padding(0, 0, 0, 10) };

        root.Controls.Add(phones, 0, 0);
        root.Controls.Add(tabs, 0, 1);
        root.Controls.Add(journal, 0, 2);

        Controls.Add(root);
        Controls.Add(BuildFooter());
        Controls.Add(BuildHeader());
        AcceptButton = sendButton;
    }

    Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Surface, Padding = new Padding(16, 0, 16, 0) };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        var logo = new PictureBox { Image = Theme.Logo(38), Size = new Size(38, 38), Location = new Point(16, 13) };
        var title = new Label { Text = "ApkDrop", Font = Theme.AppTitle, ForeColor = Theme.Text, AutoSize = true, Location = new Point(64, 9) };
        var subtitle = new Label
        {
            Text = T("Установка и обновление APK на телефоны и Android TV по Wi-Fi", "Install and update APKs on phones and Android TV over Wi-Fi"),
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Location = new Point(66, 37),
        };

        var options = Theme.Secondary("⚙  " + T("Язык и тема", "Language & theme"), 0);
        options.UseMnemonic = false; // иначе «&» в «Language & theme» превращается в подчёркивание
        options.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        options.Margin = new Padding(0);
        header.Controls.AddRange(new Control[] { logo, title, subtitle, options });
        header.Layout += (_, _) => options.Location = new Point(header.ClientSize.Width - options.Width - 16, (header.Height - options.Height) / 2);
        options.Click += (_, _) => BuildOptionsMenu().Show(options, new Point(0, options.Height));
        return header;
    }

    /// <summary>Меню «Язык и тема»: выбранный пункт отмечен галочкой; смена применяется перезапуском окна.</summary>
    ContextMenuStrip BuildOptionsMenu()
    {
        var menu = new ContextMenuStrip();
        var language = new ToolStripMenuItem(T("Язык", "Language"));
        foreach (var (value, text) in new[] { ("auto", T("Как в Windows", "Same as Windows")), ("ru", "Русский"), ("en", "English") })
        {
            var item = new ToolStripMenuItem(text) { Checked = settings.Language == value };
            item.Click += (_, _) => ChangeOption(() => settings.Language = value);
            language.DropDownItems.Add(item);
        }
        var theme = new ToolStripMenuItem(T("Тема", "Theme"));
        foreach (var (value, text) in new[]
                 {
                     ("auto", T("Как в Windows", "Same as Windows")),
                     ("light", T("Светлая", "Light")),
                     ("dark", T("Тёмная", "Dark")),
                 })
        {
            var item = new ToolStripMenuItem(text) { Checked = settings.ThemeMode == value };
            item.Click += (_, _) => ChangeOption(() => settings.ThemeMode = value);
            theme.DropDownItems.Add(item);
        }
        menu.Items.Add(language);
        menu.Items.Add(theme);
        menu.Items.Add(new ToolStripSeparator());
        var check = new ToolStripMenuItem(T("Проверять обновления при запуске", "Check for updates on start")) { Checked = settings.CheckUpdates };
        check.Click += (_, _) =>
        {
            // Применяется сразу, перезапуск не нужен.
            settings.CheckUpdates = !settings.CheckUpdates;
            settings.Save();
        };
        menu.Items.Add(check);
        menu.Items.Add(T("Проверить обновления сейчас", "Check for updates now"), null, (_, _) => _ = CheckForUpdatesAsync(manual: true));
        Theme.Style(menu);
        return menu;
    }

    /// <summary>Язык и тема задают все подписи и цвета при создании окна — проще всего перезапуститься.</summary>
    void ChangeOption(Action change)
    {
        change();
        settings.Save();
        if (busy)
        {
            Log(T("Настройка сохранена — применится после перезапуска ApkDrop (сейчас идёт отправка).",
                "Setting saved — it will apply after ApkDrop restarts (a transfer is in progress now)."), WarnColor);
            return;
        }
        try
        {
            // Без аргументов: иначе при перезапуске повторно отправился бы APK, с которым программу открыли.
            Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath) { UseShellExecute = false });
            Close();
        }
        catch (Exception ex)
        {
            Log(T("Не удалось перезапустить: ", "Could not restart: ") + ex.Message, ErrColor);
        }
    }

    /// <summary>Нижняя строка: версия слева, авторство справа.</summary>
    Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            ColumnCount = 2,
            BackColor = Theme.Background,
            Padding = new Padding(16, 0, 16, 4),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        footer.Controls.Add(new Label { Text = versionText, ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        footer.Controls.Add(new Label { Text = $"© {Theme.Author}", ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = true, Anchor = AnchorStyles.Right }, 1, 0);
        return footer;
    }

    // ---------- Drag & drop ----------

    void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnDragEnter;
        control.DragDrop += OnDragDrop;
        foreach (Control child in control.Controls) EnableDrop(child);
    }

    static string? DroppedApk(DragEventArgs e) =>
        (e.Data?.GetData(DataFormats.FileDrop) as string[])?
            .FirstOrDefault(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));

    static string? DroppedFolder(DragEventArgs e) =>
        (e.Data?.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(Directory.Exists);

    bool AutoTabActive => tabs.SelectedIndex == 1;

    void OnDragEnter(object? sender, DragEventArgs e) =>
        e.Effect = DroppedApk(e) != null || (AutoTabActive && DroppedFolder(e) != null)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    async void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (AutoTabActive)
        {
            // На вкладке автообновления перетаскивание добавляет источник в список.
            var source = DroppedApk(e) ?? DroppedFolder(e);
            if (source != null) await AddTrackedAsync(source);
            return;
        }
        var apk = DroppedApk(e);
        if (apk == null) return;
        SetApk(apk);
        await SendAsync();
    }

    // ---------- Файл ----------

    void Browse()
    {
        using var dialog = new OpenFileDialog { Filter = "Android APK (*.apk)|*.apk", Title = T("Выберите APK", "Choose an APK") };
        var dir = Path.GetDirectoryName(apkBox.Text);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dialog.InitialDirectory = dir;
        if (dialog.ShowDialog(this) == DialogResult.OK) SetApk(dialog.FileName);
    }

    void SetApk(string path)
    {
        apkBox.Text = path;
        settings.LastApk = path;
        settings.Save();
        lastSentHash = null;
        UpdateWatcher();
    }

    void UpdateWatcher()
    {
        watcher.EnableRaisingEvents = false;
        debounce.Stop();
        var path = apkBox.Text;
        if (!watchBox.Checked || path == "") return;
        var dir = Path.GetDirectoryName(path);
        if (dir == null || !Directory.Exists(dir))
        {
            Log(T($"Папка {dir} пока не существует — соберите проект и выберите файл заново.",
                $"Folder {dir} does not exist yet — build the project and choose the file again."), WarnColor);
            return;
        }
        watcher.Path = dir;
        watcher.Filter = Path.GetFileName(path);
        watcher.EnableRaisingEvents = true;
        Log(T($"Слежу за {path}", $"Watching {path}"));
    }

    void RestartDebounce()
    {
        debounce.Stop();
        debounce.Start();
    }

    async Task OnWatchedFileChangedAsync()
    {
        debounce.Stop();
        var path = apkBox.Text;
        if (!File.Exists(path)) return;
        if (!IsFileReady(path))
        {
            debounce.Start(); // сборка ещё пишет файл
            return;
        }
        string hash;
        try { hash = await Task.Run(() => Hash(path)); }
        catch (IOException) { debounce.Start(); return; }
        if (hash == lastSentHash) return;
        Log(T($"Новая сборка: {Path.GetFileName(path)}", $"New build: {Path.GetFileName(path)}"));
        await SendAsync();
    }

    static bool IsFileReady(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // ---------- Телефоны ----------

    void RefreshDevices()
    {
        refreshingList = true;
        devices.BeginUpdate();
        devices.Items.Clear();
        foreach (var d in settings.Devices)
        {
            var item = new ListViewItem(new[] { d.Name, d.Model, d.Android, $"{d.Host}:{d.Port}", d.Status })
            {
                Tag = d,
                Checked = d.Checked,
                UseItemStyleForSubItems = false,
            };
            devices.Items.Add(item);
            ColorStatus(item, d);
        }
        devices.EndUpdate();
        refreshingList = false;
    }

    void UpdateRow(Device d)
    {
        foreach (ListViewItem item in devices.Items)
        {
            if (item.Tag != d) continue;
            item.SubItems[0].Text = d.Name;
            item.SubItems[3].Text = $"{d.Host}:{d.Port}";
            item.SubItems[4].Text = d.Status;
            ColorStatus(item, d);
        }
    }

    static void ColorStatus(ListViewItem item, Device d) =>
        item.SubItems[4].ForeColor =
            d.Status.StartsWith('✔') || d.Status == Online ? OkColor :
            d.Status.StartsWith('✖') || d.Status == NotFound || d.Status == Offline ? ErrColor :
            d.Status.StartsWith('⏳') ? WarnColor : Theme.Text;

    void SetStatus(Device d, string status)
    {
        d.Status = status;
        UpdateRow(d);
    }

    async Task DiscoverAsync()
    {
        findButton.Enabled = false;
        Log(T("Ищу телефоны в сети…", "Looking for phones on the network…"));
        try
        {
            var found = await DropClient.DiscoverAsync();
            foreach (var d in settings.Devices) d.Status = NotFound;
            foreach (var (info, host) in found) Upsert(info, host);
            RefreshDevices();
            settings.Save();
            if (found.Count == 0)
            {
                Log(T("Телефоны не найдены. Проверьте: телефон в той же Wi-Fi сети, приложение ApkDrop открыто и приём включён. " +
                      "Можно добавить телефон вручную по IP (он показан в приложении).",
                      "No phones found. Check that the phone is on the same Wi-Fi network, ApkDrop is open and receiving is on. " +
                      "You can also add the phone by IP (shown in the app)."), WarnColor);
            }
            else
            {
                Log(T("Найдено: ", "Found: ") + string.Join(", ", found.Select(f => $"{f.Info.Name} ({f.Host})")), OkColor);
            }
        }
        catch (Exception ex)
        {
            Log(T("Ошибка поиска: ", "Search error: ") + ex.Message, ErrColor);
        }
        finally
        {
            findButton.Enabled = true;
        }
    }

    Device Upsert(PhoneInfo info, string host)
    {
        var d = settings.Devices.FirstOrDefault(x => x.Id == info.Id);
        if (d == null)
        {
            // ApkDrop переустановили на том же телефоне — у него новый id. Старую запись убираем,
            // а её галочку и привязки приложений переносим на новую.
            var old = settings.Devices.Where(x => x.Host == host && x.Model == info.Model).ToList();
            d = new Device { Id = info.Id, Checked = old.Count == 0 || old.Any(x => x.Checked) };
            foreach (var o in old)
            {
                settings.Devices.Remove(o);
                foreach (var app in settings.Apps)
                    if (app.DeviceIds.Remove(o.Id) && !app.DeviceIds.Contains(d.Id)) app.DeviceIds.Add(d.Id);
            }
            settings.Devices.Add(d);
        }
        d.Name = info.Name;
        d.Model = info.Model;
        d.Android = info.Android;
        d.Host = host;
        d.Port = info.Port;
        d.Status = info.CanInstall ? Online : T("в сети, но на телефоне не выдано разрешение на установку", "online, but app installs are not allowed on the phone");
        return d;
    }

    async Task AddByIpAsync()
    {
        var input = Prompt.Ask(this, T("Добавить телефон", "Add phone"),
            T("IP-адрес, который показан в приложении ApkDrop (например 192.168.1.23):", "IP address shown in the ApkDrop app (for example 192.168.1.23):"));
        if (input == null) return;
        var host = input;
        var port = DropClient.DefaultPort;
        var colon = input.LastIndexOf(':');
        if (colon > 0 && int.TryParse(input[(colon + 1)..], out var p))
        {
            host = input[..colon];
            port = p;
        }
        try
        {
            var info = await DropClient.InfoAsync(host, port)
                       ?? throw new InvalidOperationException(T("по этому адресу отвечает не ApkDrop", "this address is not ApkDrop"));
            var d = Upsert(info, host);
            d.Port = port;
            RefreshDevices();
            settings.Save();
            Log(T($"Добавлен {info.Name} ({host})", $"Added {info.Name} ({host})"), OkColor);
        }
        catch (Exception ex)
        {
            Log(T($"Не удалось подключиться к {host}:{port} — {ex.Message}", $"Could not connect to {host}:{port} — {ex.Message}"), ErrColor);
        }
    }

    Device? SelectedDevice() => devices.SelectedItems.Count > 0 ? devices.SelectedItems[0].Tag as Device : null;

    void EnterPin()
    {
        var d = SelectedDevice() ?? (settings.Devices.Count == 1 ? settings.Devices[0] : null);
        if (d == null)
        {
            Log(T("Выделите телефон в списке.", "Select a phone in the list."), WarnColor);
            return;
        }
        var pin = Prompt.Ask(this, T("PIN телефона", "Phone PIN"),
            T($"PIN из приложения ApkDrop на «{d.Name}»:", $"PIN from the ApkDrop app on “{d.Name}”:"), d.Pin ?? "");
        if (pin == null) return;
        d.Pin = pin.Replace(" ", "");
        settings.Save();
    }

    void RemoveSelected()
    {
        foreach (ListViewItem item in devices.SelectedItems)
        {
            if (item.Tag is not Device d) continue;
            settings.Devices.Remove(d);
            foreach (var app in settings.Apps) app.DeviceIds.Remove(d.Id);
        }
        RefreshDevices();
        RefreshApps();
        settings.Save();
    }

    // ---------- Отправка ----------

    async Task SendAsync()
    {
        var apk = apkBox.Text;
        if (!File.Exists(apk))
        {
            Log(T("Выберите APK-файл (кнопка «Выбрать APK…» или перетащите файл в окно).",
                "Choose an APK file (the “Choose APK…” button, or drop the file onto the window)."), ErrColor);
            return;
        }
        var targets = settings.Devices.Where(d => d.Checked).ToList();
        if (targets.Count == 0)
        {
            Log(T("Отметьте галочкой телефон в списке (или нажмите «Найти телефоны»).",
                "Check a phone in the list (or click “Find phones”)."), ErrColor);
            return;
        }
        if (busy)
        {
            resendQueued = true;
            Log(T("Отправка уже идёт — повторю после неё.", "A transfer is already running — will repeat after it."));
            return;
        }

        SetBusy(true);
        string? snapshot = null;
        try
        {
            snapshot = await SnapshotAsync(apk);
            lastSentHash = await Task.Run(() => Hash(snapshot));
            foreach (var d in targets) await SendToAsync(d, snapshot, Path.GetFileName(apk));
        }
        catch (Exception ex)
        {
            Log(T("Ошибка: ", "Error: ") + ex.Message, ErrColor);
        }
        finally
        {
            if (snapshot != null) try { File.Delete(snapshot); } catch { }
            settings.Save();
            SetBusy(false);
        }

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

    void SetBusy(bool value)
    {
        busy = value;
        sendButton.Enabled = !value;
        sendButton.Text = value ? T("Отправка…", "Sending…") : T("Установить на телефон", "Install on phone");
        if (!value) progress.Value = 0;
    }

    enum SendResult { Ok, Rejected, Unreachable }

    async Task<bool> SendToAsync(Device d, string apk, string name)
    {
        if (!await EnsureReadyAsync(d, interactive: true)) return false;
        return await InstallToAsync(d, apk, name, launchBox.Checked) == SendResult.Ok;
    }

    /// <summary>
    /// Проверяет связь и PIN. Если IP сменился — находит телефон заново по id.
    /// В неинтерактивном режиме (автообновление) не спрашивает PIN и не пишет в журнал про «не в сети».
    /// </summary>
    async Task<bool> EnsureReadyAsync(Device d, bool interactive)
    {
        var needPin = "✖ " + T("нужен PIN", "PIN required");
        // Без PIN фоновая проверка к телефону не обращается: пустая попытка засчитывается телефоном как неверная.
        if (!interactive && d.Pin == null)
        {
            SetStatus(d, needPin);
            return false;
        }
        if (interactive) SetStatus(d, T("подключение…", "connecting…"));
        PinCheck check;
        try
        {
            check = await DropClient.CheckPinAsync(d.Host, d.Port, d.Pin);
        }
        catch (Exception)
        {
            // IP мог смениться (DHCP) — ищем телефон по id.
            if (interactive) Log(T($"{d.Name}: нет ответа по {d.Host}, ищу в сети…", $"{d.Name}: no answer at {d.Host}, searching the network…"));
            var found = (await DropClient.DiscoverAsync()).FirstOrDefault(f => f.Info.Id == d.Id);
            if (found.Info == null)
            {
                SetStatus(d, Offline);
                if (interactive)
                    Log(T($"{d.Name}: телефон не отвечает. Откройте ApkDrop на телефоне и проверьте, что он в той же Wi-Fi сети.",
                        $"{d.Name}: the phone does not respond. Open ApkDrop on the phone and check it is on the same Wi-Fi network."), ErrColor);
                return false;
            }
            Upsert(found.Info, found.Host);
            UpdateRow(d);
            try
            {
                check = await DropClient.CheckPinAsync(d.Host, d.Port, d.Pin);
            }
            catch (Exception ex)
            {
                SetStatus(d, Offline);
                if (interactive) Log($"{d.Name}: {ex.Message}", ErrColor);
                return false;
            }
        }

        for (var attempt = 0; check != PinCheck.Ok; attempt++)
        {
            if (check == PinCheck.Locked || attempt >= 3)
            {
                SetStatus(d, "✖ " + T("PIN заблокирован на минуту", "PIN locked for a minute"));
                if (interactive)
                    Log(T($"{d.Name}: слишком много неверных PIN — телефон не принимает попытки минуту.",
                        $"{d.Name}: too many wrong PINs — the phone refuses attempts for a minute."), ErrColor);
                return false;
            }
            if (!interactive)
            {
                // PIN на телефоне сменили — забываем старый, чтобы не тратить попытки каждые пару минут.
                d.Pin = null;
                settings.Save();
                SetStatus(d, needPin);
                Log(T($"{d.Name}: сохранённый PIN больше не подходит — введите новый (кнопка «Ввести PIN…» или любая отправка).",
                    $"{d.Name}: the saved PIN no longer works — enter the new one (“Enter PIN…” or any transfer)."), WarnColor);
                return false;
            }
            if (d.Pin != null) Log(T($"{d.Name}: неверный PIN.", $"{d.Name}: wrong PIN."), ErrColor);
            SetStatus(d, "⏳ " + T("нужен PIN", "PIN required"));
            var pin = Prompt.Ask(this, T("PIN телефона", "Phone PIN"),
                T($"Введите PIN, который показан в приложении ApkDrop на «{d.Name}»:", $"Enter the PIN shown in the ApkDrop app on “{d.Name}”:"));
            if (pin == null)
            {
                SetStatus(d, needPin);
                return false;
            }
            d.Pin = pin.Replace(" ", "");
            settings.Save();
            check = await DropClient.CheckPinAsync(d.Host, d.Port, d.Pin);
        }
        if (!d.Status.StartsWith('✔') && !d.Status.StartsWith('✖')) SetStatus(d, Online);
        return true;
    }

    /// <summary>Отправка на телефон, для которого EnsureReadyAsync уже вернул true.</summary>
    async Task<SendResult> InstallToAsync(Device d, string apk, string name, bool launch)
    {
        var size = new FileInfo(apk).Length;
        Log(T($"{d.Name}: отправляю {name} ({Mb(size)})", $"{d.Name}: sending {name} ({Mb(size)})"));
        SetStatus(d, T("отправка…", "sending…"));
        progress.Value = 0;
        var sw = Stopwatch.StartNew();
        var upload = new Progress<double>(p => progress.Value = Math.Clamp((int)(p * 1000), 0, 1000));

        try
        {
            var outcome = await DropClient.InstallAsync(d.Host, d.Port, d.Pin!, apk, launch, upload,
                e => OnInstallEvent(d, e, sw, size));
            var text = outcome.Message + (outcome.Launched ? T(", запущено", ", launched") : "");
            SetStatus(d, (outcome.Ok ? "✔ " : "✖ ") + text);
            Log($"{d.Name}: {text}", outcome.Ok ? OkColor : ErrColor);
            if (outcome.LaunchError != null) Log(T("  Не запущено: ", "  Not launched: ") + outcome.LaunchError, WarnColor);
            return outcome.Ok ? SendResult.Ok : SendResult.Rejected;
        }
        catch (OperationCanceledException)
        {
            SetStatus(d, "✖ " + T("таймаут", "timeout"));
            Log(T($"{d.Name}: телефон слишком долго не отвечал.", $"{d.Name}: the phone took too long to respond."), ErrColor);
        }
        catch (Exception ex)
        {
            SetStatus(d, "✖ " + T("ошибка связи", "connection error"));
            Log(T($"{d.Name}: ошибка связи — ", $"{d.Name}: connection error — ") + ex.GetBaseException().Message, ErrColor);
        }
        finally
        {
            progress.Value = 0;
        }
        return SendResult.Unreachable;
    }

    /// <summary>Копия защищает от сборки, которая перезапишет APK прямо во время передачи.</summary>
    static Task<string> SnapshotAsync(string apk) => Task.Run(() =>
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"apkdrop-{Guid.NewGuid():N}.apk");
        File.Copy(apk, tmp);
        return tmp;
    });

    void OnInstallEvent(Device d, JsonElement e, Stopwatch sw, long size)
    {
        switch (PhoneInfo.Str(e, "event"))
        {
            case "received":
                progress.Value = 1000;
                var secs = Math.Max(sw.Elapsed.TotalSeconds, 0.01);
                var speed = $"{size / 1048576.0 / secs:0.0} {T("МБ/с", "MB/s")}";
                Log(T($"  передано за {secs:0.0} с ({speed})", $"  transferred in {secs:0.0} s ({speed})"));
                SetStatus(d, T("установка…", "installing…"));
                break;

            case "parsed":
                var label = PhoneInfo.Str(e, "label");
                var version = PhoneInfo.Str(e, "version");
                var code = e.GetProperty("versionCode").GetInt64();
                var installed = e.TryGetProperty("installedVersion", out var iv) && iv.ValueKind == JsonValueKind.String ? iv.GetString() : null;
                var hasInstalled = e.TryGetProperty("installedVersionCode", out var ic) && ic.ValueKind == JsonValueKind.Number;
                Log($"  {label} {version} (versionCode {code}); " + T("на телефоне: ", "on the phone: ") +
                    (hasInstalled ? $"{installed} (versionCode {ic.GetInt64()})" : T("не установлено", "not installed")));
                if (hasInstalled && ic.GetInt64() > code)
                    Log(T("  На телефоне версия новее — Android не даст понизить версию.",
                        "  The phone has a newer version — Android will not allow a downgrade."), WarnColor);
                var installer = e.TryGetProperty("installer", out var ins) && ins.ValueKind == JsonValueKind.String ? ins.GetString() : null;
                if (!hasInstalled)
                    Log(T("  Первая установка — подтвердите на телефоне, дальше обновления пойдут без вопросов.",
                        "  First install — confirm it on the phone; later updates will go through silently."), WarnColor);
                else if (installer != OwnPackage && PhoneInfo.Str(e, "package") != OwnPackage)
                    Log(T("  Приложение ставилось не через ApkDrop — в этот раз нужно подтверждение, дальше будет тихо.",
                        "  The app was not installed via ApkDrop — confirm it this time; later updates will be silent."), WarnColor);
                break;

            case "installing":
                SetStatus(d, T("установка…", "installing…"));
                break;

            case "confirm":
                SetStatus(d, "⏳ " + T("подтвердите установку на телефоне", "confirm the install on the phone"));
                var popup = !e.TryGetProperty("popup", out var pp) || pp.GetBoolean();
                Log(popup
                    ? T("  ⏳ Нажмите «Установить» на телефоне.", "  ⏳ Tap “Install” on the phone.")
                    : T("  ⏳ ApkDrop на телефоне свёрнут — откройте уведомление «Подтвердите установку» и нажмите «Установить». " +
                        "Если держать ApkDrop открытым, окно установки появится само.",
                        "  ⏳ ApkDrop is in the background on the phone — open the “Confirm install” notification and tap “Install”. " +
                        "If ApkDrop is kept open, the install prompt appears by itself."), WarnColor);
                break;

            case "self-update":
                Log(T("  Обновляется сам ApkDrop — связь прервётся, это нормально.",
                    "  ApkDrop itself is being updated — the connection will drop, that is expected."));
                break;
        }
    }

    // ---------- Журнал ----------

    void Log(string text, Color? color = null)
    {
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.SelectionColor = Theme.Muted;
        log.AppendText(DateTime.Now.ToString("HH:mm:ss "));
        log.SelectionColor = color ?? Theme.Text;
        log.AppendText(text + Environment.NewLine);
        log.ScrollToCaret();
    }
}
