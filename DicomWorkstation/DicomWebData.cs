using System.Collections.Specialized;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Serialization;

namespace DicomWorkstation;

/// <summary>
/// Implementazione minimale di DICOMweb (QIDO-RS + WADO-RS metadata/frames)
/// sopra la cache locale, sufficiente per alimentare OHIF.
/// Nessuna dipendenza da WebView2: testabile in isolamento.
/// </summary>
public static class DicomWebData
{
    /// <summary>Cache dei dataset "metadata" (senza PixelData) per non rileggere i file.</summary>
    private static readonly Dictionary<string, DicomDataset> MetaCache = new();
    private static readonly object CacheLock = new();
    private const int MetaCacheLimit = 5000;

    // ---------- QIDO-RS ----------

    /// <summary>GET /studies — ricerca studi con filtri QIDO di base.</summary>
    public static string QidoStudies(LocalStore store, NameValueCollection query)
    {
        var name = StripWildcards(query["PatientName"] ?? query["00100010"]);
        var pid = StripWildcards(query["PatientID"] ?? query["00100020"]);
        var studyUidFilter = query["StudyInstanceUID"] ?? query["0020000D"];
        var accession = StripWildcards(query["AccessionNumber"] ?? query["00080050"]);
        (DateTime? from, DateTime? to) = ParseDateRange(query["StudyDate"] ?? query["00080020"]);

        IEnumerable<StudyRecord> studies = store.Search(name, pid, from, to);
        if (!string.IsNullOrEmpty(studyUidFilter))
        {
            var uids = studyUidFilter.Split(',');
            studies = studies.Where(s => uids.Contains(s.StudyInstanceUid));
        }
        if (!string.IsNullOrEmpty(accession))
            studies = studies.Where(s =>
                s.AccessionNumber.Contains(accession, StringComparison.OrdinalIgnoreCase));

        int offset = int.TryParse(query["offset"], out var o) ? o : 0;
        int limit = int.TryParse(query["limit"], out var l) ? l : 101;

        var items = studies.Skip(offset).Take(limit).Select(StudyToDataset);
        return ToJsonArray(items);
    }

    /// <summary>GET /studies/{uid}/series</summary>
    public static string? QidoSeries(LocalStore store, string studyUid)
    {
        var study = store.GetStudy(studyUid);
        if (study == null) return null;

        var items = study.Instances
            .GroupBy(i => i.SeriesInstanceUid)
            .Select(g =>
            {
                var first = g.First();
                var ds = new DicomDataset
                {
                    { DicomTag.StudyInstanceUID, studyUid },
                    { DicomTag.SeriesInstanceUID, g.Key },
                    { DicomTag.Modality, Or(first.Modality, "OT") },
                    { DicomTag.SeriesNumber, first.SeriesNumber },
                    { DicomTag.SeriesDescription, first.SeriesDescription },
                    { DicomTag.NumberOfSeriesRelatedInstances, g.Count() },
                };
                return ds;
            });
        return ToJsonArray(items);
    }

    /// <summary>GET /studies/{uid}/series/{uid}/instances</summary>
    public static string? QidoInstances(LocalStore store, string studyUid, string seriesUid)
    {
        var study = store.GetStudy(studyUid);
        if (study == null) return null;

        var items = study.Instances
            .Where(i => i.SeriesInstanceUid == seriesUid)
            .OrderBy(i => i.InstanceNumber)
            .Select(i =>
            {
                var meta = GetMetadata(i);
                var ds = new DicomDataset
                {
                    { DicomTag.StudyInstanceUID, studyUid },
                    { DicomTag.SeriesInstanceUID, seriesUid },
                    { DicomTag.SOPInstanceUID, i.SopInstanceUid },
                    { DicomTag.SOPClassUID, meta.GetSingleValueOrDefault(DicomTag.SOPClassUID, "") },
                    { DicomTag.InstanceNumber, i.InstanceNumber },
                };
                return ds;
            });
        return ToJsonArray(items);
    }

    // ---------- WADO-RS metadata ----------

    /// <summary>GET /studies/{uid}/metadata oppure /studies/{uid}/series/{uid}/metadata</summary>
    public static string? Metadata(LocalStore store, string studyUid, string? seriesUid = null)
    {
        var study = store.GetStudy(studyUid);
        if (study == null) return null;

        var instances = study.Instances
            .Where(i => seriesUid == null || i.SeriesInstanceUid == seriesUid)
            .OrderBy(i => i.SeriesNumber).ThenBy(i => i.InstanceNumber);

        return ToJsonArray(instances.Select(GetMetadata));
    }

    // ---------- WADO-RS frames ----------

    /// <summary>
    /// GET /studies/../instances/{sop}/frames/{n} (1-based).
    /// Ritorna i byte grezzi del frame (le istanze in cache sono non compresse).
    /// </summary>
    public static byte[]? GetFrame(LocalStore store, string sopUid, int frameNumber)
    {
        var inst = store.FindInstance(sopUid);
        if (inst == null || !File.Exists(inst.FilePath)) return null;

        var file = DicomFile.Open(inst.FilePath, FileReadOption.ReadLargeOnDemand);
        var pixelData = DicomPixelData.Create(file.Dataset);
        if (frameNumber < 1 || frameNumber > pixelData.NumberOfFrames) return null;
        IByteBuffer frame = pixelData.GetFrame(frameNumber - 1);
        return frame.Data;
    }

    /// <summary>Corpo multipart/related per la risposta frames. Ritorna (body, contentType).</summary>
    public static (byte[] Body, string ContentType) BuildMultipart(byte[] payload, string partType)
    {
        const string boundary = "DICOMWEB_PART_BOUNDARY";
        var head = Encoding.ASCII.GetBytes(
            $"--{boundary}\r\nContent-Type: {partType}\r\nContent-Length: {payload.Length}\r\n\r\n");
        var tail = Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n");

        var body = new byte[head.Length + payload.Length + tail.Length];
        Buffer.BlockCopy(head, 0, body, 0, head.Length);
        Buffer.BlockCopy(payload, 0, body, head.Length, payload.Length);
        Buffer.BlockCopy(tail, 0, body, head.Length + payload.Length, tail.Length);

        return (body, $"multipart/related; type=\"{partType}\"; boundary={boundary}");
    }

    // ---------- Helpers ----------

    /// <summary>Dataset dell'istanza senza PixelData, con cache in memoria.</summary>
    private static DicomDataset GetMetadata(InstanceRecord inst)
    {
        lock (CacheLock)
            if (MetaCache.TryGetValue(inst.SopInstanceUid, out var cached))
                return cached;

        var ds = DicomFile.Open(inst.FilePath, FileReadOption.SkipLargeTags).Dataset.Clone();
        ds.Remove(DicomTag.PixelData);

        lock (CacheLock)
        {
            if (MetaCache.Count > MetaCacheLimit) MetaCache.Clear();
            MetaCache[inst.SopInstanceUid] = ds;
        }
        return ds;
    }

    private static DicomDataset StudyToDataset(StudyRecord s)
    {
        var ds = new DicomDataset
        {
            { DicomTag.StudyInstanceUID, s.StudyInstanceUid },
            { DicomTag.PatientName, s.PatientName },
            { DicomTag.PatientID, s.PatientId },
            { DicomTag.StudyDate, s.StudyDate },
            { DicomTag.StudyTime, "" },
            { DicomTag.AccessionNumber, s.AccessionNumber },
            { DicomTag.StudyDescription, s.StudyDescription },
            { DicomTag.ReferringPhysicianName, "" },
            { DicomTag.PatientBirthDate, "" },
            { DicomTag.PatientSex, "" },
            { DicomTag.StudyID, "" },
            { DicomTag.NumberOfStudyRelatedInstances, s.NumInstances },
            {
                DicomTag.NumberOfStudyRelatedSeries,
                s.Instances.Select(i => i.SeriesInstanceUid).Distinct().Count()
            },
        };
        var modalities = s.Modalities.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        ds.Add(DicomTag.ModalitiesInStudy, modalities.Length > 0 ? modalities : new[] { "OT" });
        return ds;
    }

    private static string ToJsonArray(IEnumerable<DicomDataset> items) =>
        "[" + string.Join(",", items.Select(ds => DicomJson.ConvertDicomToJson(ds))) + "]";

    private static string? StripWildcards(string? v) =>
        string.IsNullOrWhiteSpace(v) ? null : v.Replace("*", "").Replace("?", "").Trim();

    private static string Or(string a, string b) => string.IsNullOrEmpty(a) ? b : a;

    private static (DateTime?, DateTime?) ParseDateRange(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return (null, null);
        static DateTime? P(string s) =>
            DateTime.TryParseExact(s, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var d) ? d : null;

        var parts = v.Split('-');
        return parts.Length == 2 ? (P(parts[0]), P(parts[1])) : (P(v), P(v));
    }
}
