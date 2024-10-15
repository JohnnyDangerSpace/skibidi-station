using Content.Server.Objectives.Components;
using Content.Server.Store.Systems;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Heretic;
using Content.Shared.Mind;
using Content.Shared.Store.Components;
using Content.Shared.Heretic.Prototypes;
using Content.Server.Chat.Systems;
using Robust.Shared.Audio;
using Content.Server.Temperature.Components;
using Content.Server.Body.Components;
using Content.Server.Atmos.Components;
using Content.Shared.Damage;
using Content.Server.Heretic.Components;
using Content.Server.Antag;
using Robust.Shared.Random;
using System.Linq;
using Content.Shared.Humanoid;
using Robust.Server.Player;
using Content.Server.Revolutionary.Components;
using Content.Shared.Random.Helpers;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Prototypes;
using Content.Shared.Roles;

namespace Content.Server.Heretic.EntitySystems;

public sealed partial class HereticSystem : EntitySystem
{
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly StoreSystem _store = default!;
    [Dependency] private readonly HereticKnowledgeSystem _knowledge = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly SharedEyeSystem _eye = default!;
    [Dependency] private readonly AntagSelectionSystem _antag = default!;
    [Dependency] private readonly IRobustRandom _rand = default!;
    [Dependency] private readonly IPlayerManager _playerMan = default!;
    [Dependency] private readonly IPrototypeManager _prot = default!;

    private float _timer = 0f;
    private float _passivePointCooldown = 20f * 60f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HereticComponent, ComponentInit>(OnCompInit);

        SubscribeLocalEvent<HereticComponent, EventHereticUpdateTargets>(OnUpdateTargets);
        SubscribeLocalEvent<HereticComponent, EventHereticRerollTargets>(OnRerollTargets);
        SubscribeLocalEvent<HereticComponent, EventHereticAscension>(OnAscension);

        SubscribeLocalEvent<HereticComponent, BeforeDamageChangedEvent>(OnBeforeDamage);
        SubscribeLocalEvent<HereticComponent, DamageModifyEvent>(OnDamage);

        SubscribeLocalEvent<HereticMagicItemComponent, ExaminedEvent>(OnMagicItemExamine);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _timer += frameTime;

        if (_timer < _passivePointCooldown)
            return;

        _timer = 0f;

        foreach (var heretic in EntityQuery<HereticComponent>())
        {
            // passive point gain every 20 minutes
            UpdateKnowledge(heretic.Owner, heretic, 1f);
        }
    }

    public void UpdateKnowledge(EntityUid uid, HereticComponent comp, float amount)
    {
        if (TryComp<StoreComponent>(uid, out var store))
        {
            _store.TryAddCurrency(new Dictionary<string, FixedPoint2> { { "KnowledgePoint", amount } }, uid, store);
            _store.UpdateUserInterface(uid, uid, store);
        }

        if (_mind.TryGetMind(uid, out var mindId, out var mind))
            if (_mind.TryGetObjectiveComp<HereticKnowledgeConditionComponent>(mindId, out var objective, mind))
                objective.Researched += amount;
    }

    private void OnCompInit(Entity<HereticComponent> ent, ref ComponentInit args)
    {
        // add influence layer
        if (TryComp<EyeComponent>(ent, out var eye))
            _eye.SetVisibilityMask(ent, eye.VisibilityMask | EldritchInfluenceComponent.LayerMask);

        foreach (var knowledge in ent.Comp.BaseKnowledge)
            _knowledge.AddKnowledge(ent, ent.Comp, knowledge);

        RaiseLocalEvent(ent, new EventHereticRerollTargets());
    }

    #region Internal events (target reroll, ascension, etc.)

    private void OnUpdateTargets(Entity<HereticComponent> ent, ref EventHereticUpdateTargets args)
    {
        ent.Comp.SacrificeTargets = ent.Comp.SacrificeTargets
            .Where(target => TryGetEntity(target, out var tent) && Exists(tent) && !HasComp<SacrificedComponent>(tent))
            .ToList();
        Dirty<HereticComponent>(ent); // update client
    }

    private void OnRerollTargets(Entity<HereticComponent> ent, ref EventHereticRerollTargets args)
    {
        // welcome to my linq smorgasbord of doom
        // have fun figuring that out

        var targets = _antag.GetAliveConnectedPlayers(_playerMan.Sessions)
            .Where(ics => ics.AttachedEntity.HasValue && HasComp<HumanoidAppearanceComponent>(ics.AttachedEntity));

        var eligibleTargets = new List<EntityUid>();
        foreach (var target in targets)
            eligibleTargets.Add(target.AttachedEntity!.Value); // it can't be null because see .Where(HasValue)

        // no heretics or other baboons
        eligibleTargets = eligibleTargets.Where(t => !HasComp<GhoulComponent>(t) && !HasComp<SacrificedComponent>(t) && !HasComp<HereticComponent>(t)).ToList();

        var pickedTargets = new List<EntityUid?>();

        var predicates = new List<Func<EntityUid, bool>>();

        // pick one command staff
        predicates.Add(t => HasComp<CommandStaffComponent>(t));

        // pick one secoff
        predicates.Add(t =>
            _prot.TryIndex<DepartmentPrototype>("Security", out var dept) // can we get sec jobs?
            && _mind.TryGetMind(t, out var mindid, out _) // does it have a mind?
            && TryComp<JobComponent>(mindid, out var jobc) && jobc.Prototype.HasValue // does it have a job?
            && dept.Roles.Contains(jobc.Prototype!.Value)); // is that job being shitsec?

        // pick one person from the same department
        predicates.Add(t =>
            _mind.TryGetMind(t, out var tmind, out _) && _mind.TryGetMind(ent, out var ownmind, out _) // get minds
            && TryComp<JobComponent>(tmind, out var tjob) && tjob.Prototype.HasValue // get jobs
            && TryComp<JobComponent>(ownmind, out var ownjob) && ownjob.Prototype.HasValue
            && _prot.EnumeratePrototypes<DepartmentPrototype>() // compare jobs for all
                .Where(d =>
                    d.Roles.Contains(tjob.Prototype.Value)
                    && d.Roles.Contains(ownjob.Prototype.Value)) // true = same department
                .ToList().Count != 0);

        foreach (var predicate in predicates)
        {
            var list = eligibleTargets.Where(predicate).ToList();

            if (list.Count == 0)
                continue;

            // pick and take
            var picked = _rand.PickAndTake<EntityUid>(list);
            pickedTargets.Add(picked);
        }

        // add whatever more until satisfied
        for (int i = 0; i <= ent.Comp.MaxTargets - pickedTargets.Count; i++)
            if (eligibleTargets.Count > 0)
                pickedTargets.Add(_rand.PickAndTake<EntityUid>(eligibleTargets));

        // leave only unique entityuids
        pickedTargets = pickedTargets.Distinct().ToList();

        ent.Comp.SacrificeTargets = pickedTargets.ConvertAll(t => GetNetEntity(t)).ToList();
        Dirty<HereticComponent>(ent); // update client
    }

    // notify the crew of how good the person is and play the cool sound :godo:
    private void OnAscension(Entity<HereticComponent> ent, ref EventHereticAscension args)
    {
        ent.Comp.Ascended = true;

        // how???
        if (ent.Comp.CurrentPath == null)
            return;

        var pathLoc = ent.Comp.CurrentPath!.ToLower();
        var ascendSound = new SoundPathSpecifier($"/Audio/_Goobstation/Heretic/Ambience/Antag/Heretic/ascend_{pathLoc}.ogg");
        _chat.DispatchGlobalAnnouncement(Loc.GetString($"heretic-ascension-{pathLoc}"), Name(ent), true, ascendSound, Color.Pink);

        // do other logic, e.g. make heretic immune to whatever
        switch (ent.Comp.CurrentPath!)
        {
            case "Ash":
                RemComp<TemperatureComponent>(ent);
                RemComp<RespiratorComponent>(ent);
                RemComp<BarotraumaComponent>(ent);
                break;

            default:
                break;
        }
    }

    #endregion

    #region External events (damage, etc.)

    private void OnBeforeDamage(Entity<HereticComponent> ent, ref BeforeDamageChangedEvent args)
    {
        // ignore damage from heretic stuff
        if (args.Origin.HasValue && HasComp<HereticBladeComponent>(args.Origin))
            args.Cancelled = true;
    }
    private void OnDamage(Entity<HereticComponent> ent, ref DamageModifyEvent args)
    {
        if (!ent.Comp.Ascended)
            return;

        switch (ent.Comp.CurrentPath)
        {
            case "Ash":
                // nullify heat damage because zased
                args.Damage.DamageDict["Heat"] = 0;
                break;
        }
    }

    #endregion



    #region Miscellaneous

    private void OnMagicItemExamine(Entity<HereticMagicItemComponent> ent, ref ExaminedEvent args)
    {
        if (!HasComp<HereticComponent>(args.Examiner))
            return;

        args.PushMarkup(Loc.GetString("heretic-magicitem-examine"));
    }

    #endregion
}
