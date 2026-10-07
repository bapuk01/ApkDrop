using System.Diagnostics;
using static ApkDrop.L;

namespace ApkDrop;

/// <summary>
/// Окно «Доступна новая версия»: описание релиза и кнопки «Обновить / Позже / Пропустить версию».
/// Результат: OK — файл скачан и проверен (<see cref="DownloadedFile"/>), Cancel — позже, Ignore — пропустить версию.
/// </summary>
sealed class UpdateDialog : Form
{
    readonly UpdateInfo info;
    readonly Button updateButton;
    readonly Button laterButton;
    readonly Button skipButton;
    readonly ThinProgress bar = new() { Dock = DockStyle.Top, Height = 6, Visible = false, Margin = new Padding(0, 10, 0, 0) };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 6, 0, 0), Font = Theme.Small };
    CancellationTokenSource? cts;

    /// <summary>Путь к скачанному и проверенному файлу новой версии.</summary>
    public string? DownloadedFile { get; private set; }

    public UpdateDialog(UpdateInfo info)
    {
        this.info = info;
        Text = T("Доступно обновление", "Update available");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);
        Theme.Apply(this);

        var panel = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };

        panel.Controls.Add(new Label
        {
            Text = T("Доступна новая версия ApkDrop", "A new version of ApkDrop is available"),
            AutoSize = true,
            Font = Theme.CardTitle,
            Margin = new Padding(0, 0, 0, 2),
        });
        panel.Controls.Add(new Label
        {
            Text = $"{UpdateChecker.Current.ToString(2)}  →  {info.Version.ToString(2)}",
            AutoSize = true,
            ForeColor = Theme.AccentText,
            Font = Theme.BodyBold,
            Margin = new Padding(0, 0, 0, 10),
        });

        var notes = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Width = 480,
            Height = 170,
            Text = UpdateChecker.PlainNotes(info.Notes, english: L.En),
            TabStop = false,
        };
        Theme.Style(notes);
        notes.Select(0, 0);
        panel.Controls.Add(notes);

        panel.Controls.Add(bar);
        panel.Controls.Add(status);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0), WrapContents = false };
        updateButton = Theme.Primary(info.Asset != null ? T("Обновить", "Update") : T("Открыть страницу релиза", "Open release page"), 110);
        laterButton = Theme.Secondary(T("Позже", "Later"), 90);
        skipButton = Theme.Secondary(T("Пропустить версию", "Skip this version"), 110);
        updateButton.Margin = new Padding(0, 0, 8, 0);
        laterButton.Margin = new Padding(0, 0, 8, 0);
        skipButton.Margin = new Padding(0);
        buttons.Controls.AddRange(new Control[] { updateButton, laterButton, skipButton });
        panel.Controls.Add(buttons);

        Controls.Add(panel);
        // Окно появляется само, когда пользователь может печатать в другом окне: случайный Enter
        // не должен скачивать 70 МБ и перезапускать программу. Обновляет только явный клик.
        AcceptButton = laterButton;
        CancelButton = laterButton;
        ActiveControl = laterButton;

        updateButton.Click += (_, _) => OnUpdate();
        laterButton.Click += (_, _) =>
        {
            // Пока идёт скачивание, «Позже» работает как «Отмена».
            if (cts != null) cts.Cancel();
            else DialogResult = DialogResult.Cancel;
        };
        skipButton.Click += (_, _) => DialogResult = DialogResult.Ignore;
        FormClosing += (_, _) => cts?.Cancel();
    }

    void SetBusy(bool busy)
    {
        updateButton.Enabled = !busy;
        skipButton.Enabled = !busy;
        laterButton.Text = busy ? T("Отмена", "Cancel") : T("Позже", "Later");
        bar.Visible = busy;
        if (!busy) cts = null;
    }

    async void OnUpdate()
    {
        if (info.Asset == null)
        {
            OpenPage();
            DialogResult = DialogResult.Cancel;
            return;
        }

        SetBusy(true);
        status.ForeColor = Theme.Muted;
        status.Text = T("Подключение…", "Connecting…");
        cts = new CancellationTokenSource();
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            bar.Maximum = 1000;
            bar.Value = p.Total > 0 ? (int)Math.Min(1000, p.Done * 1000 / p.Total) : 0;
            status.Text = T($"Скачивание… {Mb(p.Done)} из {Mb(p.Total)}", $"Downloading… {Mb(p.Done)} of {Mb(p.Total)}");
        });
        try
        {
            DownloadedFile = await UpdateChecker.DownloadAsync(info.Asset, (Environment.ProcessPath ?? "") + ".new", progress, cts.Token);
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException)
        {
            status.Text = "";
            SetBusy(false);
        }
        catch (Exception ex)
        {
            status.ForeColor = Theme.Err;
            status.Text = (ex is UnauthorizedAccessException
                ? T("Нет прав на запись в папку программы — скачайте новую версию вручную со страницы релиза.",
                    "No write access to the program folder — download the new version manually from the release page.")
                : T("Не удалось скачать: ", "Download failed: ") + ex.GetBaseException().Message);
            SetBusy(false);
            updateButton.Text = T("Повторить", "Retry");
        }
    }

    void OpenPage()
    {
        try { Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true }); } catch { }
    }
}
