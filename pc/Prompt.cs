using static ApkDrop.L;

namespace ApkDrop;

static class Prompt
{
    public static string? Ask(IWin32Window owner, string title, string text, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
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
        var label = new Label { Text = text, AutoSize = true, MaximumSize = new Size(400, 0), Margin = new Padding(0, 0, 0, 8) };
        var box = new TextBox { Text = initial, Width = 400, Font = new Font("Consolas", 14f), BorderStyle = BorderStyle.FixedSingle };
        Theme.Style(box);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        var ok = Theme.Primary("OK", 96);
        ok.DialogResult = DialogResult.OK;
        ok.Margin = new Padding(8, 0, 0, 0);
        var cancel = Theme.Secondary(T("Отмена", "Cancel"), 96);
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Margin = new Padding(0);
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        panel.Controls.Add(label);
        panel.Controls.Add(box);
        panel.Controls.Add(buttons);
        form.Controls.Add(panel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}
