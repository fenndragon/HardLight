using Robust.Shared.GameStates;
using System.Numerics;

namespace Content.Shared._Mono.Ships.Components;

/// <summary>
/// Stores the original relative position of a docked ship to its parent
/// when FTL travel begins, so it can be restored after FTL completes.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class FTLDockedPositionComponent : Component
{
    /// <summary>
    /// The main shuttle that is performing FTL.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityUid MainShuttleUid;

    /// <summary>
    /// The position relative to the main FTL shuttle before FTL began.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Vector2 RelativePosition;

    /// <summary>
    /// The rotation relative to the main FTL shuttle before FTL began.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Angle RelativeRotation;

    /// <summary>
    /// List of docking connections between this ship and others
    /// </summary>
    [DataField]
    public List<(EntityUid DockA, EntityUid DockB)> DockConnections = new();

    /// <summary>
    /// List of ships that this ship depends on for proper positioning
    /// These ships need to be positioned first
    /// </summary>
    [DataField]
    public List<EntityUid> DependsOn = new();
}
