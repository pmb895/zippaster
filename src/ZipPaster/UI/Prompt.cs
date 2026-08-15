namespace ZipPaster.UI;

/// <summary>Small single-line text prompt; WinForms has no built-in equivalent.</summary>
internal static class Prompt
{
    public static string? Show(IWin32Window owner, string title, string message, string initialValue)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 130),
            Icon = AppIcon.Get(),
        };

        var label = new Label
        {
            Text = message,
            Location = new Point(14, 14),
            Size = new Size(392, 36),
        };

        var input = new TextBox
        {
            Text = initialValue,
            Location = new Point(14, 54),
            Width = 392,
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(230, 90),
            Width = 84,
        };

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(322, 90),
            Width = 84,
        };

        form.Controls.AddRange([label, input, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        input.SelectAll();

        return form.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }
}
