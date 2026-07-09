using System.ComponentModel;

namespace DicomWorkstation;

/// <summary>Configurazione dei parametri DICOM interamente da GUI.</summary>
public class SettingsForm : Form
{
    private readonly AppConfig _cfg;
    private readonly TextBox _txtAet = new() { Width = 160, CharacterCasing = CharacterCasing.Upper };
    private readonly NumericUpDown _numPort = new() { Minimum = 1, Maximum = 65535, Width = 90 };
    private readonly TextBox _txtStorage = new() { Width = 340 };
    private readonly DataGridView _gridPacs = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = true, AllowUserToDeleteRows = true,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
    };
    private readonly BindingList<RemotePacs> _pacsBinding;

    public SettingsForm(AppConfig cfg)
    {
        _cfg = cfg;
        Text = "Impostazioni DICOM";
        Width = 620; Height = 480;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        _txtAet.Text = cfg.LocalAet;
        _numPort.Value = Math.Clamp(cfg.ListenPort, 1, 65535);
        _txtStorage.Text = cfg.StoragePath;

        // Copia di lavoro: si applica solo su OK
        _pacsBinding = new BindingList<RemotePacs>(
            cfg.PacsList.Select(p => new RemotePacs { Name = p.Name, Aet = p.Aet, Host = p.Host, Port = p.Port }).ToList());

        BuildLayout();
    }

    private void BuildLayout()
    {
        var local = new GroupBox { Text = "Nodo locale", Dock = DockStyle.Top, Height = 120, Padding = new Padding(10) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        t.Controls.Add(new Label { Text = "AE Title:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(_txtAet, 1, 0);
        t.Controls.Add(new Label { Text = "Porta di ascolto (C-STORE SCP / destinazione C-MOVE):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        t.Controls.Add(_numPort, 1, 1);
        t.Controls.Add(new Label { Text = "Cartella cache:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        t.Controls.Add(_txtStorage, 1, 2);
        var btnBrowse = new Button { Text = "…", Width = 32 };
        btnBrowse.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog { SelectedPath = _txtStorage.Text };
            if (fb.ShowDialog(this) == DialogResult.OK) _txtStorage.Text = fb.SelectedPath;
        };
        t.Controls.Add(btnBrowse, 2, 2);
        local.Controls.Add(t);

        var pacsBox = new GroupBox { Text = "PACS remoti", Dock = DockStyle.Fill, Padding = new Padding(10) };
        _gridPacs.AutoGenerateColumns = false;
        _gridPacs.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nome", DataPropertyName = "Name" });
        _gridPacs.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "AE Title", DataPropertyName = "Aet" });
        _gridPacs.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Host / IP", DataPropertyName = "Host" });
        _gridPacs.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Porta", DataPropertyName = "Port", FillWeight = 40 });
        _gridPacs.DataSource = _pacsBinding;
        pacsBox.Controls.Add(_gridPacs);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8),
        };
        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        var btnCancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, AutoSize = true };
        btnOk.Click += (_, _) => Apply();
        buttons.Controls.AddRange(new Control[] { btnOk, btnCancel });
        AcceptButton = btnOk; CancelButton = btnCancel;

        Controls.Add(pacsBox);
        Controls.Add(local);
        Controls.Add(buttons);
        pacsBox.BringToFront();
    }

    private void Apply()
    {
        _cfg.LocalAet = _txtAet.Text.Trim() is { Length: > 0 } aet ? aet : "DICOMWS";
        _cfg.ListenPort = (int)_numPort.Value;
        _cfg.StoragePath = _txtStorage.Text.Trim();
        _cfg.PacsList = _pacsBinding
            .Where(p => !string.IsNullOrWhiteSpace(p.Aet) && !string.IsNullOrWhiteSpace(p.Host))
            .ToList();
    }
}
