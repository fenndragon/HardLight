using Content.Shared.Access.Components;
using Content.Shared.Interaction;
using Content.Shared.Shuttles.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Shared.Popups;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Content.Shared.Access;
using Content.Shared.Examine;
using Robust.Shared.Audio.Systems;

namespace Content.Shared.Shuttles.Systems;

/// <summary>
/// System that handles locking and unlocking shuttle consoles based on shuttle deeds.
/// </summary>
public abstract class SharedShuttleConsoleLockSystem : EntitySystem
{
    [Dependency] protected readonly SharedAppearanceSystem Appearance = default!;
    [Dependency] protected readonly SharedPopupSystem Popup = default!;
    [Dependency] protected readonly IGameTiming Timing = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShuttleConsoleLockComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<ShuttleConsoleLockComponent, ComponentStartup>(OnStartup);
    }

    private void OnStartup(EntityUid uid, ShuttleConsoleLockComponent component, ComponentStartup args)
    {
        UpdateAppearance(uid, component);
    }

    private void OnExamined(EntityUid uid, ShuttleConsoleLockComponent component, ExaminedEvent args)
    {
        args.PushMarkup(component.Locked ? Loc.GetString("shuttle-console-locked-examine") : Loc.GetString("shuttle-console-unlocked-examine"));
    }

    protected void UpdateAppearance(EntityUid uid, ShuttleConsoleLockComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!TryComp<AppearanceComponent>(uid, out var appearance))
            return;

        Appearance.SetData(uid, ShuttleConsoleLockVisuals.Locked, component.Locked, appearance);
    }

    /// <summary>
    /// Attempts to unlock a console with the given ID card
    /// </summary>
    public virtual bool TryUnlock(EntityUid console, EntityUid idCard, ShuttleConsoleLockComponent? lockComp = null, IdCardComponent? idComp = null, EntityUid? user = null)
    {
        // Implemented in client and server separately
        return false;
    }
}
