using System.Linq;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared._Mono.Ships.Components;
using Content.Shared.Shuttles.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using System.Numerics;

namespace Content.Server._Mono.Ships.Systems;

/// <summary>
/// This system saves and restores the relative positions of docked ships
/// during FTL travel, ensuring they maintain their original configuration.
/// </summary>
public sealed class FTLDockedPositionSystem : EntitySystem
{
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly DockingSystem _dockSystem = default!;
    [Dependency] private readonly ShuttleSystem _shuttleSystem = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    private EntityQuery<TransformComponent> _xformQuery;
    private EntityQuery<PhysicsComponent> _physicsQuery;

    /// <summary>
    /// Small safety offset to prevent exact overlaps
    /// </summary>
    private const float PositionOffset = 0.01f;

    public override void Initialize()
    {
        base.Initialize();

        _xformQuery = GetEntityQuery<TransformComponent>();
        _physicsQuery = GetEntityQuery<PhysicsComponent>();

        SubscribeLocalEvent<FTLComponent, ComponentStartup>(OnFTLStartup);
        SubscribeLocalEvent<FTLComponent, FTLCompletedEvent>(OnFTLCompleted);
    }

    /// <summary>
    /// When FTL starts, save the relative positions of all docked ships.
    /// </summary>
    private void OnFTLStartup(EntityUid uid, FTLComponent component, ComponentStartup args)
    {
        // Skip if this is a linked shuttle (not the main one)
        if (component.LinkedShuttle.HasValue)
            return;

        // Get all docked shuttles
        var dockedShuttles = new HashSet<EntityUid>();
        _shuttleSystem.GetAllDockedShuttles(uid, dockedShuttles);

        // Build a dependency graph for docked ships to handle restoration order later
        var dockedDependencies = new Dictionary<EntityUid, HashSet<EntityUid>>();
        foreach (var dockedUid in dockedShuttles)
        {
            if (dockedUid == uid)
                continue;

            dockedDependencies[dockedUid] = new HashSet<EntityUid>();
        }

        // Store relative positions for each docked shuttle (except the main one)
        foreach (var dockedUid in dockedShuttles)
        {
            if (dockedUid == uid)
                continue;

            // Skip if the entity doesn't have required components
            if (!_xformQuery.TryGetComponent(dockedUid, out var dockedXform) ||
                !_xformQuery.TryGetComponent(uid, out var mainXform))
                continue;

            var mainPos = _transform.GetWorldPosition(uid);
            var dockedPos = _transform.GetWorldPosition(dockedUid);
            var mainRot = _transform.GetWorldRotation(uid);
            var dockedRot = _transform.GetWorldRotation(dockedUid);

            // Store dock connections and build dependency graph
            var dockConnections = new List<(EntityUid DockA, EntityUid DockB)>();
            var docks = _dockSystem.GetDocks(dockedUid);
            foreach (var dock in docks)
            {
                if (!TryComp<DockingComponent>(dock, out var dockComp) ||
                    !dockComp.Docked ||
                    dockComp.DockedWith == null)
                    continue;

                var dockedWith = dockComp.DockedWith.Value;
                dockConnections.Add((dock, dockedWith));

                // Record which grid this ship is docked to
                var otherGrid = _transform.GetParentUid(dockedWith);
                if (otherGrid != uid && dockedShuttles.Contains(otherGrid) && dockedDependencies.ContainsKey(dockedUid))
                {
                    dockedDependencies[dockedUid].Add(otherGrid);
                }
            }

            // Create component to store position data
            var posComp = EnsureComp<FTLDockedPositionComponent>(dockedUid);
            posComp.MainShuttleUid = uid;
            posComp.RelativePosition = dockedPos - mainPos;
            posComp.RelativeRotation = dockedRot - mainRot;
            posComp.DockConnections = dockConnections;

            // Store the dependency information for processing order
            if (dockedDependencies.ContainsKey(dockedUid))
            {
                posComp.DependsOn = dockedDependencies[dockedUid].ToList();
            }
            else
            {
                posComp.DependsOn = new List<EntityUid>();
            }
        }
    }

    /// <summary>
    /// When FTL completes, restore the relative positions of all docked ships.
    /// </summary>
    private void OnFTLCompleted(EntityUid uid, FTLComponent component, ref FTLCompletedEvent args)
    {
        // Skip if this is a linked shuttle (not the main one)
        if (component.LinkedShuttle.HasValue)
            return;

        // Collect all docked ships with position components
        var dockedShips = new Dictionary<EntityUid, (FTLDockedPositionComponent Comp, TransformComponent Xform)>();
        var query = EntityQueryEnumerator<FTLDockedPositionComponent, TransformComponent>();

        while (query.MoveNext(out var dockedUid, out var posComp, out var dockedXform))
        {
            if (posComp.MainShuttleUid == uid)
            {
                dockedShips.Add(dockedUid, (posComp, dockedXform));

                // Disable physics for all ships to prevent interactions during positioning
                if (_physicsQuery.TryGetComponent(dockedUid, out var dockedBody))
                {
                    _shuttleSystem.Disable(dockedUid, component: dockedBody);
                }
            }
        }

        // Skip if we don't have any docked ships
        if (dockedShips.Count == 0)
            return;

        // Get the main shuttle's transform
        if (!_xformQuery.TryGetComponent(uid, out var mainXform) || mainXform.MapUid == null)
            return;

        var mainNewPos = _transform.GetWorldPosition(uid);
        var mainNewRot = _transform.GetWorldRotation(uid);

        // Process ships in dependency order to prevent overlaps
        var processed = new HashSet<EntityUid>();
        while (processed.Count < dockedShips.Count)
        {
            bool processedAny = false;

            foreach (var (dockedUid, (posComp, dockedXform)) in dockedShips)
            {
                if (processed.Contains(dockedUid))
                    continue;

                // Check if all dependencies have been processed
                bool dependenciesMet = true;
                foreach (var dependency in posComp.DependsOn)
                {
                    if (dockedShips.ContainsKey(dependency) && !processed.Contains(dependency))
                    {
                        dependenciesMet = false;
                        break;
                    }
                }

                if (!dependenciesMet)
                    continue;

                // Process this ship
                // Calculate position with a small offset to prevent exact overlaps
                var offsetPos = posComp.RelativePosition;
                if (offsetPos != Vector2.Zero)
                {
                    var dir = Vector2.Normalize(offsetPos);
                    offsetPos += dir * PositionOffset;
                }

                var newPos = mainNewPos + offsetPos;
                var newRot = mainNewRot + posComp.RelativeRotation;

                // Set the position and rotation
                _transform.SetParent(dockedUid, dockedXform, mainXform.MapUid.Value);
                _transform.SetWorldPosition(dockedUid, newPos);
                _transform.SetWorldRotation(dockedUid, newRot);

                processed.Add(dockedUid);
                processedAny = true;
            }

            // If we couldn't process any ships this iteration, we have a circular dependency
            if (!processedAny && processed.Count < dockedShips.Count)
            {
                // Process remaining ships in any order
                foreach (var (dockedUid, (posComp, dockedXform)) in dockedShips)
                {
                    if (processed.Contains(dockedUid))
                        continue;

                    // Add extra offset for potentially problematic ships
                    var offsetPos = posComp.RelativePosition;
                    if (offsetPos != Vector2.Zero)
                    {
                        var dir = Vector2.Normalize(offsetPos);
                        offsetPos += dir * PositionOffset * 5f;
                    }

                    var newPos = mainNewPos + offsetPos;
                    var newRot = mainNewRot + posComp.RelativeRotation;

                    _transform.SetParent(dockedUid, dockedXform, mainXform.MapUid.Value);
                    _transform.SetWorldPosition(dockedUid, newPos);
                    _transform.SetWorldRotation(dockedUid, newRot);

                    processed.Add(dockedUid);
                }
                break;
            }
        }

        // Re-establish all docking connections
        foreach (var (dockedUid, (posComp, _)) in dockedShips)
        {
            foreach (var (dockA, dockB) in posComp.DockConnections)
            {
                if (!TryComp<DockingComponent>(dockA, out var dockCompA) ||
                    !TryComp<DockingComponent>(dockB, out var dockCompB))
                    continue;

                _dockSystem.Dock((dockA, dockCompA), (dockB, dockCompB));
            }
        }

        // Re-enable physics and restore physics properties
        foreach (var (dockedUid, (posComp, _)) in dockedShips)
        {
            if (_physicsQuery.TryGetComponent(dockedUid, out var dockedBody))
            {
                _physics.SetLinearVelocity(dockedUid, Vector2.Zero, body: dockedBody);
                _physics.SetAngularVelocity(dockedUid, 0f, body: dockedBody);

                if (TryComp<ShuttleComponent>(dockedUid, out var dockedShuttle))
                {
                    _physics.SetLinearDamping(dockedUid, dockedBody, dockedShuttle.LinearDamping);
                    _physics.SetAngularDamping(dockedUid, dockedBody, dockedShuttle.AngularDamping);
                }

                if (HasComp<MapGridComponent>(mainXform.MapUid))
                {
                    _shuttleSystem.Disable(dockedUid, component: dockedBody);
                }
                else if (TryComp<ShuttleComponent>(dockedUid, out var shuttle))
                {
                    _shuttleSystem.Enable(dockedUid, component: dockedBody, shuttle: shuttle);
                }
            }

            // Remove the component now that we've restored the position
            RemComp<FTLDockedPositionComponent>(dockedUid);
        }
    }
}
