using System.Diagnostics.CodeAnalysis;
using Content.Server._NF.Bank;
using Content.Server.Administration.Logs;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Roles.Components;
using Content.Shared.Chat;
using Content.Shared.Database;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Roles;
using JetBrains.Annotations;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._Hardlight.StationPay;

internal sealed class ScheduledPayout(EntityUid uid, int lastPayout) : IComparable<ScheduledPayout>
{
    public EntityUid Uid { get; } = uid;
    public int LastPayout { get; } = lastPayout;

    public int CompareTo(ScheduledPayout? other)
    {
        if (other == null)
            return 1;
        if (other.Uid == Uid)
            return 0;
        var cmp = LastPayout.CompareTo(other.LastPayout);
        // we can never return 0 here because SortedSet.Contains uses SortedSet.FindNode which
        // considers a node as "contained" if its CompareTo equals 0 with any other node
        return cmp != 0 ? cmp : Uid.CompareTo(other.Uid);
    }

    public override bool Equals(object? obj)
    {
        return obj is ScheduledPayout sp && sp.Uid == Uid;
    }

    public override int GetHashCode()
    {
        return Uid.GetHashCode();
    }
}

[UsedImplicitly]
public sealed class StationPaySystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly BankSystem _bank = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly ISharedPlayerManager _player = default!;

    // TODO: this should probably be a cvar
    private const int PayoutDelay = 3600;

    private readonly Dictionary<ProtoId<JobPrototype>, int> _jobPayoutRates = new();
    // map of {Mind.OwnedEntity: lastPayoutTime} where lastPayoutTime was the round duration at time of payout
    // sorted in ascending order
    private readonly SortedSet<ScheduledPayout> _scheduledPayouts = [];

    public override void Initialize()
    {
        base.Initialize();

        // Logger.GetSawmill(SawmillName).Level = LogLevel.Verbose;
        foreach (var proto in _prototypeManager.EnumeratePrototypes<StationPayPrototype>())
        {
            _jobPayoutRates[proto.JobProto] = proto.PayPerHour;
            Log.Debug($"loaded prototype: {proto.JobProto.Id} at {proto.PayPerHour}");
        }

        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        SubscribeLocalEvent<RoleAddedEvent>(OnRoleAddedEvent);
        SubscribeLocalEvent<RoleRemovedEvent>(OnRoleRemovedEvent);

        /*
         * TODO: account for disconnecting players
         *
         * when someone disconnects add them to a removal list with a timestamp 10 minutes in the future
         *
         * after that time they are removed from the scheduledpayout dict
         *
         * if they reconnect before that time they are removed from the disconnect tracker
         *
         * this allows for a grace period where if you happen to disconnect right before the hour you still get paid
         *
         * and if you disconnect and reconnect you still get paid
         *
         * we also don't have to do any complex bookkeeping
         */
        // SubscribeLocalEvent<MindRemovedMessage>(OnMindRemoved);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        // restartroundnow command
        if (ev.Old == GameRunLevel.InRound && ev.New == GameRunLevel.PreRoundLobby)
            OnRoundEnd();
        else if (ev.New == GameRunLevel.PostRound)
            OnRoundEnd();
    }

    private void OnRoundEnd()
    {
        var now = (int)_gameTicker.RoundDuration().TotalSeconds;

        // payout anyone who worked less than an hour at round end
        foreach (var entry in _scheduledPayouts)
        {
            PayoutFor(entry.Uid, now - entry.LastPayout);
        }

        _scheduledPayouts.Clear();
    }

    private bool GetJobForEntity(
        [NotNullWhen(true)] EntityUid? uid,
        [NotNullWhen(true)] out ProtoId<JobPrototype>? jobPrototype)
    {
        jobPrototype = null;
        if (TryComp<JobTrackingComponent>(uid, out var jtc)
            && jtc.Job is {} job
            && _jobPayoutRates.ContainsKey(job))
        {
            jobPrototype = job;
        }

        return jobPrototype != null;
    }

    private void OnRoleAddedEvent(RoleAddedEvent args)
    {
        var uid = args.Mind.OwnedEntity;

        if (uid == null
            || !TryComp<BankAccountComponent>(uid, out _)
            || !GetJobForEntity(uid, out var job)
           )
        {
            Log.Info($"Character {ToPrettyString(uid)} joined but was not valid for station pay");
            return;
        }

        var now = (int)_gameTicker.RoundDuration().TotalSeconds;
        Log.Info($"{ToPrettyString(uid)} joined with job {job.Value.Id}. Round time: {now}, payout at: {now + PayoutDelay}");

        var wrapper = new ScheduledPayout(uid.Value, now);
        // as equality is determined solely by the uid and not the timestamp we can
        // remove any existing entry by removing this wrapper object
        _scheduledPayouts.Remove(wrapper);
        _scheduledPayouts.Add(wrapper);
    }

    private void OnRoleRemovedEvent(RoleRemovedEvent args)
    {
        if (args.Mind.OwnedEntity == null)
            return;

        Log.Info($"Character {args.Mind.CharacterName}'s job was removed");
        // as above, since equality is determined solely by uid we can remove from the set this way
        _scheduledPayouts.Remove(new ScheduledPayout(args.Mind.OwnedEntity.Value, 0));
    }

    private void PayoutFor(EntityUid uid, int secondsWorked)
    {
        // sanity check
        if (!_scheduledPayouts.Contains(new ScheduledPayout(uid, 0)))
        {
            Log.Error($"Attemped payout for {ToPrettyString(uid)}, but no scheduled payout was found");
            return;
        }

        if (!GetJobForEntity(uid, out var jobId))
        {
            Log.Error($"Attemped payout for {ToPrettyString(uid)}, but no valid job found");
            return;
        }

        var employedTime = (int)(secondsWorked / (double)PayoutDelay);

        // this could in principle be 0 if someone joined right before round end
        if (employedTime <= 0)
        {
            Log.Warning($"Skipping payout for {ToPrettyString(uid)} due to employedTime <= 0 (secondsWorked: {secondsWorked})");
            return;
        }

        var rate = _jobPayoutRates[(ProtoId<JobPrototype>)jobId];
        var amount = employedTime * rate;

        // TODO: deposit doesn't work on logged out players, and frontier's banksystem has no method for
        //       doing a code-based deposit without a backing session (i.e. admin player)
        //       therefore currently if you are logged out at payment time you just miss that hour...
        if (!TryComp<MindContainerComponent>(uid, out var mc)
            || !mc.HasMind
            || !TryComp<MindComponent>(mc.Mind.Value, out var mind)
            || !_player.TryGetSessionByEntity(uid, out var session))
            // || !_player.TryGetSessionById(mind.UserId, out var session))
        {
            _adminLogger.Add(
                LogType.StationPayDeposit,
                LogImpact.Medium,
                $"Could not pay {ToPrettyString(uid)} ${amount} for {secondsWorked} seconds of work as {jobId.Value.Id} because no mind or session was present."
            );
            Log.Info($"Could not pay {ToPrettyString(uid)} ${amount} for {secondsWorked} seconds of work as {jobId.Value.Id} because no mind or session was present. (round time: {(int)_gameTicker.RoundDuration().TotalSeconds})");
            return;
        }

        _adminLogger.Add(
            LogType.StationPayDeposit,
            LogImpact.Low,
            $"Paying {ToPrettyString(uid)} ${amount} for {secondsWorked} seconds of work as {jobId.Value.Id}."
        );
        Log.Info($"Paying {ToPrettyString(uid)} ${amount} for {secondsWorked} seconds of work as {jobId.Value.Id}. (round time: {(int)_gameTicker.RoundDuration().TotalSeconds})");

        if (_bank.TryBankDeposit(uid, amount))
        {
            var job = _prototypeManager.Index<JobPrototype>(jobId);
            var message = Loc.GetString("stationpay-notify-payment",
                ("pay", amount),
                ("time", secondsWorked / 60),
                ("job", job.LocalizedName)
            );
            var wrappedMessage = Loc.GetString("pda-notification-message",
                ("header", Loc.GetString("stationpay-notify-pda-header")),
                ("message", message));

            _chat.ChatMessageToOne(ChatChannel.Notifications,
                message,
                wrappedMessage,
                EntityUid.Invalid,
                false,
                session.Channel);
        }
        else
            Log.Error($"Failed to deposit station pay for uid: {ToPrettyString(uid)}");
    }

    public override void Update(float frameTime)
    {
        var now = (int)_gameTicker.RoundDuration().TotalSeconds;
        var countBefore = _scheduledPayouts.Count;

        while (_scheduledPayouts.Count > 0)
        {
            var first = _scheduledPayouts.Min!;
            if (first.LastPayout + PayoutDelay > now)
                break;

            PayoutFor(first.Uid, PayoutDelay);
            _scheduledPayouts.Remove(first);

            // it's possible that `now` is substantially past when this payout should have happened because
            // the round can be paused or played at accelerated timescales. we do not assume we're paying on
            // time, and thus we schedule the next payout relative to when this payout should have happened
            // if we're way behind we'll catch up with several simultaneous payments here
            var updated = new ScheduledPayout(first.Uid, first.LastPayout + PayoutDelay);
            _scheduledPayouts.Add(updated);
        }

        if(_scheduledPayouts.Count != countBefore)
            Log.Error($"assertion fail: _scheduledPayouts count changed from {countBefore} to {_scheduledPayouts.Count}");

        base.Update(frameTime);
    }
}
