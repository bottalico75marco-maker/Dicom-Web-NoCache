using FellowOakDicom;
using FellowOakDicom.Imaging.NativeCodec;

namespace DicomWorkstation;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Registrazione servizi fo-dicom + codec nativi per la transcodifica
        new DicomSetupBuilder()
            .RegisterServices(s => s
                .AddFellowOakDicom()
                .AddTranscoderManager<NativeTranscoderManager>())
            .SkipValidation()
            .Build();

        ApplicationConfiguration.Initialize();

        var cfg = AppConfig.Load();
        CacheCrypto.EncryptNewFiles = cfg.EncryptCache;
        var store = new LocalStore(cfg.StoragePath);

        Application.Run(new MainForm(cfg, store));
    }
}
