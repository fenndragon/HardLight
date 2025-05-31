using Robust.Shared.GameStates;

namespace Content.Shared._Mono.Ships;

/// <summary>
/// A component that enhances a shuttle's FTL range.
/// </summary>
[RegisterComponent]
[NetworkedComponent]
[AutoGenerateComponentState]
public sealed partial class FTLDriveComponent : Component
{
    /// <summary>
    /// The maximum FTL range this drive can achieve.
    /// </summary>
    [DataField]
    [AutoNetworkedField]
    public float Range = 512f;

    /// <summary>
    /// The FTL drive's cooldown between jumps.
    /// </summary>
    [DataField]
    [AutoNetworkedField]
    public float Cooldown = 10f;


    /// <summary>
    /// The FTL jump duration.
    /// </summary>
    [DataField]
    [AutoNetworkedField]
    public float HyperSpaceTime = 20f;

    /// <summary>
    /// The FTL duration until the jump starts.
    /// </summary>
    [DataField]
    [AutoNetworkedField]
    public float StartupTime = 5.5f;
}
