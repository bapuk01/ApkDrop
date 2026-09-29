using static ApkDrop.L;

namespace ApkDrop;

/// <summary>Выбор телефонов, для которых ведётся приложение из списка автообновления.</summary>
static class AppTargetsDialog
{
    public sealed record Result(bool AllDevices, List<string> DeviceIds, bool InstallMissing);

    public static Result? Show(IWin32Window owner, string appTitle, IReadOnlyList<Device> devices, TrackedApp current)
    {
        using var form = new Form
        {
            Text = T("Телефоны для приложения", "Phones for the app"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16),
        };
        Theme.Apply(form);
        var panel = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = T($"На каких телефонах следить за «{appTitle}»:", $"Which phones should keep “{appTitle}” up to date:"),
            AutoSize = true,
            MaximumSize = new Size(440, 0),
            Font = Theme.CardTitle,
            Margin = new Padding(0, 0, 0, 8),
        };
        var allRadio = Theme.Style(new RadioButton
        {
            Text = T("Все телефоны, отмеченные галочкой в главном списке", "All phones checked in the main list"),
            AutoSize = true,
        });
        var someRadio = Theme.Style(new RadioButton { Text = T("Только выбранные:", "Only these:"), AutoSize = true });
        var list = new CheckedListBox { Width = 440, Height = 150, CheckOnClick = true, IntegralHeight = false, Margin = new Padding(20, 3, 3, 3) };
        Theme.Style(list);
        foreach (var d in devices)
        {
            var text = d.Model != "" && d.Model != d.Name ? $"{d.Name}  ({d.Model})" : d.Name;
            list.Items.Add(new Item(d.Id, text), current.DeviceIds.Contains(d.Id));
        }
        var installBox = Theme.Style(new CheckBox
        {
            Text = T("Устанавливать, если приложения на телефоне ещё нет\n(первая установка — одно нажатие «Установить» на телефоне)",
                "Install if the app is not on the phone yet\n(first install — one tap on “Install” on the phone)"),
            AutoSize = true,
            Checked = current.InstallMissing,
            Margin = new Padding(3, 10, 3, 3),
        });
        var error = new Label { AutoSize = true, ForeColor = Theme.Err, Visible = false, Text = T("Отметьте хотя бы один телефон.", "Check at least one phone.") };

        allRadio.Checked = current.AllDevices;
        someRadio.Checked = !current.AllDevices;
        list.Enabled = someRadio.Checked;
        someRadio.CheckedChanged += (_, _) => { list.Enabled = someRadio.Checked; error.Visible = false; };
        list.ItemCheck += (_, _) => error.Visible = false;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        var ok = Theme.Primary("OK", 96);
        ok.DialogResult = DialogResult.OK;
        ok.Margin = new Padding(8, 0, 0, 0);
        var cancel = Theme.Secondary(T("Отмена", "Cancel"), 96);
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Margin = new Padding(0);
        buttons.Controls.AddRange(new Control[] { cancel, ok });

        panel.Controls.AddRange(new Control[] { title, allRadio, someRadio, list, installBox, error, buttons });
        form.Controls.Add(panel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.FormClosing += (_, e) =>
        {
            if (form.DialogResult == DialogResult.OK && someRadio.Checked && list.CheckedItems.Count == 0)
            {
                error.Visible = true;
                e.Cancel = true;
            }
        };

        if (form.ShowDialog(owner) != DialogResult.OK) return null;
        var ids = list.CheckedItems.Cast<Item>().Select(i => i.Id).ToList();
        return new Result(allRadio.Checked, allRadio.Checked ? current.DeviceIds : ids, installBox.Checked);
    }

    sealed record Item(string Id, string Text)
    {
        public override string ToString() => Text;
    }
}
