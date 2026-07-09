# DICOM Workstation

Workstation DICOM in un singolo eseguibile Windows:

- **Nodo DICOM** (fo-dicom): C-STORE SCP in ascolto (riceve studi e fa da
  destinazione dei C-MOVE), C-ECHO / C-FIND / C-MOVE come SCU verso i PACS.
- **Cache locale** ricercabile (nome, ID paziente, range di date), con indice
  JSON e transcodifica automatica delle istanze compresse a Explicit VR LE.
- **Viewer OHIF v3 completo** (dist ufficiale, MIT) dentro un WebView2,
  servito **in-process**: la dist è incorporata nell'exe come zip e alimentata
  da un'implementazione DICOMweb minimale (QIDO-RS + WADO-RS metadata/frames)
  sulla cache locale. Nessuna porta HTTP aperta, funziona offline.
  Il viewer minimale Cornerstone resta disponibile come fallback su
  `/viewer.html?study={uid}`.
- **Configurazione a GUI**: AE title locale, porta di ascolto, cartella cache,
  lista dei PACS remoti (menu Strumenti → Impostazioni).

La logica DICOMweb (`DicomWebData.cs`) è separata dal layer WebView2 ed è
coperta dai test runtime in `../BridgeTest` (`dotnet run`): QIDO con filtri,
metadata senza PixelData, estrazione frame e formato multipart.

## Build (richiede .NET 8 SDK)

```
dotnet build
```

## Eseguibile unico (publish)

```
dotnet publish -c Release -r win-x64
```

Output in `bin/Release/net8.0-windows/win-x64/publish/DicomWorkstation.exe`
(self-contained, single-file: non richiede .NET installato sul target).

## Prerequisiti sul PC di destinazione

- **Runtime WebView2** (Edge/Chromium): preinstallato su Windows 10/11
  aggiornati. Se assente, installarlo con l'Evergreen Bootstrapper di
  Microsoft (è l'unica dipendenza esterna: un motore browser moderno non può
  stare letteralmente dentro l'exe).
- Nota: il publish single-file estrae le librerie native (codec, WebView2
  loader) in una cartella temporanea al primo avvio — comportamento standard
  di .NET, trasparente per l'utente.

## Configurazione lato PACS (importante per il C-MOVE)

Il C-MOVE funziona solo se il PACS conosce il nostro nodo come destinazione:
va registrato sul PACS l'**AE title**, l'**IP** e la **porta di ascolto**
configurati nelle Impostazioni. In alternativa si può aggiungere il supporto
C-GET (stessa associazione, niente configurazione lato PACS, ma non tutti i
PACS lo supportano).

## Struttura

| File | Ruolo |
|---|---|
| `DicomNode.cs` | SCP (C-STORE/C-ECHO) + SCU (C-ECHO/C-FIND/C-MOVE) |
| `LocalStore.cs` | Cache su disco, indice, ricerca, transcodifica |
| `DicomWebData.cs` | DICOMweb (QIDO-RS, WADO-RS metadata/frames) sulla cache |
| `DicomWebBridge.cs` | Routing in-process per il WebView2: OHIF + DICOMweb |
| `MainForm.cs` | Ricerca cache/PACS, retrieve, apertura viewer |
| `SettingsForm.cs` | Parametri DICOM a GUI |
| `ViewerForm.cs` | Finestra WebView2 (naviga su `/viewer?StudyInstanceUIDs=`) |
| `Resources/ohif-dist.zip` | Dist ufficiale OHIF v3 (estratta dall'immagine `ohif/app`) |
| `Resources/viewer.html` + `lib/` | Viewer minimale Cornerstone di fallback |

## Note su OHIF

- La dist proviene dall'immagine Docker ufficiale `ohif/app` (build con asset
  a path assoluti dalla radice, per questo OHIF è servito sulla radice di
  `https://app.local`). Per aggiornarla: estrarre `/usr/share/nginx/html`
  dall'immagine, rimuovere i duplicati `.gz`, rizippare come
  `Resources/ohif-dist.zip`.
- `app-config.js` non è nello zip: viene **generato dal bridge**
  (`DicomWebBridge.AppConfigJs`) e punta a `/dicomweb` con
  `imageRendering: 'wadors'` e `bulkDataURI` disabilitato.
- La worklist OHIF (radice) mostra il contenuto della cache locale: la QIDO
  supporta filtri per nome, ID, data, accession e StudyInstanceUID.
- Le istanze in cache sono sempre non compresse (transcodifica al C-STORE),
  quindi i frame WADO-RS sono serviti come `application/octet-stream` senza
  bisogno di codec lato browser.

## Evoluzioni naturali

- Indice **SQLite** al posto del JSON se la cache supera qualche migliaio di studi.
- C-FIND a livello Series, retention/pulizia automatica della cache.
- Multiframe: il singolo frame è già supportato; per serie multiframe grandi
  valutare lo streaming progressivo.
