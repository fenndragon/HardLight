using Content.Shared.Shuttles.Systems;
using Content.Shared.Shuttles.Components;
using Content.Shared.Access.Components;

namespace Content.Client.Shuttles.Systems;

/// <summary>
/// Client implementation of the shuttle console lock system.
/// </summary>
public sealed class ShuttleConsoleLockSystem : SharedShuttleConsoleLockSystem
{
    /// <summary>
    /// Client implementation of TryUnlock. The actual unlock happens server-side.
    /// </summary>
    public override bool TryUnlock(EntityUid console, EntityUid idCard, ShuttleConsoleLockComponent? lockComp = null, IdCardComponent? idComp = null, EntityUid? user = null)
    {
        // Prediction only
        return false;
    }
}