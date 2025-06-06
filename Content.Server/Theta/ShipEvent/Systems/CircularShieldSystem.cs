using System.Linq;
using System.Numerics;
using Content.Server.Shuttles.Systems;
using Content.Shared.Physics;
using Content.Shared.Theta.ShipEvent.CircularShield;
using Robust.Server.GameObjects;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Physics.Systems;
using Content.Server.Power.Components;
using Content.Shared.DeviceLinking;
using Content.Shared.DeviceLinking.Events;
using Content.Shared.Power;
using Content.Shared.Projectiles;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared.Shuttles.Components;
using Content.Shared.Theta.ShipEvent.Components;
using Content.Shared.Theta.ShipEvent.UI;
using Content.Shared.UserInterface;
using Robust.Server.GameStates;
using Robust.Shared.Physics.Events;
using Robust.Shared.Threading;
using Content.Shared._Mono.SpaceArtillery;
using Robust.Shared.Map;

namespace Content.Server.Theta.ShipEvent.Systems;

public sealed class CircularShieldSystem : SharedCircularShieldSystem
{
    [Dependency] private readonly PhysicsSystem _physSys = default!;
    [Dependency] private readonly FixtureSystem _fixSys = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSys = default!;
    [Dependency] private readonly TransformSystem _formSys = default!;
    [Dependency] private readonly ShuttleConsoleSystem _shuttleConsole = default!;
    [Dependency] private readonly PvsOverrideSystem _pvsIgnoreSys = default!;
    [Dependency] private readonly IParallelManager _parallel = default!;
    [Dependency] private readonly CircularShieldRadarSystem _shieldRadar = default!; // Mono

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CircularShieldConsoleComponent, CircularShieldToggleMessage>(OnShieldToggle);
        SubscribeLocalEvent<CircularShieldConsoleComponent, CircularShieldChangeParametersMessage>(OnShieldChangeParams);
        SubscribeLocalEvent<CircularShieldConsoleComponent, AfterActivatableUIOpenEvent>(AfterUIOpen);

        SubscribeLocalEvent<CircularShieldConsoleComponent, ComponentInit>(OnShieldConsoleInit);
        SubscribeLocalEvent<CircularShieldComponent, ComponentShutdown>(OnShieldRemoved);
        SubscribeLocalEvent<CircularShieldComponent, PowerChangedEvent>(OnShieldPowerChanged);
        SubscribeLocalEvent<CircularShieldComponent, StartCollideEvent>(OnShieldEnter);
        SubscribeLocalEvent<CircularShieldComponent, NewLinkEvent>(OnShieldLink);
        SubscribeLocalEvent<CircularShieldComponent, AnchorStateChangedEvent>(OnShieldAnchorChanged);

        // Subscribe to entity termination directly for extra safety
        SubscribeLocalEvent<CircularShieldComponent, EntityTerminatingEvent>(OnShieldEntityTerminating);

        // Ensure shields are properly cleaned up when deleted
        EntityManager.EntityDeleted += OnEntityDeleted;
    }

    public override void Shutdown()
    {
        base.Shutdown();
        EntityManager.EntityDeleted -= OnEntityDeleted;

        // During server shutdown, forcibly clean up all shields
        var query = EntityQueryEnumerator<CircularShieldComponent>(); // Mono
        while (query.MoveNext(out var uid, out var shield))
        {
            // Force remove PVS override
            _pvsIgnoreSys.RemoveGlobalOverride(uid);

            // Clean up console binding
            if (shield.BoundConsole is { } boundConsole) // Mono
            {
                if (TryComp<CircularShieldConsoleComponent>(boundConsole, out var console))
                {
                    console.BoundShield = null;
                    Dirty(boundConsole, console);
                }
            }

            // Clear bound console reference
            shield.BoundConsole = null;

            // Find and delete any associated radar blips
            _shieldRadar.RemoveShieldRadarBlip(uid); // Mono

            // Remove shield fixture to break physics references
            _fixSys.DestroyFixture(uid, shield.ShieldFixtureId);

            // Make sure the entity transform is detached from any parent
            var xform = Transform(uid);
            if (xform.ParentUid != EntityUid.Invalid)
                _formSys.DetachEntity(uid, xform); // Mono

            // Mark entity for immediate deletion if possible
            if (TerminatingOrDeleted(uid))
                continue; // Mono

            try
            {
                Del(uid);
            }
            catch (Exception ex)
            {
                Log.Error($"Error deleting shield entity {ToPrettyString(uid)} during shutdown: {ex}");

                // Try force delete by queueing instead
                QueueDel(uid);
            }
        }
    }

    private void OnEntityDeleted(Entity<MetaDataComponent> entity)
    {
        // Check if the entity was a shield
        if (!TryComp<CircularShieldComponent>(entity.Owner, out var shield))
            return;

        // Make sure we remove the PVS override
        _pvsIgnoreSys.RemoveGlobalOverride(entity.Owner);

        // Clean up console binding to prevent circular references
        if (shield.BoundConsole is not { } boundConsole // Mono - Start
            || !TryComp<CircularShieldConsoleComponent>(boundConsole, out var console))
            return;

        console.BoundShield = null;
        Dirty(boundConsole, console); // Mono - End
    }

    public override void Update(float time)
    {
        base.Update(time);

        // Get all shields
        var shields = new List<(EntityUid Uid, CircularShieldComponent Shield)>();
        var query = EntityQueryEnumerator<CircularShieldComponent>();

        while (query.MoveNext(out var uid, out var shield))
            shields.Add((uid, shield)); // Mono

        // If there are enough shields to benefit from parallelization
        if (shields.Count > 4)
        {
            // Create parallel job for updating shields
            var job = new ShieldUpdateJob
            {
                System = this,
                Time = time,
                Shields = shields,
            };

            // Process shield updates in parallel
            _parallel.ProcessNow(job, shields.Count);
        }
        else
        {
            // Sequential update for small number of shields
            foreach (var shield in shields)
            {
                // Update shield effects
                DoShieldUpdateEffects(shield, time);

                // Update power surge dissipation
                UpdateDamageSurge(shield, time);
            }
        }
    }

    /// <summary>
    /// Updates the shield's power surge, reducing it over time.
    /// </summary>
    private void UpdateDamageSurge(Entity<CircularShieldComponent> shield, float deltaTime)
    {
        // If no surge, nothing to do
        if (shield.Comp.CurrentSurgePower <= 0f || shield.Comp.SurgeTimeRemaining <= 0f)
        {
            shield.Comp.CurrentSurgePower = 0f;
            shield.Comp.SurgeTimeRemaining = 0f;

            return;
        }

        // Decrease the surge time
        shield.Comp.SurgeTimeRemaining -= deltaTime;

        // If surge time has expired, reset the power surge
        if (shield.Comp.SurgeTimeRemaining > 0f)
            return;

        shield.Comp.CurrentSurgePower = 0f;
        shield.Comp.SurgeTimeRemaining = 0f;
        // Update power draw after surge dissipates
        UpdatePowerDraw(shield);
        TryUpdateConsoleState(shield);
    }

    /// <summary>
    /// Helper method to apply a power surge to the shield based on projectile impact
    /// </summary>
    private void ApplyShieldPowerSurge(Entity<CircularShieldComponent> shield)
    {
        // Add to current surge and reset timer
        shield.Comp.CurrentSurgePower += shield.Comp.ProjectileWattPerImpact;
        shield.Comp.SurgeTimeRemaining = shield.Comp.DamageSurgeDuration;

        // Update power draw immediately to reflect increased power usage
        UpdatePowerDraw(shield);
    }

    /// <summary>
    /// Helper method to apply a power surge to the shield based on projectile damage
    /// </summary>
    private void ApplyShieldPowerSurge(Entity<CircularShieldComponent> shield, ProjectileComponent projectile)
    {
        // Calculate power surge based on projectile damage
        var totalDamage = 0f; // Mono
        foreach (var (_, damageValue) in projectile.Damage.DamageDict)
            totalDamage += damageValue.Float();

        // Add to current surge based on damage and reset timer
        shield.Comp.CurrentSurgePower += totalDamage * shield.Comp.ProjectileWattPerImpact;
        shield.Comp.SurgeTimeRemaining = shield.Comp.DamageSurgeDuration;

        // Update power draw immediately to reflect increased power usage
        UpdatePowerDraw(shield);
    }

    private void AfterUIOpen(Entity<CircularShieldConsoleComponent> console, ref AfterActivatableUIOpenEvent args)
    {
        UpdateConsoleState(console);
    }

    private void UpdateConsoleState(EntityUid uid, CircularShieldConsoleComponent? console = null, RadarConsoleComponent? radar = null)
    {
        if (!Resolve(uid, ref console, ref radar)
            || console.BoundShield == null)
            return;

        if (!TryComp<CircularShieldComponent>(console.BoundShield, out var shield) ||
            !TryComp<TransformComponent>(console.BoundShield, out var transform))
            return;

        var shieldState = new ShieldInterfaceState
        {
            Coordinates = GetNetCoordinates(_formSys.GetMoverCoordinates(console.BoundShield.Value, transform)),
            Powered = shield.Powered,
            Enabled = shield.Enabled,
            Angle = shield.Angle,
            Width = shield.Width,
            MaxWidth = shield.MaxWidth,
            Radius = shield.Radius,
            MaxRadius = shield.MaxRadius,
            PowerDraw = shield.DesiredDraw
        };

        _uiSys.SetUiState(uid,
            CircularShieldConsoleUiKey.Key,
            new ShieldConsoleBoundsUserInterfaceState(
            _shuttleConsole.GetNavState(uid, new Dictionary<NetEntity, List<DockingPortState>>()),
            shieldState
            ));

        Dirty(uid, console);
    }

    private void OnShieldToggle(Entity<CircularShieldConsoleComponent> console, ref CircularShieldToggleMessage args)
    {
        if (console.Comp.BoundShield is not { } boundShield
        || !TryComp<CircularShieldComponent>(boundShield, out var shield)) // Mono
            return;

        // Check if the shield is anchored - only anchored shields can be enabled
        if (TryComp(boundShield, out TransformComponent? xform) && !xform.Anchored)
        {
            // Don't allow enabling an unanchored shield
            if (!shield.Enabled)
            {
                UpdateConsoleState( console);
                return;
            }
        }

        shield.Enabled = !shield.Enabled;
        UpdatePowerDraw(boundShield, shield);
        UpdateConsoleState(console);

        if (!shield.Enabled)
        {
            foreach (var effect in shield.Effects)
                effect.OnShieldShutdown((boundShield, shield));
        }

        // Radar blips are now handled by CircularShieldRadarSystem
        // which will react to changes in the shield component state

        Dirty(boundShield, shield);
    }

    private void OnShieldChangeParams(Entity<CircularShieldConsoleComponent> console, ref CircularShieldChangeParametersMessage args)
    {
        if (console.Comp.BoundShield is not { } boundShield
            || !TryComp<CircularShieldComponent>(boundShield, out var shield)
            || args.Radius > shield.MaxRadius
            || args.Width?.Degrees > shield.MaxWidth)
            return;

        shield.Angle = args.Angle ?? shield.Angle;

        // Ensure width is always set to 360 degrees for a full circle
        shield.Width = Angle.FromDegrees(360);
        shield.Radius = args.Radius ?? shield.Radius;

        UpdateShieldFixture((boundShield, shield));
        UpdatePowerDraw(boundShield, shield);

        Dirty(boundShield, shield);

        UpdateConsoleState(console);
    }

    //this is silly, but apparently sink component on shields does not contain linked sources on startup
    //while source component on consoles always does right after init
    //so subscribing to it instead of sink
    private void OnShieldConsoleInit(Entity<CircularShieldConsoleComponent> console, ref ComponentInit args)
    {
        _pvsIgnoreSys.AddGlobalOverride(console);

        if (!TryComp<DeviceLinkSourceComponent>(console, out var source)
            || source.LinkedPorts.Count == 0)
            return;

        var shieldUid = source.LinkedPorts.First().Key;
        if (!TryComp<CircularShieldComponent>(shieldUid, out var shieldComp))
            return;

        console.Comp.BoundShield = shieldUid;
        shieldComp.BoundConsole = console.Owner;

        // Always initialize with a full 360-degree circle
        shieldComp.Width = Angle.FromDegrees(360);
        UpdateShieldFixture((shieldUid, shieldComp));
        Dirty(shieldUid, shieldComp);

        // Make sure the shield is visible from a distance by adding a PVS override
        _pvsIgnoreSys.AddGlobalOverride(shieldUid);

        DoInitEffects((shieldUid, shieldComp));
    }

    private void OnShieldRemoved(Entity<CircularShieldComponent> shield, ref ComponentShutdown args)
    {
        // Remove PVS override to prevent "Attempted to send deleted entity" errors
        _pvsIgnoreSys.RemoveGlobalOverride(shield);

        // Clean up console binding to prevent circular references
        if (shield.Comp.BoundConsole is { } boundConsole)
        {
            if (TryComp<CircularShieldConsoleComponent>(boundConsole, out var console))
            {
                console.BoundShield = null;
                Dirty(boundConsole, console);
            }
        }

        // Clear bound console reference
        shield.Comp.BoundConsole = null;

        // Remove the radar blip if it exists
        _shieldRadar.RemoveShieldRadarBlip(shield);

        // Remove shield fixture to prevent physics references
        _fixSys.DestroyFixture(shield, shield.Comp.ShieldFixtureId);

        // Make sure the transform is properly detached to avoid parent-child issues
        var xform = Transform(shield);
        if (xform.ParentUid != EntityUid.Invalid)
            _formSys.DetachEntity(shield, xform);

        DoShutdownEffects(shield);
    }

    private void OnShieldPowerChanged(Entity<CircularShieldComponent> shield, ref PowerChangedEvent args)
    {
        shield.Comp.Powered = args.Powered;
        TryUpdateConsoleState(shield);

        if (!shield.Comp.Powered)
            DoShutdownEffects(shield);

        Dirty(shield);
    }

    private void OnShieldEnter(Entity<CircularShieldComponent> shield, ref StartCollideEvent args)
    {
        if (!shield.Comp.CanWork
            || args.OurFixtureId != shield.Comp.ShieldFixtureId
            || !EntityInShield(shield, args.OtherEntity, _formSys))
            return;

        // Check if the object colliding with the shield is a projectile from a different grid
        var isProjectileFromDifferentGrid = false;

        // Only do grid check for projectiles
        if (TryComp<ProjectileComponent>(args.OtherEntity, out var projectile))
        {
            // Check if the projectile has the ShipWeaponProjectile component
            if (!HasComp<ShipWeaponProjectileComponent>(args.OtherEntity))
                return;

            // Get the shield's grid
            if (TryComp(shield, out TransformComponent? shieldTransform) && shieldTransform.GridUid != null)
            {
                var shieldGridUid = shieldTransform.GridUid;

                // Get the projectile's grid and the shooter's grid
                EntityUid? projectileGridUid = null;
                EntityUid? shooterGridUid = null;

                if (TryComp(args.OtherEntity, out TransformComponent? projectileTransform))
                    projectileGridUid = projectileTransform.GridUid;

                if (!TerminatingOrDeleted(projectile.Shooter) && TryComp(projectile.Shooter, out TransformComponent? shooterTransform))
                    shooterGridUid = shooterTransform.GridUid;

                // Projectile is from a different grid if its grid or its shooter's grid differs from the shield's grid
                isProjectileFromDifferentGrid = projectileGridUid != shieldGridUid ||
                                               shooterGridUid != null && shooterGridUid != shieldGridUid;
            }

            // If projectile is from a different grid, the shield absorbs its energy
            if (isProjectileFromDifferentGrid)
                ApplyShieldPowerSurge(shield, projectile); // Apply power surge from impact based on projectile damage
        }

        // Process shield effects
        foreach (var effect in shield.Comp.Effects)
            effect.OnShieldEnter(args.OtherEntity, shield);
    }

    private void OnShieldLink(Entity<CircularShieldComponent> shield, ref NewLinkEvent args)
    {
        if (!TryComp<CircularShieldConsoleComponent>(args.Source, out var console))
            return;

        shield.Comp.BoundConsole = args.Source;
        console.BoundShield = shield.Owner;

        Dirty(shield);
        Dirty(args.Source, console);
    }

    private void UpdateShieldFixture(Entity<CircularShieldComponent> shield)
    {
        shield.Comp.Radius = Math.Max(shield.Comp.Radius, 0);
        shield.Comp.Width = Math.Max(shield.Comp.Width, Angle.FromDegrees(10));

        // Get the shield's transform and grid
        var transform = Transform(shield);
        var gridUidNullable = transform.GridUid;

        // Get the physics center of mass if available
        var centerOffset = Vector2.Zero;
        if (gridUidNullable is { } gridUid && TryComp<PhysicsComponent>(gridUid, out var physics))
            centerOffset = physics.LocalCenter;

        // Get or create the shield fixture
        var shieldFix = _fixSys.GetFixtureOrNull(shield, shield.Comp.ShieldFixtureId);
        if (shieldFix == null)
        {
            // Create a new circle shape at the center of mass
            PhysShapeCircle circle = new(shield.Comp.Radius, centerOffset);
            _fixSys.TryCreateFixture(shield, circle, shield.Comp.ShieldFixtureId, hard: false, collisionLayer: (int) CollisionGroup.BulletImpassable);
        }
        else
        {
            // Update existing fixture with new radius and center offset
            _physSys.SetRadius(shield, shield.Comp.ShieldFixtureId, shieldFix, shieldFix.Shape, shield.Comp.Radius);

            // Update the shape's position to the center of mass
            if (shieldFix.Shape is PhysShapeCircle circle)
                _physSys.SetPosition(shield, shield.Comp.ShieldFixtureId, shieldFix, circle, centerOffset);
        }

        // The radar blip is now handled by CircularShieldRadarSystem
        // We don't need to do anything with the radar blip here
        Dirty(shield);
    }

    /// <summary>
    /// Overload for convenience.
    /// </summary>
    private void UpdatePowerDraw(EntityUid uid, CircularShieldComponent shield)
    {
        UpdatePowerDraw((uid, shield));
    }

    private void UpdatePowerDraw(Entity<CircularShieldComponent> shield)
    {
        // Check if the shield is anchored - if not, ensure it stays unpowered
        var xform = Transform(shield);
        if (!xform.Anchored)
        {
            shield.Comp.Powered = false;
            shield.Comp.Enabled = false;

            if (TryComp<ApcPowerReceiverComponent>(shield, out var reciever))
            {
                reciever.PowerDisabled = true;
                Dirty(shield, reciever);
            }

            // Update console UI to reflect changes
            TryUpdateConsoleState(shield);
            return;
        }

        // Normal power draw calculation for anchored shields
        if (TryComp<ApcPowerReceiverComponent>(shield, out var receiver))
        {
            receiver.Load = shield.Comp.DesiredDraw;
            Dirty(shield, receiver);
        }
        else if (shield.Comp.DesiredDraw > 0)
            shield.Comp.Powered = false;

        // Power off shield if above the max power usage
        if (shield.Comp.DesiredDraw > shield.Comp.PowerDrawLimit)
            shield.Comp.Powered = false;
        else if (shield.Comp.DesiredDraw < shield.Comp.ResetPower)// Turn shield back on when under this power usage amount
            shield.Comp.Powered = true;

        // Update console UI if bound to display new power consumption
        TryUpdateConsoleState(shield);
    }

    private void OnShieldEntityTerminating(Entity<CircularShieldComponent> shield, ref EntityTerminatingEvent args)
    {
        // Force remove PVS override
        _pvsIgnoreSys.RemoveGlobalOverride(shield);

        // Remove the radar blip
        _shieldRadar.RemoveShieldRadarBlip(shield);

        // Clean up console binding
        if (shield.Comp.BoundConsole is not { } boundConsole)
            return;

        if (TryComp<CircularShieldConsoleComponent>(boundConsole, out var console))
        {
            shield.Comp.BoundConsole = null;
            Dirty(boundConsole, console);
        }

        // Clear reference
        shield.Comp.BoundConsole = null;
    }

    /// <summary>
    /// Handles the shield's response to being anchored or unanchored.
    /// Shields should only be powered when anchored.
    /// </summary>
    private void OnShieldAnchorChanged(Entity<CircularShieldComponent> shield, ref AnchorStateChangedEvent args)
    {
        if (!args.Anchored)
        {
            // When unanchored, disable power and set shield to inactive
            shield.Comp.Powered = false;
            shield.Comp.Enabled = false;

            // Update power draw to ensure it's properly disabled
            UpdatePowerDraw(shield);

            // If we have an ApcPowerReceiver, disable it while unanchored
            if (TryComp<ApcPowerReceiverComponent>(shield, out var powerReceiver))
            {
                powerReceiver.PowerDisabled = true;
                Dirty(shield, powerReceiver);
            }

            // Properly shut down all shield effects
            DoShutdownEffects(shield);
        }
        else
        {
            // When re-anchored, re-enable the power receiver
            if (TryComp<ApcPowerReceiverComponent>(shield, out var powerReceiver))
            {
                powerReceiver.PowerDisabled = false;
                Dirty(shield, powerReceiver);

                // We don't immediately set shield.Powered = true here
                // Instead, let the PowerChangedEvent handle that properly
                // This ensures we respect the actual power state
            }

            // Update power draw to respect circuit changes
            UpdatePowerDraw(shield);
        }

        // Update the console UI if bound to a console
        TryUpdateConsoleState(shield);
        Dirty(shield);
    }

    private bool TryUpdateConsoleState(Entity<CircularShieldComponent> shield)
    {
        if (shield.Comp.BoundConsole is not { } console)
            return false;

        UpdateConsoleState(console);
        Dirty(shield.Owner, shield.Comp);

        return true;
    }

    private sealed class ShieldUpdateJob : IParallelRobustJob
    {
        public CircularShieldSystem System = default!;
        public float Time;
        public List<(EntityUid Uid, CircularShieldComponent Shield)> Shields = default!;

        // Process shields in batches for better performance
        public int BatchSize => 4;
        public int MinimumBatchParallel => 2;

        public void Execute(int index)
        {
            if (index >= Shields.Count)
                return;

            var shield = Shields[index];

            // Update shield effects
            foreach (var effect in shield.Shield.Effects)
                effect.OnShieldUpdate(shield, Time);

            // Update power surge dissipation
            System.UpdateDamageSurge(shield, Time);
        }
    }
}
