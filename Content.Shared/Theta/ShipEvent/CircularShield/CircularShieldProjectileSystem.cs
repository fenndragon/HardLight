using Content.Shared.Projectiles;
using Content.Shared.Theta.ShipEvent.Components;
using Robust.Shared.Threading;
using Robust.Shared.Timing;
using System.Collections.Concurrent;
using System.Linq;

namespace Content.Shared.Theta.ShipEvent.CircularShield;

/// <summary>
/// This system handles projectile phasing for circular shields without
/// creating duplicate subscriptions to the PreventCollideEvent.
/// </summary>
public sealed class CircularShieldProjectileSystem : EntitySystem
{
    [Dependency] private readonly SharedCircularShieldSystem _shieldSys = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IParallelManager _parallel = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;

    // Cache active shields to avoid re-querying every projectile check
    private List<(EntityUid ShieldUid, CircularShieldComponent Shield)> _activeShields = new();
    private TimeSpan _lastShieldUpdate;
    private const float ShieldCacheUpdateInterval = 0.2f; // Update shield cache every 200ms

    public override void Initialize()
    {
        base.Initialize();

        // Subscribe to the ProjectileCollisionAttemptEvent to check if the projectile should phase through entities
        SubscribeLocalEvent<ProjectileCollisionAttemptEvent>(OnProjectileCollisionAttempt);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Update active shield cache periodically
        var now = _gameTiming.CurTime;
        if ((now - _lastShieldUpdate).TotalSeconds > ShieldCacheUpdateInterval)
        {
            UpdateActiveShields();
            _lastShieldUpdate = now;
        }
    }

    /// <summary>
    /// Updates the cache of active shields with shield effects
    /// </summary>
    private void UpdateActiveShields()
    {
        _activeShields.Clear();
        var query = EntityQueryEnumerator<TransformComponent, CircularShieldComponent>();

        while (query.MoveNext(out var shieldUid, out _, out var shield))
        {
            if (shield.Effects.Count == 0 || !shield.CanWork)
                continue;

            var hasPhaseEffect = shield.Effects.Any(e => e is CircularShieldTempSpeedChangeEffect speedEffect && speedEffect.ProjectilePhasing);

            if (hasPhaseEffect)
                _activeShields.Add((shieldUid, shield));
            
        }
    }

    /// <summary>
    /// Handles the ProjectileCollisionAttemptEvent and cancels the collision if the projectile should phase through entities
    /// </summary>
    private void OnProjectileCollisionAttempt(ref ProjectileCollisionAttemptEvent args)
    {
        if (ShouldProjectilePhase(args.Projectile))
            args.Cancelled = true;
    }

    /// <summary>
    /// Checks if a projectile should phase through entities based on shield effects
    /// </summary>
    public bool ShouldProjectilePhase(EntityUid projectileUid)
    {
        // Don't do anything if the entity is not a projectile
        if (!HasComp<ProjectileComponent>(projectileUid))
            return false;

        // For single shield scenarios or empty shield lists, use the sequential approach
        if (_activeShields.Count <= 1)
            return CheckShieldsSequential(projectileUid);

        // For multiple shields, try to parallelize the check
        return CheckShieldsParallel(projectileUid);
    }

    /// <summary>
    /// Sequential implementation for checking if a projectile should phase
    /// </summary>
    private bool CheckShieldsSequential(EntityUid projectileUid)
    {
        foreach (var shield in _activeShields)
        {
            // Check if the projectile is in the shield range based on grid center
            if (!_shieldSys.EntityInShield(shield, projectileUid, _transform))
                continue;

            foreach (var effect in shield.Shield.Effects)
            {
                if (effect is not CircularShieldTempSpeedChangeEffect { ProjectilePhasing: true } speedEffect)
                    continue;

                // First check if the projectile is already in the tracked list
                var wasAlreadyTracked = speedEffect.TrackedProjectiles.Contains(projectileUid);

                // If projectile is not in tracked list yet, try to add it through OnShieldEnter
                if (!wasAlreadyTracked)
                    _shieldSys.DoEnterEffects(shield, projectileUid);

                // NOW check if the projectile was actually added to the tracked list
                // This handles the case where OnShieldEnter decided not to track it
                // (like if it's from the same grid)
                if (speedEffect.TrackedProjectiles.Contains(projectileUid))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parallel implementation for checking if a projectile should phase
    /// </summary>
    private bool CheckShieldsParallel(EntityUid projectileUid)
    {
        // For small shield counts, parallelization might not be worth it
        if (_activeShields.Count < 4)
            return CheckShieldsSequential(projectileUid);

        var result = new ConcurrentBag<bool>();

        // Create a parallel job to process shield checks
        var job = new ShieldCheckJob
        {
            System = this,
            ShieldSys = _shieldSys,
            Transform = _transform,
            ProjectileUid = projectileUid,
            Shields = _activeShields,
            Result = result,
        };

        // Process the job now, blocking until complete
        _parallel.ProcessNow(job, _activeShields.Count);

        // If any shield returned true, the projectile should phase
        return result.Any(r => r);
    }

    /// <summary>
    /// Job for parallel processing of shield checks
    /// </summary>
    private class ShieldCheckJob : IParallelRobustJob
    {
        public CircularShieldProjectileSystem System = default!;
        public SharedCircularShieldSystem ShieldSys = default!;
        public SharedTransformSystem Transform = default!;
        public EntityUid ProjectileUid;
        public List<(EntityUid ShieldUid, CircularShieldComponent Shield)> Shields = default!;
        public ConcurrentBag<bool> Result = default!;

        // Process shields in batches for better performance
        public int BatchSize => 4;
        public int MinimumBatchParallel => 1;

        public void Execute(int index)
        {
            if (index >= Shields.Count)
                return;

            var shield = Shields[index];

            // Check if the projectile is in the shield range
            if (!ShieldSys.EntityInShield(shield, ProjectileUid, Transform))
                return;

            foreach (var effect in shield.Shield.Effects)
            {
                if (effect is not CircularShieldTempSpeedChangeEffect { ProjectilePhasing: true } speedEffect)
                    continue;

                // Check if already tracked
                lock (speedEffect.TrackedProjectiles)
                {
                    var wasAlreadyTracked = speedEffect.TrackedProjectiles.Contains(ProjectileUid);

                    // Try to add if not tracked
                    if (!wasAlreadyTracked)
                    {
                        // Need synchronization when adding to TrackedProjectiles

                        speedEffect.OnShieldEnter(ProjectileUid, shield);
                    }

                    // Check if it was added
                    if (speedEffect.TrackedProjectiles.Contains(ProjectileUid))
                    {
                        Result.Add(true);
                        return;
                    }
                }
            }

            Result.Add(false);
        }
    }
}
