using System.Linq;
using System.Numerics;
using Content.Server._Mono.Radar;
using Content.Server.Theta.ShipEvent.Components;
using Content.Shared.Theta.ShipEvent.Components;
using Content.Shared._Mono.Radar;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server.Theta.ShipEvent.Systems;

/// <summary>
/// This system handles displaying circular shields on radar displays.
/// </summary>
public sealed class CircularShieldRadarSystem : EntitySystem
{
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IEntityManager _entityManager = default!;

    // Dictionary to keep track of radar blip entities for each shield
    private readonly Dictionary<EntityUid, EntityUid> _shieldRadarBlips = new();

    public override void Initialize()
    {
        base.Initialize();

        // Add radar blip components to shields when they're created
        SubscribeLocalEvent<CircularShieldComponent, ComponentStartup>(OnShieldStartup);

        // Subscribe to shield movement events
        SubscribeLocalEvent<CircularShieldComponent, MoveEvent>(OnShieldMoved);

        // Subscribe to parent change events
        SubscribeLocalEvent<CircularShieldComponent, EntParentChangedMessage>(OnShieldParentChanged);

        // We don't need to subscribe to ComponentShutdown directly
        // Instead we'll check if shields still exist during the Update method
    }

    private void OnShieldStartup(EntityUid uid, CircularShieldComponent component, ComponentStartup args)
    {
        // Get the shield's parent grid
        var xform = Transform(uid);
        var gridUid = xform.GridUid;

        // We only want to add radar blips for shields attached to grids
        if (gridUid == null || !TryComp<MapGridComponent>(gridUid, out _))
            return;

        // Create a special radar blip entity for this shield at the grid's center of mass
        CreateRadarBlipForShield(uid, component, gridUid.Value);
    }

    private void OnShieldMoved(EntityUid uid, CircularShieldComponent component, ref MoveEvent args)
    {
        // Check if this shield already has a blip
        if (!_shieldRadarBlips.TryGetValue(uid, out var blipUid))
            return;

        // Get the shield's current grid
        var xform = Transform(uid);
        var currentGridUidNullable = xform.GridUid;

        // If the shield is no longer on a grid, delete the blip
        if (currentGridUidNullable is not { } currentGridUid || !HasComp<MapGridComponent>(currentGridUid))
        {
            if (!TerminatingOrDeleted(blipUid))
                Del(blipUid);

            _shieldRadarBlips.Remove(uid);
            return;
        }

        // Get the blip's current grid
        var blipXform = Transform(blipUid);
        var blipGridUid = blipXform.ParentUid;

        // If the shield moved to a different grid, recreate the blip on the new grid
        if (blipGridUid == currentGridUid)
            return;

        // Delete the old blip
        if (!TerminatingOrDeleted(blipUid))
            Del(blipUid);

        _shieldRadarBlips.Remove(uid);

        // Create a new blip on the current grid
        CreateRadarBlipForShield(uid, component, currentGridUid);
    }

    private void OnShieldParentChanged(EntityUid uid, CircularShieldComponent component, ref EntParentChangedMessage args)
    {
        // Get the shield's current grid
        if (args.Transform.GridUid is not { } currentGridUid)
            return;

        // If we don't have a blip for this shield yet, and it's now on a grid, create one
        if (!_shieldRadarBlips.TryGetValue(uid, out var blipUid))
        {
            if (HasComp<MapGridComponent>(currentGridUid))
                CreateRadarBlipForShield(uid, component, currentGridUid);

            return;
        }

        // If the shield is no longer on a grid, delete the blip
        if (!HasComp<MapGridComponent>(currentGridUid))
        {
            if (!TerminatingOrDeleted(blipUid))
                Del(blipUid);

            _shieldRadarBlips.Remove(uid);
            return;
        }

        // Get the blip's current grid
        var blipXform = Transform(blipUid);
        var blipGridUid = blipXform.ParentUid;

        // If the shield moved to a different grid, recreate the blip on the new grid
        if (blipGridUid == currentGridUid)
            return;

        // Delete the old blip
        if (!TerminatingOrDeleted(blipUid))
            Del(blipUid);

        _shieldRadarBlips.Remove(uid);

        CreateRadarBlipForShield(uid, component, currentGridUid);
    }

    private void CreateRadarBlipForShield(EntityUid shieldUid, CircularShieldComponent shield, EntityUid gridUid)
    {
        // First check if we already have a blip for this shield
        if (_shieldRadarBlips.ContainsKey(shieldUid))
            return;

        // Get the grid's center of mass if it has physics
        var centerOfMass = Vector2.Zero;
        if (TryComp<PhysicsComponent>(gridUid, out var physics))
            centerOfMass = physics.LocalCenter;

        // Spawn a new entity for the radar blip at the grid's center of mass
        var blipEntity = _entityManager.SpawnEntity(null, new EntityCoordinates(gridUid, centerOfMass));

        // Add the radar blip component
        var radarComp = EnsureComp<RadarBlipComponent>(blipEntity);
        radarComp.RadarColor = shield.Color;
        radarComp.Scale = shield.Radius;
        radarComp.Shape = RadarBlipShape.Ring;
        radarComp.Enabled = shield.CanWork;
        radarComp.VisibleFromOtherGrids = true;

        // Add marker component
        var markerComp = EnsureComp<CircularShieldRadarComponent>(blipEntity);
        markerComp.VisibleFromOtherGrids = true;

        // Store the reference to the shield for updates
        _shieldRadarBlips[shieldUid] = blipEntity;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Check if any shields have been removed and clean up their blips
        var toRemove = new List<EntityUid>();
        foreach (var (shieldUid, blipUid) in _shieldRadarBlips)
        {
            if (TerminatingOrDeleted(shieldUid))
            {
                // Shield entity no longer exists, clean up the blip
                if (!TerminatingOrDeleted(blipUid))
                    Del(blipUid);

                toRemove.Add(shieldUid);
                continue;
            }

            // Skip if the blip no longer exists
            if (TerminatingOrDeleted(blipUid))
            {
                toRemove.Add(shieldUid);
                continue;
            }

            // Make sure the shield still has the component
            if (!TryComp<CircularShieldComponent>(shieldUid, out var shield))
            {
                // Shield lost its component, clean up the blip
                Del(blipUid);
                toRemove.Add(shieldUid);
                continue;
            }

            // Check if grid still exists and update position if needed
            var xform = Transform(shieldUid);
            var gridUid = xform.GridUid;
            if (gridUid != null && TryComp<PhysicsComponent>(gridUid.Value, out var physics))
            {
                var centerOfMass = physics.LocalCenter;
                var blipXform = Transform(blipUid);

                // Only update if position has changed significantly
                if ((blipXform.LocalPosition - centerOfMass).LengthSquared() > 0.01f)
                    _transform.SetLocalPosition(blipUid, centerOfMass);
            }

            // Make sure the blip still has the radar component
            if (!TryComp<RadarBlipComponent>(blipUid, out var radar))
                continue;

            // Update radar component properties
            radar.RadarColor = shield.Color;
            radar.Scale = shield.Radius;
            radar.Enabled = shield.CanWork;
            radar.VisibleFromOtherGrids = true;
        }

        // Remove any shields that no longer exist from our tracking
        foreach (var shieldUid in toRemove)
            _shieldRadarBlips.Remove(shieldUid);
    }

    public override void Shutdown()
    {
        base.Shutdown();

        // Clean up all radar blip entities
        foreach (var blipUid in _shieldRadarBlips.Values.Where(blipUid => !TerminatingOrDeleted(blipUid)))
            Del(blipUid);

        _shieldRadarBlips.Clear();
    }

    /// <summary>
    /// Explicitly removes the radar blip for a shield entity.
    /// Used during shield deletion and server shutdown.
    /// </summary>
    public void RemoveShieldRadarBlip(EntityUid shieldUid)
    {
        if (!_shieldRadarBlips.TryGetValue(shieldUid, out var blipUid))
            return;

        // Delete the blip if it exists
        if (!TerminatingOrDeleted(blipUid))
            Del(blipUid);

        // Remove from our tracking dictionary
        _shieldRadarBlips.Remove(shieldUid);
    }
}

