using System.Linq;
using Content.Shared.Shuttles.Systems;
using Content.Shared.Shuttles.Components;
using Content.Shared.Access.Components;
using Content.Shared._NF.Shipyard.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Hands.Systems;
using Content.Shared._NF.Shipyard;
using Content.Shared.UserInterface;
using Robust.Shared.Audio.Systems;
using Content.Shared.Verbs;
using Robust.Shared.Utility;
using Content.Server._NF.Shipyard.Components;
using Content.Shared.PDA;
using Robust.Shared.Audio;

namespace Content.Server.Shuttles.Systems;

/// <summary>
/// Server-side implementation of the shuttle console lock system.
/// </summary>
public sealed class ShuttleConsoleLockSystem : SharedShuttleConsoleLockSystem
{
    [Dependency] private readonly ShuttleConsoleSystem _consoleSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly HandsSystem _handsSystem = default!;
    [Dependency] private readonly ShuttleSystem _shuttleSystem = default!;

    /// <inheritdoc/>
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShuttleConsoleLockComponent, ComponentInit>(OnShuttleConsoleLockInit); // Subscribe to component init to handle default lock state
        SubscribeLocalEvent<ShuttleConsoleLockComponent, GetVerbsEvent<AlternativeVerb>>(AddUnlockVerb); // Add context menu verb
        SubscribeLocalEvent<ShuttleConsoleLockComponent, ActivatableUIOpenAttemptEvent>(OnUIOpenAttempt); // Keep the UI open attempt block to prevent piloting locked consoles
    }

    /// <summary>
    /// Initializes the lock component, ensuring consoles with no ShuttleId are unlocked by default
    /// </summary>
    private void OnShuttleConsoleLockInit(EntityUid uid, ShuttleConsoleLockComponent component, ComponentInit args)
    {
        // If there's no shuttle ID, the console should be unlocked
        if (string.IsNullOrEmpty(component.ShuttleId))
            component.Locked = false;
    }

    /// <summary>
    /// Adds a verb to unlock the console if the player has an ID card or voucher in hand
    /// </summary>
    private void AddUnlockVerb(EntityUid uid,
        ShuttleConsoleLockComponent component,
        GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        // Check if player has an ID card or voucher in hand
        var idCards = FindAccessibleIdCards(args.User);
        var vouchers = FindAccessibleVouchers(args.User);

        if (idCards.Count == 0 && vouchers.Count == 0)
            return;

        AlternativeVerb verb = new()
        {
            Act = () => TryToggleLock(uid, args.User, component),
            Text = component.Locked
                ? Loc.GetString("shuttle-console-verb-unlock")
                : Loc.GetString("shuttle-console-verb-lock"),
            Icon = component.Locked
                ? new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/unlock.svg.192dpi.png"))
                : new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/lock.svg.192dpi.png")),
            Priority = 10,
        };

        args.Verbs.Add(verb);
    }

    /// <summary>
    /// Tries to toggle the lock state of the console using an ID card or voucher from the player
    /// </summary>
    private void TryToggleLock(EntityUid uid, EntityUid user, ShuttleConsoleLockComponent component)
    {
        // If locked, try to unlock
        if (component.Locked)
        {
            // Handle emergency lock case first
            if (component.EmergencyLocked)
                Popup.PopupEntity(Loc.GetString("shuttle-console-emergency-locked"),
                    uid,
                    user); // For emergency mode, just show the emergency message and don't try to unlock with deeds
            //return;

            // Normal unlock procedure for non-emergency locks
            // Try each ID card the user has
            var idCards = FindAccessibleIdCards(user);
            var unlocked = idCards.Any(idCard => TryUnlock(uid, idCard, component, user: user));

            // If ID cards didn't work, try each voucher
            if (!unlocked && FindAccessibleVouchers(user).Any(voucher => TryUnlockWithVoucher(uid, voucher, component)))
                unlocked = true;

            // If we reach here and nothing worked, show error
            if (!unlocked)
                Popup.PopupEntity(Loc.GetString("shuttle-console-wrong-deed"), uid, user);
        }
        // If unlocked, try to lock it again (only works if it's your ship)
        else
        {
            // Don't allow locking if there's no shuttle ID
            if (string.IsNullOrEmpty(component.ShuttleId))
            {
                Popup.PopupEntity(Loc.GetString("shuttle-console-no-ship-id"), uid, user);
                return;
            }

            // Try ID cards first
            var idCards = FindAccessibleIdCards(user);
            var validLock = idCards.Any(idCard => TryLock(uid, idCard, component));

            // If ID cards didn't work, try vouchers
            if (!validLock && FindAccessibleVouchers(user).Any(voucher => TryLockWithVoucher(uid, voucher, component)))
                validLock = true;

            if (!validLock)
                Popup.PopupEntity(Loc.GetString("shuttle-console-cannot-lock"), uid, user);
        }
    }

    /// <summary>
    /// Tries to lock the console with the given ID card
    /// </summary>
    private bool TryLock(EntityUid console,
        EntityUid idCard,
        ShuttleConsoleLockComponent? lockComp = null,
        IdCardComponent? idComp = null)
    {
        if (!Resolve(console, ref lockComp) || !Resolve(idCard, ref idComp))
            return false;

        // If the console is already locked, do nothing
        if (lockComp.Locked)
            return false;

        // Can't lock a console without a shuttle ID
        if (string.IsNullOrEmpty(lockComp.ShuttleId))
            return false;

        // Only allow locking if this ID card has a matching deed
        var hasMatchingDeed = false;

        // Find all deed components (either on the ID or pointing to this ID)
        var query = EntityQueryEnumerator<ShuttleDeedComponent>();
        while (query.MoveNext(out var entity, out var deed))
        {
            // Check if this is for the same shuttle
            if ((entity != idCard && deed.DeedHolder != idCard)
                || deed.ShuttleUid == null
                || deed.ShuttleUid.Value.ToString() != lockComp.ShuttleId)
                continue;

            hasMatchingDeed = true;
            break;
        }

        if (!hasMatchingDeed)
            return false;

        // Success! Lock the console
        lockComp.Locked = true;
        _audio.PlayPvs(idComp.SwipeSound, console);
        Popup.PopupEntity(Loc.GetString("shuttle-console-locked-success"), console);

        // Remove any pilots
        if (!TryComp<ShuttleConsoleComponent>(console, out var shuttleComp))
            return true;

        // Clone the list to avoid modification during enumeration
        var pilots = shuttleComp.SubscribedPilots.ToList();
        foreach (var pilot in pilots)
            _consoleSystem.RemovePilot(pilot);

        return true;
    }

    /// <summary>
    /// Tries to unlock the console with the given voucher
    /// </summary>
    private bool TryUnlockWithVoucher(EntityUid console,
        EntityUid voucher,
        ShuttleConsoleLockComponent? lockComp = null)
    {
        if (!Resolve(console, ref lockComp))
            return false;

        // If the console is already unlocked, do nothing
        if (!lockComp.Locked)
            return false;

        // Can't unlock a console without a shuttle ID
        if (string.IsNullOrEmpty(lockComp.ShuttleId))
            return false;

        // Get the voucher's UID
        var voucherUid = voucher.ToString();
        var deedFound = false;
        var query = EntityQueryEnumerator<ShuttleDeedComponent>();

        while (query.MoveNext(out var entity, out var deed))
        {
            var deedShuttleId = deed.ShuttleUid?.ToString();

            // Check if this deed was purchased with this specific voucher and matches the shuttle ID
            if (!deed.PurchasedWithVoucher ||
                deed.ShuttleUid == null ||
                lockComp.ShuttleId == null ||
                deedShuttleId != lockComp.ShuttleId ||
                deed.PurchaseVoucherUid != voucherUid)
                continue;
            deedFound = true;
            Log.Debug("Found matching voucher-purchased deed for shuttle console {0}", console);
            break;
        }

        if (!deedFound)
        {
            Log.Debug("No matching voucher-purchased deed found for shuttle console {0}", console);
            _audio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_sigh.ogg"), voucher);
            return false;
        }

        // Success! Unlock the console
        Log.Debug("Successfully unlocked shuttle console {0} with voucher {1}", console, voucher);
        lockComp.Locked = false;
        _audio.PlayPvs(new SoundPathSpecifier("/Audio/Machines/id_swipe.ogg"), console);
        Popup.PopupEntity(Loc.GetString("shuttle-console-unlocked"), console);
        return true;
    }

    /// <summary>
    /// Tries to lock the console with the given voucher
    /// </summary>
    private bool TryLockWithVoucher(EntityUid console, EntityUid voucher, ShuttleConsoleLockComponent? lockComp = null)
    {
        if (!Resolve(console, ref lockComp))
            return false;

        // If the console is already locked, do nothing
        if (lockComp.Locked)
            return false;

        // Can't lock a console without a shuttle ID
        if (string.IsNullOrEmpty(lockComp.ShuttleId))
            return false;

        // Get the voucher's UID
        var voucherUid = voucher.ToString();
        var deedFound = false;
        var query = EntityQueryEnumerator<ShuttleDeedComponent>();

        while (query.MoveNext(out var entity, out var deed))
        {
            var deedShuttleId = deed.ShuttleUid?.ToString();

            // Check if this deed was purchased with this specific voucher and matches the shuttle ID
            if (!deed.PurchasedWithVoucher ||
                deed.ShuttleUid == null ||
                lockComp.ShuttleId == null ||
                deedShuttleId != lockComp.ShuttleId ||
                deed.PurchaseVoucherUid != voucherUid)
                continue;

            deedFound = true;
            Log.Debug("Found matching voucher-purchased deed for shuttle console {0}", console);
            break;
        }

        if (!deedFound)
        {
            Log.Debug("No matching voucher-purchased deed found for shuttle console {0}", console);
            _audio.PlayPvs(new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_sigh.ogg"), voucher);
            return false;
        }

        // Success! Lock the console
        Log.Debug("Successfully locked shuttle console {0} with voucher {1}", console, voucher);
        lockComp.Locked = true;
        _audio.PlayPvs(new SoundPathSpecifier("/Audio/Machines/id_swipe.ogg"), console);
        Popup.PopupEntity(Loc.GetString("shuttle-console-locked-success"), console);

        // Remove any pilots
        if (!TryComp<ShuttleConsoleComponent>(console, out var shuttleComp))
            return true;
        // Clone the list to avoid modification during enumeration
        var pilots = shuttleComp.SubscribedPilots.ToList();
        foreach (var pilot in pilots)
            _consoleSystem.RemovePilot(pilot);

        return true;
    }

    /// <summary>
    /// Prevents using the console UI if it's locked
    /// </summary>
    private void OnUIOpenAttempt(EntityUid uid,
        ShuttleConsoleLockComponent component,
        ActivatableUIOpenAttemptEvent args)
    {
        if (component.Locked)
        {
            Popup.PopupEntity(Loc.GetString("shuttle-console-locked"), uid, args.User);
            args.Cancel();
        }
    }

    /// <summary>
    /// Finds all ID cards accessible to a user (in hands or worn)
    /// </summary>
    private List<EntityUid> FindAccessibleIdCards(EntityUid user)
    {
        var results = new List<EntityUid>();

        // Check hands
        var hands = _handsSystem.EnumerateHands(user);
        foreach (var hand in hands)
        {
            if (hand.HeldEntity == null)
                continue;

            if (TryComp<IdCardComponent>(hand.HeldEntity, out _))
                results.Add(hand.HeldEntity.Value);

            if (TryComp<PdaComponent>(hand.HeldEntity, out var pdaComponent) && pdaComponent.ContainedId is not null)
                results.Add(pdaComponent.ContainedId.Value);
        }

        return results;
    }

    /// <summary>
    /// Finds all vouchers accessible to a user (in hands)
    /// </summary>
    private List<EntityUid> FindAccessibleVouchers(EntityUid user)
    {
        var results = new List<EntityUid>();

        // Check hands
        var hands = _handsSystem.EnumerateHands(user);
        foreach (var hand in hands)
        {
            if (hand.HeldEntity == null)
                continue;

            if (TryComp<ShipyardVoucherComponent>(hand.HeldEntity, out _))
                results.Add(hand.HeldEntity.Value);
        }

        return results;
    }

    /// <summary>
    /// Server-side implementation of TryUnlock
    /// </summary>
    public override bool TryUnlock(EntityUid console,
        EntityUid idCard,
        ShuttleConsoleLockComponent? lockComp = null,
        IdCardComponent? idComp = null,
        EntityUid? user = null)
    {
        if (!Resolve(console, ref lockComp) || !Resolve(idCard, ref idComp))
            return false;

        // If the console is already unlocked, do nothing
        if (!lockComp.Locked)
            return false;

        // Special handling for emergency locks - requires TSF company access
        if (lockComp.EmergencyLocked)
        {
            // Check if the ID card or user has TSF company access
            var hasTsfAccess = false;

            // Check for access tags on the ID card
            if (TryComp<AccessComponent>(idCard, out var access))
                hasTsfAccess = access.Tags.Contains("Nfsd") || access.Tags.Contains("Security"); // Check if ID has TSF or Security access

            // Success! Clear the emergency lock state
            lockComp.EmergencyLocked = false;
            lockComp.Locked = false;
            _audio.PlayPvs(idComp.SwipeSound, console);
            Popup.PopupEntity(Loc.GetString("shuttle-console-emergency-unlocked"), console);
            return true;
        }

        // If there's no shuttle ID, there's nothing to unlock against
        if (string.IsNullOrEmpty(lockComp.ShuttleId))
        {
            lockComp.Locked = false;
            return true;
        }

        // Get the ID's uid string to compare with the lock
        Log.Debug("Attempting to unlock shuttle console {0} with card {1}. Lock ID: {2}",
            console,
            idCard,
            lockComp.ShuttleId);

        // First approach: Check if this ID card IS the deed holder
        var deedFound = false;
        var deeds = new List<(EntityUid Entity, ShuttleDeedComponent Component)>();

        // Find all deed components (either on the ID or pointing to this ID)
        var query = EntityQueryEnumerator<ShuttleDeedComponent>();
        while (query.MoveNext(out var entity, out var deed))
        {
            // Case 1: The deed is on this ID card
            if (entity == idCard)
            {
                deeds.Add((entity, deed));
                Log.Debug("Found deed on ID card {0}", idCard);
            }
            // Case 2: The deed points to this ID card as its holder
            else if (deed.DeedHolder == idCard)
            {
                deeds.Add((entity, deed));
                Log.Debug("Found deed with DeedHolder {0} matching ID {1}", deed.DeedHolder, idCard);
            }
        }

        // No deeds on this ID card
        if (deeds.Count == 0)
        {
            Log.Debug("No deeds found for ID card {0}", idCard);
            _audio.PlayPvs(idComp.ErrorSound, idCard);
            return false;
        }

        // Check if any deed matches the shuttle ID
        foreach (var (_, deed) in deeds)
        {
            var deedShuttleId = deed.ShuttleUid?.ToString();

            Log.Debug("Checking deed shuttle ID {0} against lock shuttle ID {1}", deedShuttleId, lockComp.ShuttleId);

            if (deed.ShuttleUid == null ||
                lockComp.ShuttleId == null ||
                deedShuttleId != lockComp.ShuttleId)
                continue;

            deedFound = true;
            Log.Debug("Found matching deed for shuttle console {0}", console);
            break;
        }

        if (!deedFound)
        {
            Log.Debug("No matching deed found for shuttle console {0}", console);
            _audio.PlayPvs(idComp.ErrorSound, idCard);
            return false;
        }

        // Success! Unlock the console
        Log.Debug("Successfully unlocked shuttle console {0}", console);
        lockComp.Locked = false;
        _audio.PlayPvs(idComp.SwipeSound, console);
        Popup.PopupEntity(Loc.GetString("shuttle-console-unlocked"), console);
        return true;
    }

    /// <summary>
    /// Sets the shuttle ID for a console lock component.
    /// This should be called when a ship is purchased.
    /// </summary>
    public void SetShuttleId(EntityUid console, string shuttleId, ShuttleConsoleLockComponent? lockComp = null)
    {
        if (!Resolve(console, ref lockComp))
            return;

        lockComp.ShuttleId = shuttleId;

        // Only lock if there's a valid shuttle ID
        lockComp.Locked = !string.IsNullOrEmpty(shuttleId);

        // Remove any pilots when locking the console
        if (!lockComp.Locked || !TryComp<ShuttleConsoleComponent>(console, out var shuttleComp))
            return;

        // Clone the list to avoid modification during enumeration
        var pilots = shuttleComp.SubscribedPilots.ToList();
        foreach (var pilot in pilots)
            _consoleSystem.RemovePilot(pilot);

    }

    /// <summary>
    /// Sets a console into emergency locked mode
    /// </summary>
    public void SetEmergencyLock(EntityUid console, bool enabled)
    {
        var lockComp = EnsureComp<ShuttleConsoleLockComponent>(console);

        // Update existing component
        lockComp.Locked = enabled || !string.IsNullOrEmpty(lockComp.ShuttleId);
        lockComp.EmergencyLocked = enabled;

        // Handle IFF visibility
        if (Transform(console).GridUid is not { } iffVisibilityGridUid)
            return;

        HandleIff(enabled, iffVisibilityGridUid, lockComp);

        Dirty(console, lockComp);

        // Remove any pilots when locking the console
        if (!lockComp.Locked || !TryComp<ShuttleConsoleComponent>(console, out var shuttleComp))
            return;

        // Clone the list to avoid modification during enumeration
        var pilots = shuttleComp.SubscribedPilots.ToList();
        foreach (var pilot in pilots)
            _consoleSystem.RemovePilot(pilot);
    }

    private void HandleIff(bool enabled, EntityUid iffVisibilityGridUid, ShuttleConsoleLockComponent lockComp)
    {
        if (enabled)
        {
            // Save current IFF flags and make visible
            if (TryComp<IFFComponent>(iffVisibilityGridUid, out var iff))
            {
                lockComp.OriginalIFFFlags = iff.Flags;

                // Remove hiding flags
                _shuttleSystem.RemoveIFFFlag(iffVisibilityGridUid, IFFFlags.Hide | IFFFlags.HideLabel);
            }
            else
            {
                // If no IFF component exists, add one that's visible
                var iffComp = EnsureComp<IFFComponent>(iffVisibilityGridUid);
                lockComp.OriginalIFFFlags = iffComp.Flags;
            }

            return;
        }

        // Restore original flags
        if (!TryComp<IFFComponent>(iffVisibilityGridUid, out _))
            return;
        // Clear all flags first
        _shuttleSystem.RemoveIFFFlag(iffVisibilityGridUid, IFFFlags.Hide | IFFFlags.HideLabel);
        // Then restore the original flags that were hiding
        if ((lockComp.OriginalIFFFlags & IFFFlags.Hide) != 0)
            _shuttleSystem.AddIFFFlag(iffVisibilityGridUid, IFFFlags.Hide);

        if ((lockComp.OriginalIFFFlags & IFFFlags.HideLabel) != 0)
            _shuttleSystem.AddIFFFlag(iffVisibilityGridUid, IFFFlags.HideLabel);
    }
}
