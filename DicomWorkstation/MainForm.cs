namespace DicomWorkstation;

public class MainForm : Form
{
    private readonly AppConfig _cfg;
    private readonly LocalStore _store;

    private readonly ComboBox _cboSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly TextBox _txtName = new() { Width = 160, PlaceholderText = "Cognome^Nome" };
    private readonly TextBox _txtId = new() { Width = 120, PlaceholderText = "Patient ID" };
    private readonly CheckBox _chkDate = new() { Text = "Data:", AutoSize = true };
    private readonly DateTimePicker _dtFrom = new() { Format = DateTimePickerFormat.Short, Width = 105, Enabled = false };
    private readonly DateTimePicker _dtTo = new() { Format = DateTimePickerFormat.Short, Width = 105, Enabled = false };
    private readonly Button _btnSearch = new() { Text = "Cerca" };
    private readonly Button _btnEcho = new() { Text = "C-ECHO" };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
    };
    private readonly Button _btnRetrieve = new() { Text = "Recupera (C-MOVE)", AutoSize = true, Enabled = false };
    private readonly Button _btnView = new() { Text = "Visualizza", AutoSize = true, Enabled = false };
    private readonly Button _btnDelete = new() { Text = "Elimina da cache", AutoSize = true, Enabled = false };
    private readonly Label _lblStatus = new() { AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _lblScp = new();

    private List<StudyRecord> _results = new();

    public MainForm(AppConfig cfg, LocalStore store)
    {
        _cfg = cfg;
        _store = store;

        Text = "DICOM Workstation";
        Width = 1000; Height = 620;
        StartPosition = FormStartPosition.CenterScreen;

        BuildMenu();
        BuildLayout();
        BuildGridColumns();

        _chkDate.CheckedChanged += (_, _) =>
            _dtFrom.Enabled = _dtTo.Enabled = _chkDate.Checked;
        _btnSearch.Click += async (_, _) => await SearchAsync();
        _btnEcho.Click += async (_, _) => await EchoAsync();
        _btnRetrieve.Click += async (_, _) => await RetrieveAsync();
        _btnView.Click += (_, _) => ViewSelected();
        _btnDelete.Click += (_, _) => DeleteSelected();
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ViewSelected(); };
        _cboSource.SelectedIndexChanged += (_, _) => { _results.Clear(); BindResults(); };

        _store.Changed += () => { if (IsHandleCreated) BeginInvoke(RefreshCacheView); };
        DicomNode.Log += msg => { if (IsHandleCreated) BeginInvoke(() => _lblScp.Text = msg); };

        ReloadSources();
        RestartNode();
    }

    private void BuildMenu()
    {
        var menu = new MenuStrip();
        var tools = new ToolStripMenuItem("&Strumenti");
        var settings = new ToolStripMenuItem("&Impostazioni…", null, (_, _) => OpenSettings());
        tools.DropDownItems.Add(settings);
        menu.Items.Add(tools);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void BuildLayout()
    {
        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 6, 6, 0),
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
        };
        top.Controls.AddRange(new Control[]
        {
            new Label { Text = "Sorgente:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) },
            _cboSource,
            new Label { Text = "Paziente:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) },
            _txtName, _txtId, _chkDate, _dtFrom, _dtTo, _btnSearch, _btnEcho,
        });

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(6, 4, 6, 0),
        };
        bottom.Controls.AddRange(new Control[] { _btnView, _btnRetrieve, _btnDelete, _lblStatus });

        _statusStrip.Items.Add(_lblScp);

        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(bottom);
        Controls.Add(_statusStrip);
        _grid.BringToFront();
    }

    private void BuildGridColumns()
    {
        _grid.Columns.Add("PatientName", "Paziente");
        _grid.Columns.Add("PatientId", "ID");
        _grid.Columns.Add("StudyDate", "Data");
        _grid.Columns.Add("Description", "Descrizione");
        _grid.Columns.Add("Modalities", "Modalità");
        _grid.Columns.Add("Instances", "Immagini");
        _grid.Columns.Add("InCache", "In cache");
        _grid.Columns["Instances"]!.FillWeight = 40;
        _grid.Columns["InCache"]!.FillWeight = 40;
        _grid.Columns["Modalities"]!.FillWeight = 45;
        _grid.Columns["StudyDate"]!.FillWeight = 55;
    }

    private bool IsCacheSource => _cboSource.SelectedIndex == 0;
    private RemotePacs? SelectedPacs =>
        IsCacheSource || _cboSource.SelectedIndex < 1 ? null : _cfg.PacsList[_cboSource.SelectedIndex - 1];
    private StudyRecord? SelectedStudy =>
        _grid.SelectedRows.Count > 0 && _grid.SelectedRows[0].Index < _results.Count
            ? _results[_grid.SelectedRows[0].Index] : null;

    private void ReloadSources()
    {
        var idx = _cboSource.SelectedIndex;
        _cboSource.Items.Clear();
        _cboSource.Items.Add("Cache locale");
        foreach (var p in _cfg.PacsList) _cboSource.Items.Add(p.ToString());
        _cboSource.SelectedIndex = idx >= 0 && idx < _cboSource.Items.Count ? idx : 0;
    }

    private async Task SearchAsync()
    {
        DateTime? from = _chkDate.Checked ? _dtFrom.Value.Date : null;
        DateTime? to = _chkDate.Checked ? _dtTo.Value.Date : null;

        try
        {
            _btnSearch.Enabled = false;
            _lblStatus.Text = "Ricerca in corso…";

            if (IsCacheSource)
            {
                _results = _store.Search(_txtName.Text, _txtId.Text, from, to);
            }
            else if (SelectedPacs is { } pacs)
            {
                _results = await DicomNode.FindStudiesAsync(pacs, _txtName.Text, _txtId.Text, from, to);
            }

            BindResults();
            _lblStatus.Text = $"{_results.Count} studi trovati.";
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Errore: " + ex.Message;
        }
        finally
        {
            _btnSearch.Enabled = true;
        }
    }

    private void BindResults()
    {
        _grid.Rows.Clear();
        foreach (var s in _results)
        {
            var inCache = _store.Contains(s.StudyInstanceUid);
            _grid.Rows.Add(
                s.PatientName.Replace('^', ' ').Trim(), s.PatientId, s.StudyDateFormatted,
                s.StudyDescription, s.Modalities, s.NumInstances,
                inCache ? "✓" : "");
        }
        UpdateButtons();
    }

    private void RefreshCacheView()
    {
        if (IsCacheSource)
        {
            _results = _store.Search(_txtName.Text, _txtId.Text, null, null);
        }
        BindResults();
    }

    private void UpdateButtons()
    {
        var s = SelectedStudy;
        var inCache = s != null && _store.Contains(s.StudyInstanceUid);
        _btnView.Enabled = inCache;
        _btnDelete.Enabled = inCache;
        _btnRetrieve.Enabled = s != null && !IsCacheSource;
    }

    private async Task EchoAsync()
    {
        if (SelectedPacs is not { } pacs)
        {
            _lblStatus.Text = "Seleziona un PACS come sorgente per il C-ECHO.";
            return;
        }
        _lblStatus.Text = $"C-ECHO verso {pacs.Aet}…";
        var ok = await DicomNode.EchoAsync(pacs);
        _lblStatus.Text = ok ? $"C-ECHO verso {pacs.Aet}: OK" : $"C-ECHO verso {pacs.Aet}: FALLITO";
    }

    private async Task RetrieveAsync()
    {
        if (SelectedStudy is not { } study || SelectedPacs is not { } pacs) return;
        try
        {
            _btnRetrieve.Enabled = false;
            _lblStatus.Text = "C-MOVE in corso…";
            var status = await DicomNode.MoveStudyAsync(pacs, study.StudyInstanceUid, (done, remaining, failed) =>
            {
                BeginInvoke(() => _lblStatus.Text =
                    $"C-MOVE: {done} completate, {remaining} rimanenti, {failed} fallite");
            });
            _lblStatus.Text = $"C-MOVE terminato: {status}";
            BindResults(); // aggiorna la colonna "In cache"
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Errore C-MOVE: " + ex.Message +
                " (verificare che il PACS conosca il nostro AET/IP/porta)";
        }
        finally
        {
            _btnRetrieve.Enabled = true;
        }
    }

    private void ViewSelected()
    {
        if (SelectedStudy is not { } study || !_store.Contains(study.StudyInstanceUid)) return;
        new ViewerForm(_store, study.StudyInstanceUid).Show(this);
    }

    private void DeleteSelected()
    {
        if (SelectedStudy is not { } study) return;
        if (MessageBox.Show(this, "Eliminare lo studio dalla cache locale?", "Conferma",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _store.DeleteStudy(study.StudyInstanceUid);
        }
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm(_cfg);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _cfg.Save();
            _store.SetStoragePath(_cfg.StoragePath);
            ReloadSources();
            RestartNode();
        }
    }

    private void RestartNode()
    {
        try
        {
            DicomNode.Start(_cfg, _store);
            _lblScp.Text = $"SCP attivo — AET {_cfg.LocalAet}, porta {_cfg.ListenPort}";
        }
        catch (Exception ex)
        {
            _lblScp.Text = "SCP NON attivo: " + ex.Message;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DicomNode.Stop();
        base.OnFormClosed(e);
    }
}
