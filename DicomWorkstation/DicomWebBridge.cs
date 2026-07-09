using System.Collections.Specialized;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace DicomWorkstation;

/// <summary>
/// "Server web" in-process: intercetta le richieste del WebView2 verso
/// https://app.local/* e le serve dalla memoria, senza aprire porte HTTP.
///
/// Routing:
///   /app-config.js                       configurazione OHIF generata
///   /dicomweb/*                          QIDO-RS / WADO-RS (vedi DicomWebData)
///   /viewer.html, /lib/*, /api/*,
///   /instances/*                         viewer minimale legacy
///   /*                                   dist OHIF dallo zip incorporato
///                                        (fallback SPA su index.html)
/// </summary>
public class DicomWebBridge
{
    public const string Origin = "https://app.local";

    private readonly LocalStore _store;
    private readonly Assembly _asm = typeof(DicomWebBridge).Assembly;
    private readonly string[] _resourceNames;
    private SynchronizationContext? _uiContext;

    // Dist OHIF: zip incorporato, aperto una volta e letto con lock
    private static ZipArchive? _ohif;
    private static readonly object OhifLock = new();

    private record Payload(byte[] Body, int Status, string Headers);

    public DicomWebBridge(LocalStore store)
    {
        _store = store;
        _resourceNames = _asm.GetManifestResourceNames();
    }

    public void Attach(CoreWebView2 core)
    {
        _uiContext = SynchronizationContext.Current;
        core.AddWebResourceRequestedFilter($"{Origin}/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (s, e) => Handle(core.Environment, e);
    }

    private void Handle(CoreWebView2Environment env, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var deferral = e.GetDeferral();

        // Il lavoro pesante gira fuori dal thread UI; la risposta viene
        // creata sul thread UI (requisito degli oggetti CoreWebView2).
        Task.Run(() =>
        {
            Payload p;
            try { p = Route(uri); }
            catch (Exception ex)
            {
                p = new Payload(Encoding.UTF8.GetBytes(ex.ToString()), 500,
                                "Content-Type: text/plain");
            }

            _uiContext!.Post(_ =>
            {
                try
                {
                    e.Response = env.CreateWebResourceResponse(
                        new MemoryStream(p.Body), p.Status,
                        p.Status == 200 ? "OK" : "Error", p.Headers);
                }
                finally { deferral.Complete(); }
            }, null);
        });
    }

    private Payload Route(Uri uri)
    {
        var path = uri.AbsolutePath;

        if (path == "/app-config.js")
            return Text(AppConfigJs, "application/javascript");

        if (path.StartsWith("/dicomweb/"))
            return DicomWeb(path["/dicomweb".Length..],
                            System.Web.HttpUtility.ParseQueryString(uri.Query));

        // --- viewer minimale legacy ---
        if (path == "/viewer.html")
            return FromResource("viewer.html", "text/html; charset=utf-8");
        if (path.StartsWith("/lib/"))
            return FromResource(Path.GetFileName(path), "application/javascript");
        if (path == "/api/study")
            return LegacyStudyJson(System.Web.HttpUtility.ParseQueryString(uri.Query)["uid"] ?? "");
        if (path.StartsWith("/instances/"))
            return LegacyInstanceFile(Path.GetFileNameWithoutExtension(path));

        // --- dist OHIF ---
        return Ohif(path);
    }

    // ---------- OHIF static ----------

    private static ZipArchive OpenOhif(Assembly asm, string[] names)
    {
        if (_ohif != null) return _ohif;
        var resName = names.First(n => n.EndsWith("ohif-dist.zip", StringComparison.OrdinalIgnoreCase));
        var ms = new MemoryStream();
        using (var s = asm.GetManifestResourceStream(resName)!) s.CopyTo(ms);
        _ohif = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        return _ohif;
    }

    private Payload Ohif(string path)
    {
        var rel = path.TrimStart('/');
        if (rel == "") rel = "index.html";

        lock (OhifLock)
        {
            var zip = OpenOhif(_asm, _resourceNames);
            var entry = zip.GetEntry(rel);

            // Fallback SPA: percorsi di routing (senza estensione) -> index.html
            if (entry == null && !Path.HasExtension(rel))
                entry = zip.GetEntry("index.html");
            if (entry == null)
                return NotFound();

            using var s = entry.Open();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            return new Payload(ms.ToArray(), 200, $"Content-Type: {Mime(entry.Name)}");
        }
    }

    private static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "application/javascript",
        ".css" => "text/css",
        ".json" or ".map" => "application/json",
        ".wasm" => "application/wasm",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".webmanifest" => "application/manifest+json",
        _ => "application/octet-stream",
    };

    // ---------- DICOMweb ----------

    private Payload DicomWeb(string path, NameValueCollection query)
    {
        var seg = path.Trim('/').Split('/');
        // studies
        if (seg.Length == 1 && seg[0] == "studies")
            return Json(DicomWebData.QidoStudies(_store, query));
        // studies/{uid}/metadata
        if (seg.Length == 3 && seg[0] == "studies" && seg[2] == "metadata")
            return JsonOr404(DicomWebData.Metadata(_store, seg[1]));
        // studies/{uid}/series
        if (seg.Length == 3 && seg[0] == "studies" && seg[2] == "series")
            return JsonOr404(DicomWebData.QidoSeries(_store, seg[1]));
        // studies/{uid}/series/{uid}/metadata
        if (seg.Length == 5 && seg[0] == "studies" && seg[2] == "series" && seg[4] == "metadata")
            return JsonOr404(DicomWebData.Metadata(_store, seg[1], seg[3]));
        // studies/{uid}/series/{uid}/instances
        if (seg.Length == 5 && seg[0] == "studies" && seg[2] == "series" && seg[4] == "instances")
            return JsonOr404(DicomWebData.QidoInstances(_store, seg[1], seg[3]));
        // studies/{uid}/series/{uid}/instances/{uid}/frames/{n}
        if (seg.Length == 8 && seg[6] == "frames" && int.TryParse(seg[7], out var frame))
        {
            var data = DicomWebData.GetFrame(_store, seg[5], frame);
            if (data == null) return NotFound();
            var (body, contentType) = DicomWebData.BuildMultipart(data, "application/octet-stream");
            return new Payload(body, 200, $"Content-Type: {contentType}");
        }
        return NotFound();
    }

    // ---------- viewer minimale legacy ----------

    private Payload FromResource(string fileName, string contentType)
    {
        var res = _resourceNames.FirstOrDefault(n =>
            n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase) ||
            n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (res == null) return NotFound();
        using var s = _asm.GetManifestResourceStream(res)!;
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return new Payload(ms.ToArray(), 200, $"Content-Type: {contentType}");
    }

    private Payload LegacyStudyJson(string studyUid)
    {
        var study = _store.GetStudy(studyUid);
        if (study == null) return NotFound();

        var payload = new
        {
            patientName = study.PatientName.Replace('^', ' ').Trim(),
            patientId = study.PatientId,
            studyDate = study.StudyDateFormatted,
            description = study.StudyDescription,
            series = study.Instances
                .GroupBy(i => i.SeriesInstanceUid)
                .OrderBy(g => g.First().SeriesNumber)
                .Select(g => new
                {
                    uid = g.Key,
                    description = g.First().SeriesDescription,
                    modality = g.First().Modality,
                    number = g.First().SeriesNumber,
                    instances = g.OrderBy(i => i.InstanceNumber)
                                 .Select(i => $"/instances/{i.SopInstanceUid}.dcm")
                                 .ToArray(),
                }),
        };
        return Json(System.Text.Json.JsonSerializer.Serialize(payload));
    }

    private Payload LegacyInstanceFile(string sopUid)
    {
        var inst = _store.FindInstance(sopUid);
        if (inst == null || !File.Exists(inst.FilePath)) return NotFound();
        return new Payload(File.ReadAllBytes(inst.FilePath), 200,
                           "Content-Type: application/dicom");
    }

    // ---------- helpers ----------

    private static Payload Text(string s, string contentType) =>
        new(Encoding.UTF8.GetBytes(s), 200, $"Content-Type: {contentType}; charset=utf-8");
    private static Payload Json(string s) =>
        new(Encoding.UTF8.GetBytes(s), 200, "Content-Type: application/dicom+json");
    private static Payload JsonOr404(string? s) => s == null ? NotFound() : Json(s);
    private static Payload NotFound() =>
        new(Array.Empty<byte>(), 404, "Content-Type: text/plain");

    /// <summary>Configurazione OHIF: punta al DICOMweb in-process.</summary>
    private const string AppConfigJs = """
        window.config = {
          name: 'embedded',
          routerBasename: '/',
          extensions: [],
          modes: [],
          customizationService: {},
          showStudyList: true,
          maxNumberOfWebWorkers: 3,
          showWarningMessageForCrossOrigin: false,
          showCPUFallbackMessage: true,
          showLoadingIndicator: true,
          strictZSpacingForVolumeViewport: true,
          investigationalUseDialog: { option: 'never' },
          defaultDataSourceName: 'dicomweb',
          dataSources: [
            {
              namespace: '@ohif/extension-default.dataSourcesModule.dicomweb',
              sourceName: 'dicomweb',
              configuration: {
                friendlyName: 'Cache locale',
                name: 'local',
                qidoRoot: '/dicomweb',
                wadoRoot: '/dicomweb',
                wadoUriRoot: '/dicomweb',
                qidoSupportsIncludeField: false,
                supportsReject: false,
                imageRendering: 'wadors',
                thumbnailRendering: 'wadors',
                enableStudyLazyLoad: true,
                supportsFuzzyMatching: false,
                supportsWildcard: true,
                staticWado: false,
                singlepart: 'bulkdata',
                bulkDataURI: { enabled: false },
                omitQuotationForMultipartRequest: true,
              },
            },
          ],
        };
        """;
}
