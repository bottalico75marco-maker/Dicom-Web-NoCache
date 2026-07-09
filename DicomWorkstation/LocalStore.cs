using System.Text.Json;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;

namespace DicomWorkstation;

/// <summary>
/// Cache locale degli studi DICOM: file su disco + indice JSON in memoria.
/// Le istanze compresse vengono transcodificate a Explicit VR Little Endian
/// al salvataggio, così il viewer web non ha bisogno di codec JS.
/// </summary>
public class LocalStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, StudyRecord> _studies = new();
    private string _storagePath;

    public event Action? Changed;

    public LocalStore(string storagePath)
    {
        _storagePath = storagePath;
        Directory.CreateDirectory(_storagePath);
        LoadIndex();
    }

    private string IndexFile => Path.Combine(_storagePath, "index.json");

    public void SetStoragePath(string path)
    {
        lock (_lock)
        {
            if (string.Equals(path, _storagePath, StringComparison.OrdinalIgnoreCase)) return;
            _storagePath = path;
            Directory.CreateDirectory(_storagePath);
            _studies.Clear();
            LoadIndex();
        }
        Changed?.Invoke();
    }

    private void LoadIndex()
    {
        try
        {
            if (File.Exists(IndexFile))
            {
                var list = JsonSerializer.Deserialize<List<StudyRecord>>(File.ReadAllText(IndexFile));
                if (list != null)
                    foreach (var s in list)
                        _studies[s.StudyInstanceUid] = s;
            }
        }
        catch { /* indice corrotto: la cache riparte vuota, i file restano su disco */ }
    }

    private void SaveIndexNoLock()
    {
        var json = JsonSerializer.Serialize(_studies.Values.ToList());
        File.WriteAllText(IndexFile, json);
    }

    /// <summary>Aggiunge un file DICOM ricevuto (C-STORE in ingresso o import manuale).</summary>
    public void AddFile(DicomFile file)
    {
        var ds = file.Dataset;
        var studyUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "");
        var sopUid = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, "");
        if (studyUid == "" || sopUid == "") return;

        // Transcodifica a Explicit VR LE se compresso (richiede fo-dicom.codecs)
        if (file.FileMetaInfo.TransferSyntax.IsEncapsulated)
        {
            try
            {
                var transcoder = new DicomTranscoder(file.FileMetaInfo.TransferSyntax,
                                                     DicomTransferSyntax.ExplicitVRLittleEndian);
                file = transcoder.Transcode(file);
                ds = file.Dataset;
            }
            catch { /* transfer syntax non supportata: si salva l'originale */ }
        }

        lock (_lock)
        {
            var dir = Path.Combine(_storagePath, Sanitize(studyUid));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Sanitize(sopUid) + ".dcm");
            file.Save(path);

            if (!_studies.TryGetValue(studyUid, out var study))
            {
                study = new StudyRecord
                {
                    StudyInstanceUid = studyUid,
                    PatientName = ds.GetSingleValueOrDefault(DicomTag.PatientName, ""),
                    PatientId = ds.GetSingleValueOrDefault(DicomTag.PatientID, ""),
                    StudyDate = ds.GetSingleValueOrDefault(DicomTag.StudyDate, ""),
                    StudyDescription = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, ""),
                    AccessionNumber = ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, ""),
                };
                _studies[studyUid] = study;
            }

            // Evita duplicati alla ri-ricezione della stessa istanza
            study.Instances.RemoveAll(i => i.SopInstanceUid == sopUid);
            study.Instances.Add(new InstanceRecord
            {
                SeriesInstanceUid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, ""),
                SopInstanceUid = sopUid,
                SeriesDescription = ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, ""),
                Modality = ds.GetSingleValueOrDefault(DicomTag.Modality, ""),
                SeriesNumber = ds.GetSingleValueOrDefault(DicomTag.SeriesNumber, 0),
                InstanceNumber = ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0),
                FilePath = path,
            });
            study.NumInstances = study.Instances.Count;
            study.Modalities = string.Join("\\",
                study.Instances.Select(i => i.Modality).Where(m => m != "").Distinct());

            SaveIndexNoLock();
        }
        Changed?.Invoke();
    }

    /// <summary>Ricerca nella cache locale (name/id contains, range di date opzionale).</summary>
    public List<StudyRecord> Search(string? patientName, string? patientId, DateTime? from, DateTime? to)
    {
        lock (_lock)
        {
            IEnumerable<StudyRecord> q = _studies.Values;
            if (!string.IsNullOrWhiteSpace(patientName))
                q = q.Where(s => s.PatientName.Contains(patientName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(patientId))
                q = q.Where(s => s.PatientId.Contains(patientId, StringComparison.OrdinalIgnoreCase));
            if (from.HasValue)
                q = q.Where(s => string.Compare(s.StudyDate, from.Value.ToString("yyyyMMdd")) >= 0);
            if (to.HasValue)
                q = q.Where(s => s.StudyDate != "" &&
                                 string.Compare(s.StudyDate, to.Value.ToString("yyyyMMdd")) <= 0);
            return q.OrderByDescending(s => s.StudyDate).ToList();
        }
    }

    public StudyRecord? GetStudy(string studyUid)
    {
        lock (_lock) return _studies.TryGetValue(studyUid, out var s) ? s : null;
    }

    public bool Contains(string studyUid)
    {
        lock (_lock) return _studies.ContainsKey(studyUid);
    }

    public InstanceRecord? FindInstance(string sopUid)
    {
        lock (_lock)
            return _studies.Values.SelectMany(s => s.Instances)
                                  .FirstOrDefault(i => i.SopInstanceUid == sopUid);
    }

    public void DeleteStudy(string studyUid)
    {
        lock (_lock)
        {
            if (!_studies.Remove(studyUid)) return;
            try { Directory.Delete(Path.Combine(_storagePath, Sanitize(studyUid)), true); } catch { }
            SaveIndexNoLock();
        }
        Changed?.Invoke();
    }

    private static string Sanitize(string uid) =>
        string.Concat(uid.Where(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'));
}
