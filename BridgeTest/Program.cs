using System.Collections.Specialized;
using System.Text.Json;
using DicomWorkstation;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

new DicomSetupBuilder().RegisterServices(s => s.AddFellowOakDicom()).SkipValidation().Build();

var dir = Path.Combine(Path.GetTempPath(), "dws-test-" + Guid.NewGuid().ToString("N"));
var store = new LocalStore(dir);

// --- Genera uno studio sintetico: 1 serie CT, 3 istanze 64x64 16-bit ---
var studyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
var seriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

for (int i = 1; i <= 3; i++)
{
    var ds = new DicomDataset
    {
        { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
        { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID().UID },
        { DicomTag.StudyInstanceUID, studyUid },
        { DicomTag.SeriesInstanceUID, seriesUid },
        { DicomTag.PatientName, "ROSSI^MARIO" },
        { DicomTag.PatientID, "PAT001" },
        { DicomTag.StudyDate, "20260315" },
        { DicomTag.StudyDescription, "TC Torace" },
        { DicomTag.SeriesDescription, "Assiale" },
        { DicomTag.Modality, "CT" },
        { DicomTag.SeriesNumber, 1 },
        { DicomTag.InstanceNumber, i },
        { DicomTag.Rows, (ushort)64 },
        { DicomTag.Columns, (ushort)64 },
        { DicomTag.BitsAllocated, (ushort)16 },
        { DicomTag.BitsStored, (ushort)16 },
        { DicomTag.HighBit, (ushort)15 },
        { DicomTag.PixelRepresentation, (ushort)0 },
        { DicomTag.SamplesPerPixel, (ushort)1 },
        { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
    };
    var pixels = new byte[64 * 64 * 2];
    new Random(i).NextBytes(pixels);
    var pd = DicomPixelData.Create(ds, true);
    pd.AddFrame(new MemoryByteBuffer(pixels));

    store.AddFile(new DicomFile(ds));
}

int failures = 0;
void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }

// --- QIDO studies ---
var q = new NameValueCollection { ["PatientName"] = "*ROSSI*" };
var studiesJson = DicomWebData.QidoStudies(store, q);
using (var doc = JsonDocument.Parse(studiesJson))
{
    var arr = doc.RootElement;
    Check("QIDO studies: 1 risultato", arr.GetArrayLength() == 1);
    var s = arr[0];
    Check("QIDO studies: StudyInstanceUID", s.GetProperty("0020000D").GetProperty("Value")[0].GetString() == studyUid);
    Check("QIDO studies: PatientName alfabetico", s.GetProperty("00100010").GetProperty("Value")[0].GetProperty("Alphabetic").GetString() == "ROSSI^MARIO");
    Check("QIDO studies: NumberOfStudyRelatedInstances=3", s.GetProperty("00201208").GetProperty("Value")[0].GetInt32() == 3);
    Check("QIDO studies: ModalitiesInStudy=CT", s.GetProperty("00080061").GetProperty("Value")[0].GetString() == "CT");
}

// filtro che non deve matchare
Check("QIDO studies: filtro negativo",
    JsonDocument.Parse(DicomWebData.QidoStudies(store, new NameValueCollection { ["PatientID"] = "NOPE" }))
        .RootElement.GetArrayLength() == 0);

// filtro per data
Check("QIDO studies: range date",
    JsonDocument.Parse(DicomWebData.QidoStudies(store, new NameValueCollection { ["StudyDate"] = "20260101-20261231" }))
        .RootElement.GetArrayLength() == 1);

// --- QIDO series ---
var seriesJson = DicomWebData.QidoSeries(store, studyUid)!;
using (var doc = JsonDocument.Parse(seriesJson))
{
    Check("QIDO series: 1 serie", doc.RootElement.GetArrayLength() == 1);
    Check("QIDO series: Modality CT", doc.RootElement[0].GetProperty("00080060").GetProperty("Value")[0].GetString() == "CT");
    Check("QIDO series: NumberOfSeriesRelatedInstances=3", doc.RootElement[0].GetProperty("00201209").GetProperty("Value")[0].GetInt32() == 3);
}

// --- QIDO instances ---
var instJson = DicomWebData.QidoInstances(store, studyUid, seriesUid)!;
string sopUid;
using (var doc = JsonDocument.Parse(instJson))
{
    Check("QIDO instances: 3 istanze", doc.RootElement.GetArrayLength() == 3);
    Check("QIDO instances: SOPClassUID CT", doc.RootElement[0].GetProperty("00080016").GetProperty("Value")[0].GetString() == DicomUID.CTImageStorage.UID);
    sopUid = doc.RootElement[0].GetProperty("00080018").GetProperty("Value")[0].GetString()!;
}

// --- WADO-RS metadata (serie) ---
var metaJson = DicomWebData.Metadata(store, studyUid, seriesUid)!;
using (var doc = JsonDocument.Parse(metaJson))
{
    Check("Metadata: 3 istanze", doc.RootElement.GetArrayLength() == 3);
    Check("Metadata: Rows presente", doc.RootElement[0].GetProperty("00280010").GetProperty("Value")[0].GetInt32() == 64);
    Check("Metadata: PixelData assente", !doc.RootElement[0].TryGetProperty("7FE00010", out _));
    Check("Metadata: PhotometricInterpretation", doc.RootElement[0].GetProperty("00280004").GetProperty("Value")[0].GetString() == "MONOCHROME2");
}

// --- WADO-RS frame ---
var frame = DicomWebData.GetFrame(store, sopUid, 1);
Check("Frame: dimensione 64*64*2", frame != null && frame.Length == 64 * 64 * 2);
Check("Frame: frame inesistente -> null", DicomWebData.GetFrame(store, sopUid, 99) == null);

var (body, ct) = DicomWebData.BuildMultipart(frame!, "application/octet-stream");
Check("Multipart: content-type", ct.StartsWith("multipart/related"));
Check("Multipart: corpo > payload", body.Length > frame!.Length);

// --- Ricerca cache "classica" (usata dalla MainForm) ---
Check("Store search per nome", store.Search("rossi", null, null, null).Count == 1);
Check("Store search per id", store.Search(null, "PAT001", null, null).Count == 1);

// --- Cifratura leggera della cache (CacheCrypto) ---
// Nuovo studio salvato con cifratura attiva: su disco niente "DICM",
// ma QIDO/metadata/frames devono funzionare identici (auto-rilevamento).
CacheCrypto.EncryptNewFiles = true;
var encStudyUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
var encSopUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
{
    var ds = new DicomDataset
    {
        { DicomTag.SOPClassUID, DicomUID.CTImageStorage },
        { DicomTag.SOPInstanceUID, encSopUid },
        { DicomTag.StudyInstanceUID, encStudyUid },
        { DicomTag.SeriesInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID().UID },
        { DicomTag.PatientName, "VERDI^LUIGI" },
        { DicomTag.PatientID, "PAT002" },
        { DicomTag.Modality, "CT" },
        { DicomTag.SeriesNumber, 1 },
        { DicomTag.InstanceNumber, 1 },
        { DicomTag.Rows, (ushort)64 },
        { DicomTag.Columns, (ushort)64 },
        { DicomTag.BitsAllocated, (ushort)16 },
        { DicomTag.BitsStored, (ushort)16 },
        { DicomTag.HighBit, (ushort)15 },
        { DicomTag.PixelRepresentation, (ushort)0 },
        { DicomTag.SamplesPerPixel, (ushort)1 },
        { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
    };
    var pixels = new byte[64 * 64 * 2];
    new Random(42).NextBytes(pixels);
    var pd = DicomPixelData.Create(ds, true);
    pd.AddFrame(new MemoryByteBuffer(pixels));
    store.AddFile(new DicomFile(ds));
}
CacheCrypto.EncryptNewFiles = false;

var encPath = store.FindInstance(encSopUid)!.FilePath;
var rawOnDisk = File.ReadAllBytes(encPath);
Check("Crypto: file su disco non è DICOM in chiaro",
    !(rawOnDisk.Length > 132 && rawOnDisk[128] == 'D' && rawOnDisk[129] == 'I' && rawOnDisk[130] == 'C' && rawOnDisk[131] == 'M'));
Check("Crypto: metadata leggibili dal file cifrato",
    DicomWebData.Metadata(store, encStudyUid) is { } mj &&
    JsonDocument.Parse(mj).RootElement.GetArrayLength() == 1);
var encFrame = DicomWebData.GetFrame(store, encSopUid, 1);
Check("Crypto: frame dal file cifrato", encFrame != null && encFrame.Length == 64 * 64 * 2);
Check("Crypto: ReadAllBytes decifra a DICOM valido",
    CacheCrypto.ReadAllBytes(encPath) is { } plain &&
    plain.Length > 132 && plain[128] == 'D' && plain[129] == 'I' && plain[130] == 'C' && plain[131] == 'M');
// I file in chiaro preesistenti restano leggibili (cache mista)
Check("Crypto: file in chiaro ancora leggibili", DicomWebData.GetFrame(store, sopUid, 1) != null);

Console.WriteLine(failures == 0 ? "\nTUTTI I TEST PASSANO" : $"\n{failures} TEST FALLITI");
Environment.Exit(failures);
