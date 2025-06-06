using Content.Shared.Shuttles.Systems;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Shuttles.Components;

/// <summary>
/// Component that handles locking shuttle consoles until an ID card with the matching
/// shuttle deed is used to unlock it.
/// </summary>
[RegisterComponent, NetworkedComponent]
[Access(typeof(SharedShuttleConsoleLockSystem))]
public sealed partial class ShuttleConsoleLockComponent : Component
{
    /// <summary>
    /// Whether the console is currently locked
    /// </summary>
    [DataField]
    public bool Locked = true;

    /// <summary>
    /// Whether the console is locked due to an emergency broadcast
    /// Only corporate TSF employees can unlock this state
    /// </summary>
    [DataField]
    public bool EmergencyLocked;

    /// <summary>
    /// The ID of the shuttle this console is locked to
    /// </summary>
    [DataField]
    public string? ShuttleId;

    /// <summary>
    /// The original IFF flags before emergency lock was activated
    /// Used to restore the IFF state after emergency lockdown is cleared
    /// </summary>
    [DataField]
    public IFFFlags OriginalIFFFlags = IFFFlags.None;
}

[Serializable, NetSerializable]
public enum ShuttleConsoleLockVisuals : byte
{
    Locked,
    EmergencyLocked,
}
