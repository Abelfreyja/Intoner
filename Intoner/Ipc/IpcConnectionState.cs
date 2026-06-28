namespace Intoner.Ipc;

public enum IpcConnectionState
{
    Unknown = 0,
    MissingPlugin = 1,
    VersionMismatch = 2,
    PluginDisabled = 3,
    NotReady = 4,
    Available = 5,
    Error = 6,
}
