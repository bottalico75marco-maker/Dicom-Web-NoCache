using System.Security.Cryptography;
using System.Text;
using FellowOakDicom;

namespace DicomWorkstation;

/// <summary>
/// Cifratura leggera e opzionale dei file in cache (AES-CBC, chiave derivata
/// dall'hostname del PC). Obiettivo: rendere i file illeggibili se copiati
/// altrove, NON resistere a un attaccante determinato — la chiave non è un
/// segreto, è protezione "da armadietto".
///
/// La lettura riconosce da sola i file cifrati (magic in testa), quindi cache
/// miste (file salvati prima/dopo l'attivazione) funzionano sempre; il flag
/// governa solo la scrittura dei nuovi file.
/// </summary>
public static class CacheCrypto
{
    /// <summary>Se true, i nuovi file in cache vengono cifrati. Da config.</summary>
    public static bool EncryptNewFiles { get; set; }

    private static readonly byte[] Magic = "DWSCRY1\0"u8.ToArray();

    private static readonly byte[] Key = SHA256.HashData(
        Encoding.UTF8.GetBytes("DicomWorkstation|" + Environment.MachineName.ToUpperInvariant()));

    // ---------- scrittura ----------

    /// <summary>Salva un DicomFile in cache, cifrandolo se la cifratura è attiva.</summary>
    public static void WriteDicom(DicomFile file, string path)
    {
        if (!EncryptNewFiles)
        {
            file.Save(path);
            return;
        }
        var ms = new MemoryStream();
        file.Save(ms);
        File.WriteAllBytes(path, Protect(ms.ToArray()));
    }

    // ---------- lettura (auto-rilevamento) ----------

    /// <summary>Apre un file DICOM di cache, decifrando al volo se necessario.</summary>
    public static DicomFile OpenDicom(string path, FileReadOption option)
    {
        if (!HasMagic(path))
            return DicomFile.Open(path, option);
        var plain = Unprotect(File.ReadAllBytes(path));
        return DicomFile.Open(new MemoryStream(plain));
    }

    /// <summary>Byte del file DICOM in chiaro (per servirlo così com'è al viewer).</summary>
    public static byte[] ReadAllBytes(string path) => Unprotect(File.ReadAllBytes(path));

    // ---------- primitive ----------

    private static bool HasMagic(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using var fs = File.OpenRead(path);
        return fs.Read(head) == head.Length && head.SequenceEqual(Magic);
    }

    private static bool IsProtected(byte[] raw) =>
        raw.Length > Magic.Length + 16 && raw.AsSpan(0, Magic.Length).SequenceEqual(Magic);

    /// <summary>Cifra un blob (magic + IV + AES-CBC). Usato per cache e impostazioni.</summary>
    public static byte[] Protect(byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var cipher = enc.TransformFinalBlock(plain, 0, plain.Length);

        var result = new byte[Magic.Length + aes.IV.Length + cipher.Length];
        Magic.CopyTo(result, 0);
        aes.IV.CopyTo(result, Magic.Length);
        cipher.CopyTo(result, Magic.Length + aes.IV.Length);
        return result;
    }

    /// <summary>Decifra un blob se cifrato, altrimenti lo restituisce invariato.</summary>
    public static byte[] Unprotect(byte[] raw)
    {
        if (!IsProtected(raw)) return raw;
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.IV = raw.AsSpan(Magic.Length, 16).ToArray();
        using var dec = aes.CreateDecryptor();
        var offset = Magic.Length + 16;
        return dec.TransformFinalBlock(raw, offset, raw.Length - offset);
    }
}
