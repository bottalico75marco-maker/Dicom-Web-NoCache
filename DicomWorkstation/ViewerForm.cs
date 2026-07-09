using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DicomWorkstation;

/// <summary>Finestra viewer: WebView2 che carica il viewer incorporato via bridge in-process.</summary>
public class ViewerForm : Form
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly LocalStore _store;
    private readonly string _studyUid;

    public ViewerForm(LocalStore store, string studyUid)
    {
        _store = store;
        _studyUid = studyUid;
        Text = "Viewer";
        Width = 1100; Height = 750;
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            // Cartella dati utente del runtime WebView2 (cache del browser)
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DicomWorkstation", "webview2");
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _web.EnsureCoreWebView2Async(env);

            new DicomWebBridge(_store).Attach(_web.CoreWebView2);

            // Diagnostica in bridge.log: esito navigazione, crash del renderer,
            // console ed eccezioni JS (via DevTools protocol).
            DicomWebBridge.LogLine($"--- viewer aperto, studio {_studyUid} ---");
            _web.CoreWebView2.NavigationCompleted += (_, a) =>
                DicomWebBridge.LogLine($"navigazione: success={a.IsSuccess} err={a.WebErrorStatus}");
            _web.CoreWebView2.ProcessFailed += (_, a) =>
                DicomWebBridge.LogLine($"processo WebView2 fallito: {a.ProcessFailedKind}");
            try
            {
                await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
                _web.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled")
                    .DevToolsProtocolEventReceived += (_, a) =>
                        DicomWebBridge.LogLine("console: " + a.ParameterObjectAsJson);
                _web.CoreWebView2.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown")
                    .DevToolsProtocolEventReceived += (_, a) =>
                        DicomWebBridge.LogLine("eccezione JS: " + a.ParameterObjectAsJson);
            }
            catch { /* la diagnostica non deve bloccare il viewer */ }

            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            // Viewer OHIF completo; il viewer minimale resta su /viewer.html?study={uid}
            _web.CoreWebView2.Navigate(
                $"{DicomWebBridge.Origin}/viewer?StudyInstanceUIDs={Uri.EscapeDataString(_studyUid)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Impossibile inizializzare WebView2. Verificare che il runtime WebView2 " +
                "sia installato (preinstallato su Windows 10/11 aggiornati).\n\n" + ex.Message,
                "Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }
}
