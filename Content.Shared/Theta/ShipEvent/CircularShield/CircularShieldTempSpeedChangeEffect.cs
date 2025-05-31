using Content.Shared.Projectiles;
using Content.Shared.Theta.ShipEvent.Components;
using Robust.Shared.Physics.Systems;
using System.Collections.Concurrent;
using System.Linq;
using Content.Shared._Mono.SpaceArtillery;

namespace Content.Shared.Theta.ShipEvent.CircularShield;

public sealed partial class CircularShieldTempSpeedChangeEffect : CircularShieldEffect
{
    private IEntityManager _entMan = default!;
    private SharedTransformSystem _formSys = default!;
    private SharedPhysicsSystem _physSys = default!;
    private SharedCircularShieldSystem _shieldSys = default!;

    [DataField(required: true)]
    public float SpeedModifier;

    [DataField]
    public bool ProjectilesOnly = true;

    [DataField]
    public bool ProjectilePhasing = true;

    [DataField]
    public bool DestroyProjectiles = true;

    // Changed from HashSet to ConcurrentHashSet for thread safety
    private readonly ConcurrentDictionary<EntityUid, byte> _trackedUids = new();

    // These public properties allow systems to check which entities should phase
    public EntityUid ShieldEntity { get; private set; }
    public CircularShieldComponent? ShieldComponent { get; private set; }

    // Expose a thread-safe collection interface to check tracked projectiles
    public ICollection<EntityUid> TrackedProjectiles => _trackedUids.Keys;

    public override void OnShieldInit(Entity<CircularShieldComponent> shield)
    {
        _entMan = IoCManager.Resolve<IEntityManager>();
        _formSys = _entMan.System<SharedTransformSystem>();
        _physSys = _entMan.System<SharedPhysicsSystem>();
        _shieldSys = _entMan.System<SharedCircularShieldSystem>();

        ShieldEntity = shield.Owner;
        ShieldComponent = shield.Comp;
    }

    public override void OnShieldShutdown(Entity<CircularShieldComponent> shield)
    {
        foreach (var id in _trackedUids.Keys)
            RestoreVelocity(id);

        _trackedUids.Clear();
        ShieldComponent = null;
    }

    public override void OnShieldUpdate(Entity<CircularShieldComponent> shield, float time)
    {
        base.OnShieldUpdate(shield, time);
        if (_trackedUids.IsEmpty)
            return;

        // Create a copy of keys to avoid collection modification issues during iteration
        var keysToProcess = _trackedUids.Keys.ToArray();

        foreach (var trackedUid in keysToProcess)
        {
            if (!_entMan.EntityExists(trackedUid))
            {
                _trackedUids.TryRemove(trackedUid, out _);
                continue;
            }

            if (!_shieldSys.EntityInShield(shield, trackedUid, _formSys))
            {
                RestoreVelocity(trackedUid);
                _trackedUids.TryRemove(trackedUid, out _);
            }
        }
    }

    public override void OnShieldEnter(EntityUid uid, Entity<CircularShieldComponent> shield)
    {
        // Initialize entity manager if it hasn't been initialized yet
        _entMan = IoCManager.Resolve<IEntityManager>();
        _formSys = _entMan.System<SharedTransformSystem>();
        _physSys = _entMan.System<SharedPhysicsSystem>();
        _shieldSys = _entMan.System<SharedCircularShieldSystem>();

        // If the entity doesn't exist, don't process it
        if (!_entMan.EntityExists(uid))
            return;

        // Flag to determine if we should affect the projectile
        var shouldAffectProjectile = true;

        if (!ProjectilesOnly)
            return;

        // If we're only affecting projectiles, check if this entity is a projectile
        if (!_entMan.HasComponent<ProjectileComponent>(uid)
            || !_entMan.HasComponent<ShipWeaponProjectileComponent>(uid))
            return;

        // Make sure we have a valid shield entity
        if (ShieldEntity == default || !_entMan.EntityExists(ShieldEntity))
        {
            // Shield entity not properly initialized, fall back to the current shield
            ShieldEntity = shield;

            // If still invalid, skip grid checking but allow effects by default
            if (ShieldEntity == default || !_entMan.EntityExists(ShieldEntity))
                goto ApplyEffects;
        }

        try
        {
            // Get the shield's grid
            if (!_entMan.TryGetComponent(ShieldEntity, out TransformComponent? shieldTransform))
                goto ApplyEffects;

            var shieldGridUid = shieldTransform.GridUid;

            // Get the projectile's grid
            if (!_entMan.TryGetComponent(uid, out TransformComponent? projectileTransform))
                goto ApplyEffects;

            var projectileGridUid = projectileTransform.GridUid;

            // Get the shooter's grid if possible
            EntityUid? shooterGridUid = null;
            if (_entMan.HasComponent<ProjectileComponent>(uid) &&
                _entMan.TryGetComponent(uid, out ProjectileComponent? projectileComp) &&
                projectileComp.Shooter.HasValue &&
                _entMan.EntityExists(projectileComp.Shooter.Value) &&
                _entMan.TryGetComponent(projectileComp.Shooter.Value, out TransformComponent? shooterTransform))
            {
                shooterGridUid = shooterTransform.GridUid;
            }

            // Only affect projectiles that are from a different grid than the shield
            // or if the shooter is from a different grid than the shield
            bool isSameGrid = (shieldGridUid == projectileGridUid) || (shooterGridUid.HasValue && shieldGridUid == shooterGridUid);

            // If projectile is from the same grid, don't affect it
            if (isSameGrid)
                shouldAffectProjectile = false;
        }
        catch (Exception)
        {
            // If any error occurs during grid checking, we'll continue with default behavior
        }

        ApplyEffects:
        // If we should destroy projectiles and this projectile should be affected
        if (DestroyProjectiles && shouldAffectProjectile)
        {
            // Queue deletion of the projectile
            _entMan.QueueDeleteEntity(uid);
        }
        else if (shouldAffectProjectile)
        {
            // Only track projectiles for phasing if they should be affected and we're not destroying them
            if (ProjectilePhasing)
                _trackedUids.TryAdd(uid, 0); // Value is not used, just a placeholder for the concurrent dictionary

            // Apply speed change if entity is in the shield and should be affected
            if (_entMan.TryGetComponent(uid, out TransformComponent? form))
                _physSys.SetLinearVelocity(uid, _physSys.GetLinearVelocity(uid, _formSys.GetWorldPosition(form), xform: form) * SpeedModifier);
        }
    }

    private void RestoreVelocity(EntityUid uid)
    {
        if (!_entMan.TryGetComponent(uid, out TransformComponent? transform))
            return;

        var currentVel = _physSys.GetLinearVelocity(uid, _formSys.GetWorldPosition(transform), xform: transform);
        _physSys.SetLinearVelocity(uid, currentVel / SpeedModifier);

        // No need to restore velocity if the entity is deleted
    }
}
