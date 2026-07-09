namespace DicomWorkstation;

/// <summary>
/// Dialogo password per l'accesso alle impostazioni.
/// La password è l'hostname del PC (confronto case-insensitive).
/// </summary>
public static class PasswordDialog
{
    public static bool Verifica(IWin32Window owner)
    {
        using var dlg = new Form
        {
            Text = "Impostazioni protette",
            Width = 400, Height = 160,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
        };
        var lbl = new Label
        {
            Text = "Inserire la password per accedere alle impostazioni:",
            AutoSize = true, Left = 12, Top = 12,
        };
        var txt = new TextBox { Left = 12, Top = 38, Width = 358, UseSystemPasswordChar = true };
        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 214, Top = 74, AutoSize = true };
        var btnCancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, Left = 295, Top = 74, AutoSize = true };
        dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
        dlg.AcceptButton = btnOk;
        dlg.CancelButton = btnCancel;

        if (dlg.ShowDialog(owner) != DialogResult.OK) return false;

        if (string.Equals(txt.Text.Trim(), Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;

        MessageBox.Show(owner, "Password errata.", "Impostazioni",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }
}
