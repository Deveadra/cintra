namespace MeetingCompanion.Platform;

/// <summary>CreationFileTime is the Windows process creation FILETIME, not a wall-clock estimate.</summary>
public sealed record ProcessIdentity(int ProcessId, long CreationFileTime, string ExecutablePath);
public sealed record ProcessObservation(int ProcessId, int ParentProcessId, string ExecutableName,
    ProcessIdentity? Identity, long? WindowHandle, string? DiagnosticCode = null);
public enum RenderSessionState { Inactive, Active, Expired }
public sealed record RenderSession(string EndpointId, string SessionId, ProcessIdentity? Process,
    RenderSessionState State, bool MultiProcess, bool SystemSounds);
public sealed record AudioEndpoint(string Id, string DisplayName, bool IsCapture);
public sealed record CallSourceInventory(int WindowsBuild, bool ProcessLoopbackSupported,
    ProcessObservation[] Processes, AudioEndpoint[] Endpoints, RenderSession[] Sessions,
    string[] Diagnostics, bool ProcessesComplete = true)
{
    public bool AudioInventoryComplete => Diagnostics.Length == 0;
}

/// <summary>Discovery is read-only. It must not capture audio, launch apps or read window titles.</summary>
public interface ICallSourceInventory
{
    Task<CallSourceInventory> ReadAsync(CancellationToken cancellationToken);
    IProcessIdentityLease Pin(ProcessIdentity identity);
}

/// <summary>Keep this lease until native capture has stopped, including its startup handshake.</summary>
public interface IProcessIdentityLease : IDisposable
{
    bool IsAlive { get; }
}
