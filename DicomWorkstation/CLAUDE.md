# CLAUDE.md — DICOM Workstation

Brief di progetto per Claude Code. Leggere per intero prima di modificare il codice:
molte scelte non ovvie sono vincoli, non preferenze.

## Obiettivo e vincoli non negoziabili

Workstation DICOM per Windows distribuita come **UN SOLO ESEGUIBILE**.

1. **Niente Docker, niente servizi esterni, niente processi separati** (quindi
   niente Orthanc/dcm4chee come backend).
2. **Nessuna porta HTTP aperta**: il viewer web è servito in-process
   intercettando le richieste del WebView2 (`WebResourceRequested`).
   L'unica porta aperta è quella DICOM dell'SCP (è il suo mestiere).
3. Tutte le risorse web (dist OHIF, viewer di fallback, librerie JS) sono
   **embedded nell'assembly**. L'app deve funzionare offline.
4. Unica dipendenza di sistema ammessa: il **runtime WebView2** (Evergreen,
   preinstallato su Win10/11 aggiornati). Non aggiungere altre dipendenze
   native senza motivo forte.
5. Lingua della UI e dei commenti: **italiano**.

## Stack

- .NET 8, WinForms (`net8.0-windows`), C# nullable enabled
- `fo-dicom` 5.2.6 + `fo-dicom.codecs` 5.16.7 (transcodifica nativa)
- `Microsoft.Web.WebView2` 1.0.4022.49
- Viewer principale: **OHIF v3** (dist ufficiale embedded), fallback minimale
  Cornerstone 2.x
- `<EnableWindowsTargeting>true</EnableWindowsTargeting>`: il progetto **compila
  anche su Linux** (utile per CI e per i test), ma ovviamente gira solo su Windows.

## Funzionalità implementate e verificate

- C-STORE SCP in ascolto (riceve studi, fa da destinazione C-MOVE) + C-ECHO SCP
- SCU: C-ECHO, C-FIND (Study level, wildcard su PatientName, range date),
  C-MOVE con progresso
- Cache locale su disco con indice JSON, ricerca per nome/ID/date,
  eliminazione studi, colonna "in cache" nei risultati
- Transcodifica automatica a Explicit VR Little Endian al salvataggio
  (JPEG, JPEG-LS, JPEG2000, RLE → raw). Conseguenza importante: **tutto ciò
  che è in cache è non compresso**, il viewer non decodifica mai.
- DICOMweb in-process su cache: QIDO-RS (studies/series/instances con filtri),
  WADO-RS metadata (senza PixelData, con cache in RAM), WADO-RS frames
  (multipart/related, octet-stream)
- Viewer OHIF completo (worklist inclusa: la radice mostra la cache locale)
- Configurazione a GUI: AET locale, porta, cartella cache, lista PACS
  (persistita in `%AppData%\DicomWorkstation\config.json`)

Stato build: compila pulita. Test runtime della logica DICOMweb: 22/22 PASS
(`BridgeTest/`). **Non ancora testato su Windows reale**: il primo smoke test
da fare è aprire uno studio in OHIF e guardare il DevTools del WebView2
(Ctrl+Shift+I dopo aver riabilitato i menu) per eventuali 404 su `/dicomweb/*`.

## Mappa dei file

| File | Ruolo |
|---|---|
| `Program.cs` | Setup fo-dicom (`DicomSetupBuilder` + `NativeTranscoderManager`, `SkipValidation`) |
| `AppConfig.cs` | Modelli (`AppConfig`, `RemotePacs`, `StudyRecord`, `InstanceRecord`) + persistenza JSON |
| `LocalStore.cs` | Cache: salvataggio file, transcodifica, indice, ricerca. Thread-safe con lock unico |
| `DicomNode.cs` | Statico: SCP (`CStoreScp`) + SCU. Lo store è condiviso via proprietà statica perché fo-dicom istanzia lui i service |
| `DicomWebData.cs` | **Logica DICOMweb pura, zero dipendenze UI/WebView2.** Testabile su Linux |
| `DicomWebBridge.cs` | Routing `WebResourceRequested`: OHIF da zip, `app-config.js` generato, `/dicomweb/*`, viewer legacy |
| `MainForm.cs` | UI: sorgente (cache/PACS), ricerca, griglia, C-MOVE, apertura viewer |
| `SettingsForm.cs` | Parametri DICOM a GUI (copia di lavoro, applica su OK) |
| `ViewerForm.cs` | WebView2 → `https://app.local/viewer?StudyInstanceUIDs={uid}` |
| `Resources/ohif-dist.zip` | Dist OHIF v3 (embedded, ~27 MB) |
| `Resources/viewer.html`, `Resources/lib/*` | Viewer minimale di fallback (`/viewer.html?study={uid}`) |
| `../BridgeTest/` | Harness test: genera CT sintetiche, valida QIDO/metadata/frames/multipart |

Mantenere la separazione `DicomWebData` (logica) / `DicomWebBridge` (trasporto):
i test dipendono da questo.

## Decisioni tecniche e trappole note (NON regredire su questi punti)

1. **Origin fittizio `https://app.local`**: OHIF è servito sulla RADICE perché
   la dist ufficiale (estratta dall'immagine Docker `ohif/app`) ha asset con
   path assoluti (`/app.bundle.*.js`). Non spostarla sotto un prefisso senza
   ricompilare OHIF con `PUBLIC_URL` diverso.
2. **`app-config.js` è generato dal bridge** (costante `AppConfigJs` in
   `DicomWebBridge.cs`), NON sta nello zip (nell'immagine Docker è un file
   vuoto riempito a runtime). Config chiave: `routerBasename:'/'`,
   `imageRendering:'wadors'`, `bulkDataURI:{enabled:false}` (forza il
   recupero via frames), `supportsWildcard:true`.
3. **Threading WebView2**: gli oggetti CoreWebView2 vanno toccati SOLO sul
   thread UI. Il pattern in `DicomWebBridge.Handle` è: `GetDeferral()` →
   lavoro su `Task.Run` → `SynchronizationContext.Post` per creare la
   response e completare il deferral. Non "semplificare" in sincrono: le
   metadata di una serie da 500 fette congelerebbero la UI.
4. **`ZipArchive` non è thread-safe**: tutte le letture della dist OHIF
   passano dal lock `OhifLock`. Se diventa un collo di bottiglia, la
   soluzione è un `Dictionary<string, byte[]>` precaricato, non rimuovere il lock.
5. **Fallback SPA**: path senza estensione → `index.html` (routing client di
   OHIF, es. `/viewer`). Path con estensione non trovati → 404 vero.
6. **Risorse embedded "sfuse"** (viewer legacy): i nomi MSBuild sono mangled,
   il lookup è per suffisso (`FromResource`). Lo zip OHIF invece usa path
   esatti dentro l'archivio. Non mescolare i due meccanismi.
7. **C-MOVE**: funziona solo se il PACS ha registrato AET/IP/porta del nostro
   nodo. Non è un bug. L'alternativa senza configurazione lato PACS è C-GET
   (vedi roadmap).
8. **`SkipValidation()`** nel setup fo-dicom: necessario perché costruiamo
   dataset QIDO parziali e ingeriamo file dal mondo reale. Non rimuoverlo.
9. **Multipart frames**: boundary fisso, parte `application/octet-stream`,
   `omitQuotationForMultipartRequest:true` nella config OHIF. I frame sono
   sempre raw little-endian (garantito dalla transcodifica in ingresso).
10. **fo-dicom 5.x**: `DicomTranscoder` sta in `FellowOakDicom.Imaging.Codec`;
    il ctor di `DicomService` è `(INetworkStream, Encoding, ILogger,
    DicomServiceDependencies)`; il JSON DICOM si genera con
    `DicomJson.ConvertDicomToJson(dataset)` (`FellowOakDicom.Serialization`).

## Comandi

```bash
# build (funziona anche su Linux)
dotnet build

# test della logica DICOMweb (da BridgeTest/, gira su Linux)
dotnet run

# eseguibile unico (da DicomWorkstation/, su Windows o cross)
dotnet publish -c Release -r win-x64
# output: bin/Release/net8.0-windows/win-x64/publish/DicomWorkstation.exe
```

Il publish è già configurato nel csproj: self-contained, single-file,
`IncludeNativeLibrariesForSelfExtract` (i nativi vengono estratti in temp al
primo avvio: comportamento standard, non "aggiustarlo").

## Come aggiornare la dist OHIF

1. Estrarre `/usr/share/nginx/html` dall'immagine `ohif/app` (docker pull, o
   layer via registry API come fatto finora)
2. Eliminare i `*.gz` e `serve.json`
3. Rizippare il CONTENUTO (index.html alla radice dello zip) come
   `Resources/ohif-dist.zip`
4. Verificare in `index.html` che gli asset siano ancora a path assoluti e che
   `/app-config.js` sia ancora referenziato; rilanciare i test

## Roadmap suggerita (in ordine di valore)

1. **Smoke test su Windows** + fix di eventuali endpoint DICOMweb mancanti
   (aggiungerli in `DicomWebData` + routing in `DicomWebBridge` + test in `BridgeTest`)
2. **C-GET** come alternativa al C-MOVE (niente configurazione lato PACS;
   attenzione: le presentation context di storage vanno negoziate come SCP
   role nella stessa associazione)
3. **Indice SQLite** al posto del JSON oltre i ~2000 studi (introdurre
   un'interfaccia `IStudyIndex` per non toccare i consumer)
4. Retention automatica della cache (max GB / max giorni, configurabile a GUI)
5. C-FIND a livello Series; import manuale di file/cartelle DICOM (drag & drop)
6. Log su file (Serilog o simile) con finestra di log nella UI

## Regole di lavoro

- Ogni nuovo endpoint o modifica a `DicomWebData` DEVE avere un test in
  `BridgeTest/Program.cs` (pattern `Check(...)` esistente; exit code = numero
  di fallimenti)
- Prima di toccare `DicomWebBridge`, rileggere i punti 3–5 delle trappole
- Niente pacchetti NuGet nuovi senza necessità reale; niente ASP.NET/Kestrel
  (violerebbe il vincolo "nessuna porta HTTP")
- UI: WinForms costruita in codice (niente designer file); stringhe utente in italiano
