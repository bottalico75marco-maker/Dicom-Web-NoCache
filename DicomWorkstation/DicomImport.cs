using FellowOakDicom;
using FellowOakDicom.Media;

namespace DicomWorkstation;

/// <summary>
/// Import di studi da supporti esterni (CD/DVD/USB o cartelle su disco).
/// Solo enumerazione dei file: il salvataggio (con transcodifica) resta in LocalStore.AddFile.
/// </summary>
public static class DicomImport
{
    /// <summary>Percorsi assoluti dei file immagine referenziati da un DICOMDIR.</summary>
    public static List<string> ReferencedFiles(string dicomdirPath)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(dicomdirPath))!;
        var dicomdir = DicomDirectory.Open(dicomdirPath);
        var result = new List<string>();
        foreach (var record in dicomdir.RootDirectoryRecordCollection)
            Collect(record, baseDir, result);
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Collect(DicomDirectoryRecord record, string baseDir, List<string> result)
    {
        // Il Referenced File ID è multi-valore: componenti di percorso relative
        // alla cartella del DICOMDIR (PS3.10 — File ID components).
        if (record.TryGetValues(DicomTag.ReferencedFileID, out string[]? parts) && parts is { Length: > 0 })
        {
            var path = Path.Combine(baseDir, Path.Combine(parts));
            if (File.Exists(path)) result.Add(path);
        }
        foreach (var lower in record.LowerLevelDirectoryRecordCollection)
            Collect(lower, baseDir, result);
    }

    /// <summary>
    /// Scansione ricorsiva di una cartella: restituisce i file con header DICOM
    /// valido (preambolo "DICM"). Il DICOMDIR stesso viene escluso.
    /// </summary>
    public static List<string> ScanFolder(string folder)
    {
        var result = new List<string>();
        foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(f), "DICOMDIR", StringComparison.OrdinalIgnoreCase))
                continue;
            try { if (DicomFile.HasValidHeader(f)) result.Add(f); }
            catch { /* file non leggibile: si salta */ }
        }
        return result;
    }
}
