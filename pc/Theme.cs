using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ApkDrop;

/// <summary>
/// Единый спокойный стиль окна: фон, карточки, зелёный акцент как у приложения на телефоне.
/// Две палитры — светлая и тёмная; выбирается один раз при запуске (<see cref="Init"/>).
/// </summary>
static class Theme
{
    public const string Author = "Bapuk01";

    public static bool Dark { get; private set; }

    public static Color Background { get; private set; }
    public static Color Surface { get; private set; }
    public static Color Border { get; private set; }
    public static Color Hover { get; private set; }
    /// <summary>Фон поля ввода и журнала — чуть отличается от карточки.</summary>
    public static Color Input { get; private set; }
    public static Color HeaderBack { get; private set; }

    /// <summary>Заливка главных кнопок (белый текст поверх).</summary>
    public static Color Accent { get; private set; }
    public static Color AccentHover { get; private set; }
    public static Color AccentDisabled { get; private set; }
    public static Color AccentSoft { get; private set; }
    /// <summary>Зелёный для текста и линий: в тёмной теме он светлее, чтобы читался.</summary>
    public static Color AccentText { get; private set; }

    public static Color Text { get; private set; }
    public static Color Muted { get; private set; }
    public static Color Danger { get; private set; }
    public static Color Ok { get; private set; }
    public static Color Warn { get; private set; }
    public static Color Err { get; private set; }

    public static readonly Font Body = new("Segoe UI", 9.5f);
    public static readonly Font BodyBold = new("Segoe UI Semibold", 9.5f);
    public static readonly Font Small = new("Segoe UI", 8.5f);
    public static readonly Font SmallBold = new("Segoe UI Semibold", 8.5f);
    public static readonly Font CardTitle = new("Segoe UI Semibold", 11f);
    public static readonly Font AppTitle = new("Segoe UI Semibold", 15f);
    public static readonly Font Mono = new("Consolas", 9.5f);

    /// <param name="mode">"light", "dark" или "auto" — как в Windows (Параметры → Персонализация → Цвета).</param>
    public static void Init(string? mode)
    {
        Dark = mode switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemPrefersDark(),
        };

        if (Dark)
        {
            Background = Color.FromArgb(27, 29, 33);
            Surface = Color.FromArgb(36, 39, 44);
            Border = Color.FromArgb(58, 63, 70);
            Hover = Color.FromArgb(46, 50, 56);
            Input = Color.FromArgb(30, 33, 37);
            HeaderBack = Color.FromArgb(42, 45, 51);
            Accent = Color.FromArgb(56, 142, 60);
            AccentHover = Color.FromArgb(67, 160, 71);
            AccentDisabled = Color.FromArgb(52, 78, 55);
            AccentSoft = Color.FromArgb(42, 58, 44);
            AccentText = Color.FromArgb(129, 199, 132);
            Text = Color.FromArgb(230, 232, 235);
            Muted = Color.FromArgb(154, 163, 173);
            Danger = Color.FromArgb(239, 83, 80);
            Ok = Color.FromArgb(102, 187, 106);
            Warn = Color.FromArgb(255, 183, 77);
            Err = Color.FromArgb(239, 83, 80);
        }
        else
        {
            Background = Color.FromArgb(243, 245, 247);
            Surface = Color.White;
            Border = Color.FromArgb(221, 226, 231);
            Hover = Color.FromArgb(234, 237, 240);
            Input = Color.FromArgb(248, 249, 250);
            HeaderBack = Color.FromArgb(246, 247, 249);
            Accent = Color.FromArgb(46, 125, 50);
            AccentHover = Color.FromArgb(27, 94, 32);
            AccentDisabled = Color.FromArgb(165, 200, 167);
            AccentSoft = Color.FromArgb(232, 243, 233);
            AccentText = Color.FromArgb(46, 125, 50);
            Text = Color.FromArgb(31, 35, 40);
            Muted = Color.FromArgb(91, 100, 112);
            Danger = Color.FromArgb(198, 40, 40);
            Ok = Color.FromArgb(46, 125, 50);
            Warn = Color.FromArgb(180, 95, 0);
            Err = Color.FromArgb(198, 40, 40);
        }
    }

    static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------- Окна ----------

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Цвета формы и тёмная полоса заголовка (Windows 10 2004+ и Windows 11).</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Surface;
        form.ForeColor = Text;
        form.Font = Body;
        form.HandleCreated += (_, _) =>
        {
            if (!Dark) return;
            var on = 1;
            // 20 — DWMWA_USE_IMMERSIVE_DARK_MODE; на ранних сборках Windows 10 был номер 19.
            if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
        };
    }

    // ---------- Кнопки ----------

    public static Button Primary(string text, int minWidth = 170)
    {
        var b = Base(text, minWidth);
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.Font = BodyBold;
        b.FlatAppearance.BorderColor = Accent;
        b.FlatAppearance.MouseOverBackColor = AccentHover;
        b.FlatAppearance.MouseDownBackColor = AccentHover;
        // Недоступная зелёная кнопка иначе выглядит как обычная серая — делаем её бледно-зелёной.
        b.EnabledChanged += (_, _) =>
        {
            b.BackColor = b.Enabled ? Accent : AccentDisabled;
            b.FlatAppearance.BorderColor = b.BackColor;
        };
        return b;
    }

    public static Button Secondary(string text, int minWidth = 170)
    {
        var b = Base(text, minWidth);
        b.BackColor = Surface;
        b.ForeColor = Text;
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.MouseOverBackColor = Hover;
        b.FlatAppearance.MouseDownBackColor = AccentSoft;
        return b;
    }

    static Button Base(string text, int minWidth) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(minWidth, 32),
        Margin = new Padding(0, 0, 0, 6),
        Padding = new Padding(10, 0, 10, 0),
        FlatStyle = FlatStyle.Flat,
        Cursor = Cursors.Hand,
        UseVisualStyleBackColor = false,
        Font = Body,
    };

    // ---------- Поля и списки ----------

    /// <summary>
    /// Флажок/переключатель: цвет текста по теме. Сам квадрат/кружок оставляем системным —
    /// у плоского варианта в тёмной теме не видно, какой переключатель выбран.
    /// </summary>
    public static T Style<T>(T box) where T : ButtonBase
    {
        box.ForeColor = Text;
        box.BackColor = Color.Transparent;
        return box;
    }

    public static void Style(TextBoxBase box)
    {
        box.BackColor = Input;
        box.ForeColor = Text;
    }

    /// <summary>
    /// Плоский список: без рамки (её рисует карточка), с более высокими строками и своей шапкой —
    /// системная шапка в тёмной теме осталась бы белой.
    /// </summary>
    public static void Style(ListView list)
    {
        list.BorderStyle = BorderStyle.None;
        list.BackColor = Surface;
        list.ForeColor = Text;
        list.Font = Body;
        // Высота строки в ListView задаётся размером картинок — пустая «картинка» 1×26 делает строки просторнее.
        list.SmallImageList = new ImageList { ImageSize = new Size(1, 26) };

        list.OwnerDraw = true;
        list.DrawItem += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) => e.DrawDefault = true;
        list.DrawColumnHeader += (_, e) =>
        {
            using (var back = new SolidBrush(HeaderBack)) e.Graphics.FillRectangle(back, e.Bounds);
            using (var line = new Pen(Border))
            {
                e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                if (e.ColumnIndex > 0) e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Top + 5, e.Bounds.Left, e.Bounds.Bottom - 6);
            }
            var text = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, SmallBold, text, Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        // Тёмные полосы прокрутки и флажки; в светлой теме — современная подсветка строк как в Проводнике.
        list.HandleCreated += (_, _) => SetWindowTheme(list.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    public static void Style(ListBox list)
    {
        list.BackColor = Input;
        list.ForeColor = Text;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.HandleCreated += (_, _) => { if (Dark) SetWindowTheme(list.Handle, "DarkMode_Explorer", null); };
    }

    public static void Style(RichTextBox box)
    {
        box.BackColor = Input;
        box.ForeColor = Text;
        box.HandleCreated += (_, _) => { if (Dark) SetWindowTheme(box.Handle, "DarkMode_Explorer", null); };
    }

    public static void Style(ToolStrip menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        menu.Font = Body;
        void Paint(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                item.ForeColor = Text;
                if (item is ToolStripMenuItem m && m.HasDropDownItems)
                {
                    m.DropDown.BackColor = Surface;
                    Paint(m.DropDownItems);
                }
            }
        }
        Paint(menu.Items);
        menu.ItemAdded += (_, e) => e.Item!.ForeColor = Text;
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color CheckBackground => AccentSoft;
        public override Color CheckSelectedBackground => AccentSoft;
        public override Color CheckPressedBackground => AccentSoft;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }

    /// <summary>Последняя колонка списка занимает всё оставшееся место — и при первом показе, и при изменении размера.</summary>
    public static void FillLastColumn(ListView list, int minWidth)
    {
        lastColumnMin[list] = minWidth;
        list.ClientSizeChanged += (_, _) => FitLastColumn(list);
        list.VisibleChanged += (_, _) => FitLastColumn(list);
        list.ColumnWidthChanged += (_, e) => { if (e.ColumnIndex < list.Columns.Count - 1) FitLastColumn(list); };
    }

    static readonly Dictionary<ListView, int> lastColumnMin = new();
    static bool fitting;

    /// <summary>Подогнать сейчас — нужно после первого показа окна, когда размеры уже окончательные.</summary>
    public static void FitLastColumn(ListView list)
    {
        if (fitting || list.Columns.Count == 0 || !list.IsHandleCreated || !lastColumnMin.TryGetValue(list, out var min)) return;
        fitting = true;
        try
        {
            var used = 0;
            for (var i = 0; i < list.Columns.Count - 1; i++) used += list.Columns[i].Width;
            list.Columns[^1].Width = Math.Max(min, list.ClientSize.Width - used - 1);
        }
        finally
        {
            fitting = false;
        }
    }

    /// <summary>Тонкая рамка вокруг списка/журнала внутри карточки.</summary>
    public static Panel Framed(Control content)
    {
        var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Border, Margin = new Padding(0) };
        content.Dock = DockStyle.Fill;
        frame.Controls.Add(content);
        return frame;
    }

    public static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Muted,
        Font = Small,
        Margin = new Padding(0, 4, 0, 4),
    };

    /// <summary>Значок приложения: зелёный круг со стрелкой вниз — тот же, что у приложения на телефоне.</summary>
    public static Bitmap Logo(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Accent)) g.FillEllipse(fill, 0, 0, size - 1, size - 1);
        var s = size / 24f;
        using var pen = new Pen(Color.White, Math.Max(1.5f, 2.4f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLine(pen, 12 * s, 6 * s, 12 * s, 14.5f * s);
        g.DrawLines(pen, new[] { new PointF(8 * s, 10.5f * s), new PointF(12 * s, 14.5f * s), new PointF(16 * s, 10.5f * s) });
        g.DrawLine(pen, 7.5f * s, 18 * s, 16.5f * s, 18 * s);
        return bmp;
    }
}

/// <summary>Карточка с тонкой рамкой, заголовком и (необязательно) пояснением рядом.</summary>
sealed class Card : Panel
{
    readonly TableLayoutPanel layout = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface };

    public Card(string? title, string? subtitle, Control content)
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;
        Padding = new Padding(14, 10, 14, 12);
        Margin = new Padding(0, 0, 0, 12);
        Dock = DockStyle.Fill;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        if (title != null)
        {
            var head = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
            head.Controls.Add(new Label { Text = title, AutoSize = true, Font = Theme.CardTitle, ForeColor = Theme.Text, Margin = new Padding(0, 0, 8, 0) });
            if (subtitle != null)
                head.Controls.Add(new Label { Text = subtitle, AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, Margin = new Padding(0, 5, 0, 0) });
            layout.Controls.Add(head, 0, 0);
        }
        content.Dock = DockStyle.Fill;
        content.Margin = new Padding(0);
        layout.Controls.Add(content, 0, 1);
        Controls.Add(layout);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Invalidate();
    }
}

/// <summary>Плоские вкладки: подписи сверху, у активной — зелёный текст и полоска снизу; содержимое в карточке.</summary>
sealed class TabStrip : TableLayoutPanel
{
    readonly FlowLayoutPanel header = new() { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), BackColor = Theme.Background };
    readonly Panel host = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface };
    readonly List<(TabButton Tab, Control Page)> pages = new();

    public int SelectedIndex { get; private set; } = -1;
    public event EventHandler? SelectedIndexChanged;

    public TabStrip()
    {
        Dock = DockStyle.Fill;
        ColumnCount = 1;
        RowCount = 2;
        Margin = new Padding(0, 0, 0, 12);
        BackColor = Theme.Background;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(header, 0, 0);
        Controls.Add(new Card(null, null, host) { Margin = new Padding(0) }, 0, 1);
    }

    public void AddPage(string title, Control page)
    {
        var index = pages.Count;
        var tab = new TabButton
        {
            Text = title,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyBold,
            ForeColor = Theme.Muted,
            BackColor = Theme.Background,
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 0, 4, 0),
            Padding = new Padding(8, 4, 8, 6),
            UseVisualStyleBackColor = false,
        };
        tab.FlatAppearance.BorderSize = 0;
        tab.FlatAppearance.MouseOverBackColor = Theme.Hover;
        tab.FlatAppearance.MouseDownBackColor = Theme.AccentSoft;
        tab.Paint += (_, e) =>
        {
            if (index != SelectedIndex) return;
            using var bar = new SolidBrush(Theme.AccentText);
            e.Graphics.FillRectangle(bar, 6, tab.Height - 3, tab.Width - 12, 3);
        };
        tab.Click += (_, _) => Select(index);
        header.Controls.Add(tab);

        page.Dock = DockStyle.Fill;
        page.Visible = false;
        host.Controls.Add(page);
        pages.Add((tab, page));
        if (SelectedIndex < 0) Select(0);
    }

    public void Select(int index)
    {
        if (index == SelectedIndex) return;
        SelectedIndex = index;
        for (var i = 0; i < pages.Count; i++)
        {
            var active = i == index;
            pages[i].Page.Visible = active;
            pages[i].Tab.ForeColor = active ? Theme.AccentText : Theme.Muted;
            pages[i].Tab.Invalidate();
        }
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Подпись вкладки: без рамки фокуса — активную вкладку и так видно по зелёной полоске.</summary>
sealed class TabButton : Button
{
    public TabButton() => SetStyle(ControlStyles.Selectable, false);

    protected override bool ShowFocusCues => false;
}

/// <summary>Тонкая полоса прогресса в цветах темы (системная в тёмной теме осталась бы светлой).</summary>
sealed class ThinProgress : Control
{
    int value;

    public int Maximum { get; set; } = 1000;

    public int Value
    {
        get => value;
        set
        {
            var v = Math.Clamp(value, 0, Maximum);
            if (v == this.value) return;
            this.value = v;
            Invalidate();
        }
    }

    public ThinProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 4;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var track = new SolidBrush(Theme.Border)) e.Graphics.FillRectangle(track, ClientRectangle);
        if (value <= 0 || Maximum <= 0) return;
        var width = (int)((long)ClientSize.Width * value / Maximum);
        using var fill = new SolidBrush(Theme.AccentText);
        e.Graphics.FillRectangle(fill, 0, 0, width, ClientSize.Height);
    }
}
