using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.Damage.Systems;
using Content.Shared._Mono;
using Content.Shared.Damage.Components;
using Content.Shared.Ghost;
using Content.Shared.Mind;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;

namespace Content.Server._Mono;

/// <summary>
/// System that handles the GridGodModeComponent, which applies GodMode to all non-organic entities on a grid.
/// </summary>
public sealed class GridGodModeSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly GodmodeSystem _godmode = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GridGodModeComponent, ComponentStartup>(OnGridGodModeStartup);
        SubscribeLocalEvent<GridGodModeComponent, ComponentShutdown>(OnGridGodModeShutdown);
        SubscribeLocalEvent<MoveEvent>(OnEntityMoved);
        SubscribeLocalEvent<EntParentChangedMessage>(OnEntityParentChanged);
        SubscribeLocalEvent<EntInsertedIntoContainerMessage>(OnEntityInsertedInContainer);
        SubscribeLocalEvent<EntRemovedFromContainerMessage>(OnEntityRemovedFromContainer);
    }

    private void OnGridGodModeStartup(EntityUid uid, GridGodModeComponent component, ComponentStartup args)
    {
        // Verify this is applied to a grid
        if (!HasComp<MapGridComponent>(uid))
        {
            Log.Warning($"GridGodModeComponent applied to non-grid entity {ToPrettyString(uid)}");
            return;
        }

        // Find all entities on the grid and apply GodMode to them if they're not organic
        var allEntitiesOnGrid = _lookup.GetEntitiesIntersecting(uid).ToHashSet();

        foreach (var entity in allEntitiesOnGrid)
        {
            // Skip the grid itself and entities inside containers (they'll be handled by container logic)
            if (entity == uid || _container.IsEntityInContainer(entity))
                continue;

            ProcessEntityOnGrid(uid, entity, component);
        }
    }

    private void OnGridGodModeShutdown(EntityUid uid, GridGodModeComponent component, ComponentShutdown args)
    {
        // When the component is removed, remove GodMode from all protected entities
        foreach (var entity in component.ProtectedEntities.ToList())
        {
            if (EntityManager.EntityExists(entity))
            {
                RemoveGodMode(entity);
            }
        }

        component.ProtectedEntities.Clear();
    }

    private void OnEntityMoved(ref MoveEvent args)
    {
        // Check if the entity moved to or from a grid with GridGodModeComponent
        var entity = args.Entity;

        // Skip entities in containers as they're handled by container events
        if (_container.IsEntityInContainer(entity.Owner))
            return;

        // If the entity is already protected by a GridGodModeComponent, check if it left the grid
        if (TryGetGridGodModeComponent(args.OldPosition.EntityId, out var oldGridComp) &&
            oldGridComp != null && oldGridComp.ProtectedEntities.Contains(entity.Owner) &&
            args.NewPosition.EntityId != args.OldPosition.EntityId)
        {
            RemoveGodMode(entity.Owner);
            oldGridComp.ProtectedEntities.Remove(entity.Owner);
        }

        // If the entity moved to a grid with GridGodModeComponent, check if it should get GodMode
        if (args.NewPosition.EntityId.IsValid() && // Ensure NewPosition.EntityId is valid
            TryGetGridGodModeComponent(args.NewPosition.EntityId, out var newGridComp) &&
            newGridComp != null && !newGridComp.ProtectedEntities.Contains(entity.Owner))
        {
            ProcessEntityOnGrid(args.NewPosition.EntityId, entity.Owner, newGridComp);
        }
    }

    private void OnEntityParentChanged(ref EntParentChangedMessage args)
    {
        var entity = args.Entity;

        // Skip entities in containers as they're handled by container events
        if (_container.IsEntityInContainer(entity))
            return;

        // If the entity was on a protected grid and left
        if (args.OldParent.HasValue && args.OldParent.Value.IsValid() && // Ensure OldParent is valid
            TryGetGridGodModeComponent(args.OldParent.Value, out var oldGridComp) &&
            oldGridComp != null && oldGridComp.ProtectedEntities.Contains(entity))
        {
            // Entity moved away from a protected grid - remove GodMode
            RemoveGodMode(entity);
            oldGridComp.ProtectedEntities.Remove(entity);
        }

        // If the entity moved to a protected grid
        if (args.Transform.ParentUid.IsValid() && // Ensure ParentUid is valid before using it
            TryGetGridGodModeComponent(args.Transform.ParentUid, out var newGridComp) &&
            newGridComp != null && !newGridComp.ProtectedEntities.Contains(entity))
        {
            ProcessEntityOnGrid(args.Transform.ParentUid, entity, newGridComp);
        }
    }

    // New handler for entities inserted into containers
    private void OnEntityInsertedInContainer(EntInsertedIntoContainerMessage args)
    {
        var entity = args.Entity;
        // Entity was protected but is now in a container - remove protection
        // Iterate over all grids that might be protecting this entity.
        var query = EntityQueryEnumerator<GridGodModeComponent, TransformComponent>();
        while (query.MoveNext(out var gridUid, out var gridComp, out _)) // Querying for the component directly on grids
        {
            if (gridComp.ProtectedEntities.Contains(entity))
            {
                RemoveGodMode(entity);
                gridComp.ProtectedEntities.Remove(entity);
                // It's unlikely to be protected by multiple grids, but break if you're certain.
            }
        }
    }

    // New handler for entities removed from containers
    private void OnEntityRemovedFromContainer(EntRemovedFromContainerMessage args)
    {
        var entity = args.Entity;
        // If the entity is now directly on a protected grid
        if (TryComp<TransformComponent>(entity, out var xform) &&
            xform.GridUid.HasValue && // Ensure GridUid is not null
            TryGetGridGodModeComponent(xform.GridUid.Value, out var gridComp) &&
            gridComp != null && // Ensure component is found
            !gridComp.ProtectedEntities.Contains(entity))
        {
            ProcessEntityOnGrid(xform.GridUid.Value, entity, gridComp);
        }
    }

    /// <summary>
    /// Process an entity on a grid and apply GodMode if appropriate
    /// </summary>
    private void ProcessEntityOnGrid(EntityUid gridUid, EntityUid entityUid, GridGodModeComponent component)
    {
        // Don't apply GodMode to organic entities or ghosts
        if (IsOrganic(entityUid) || HasComp<GhostComponent>(entityUid))
            return;

        ApplyGodMode(gridUid, entityUid, component);
    }

    /// <summary>
    /// Applies GodMode to an entity and adds it to the protected entities list
    /// </summary>
    private void ApplyGodMode(EntityUid gridUid, EntityUid entityUid, GridGodModeComponent component)
    {
        // Skip if the entity is already protected
        if (component.ProtectedEntities.Contains(entityUid))
            return;

        // Apply GodMode
        _godmode.EnableGodmode(entityUid);
        component.ProtectedEntities.Add(entityUid);
    }

    /// <summary>
    /// Removes GodMode from an entity
    /// </summary>
    private void RemoveGodMode(EntityUid entityUid)
    {
        if (HasComp<GodmodeComponent>(entityUid))
        {
            _godmode.DisableGodmode(entityUid);
        }
    }

    /// <summary>
    /// Helper method to get the GridGodModeComponent from a grid entity
    /// </summary>
    private bool TryGetGridGodModeComponent(EntityUid? gridUid, [NotNullWhen(true)] out GridGodModeComponent? component)
    {
        component = null;

        if (gridUid == null || !gridUid.Value.IsValid() || !EntityManager.EntityExists(gridUid.Value))
            return false;

        return TryComp(gridUid.Value, out component);
    }

    /// <summary>
    /// Checks if an entity is organic (i.e., has a mind or is a mob)
    /// </summary>
    private bool IsOrganic(EntityUid entityUid)
    {
        // Skip ghosts
        if (HasComp<GhostComponent>(entityUid))
            return false;

        // Check if we have a player entity that's either still around or alive and may come back
        if (_mind.TryGetMind(entityUid, out var mind, out var mindComp) &&
            (mindComp.Session != null || !_mind.IsCharacterDeadPhysically(mindComp)))
        {
            return true;
        }

        // Also consider anything with a MobStateComponent as organic
        if (HasComp<MobStateComponent>(entityUid))
        {
            return true;
        }

        return false;
    }
}
