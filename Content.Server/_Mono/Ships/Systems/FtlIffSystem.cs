using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared._Mono.Ships.Components;
using Content.Shared.Shuttles.Components;

namespace Content.Server._Mono.Ships.Systems;

/// <summary>
/// Manages IFF flags for ships during FTL travel.
/// </summary>
public sealed class FtlIffSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly ShuttleSystem _shuttleSystem = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<FTLStartedEvent>(OnFtlStarted);
        SubscribeLocalEvent<FTLCompletedEvent>(OnFtlCompleted);
    }

    private void OnFtlStarted(ref FTLStartedEvent args)
    {
        var gridUid = args.Entity;

        if (!_entityManager.TryGetComponent<IFFComponent>(gridUid, out var iffComp))
            return;

        // Process the main FTL ship
        ProcessShipFtlStart(gridUid, iffComp);

        // Get all docked ships
        var dockedShuttles = new HashSet<EntityUid>();
        _shuttleSystem.GetAllDockedShuttles(gridUid, dockedShuttles);

        // Process each docked ship (excluding the main ship which we already processed)
        foreach (var dockedUid in dockedShuttles)
        {
            if (dockedUid == gridUid)
                continue;

            if (_entityManager.TryGetComponent<IFFComponent>(dockedUid, out var dockedIffComp))
            {
                ProcessShipFtlStart(dockedUid, dockedIffComp);
                //Log.Debug($"Applied FTL IFF hiding to docked ship {ToPrettyString(dockedUid)}");
            }
        }
    }

    private void ProcessShipFtlStart(EntityUid shipUid, IFFComponent iffComp)
    {
        var tempStorageComp = _entityManager.EnsureComponent<TemporaryFtlIffStorageComponent>(shipUid);
        tempStorageComp.OriginalFlags = iffComp.Flags;
        _entityManager.Dirty(shipUid, tempStorageComp);

        _shuttleSystem.AddIFFFlag(shipUid, IFFFlags.Hide, iffComp);

        //Log.Debug($"FTL started for {ToPrettyString(shipUid)}. Saved IFF flags: {tempStorageComp.OriginalFlags}. Set IFF to Hide.");
    }

    private void OnFtlCompleted(ref FTLCompletedEvent args)
    {
        var gridUid = args.Entity;

        // Process the main FTL ship
        if (_entityManager.TryGetComponent<TemporaryFtlIffStorageComponent>(gridUid, out var tempStorageComp))
        {
            ProcessShipFtlComplete(gridUid, tempStorageComp);
        }

        // Get all docked ships
        var dockedShuttles = new HashSet<EntityUid>();
        _shuttleSystem.GetAllDockedShuttles(gridUid, dockedShuttles);

        // Process each docked ship (excluding the main ship which we already processed)
        foreach (var dockedUid in dockedShuttles)
        {
            if (dockedUid == gridUid)
                continue;

            if (_entityManager.TryGetComponent<TemporaryFtlIffStorageComponent>(dockedUid, out var dockedTempComp))
            {
                ProcessShipFtlComplete(dockedUid, dockedTempComp);
                //Log.Debug($"Restored FTL IFF flags for docked ship {ToPrettyString(dockedUid)}");
            }
        }
    }

    private void ProcessShipFtlComplete(EntityUid shipUid, TemporaryFtlIffStorageComponent tempStorageComp)
    {
        if (!_entityManager.TryGetComponent<IFFComponent>(shipUid, out var iffComp))
        {
            _entityManager.RemoveComponent<TemporaryFtlIffStorageComponent>(shipUid);
            //Log.Warning($"FTL completed for {ToPrettyString(shipUid)}, but IFFComponent was missing. Removed TemporaryFtlIffStorageComponent.");
            return;
        }

        _shuttleSystem.RemoveIFFFlag(shipUid, IFFFlags.Hide, iffComp);
        _shuttleSystem.RemoveIFFFlag(shipUid, IFFFlags.HideLabel, iffComp);

        foreach (IFFFlags flagValue in Enum.GetValues(typeof(IFFFlags)))
        {
            if (flagValue == IFFFlags.None)
                continue;

            // If this specific flag was present in the original set, add it back.
            if ((tempStorageComp.OriginalFlags & flagValue) == flagValue)
            {
                _shuttleSystem.AddIFFFlag(shipUid, flagValue, iffComp);
            }
        }

        _entityManager.RemoveComponent<TemporaryFtlIffStorageComponent>(shipUid);

        //Log.Debug($"FTL completed for {ToPrettyString(shipUid)}. Original flags were: {tempStorageComp.OriginalFlags}. Current flags after restoration: {iffComp.Flags}.");
    }
}
