using System.Text;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;

namespace DicomWorkstation;

/// <summary>
/// Nodo DICOM dell'applicazione: SCP C-STORE/C-ECHO in ascolto (per ricevere
/// gli studi, anche come destinazione dei C-MOVE) e funzioni SCU verso i PACS.
/// </summary>
public static class DicomNode
{
    private static IDicomServer? _server;

    /// <summary>Store condiviso con l'SCP (le istanze del servizio sono create da fo-dicom).</summary>
    public static LocalStore? Store { get; private set; }
    public static string LocalAet { get; private set; } = "DICOMWS";
    public static event Action<string>? Log;

    internal static void RaiseLog(string msg) => Log?.Invoke(msg);

    public static bool IsRunning => _server?.IsListening ?? false;

    public static void Start(AppConfig cfg, LocalStore store)
    {
        Stop();
        Store = store;
        LocalAet = cfg.LocalAet;
        _server = DicomServerFactory.Create<CStoreScp>(cfg.ListenPort);
        RaiseLog($"SCP in ascolto su porta {cfg.ListenPort} (AET {cfg.LocalAet})");
    }

    public static void Stop()
    {
        _server?.Dispose();
        _server = null;
    }

    private static IDicomClient CreateClient(RemotePacs pacs) =>
        DicomClientFactory.Create(pacs.Host, pacs.Port, false, LocalAet, pacs.Aet);

    /// <summary>C-ECHO di verifica connettività.</summary>
    public static async Task<bool> EchoAsync(RemotePacs pacs)
    {
        try
        {
            var client = CreateClient(pacs);
            var ok = false;
            var req = new DicomCEchoRequest();
            req.OnResponseReceived += (_, resp) => ok = resp.Status == DicomStatus.Success;
            await client.AddRequestAsync(req);
            await client.SendAsync();
            return ok;
        }
        catch (Exception ex)
        {
            RaiseLog($"C-ECHO fallito: {ex.Message}");
            return false;
        }
    }

    /// <summary>C-FIND a livello Study.</summary>
    public static async Task<List<StudyRecord>> FindStudiesAsync(
        RemotePacs pacs, string? patientName, string? patientId, DateTime? from, DateTime? to)
    {
        var results = new List<StudyRecord>();
        var client = CreateClient(pacs);

        var req = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);
        var ds = req.Dataset;
        ds.AddOrUpdate(DicomTag.PatientName,
            string.IsNullOrWhiteSpace(patientName) ? "" : $"*{patientName.Trim()}*");
        ds.AddOrUpdate(DicomTag.PatientID,
            string.IsNullOrWhiteSpace(patientId) ? "" : patientId.Trim());
        var dateRange = (from, to) switch
        {
            (not null, not null) => $"{from:yyyyMMdd}-{to:yyyyMMdd}",
            (not null, null) => $"{from:yyyyMMdd}-",
            (null, not null) => $"-{to:yyyyMMdd}",
            _ => ""
        };
        ds.AddOrUpdate(DicomTag.StudyDate, dateRange);
        // Return keys
        ds.AddOrUpdate(DicomTag.StudyInstanceUID, "");
        ds.AddOrUpdate(DicomTag.StudyDescription, "");
        ds.AddOrUpdate(DicomTag.AccessionNumber, "");
        ds.AddOrUpdate(DicomTag.ModalitiesInStudy, "");
        ds.AddOrUpdate(DicomTag.NumberOfStudyRelatedInstances, "");

        req.OnResponseReceived += (_, resp) =>
        {
            if (resp.Status == DicomStatus.Pending && resp.HasDataset)
            {
                var d = resp.Dataset;
                results.Add(new StudyRecord
                {
                    StudyInstanceUid = d.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, ""),
                    PatientName = d.GetSingleValueOrDefault(DicomTag.PatientName, ""),
                    PatientId = d.GetSingleValueOrDefault(DicomTag.PatientID, ""),
                    StudyDate = d.GetSingleValueOrDefault(DicomTag.StudyDate, ""),
                    StudyDescription = d.GetSingleValueOrDefault(DicomTag.StudyDescription, ""),
                    AccessionNumber = d.GetSingleValueOrDefault(DicomTag.AccessionNumber, ""),
                    Modalities = string.Join("\\",
                        d.GetValues<string>(DicomTag.ModalitiesInStudy) ?? Array.Empty<string>()),
                    NumInstances = int.TryParse(
                        d.GetSingleValueOrDefault(DicomTag.NumberOfStudyRelatedInstances, ""),
                        out var n) ? n : 0,
                });
            }
        };

        await client.AddRequestAsync(req);
        await client.SendAsync();
        return results;
    }

    /// <summary>
    /// C-MOVE di uno studio verso il nostro AET (il PACS deve avere il nostro
    /// nodo configurato come destinazione: AET, IP e porta di ascolto).
    /// Il callback riceve (completate, rimanenti, fallite).
    /// </summary>
    public static async Task<DicomStatus> MoveStudyAsync(
        RemotePacs pacs, string studyUid, Action<int, int, int>? progress)
    {
        var client = CreateClient(pacs);
        var status = DicomStatus.ProcessingFailure;
        var req = new DicomCMoveRequest(LocalAet, studyUid);
        req.OnResponseReceived += (_, resp) =>
        {
            progress?.Invoke(resp.Completed, resp.Remaining, resp.Failures);
            if (resp.Status != DicomStatus.Pending) status = resp.Status;
        };
        await client.AddRequestAsync(req);
        await client.SendAsync();
        return status;
    }
}

/// <summary>Provider SCP: accetta associazioni e salva le istanze ricevute in cache.</summary>
public class CStoreScp : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
{
    private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes =
    {
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian,
    };

    private static readonly DicomTransferSyntax[] AcceptedImageTransferSyntaxes =
    {
        // Non compressi
        DicomTransferSyntax.ExplicitVRLittleEndian,
        DicomTransferSyntax.ExplicitVRBigEndian,
        DicomTransferSyntax.ImplicitVRLittleEndian,
        // Compressi (verranno transcodificati al salvataggio)
        DicomTransferSyntax.RLELossless,
        DicomTransferSyntax.JPEGLSLossless,
        DicomTransferSyntax.JPEGLSNearLossless,
        DicomTransferSyntax.JPEGProcess1,
        DicomTransferSyntax.JPEGProcess2_4,
        DicomTransferSyntax.JPEGProcess14SV1,
        DicomTransferSyntax.JPEG2000Lossless,
        DicomTransferSyntax.JPEG2000Lossy,
    };

    public CStoreScp(INetworkStream stream, Encoding fallbackEncoding, ILogger log,
                     DicomServiceDependencies dependencies)
        : base(stream, fallbackEncoding, log, dependencies) { }

    public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
    {
        foreach (var pc in association.PresentationContexts)
        {
            if (pc.AbstractSyntax == DicomUID.Verification)
                pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
            else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None)
                pc.AcceptTransferSyntaxes(AcceptedImageTransferSyntaxes);
        }
        DicomNode.RaiseLog($"Associazione da {association.CallingAE}");
        return SendAssociationAcceptAsync(association);
    }

    public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();
    public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }
    public void OnConnectionClosed(Exception? exception) { }

    public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
    {
        try
        {
            DicomNode.Store?.AddFile(request.File);
            return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.Success));
        }
        catch (Exception ex)
        {
            DicomNode.RaiseLog($"Errore C-STORE: {ex.Message}");
            return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.ProcessingFailure));
        }
    }

    public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e) => Task.CompletedTask;

    public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request) =>
        Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
}
