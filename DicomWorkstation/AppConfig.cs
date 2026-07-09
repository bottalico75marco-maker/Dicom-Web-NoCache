using System.Text;
using System.Text.Json;

namespace DicomWorkstation;

/// <summary>Nodo PACS remoto configurabile da GUI.</summary>
public class RemotePacs
{
    public string Name { get; set; } = "";
    public string Aet { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 104;

    public override string ToString() => $"{Name} ({Aet}@{Host}:{Port})";
}

/// <summary>Configurazione applicativa, persistita in %AppData%\DicomWorkstation\config.json.</summary>
public class AppConfig
{
    public string LocalAet { get; set; } = "DICOMWS";
    public int ListenPort { get; set; } = 11112;
    public string StoragePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "DicomWorkstation", "storage");
    public List<RemotePacs> PacsList { get; set; } = new();
    /// <summary>Cifratura leggera dei nuovi file in cache (vedi CacheCrypto).</summary>
    public bool EncryptCache { get; set; }

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DicomWorkstation");
    /// <summary>File impostazioni, sempre cifrato (vedi CacheCrypto).</summary>
    private static string ConfigFile => Path.Combine(ConfigDir, "appsettings.dat");
    /// <summary>Vecchio formato in chiaro: letto per migrazione, eliminato al primo Save.</summary>
    private static string LegacyConfigFile => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = Encoding.UTF8.GetString(CacheCrypto.Unprotect(File.ReadAllBytes(ConfigFile)));
                return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            if (File.Exists(LegacyConfigFile))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(LegacyConfigFile)) ?? new AppConfig();
        }
        catch { /* config corrotta o di un altro PC: si riparte dai default */ }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllBytes(ConfigFile, CacheCrypto.Protect(Encoding.UTF8.GetBytes(json)));
        try { File.Delete(LegacyConfigFile); } catch { /* non bloccante */ }
    }
}

/// <summary>Singola istanza (immagine) presente in cache.</summary>
public class InstanceRecord
{
    public string SeriesInstanceUid { get; set; } = "";
    public string SopInstanceUid { get; set; } = "";
    public string SeriesDescription { get; set; } = "";
    public string Modality { get; set; } = "";
    public int SeriesNumber { get; set; }
    public int InstanceNumber { get; set; }
    public string FilePath { get; set; } = "";
}

/// <summary>Studio: usato sia per la cache locale sia come DTO dei risultati C-FIND.</summary>
public class StudyRecord
{
    public string StudyInstanceUid { get; set; } = "";
    public string PatientName { get; set; } = "";
    public string PatientId { get; set; } = "";
    public string StudyDate { get; set; } = "";       // yyyyMMdd
    public string StudyDescription { get; set; } = "";
    public string AccessionNumber { get; set; } = "";
    public string Modalities { get; set; } = "";
    public int NumInstances { get; set; }
    public List<InstanceRecord> Instances { get; set; } = new();

    public string StudyDateFormatted =>
        StudyDate.Length == 8 ? $"{StudyDate[6..8]}/{StudyDate[4..6]}/{StudyDate[..4]}" : StudyDate;
}
