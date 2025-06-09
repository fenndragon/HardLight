using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server._NF.Bank;
using Content.Server.Chat.Managers;
using Content.Server.GameTicking;
using Content.Shared._NF.Bank.Components;
using Content.Shared._NF.Roles.Components;
using Content.Shared.Chat;
using Content.Shared.Mind;
using Content.Shared.Mind.Components;
using Content.Shared.Roles;
using JetBrains.Annotations;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._Hardlight.StationPay;

[UsedImplicitly]
public sealed class StationPaySystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly BankSystem _bank = default!;
    [Dependency] private readonly IChatManager _chat = default!;
    [Dependency] private readonly ISharedPlayerManager _player = default!;

    // TODO: this should probably be a cvar
    private const int PayoutDelay = 3600;

    // map of {Mind.OwnedEntity: lastPayoutTime} where lastPayoutTime was the round duration at time of payout
    // sorted in ascending order
    private readonly Dictionary<ProtoId<JobPrototype>, int> _jobPayoutRates = new();
    private OrderedDictionary<EntityUid, int> _scheduledPayouts = new();

    public override void Initialize()
    {
        base.Initialize();

        foreach (var proto in _prototypeManager.EnumeratePrototypes<StationPayPrototype>())
        {
            _jobPayoutRates[proto.JobProto] = proto.PayPerHour;
            Log.Debug($"[stationpay] loaded prototype: {proto.JobProto.Id} at {proto.PayPerHour}");
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
        foreach (var (uid, lastPayout) in _scheduledPayouts)
        {
            PayoutFor(uid, now - lastPayout);
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
            Log.Debug($"[stationpay] Character {args.Mind.CharacterName} joined but was not valid for station pay");
            return;
        }

        var now = (int)_gameTicker.RoundDuration().TotalSeconds;
        Log.Debug($"[stationpay] Character {args.Mind.CharacterName}/{uid} joined with job ${job.Value.Id}. Round time: {now}, payout: {now + PayoutDelay}");

        _scheduledPayouts.Insert(
            _scheduledPayouts.Count,
            (EntityUid)uid,
            (int)_gameTicker.RoundDuration().TotalSeconds + PayoutDelay
        );
    }

    private void OnRoleRemovedEvent(RoleRemovedEvent args)
    {
        if (args.Mind.OwnedEntity == null)
            return;

        Log.Debug($"[stationpay] Character {args.Mind.CharacterName}'s job was removed");
        _scheduledPayouts.Remove((EntityUid)args.Mind.OwnedEntity);
    }

    private void PayoutFor(EntityUid uid, int secondsWorked)
    {
        if (!_scheduledPayouts.ContainsKey(uid))
        {
            Log.Debug($"[stationpay] Attemped payout for {uid}, but no scheduled payout was found");
            return;
        }

        if (!GetJobForEntity(uid, out var jobId))
        {
            Log.Debug($"[stationpay] Attemped payout for {uid}, but no valid job found");
            return;
        }

        var employedTime = (int)(secondsWorked / (double)PayoutDelay);

        // this could in principle be 0 if someone joined right before round end
        if (employedTime <= 0)
        {
            Log.Debug($"[stationpay] Skipping payout for {uid} due to employedTime <= 0 (secondsWorked: {secondsWorked})");
            return;
        }

        var rate = _jobPayoutRates[(ProtoId<JobPrototype>)jobId];
        var amount = employedTime * rate;
        Log.Info($"Paying entity {uid} ${amount} for {secondsWorked} seconds of work as {jobId.Value.Id}.");

        if (_bank.TryBankDeposit(uid, amount))
        {
            if (!TryComp<MindContainerComponent>(uid, out var mc)
                || !mc.HasMind
                || !TryComp<MindComponent>(mc.Mind.Value, out var mind))
            {
                Log.Debug($"[stationpay] Skipping payout for {uid} due to no mind present");
                return;
            }

            if (!_player.TryGetSessionById(mind.UserId, out var session))
            {
                Log.Debug($"[stationpay] Skipping payout for {uid} due to no session");
                return;
            }

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
            Log.Error("[stationpay] Failed to deposit station pay for uid: " + uid);
    }

    public override void Update(float frameTime)
    {
        var now = (int)_gameTicker.RoundDuration().TotalSeconds;
        var updated = new Lazy<OrderedDictionary<EntityUid, int>>(() => new OrderedDictionary<EntityUid, int>(_scheduledPayouts));

        foreach (var (uid, scheduledPayoutTime) in _scheduledPayouts)
        {
            if (scheduledPayoutTime > now)
                break;

            var dict = updated.Value;
            dict.Remove(uid);
            // schedule their next payout for 1 hour after the previous scheduled payout
            dict.Insert(dict.Count, uid, scheduledPayoutTime + PayoutDelay);

            PayoutFor(uid, PayoutDelay);
        }

        if (updated.IsValueCreated)
        {
            _scheduledPayouts.Clear();
            foreach (var entry in updated.Value.ToList().OrderBy(it => it.Value))
            {
                _scheduledPayouts.Add(entry.Key, entry.Value);
            }
        }

        base.Update(frameTime);
    }
}
