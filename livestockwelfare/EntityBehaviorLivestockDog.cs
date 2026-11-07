using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// What only dogs have (raised wolves and the Wolf taming mod's dogs): a bond with people that
/// grows with petting and fades without it, barking at threats, guarding the herd and chasing
/// crop eaters off the garden (see AiTaskLivestockDogGuard), and not hunting the player's own
/// animals. Only kept dogs take part; wild wolves stay wolves. Their thirst, shelter, hygiene and age use the livestock behaviors.
/// </summary>
public class EntityBehaviorLivestockDog : EntityBehavior
{
    public const string Code = "livestockwelfare.dog";

    const string BondAttr = "lwBond";
    const string LastUpdateAttr = "lwBondLastHours";
    const string LastPettedAttr = "lwBondLastPettedHours";
    // Synced so the client can show "lonely" in the info text.
    const string LonelyAttr = "lwLonely";
    // Away on a Feral Kinship Companions expedition (as last seen), and resting after it until then.
    const string OnTripAttr = "lwOnTrip";
    const string TiredUntilAttr = "lwTiredUntilHours";
    // Feral Kinship Companions sets this in Pet AI's tree while a companion is away on an expedition,
    // and keeps the trip's phase in its own tree: Leaving (still walking off), Suspended (gone),
    // Returning, Resuming, then Complete. The dog doesn't tick while gone, so both are checked.
    const string FkcAwayKey = "feralKinshipExpeditionAway";
    const string FkcTripAttr = "feralKinshipRetainedNativeExpeditionV1";
    static readonly string[] FkcAwayPhases = { "Leaving", "Suspended", "Returning", "Resuming" };

    const float CheckIntervalSeconds = 2;
    const double MaxCatchUpHours = 24;
    // A player who hurt the dog this recently may be bitten back (ms).
    const long RetaliateMs = 30000;
    // Barking: a growl the herd can hear; whining: the pup howl, when lonely at night.
    // Vanilla has growl1-4 and pup-howl1-2; one is picked at random.
    const string BarkSound = "sounds/creature/wolf/growl";
    const int BarkSounds = 4;
    const string WhineSound = "sounds/creature/wolf/pup-howl";
    const int WhineSounds = 2;
    const double LonelyWhineChance = 0.02;
    const double LonelyMopeChance = 0.03;
    const float MopeMinSeconds = 15, MopeMaxSeconds = 45;

    bool baby;
    float walkSpeed;
    float restWalkSpeed;
    float accum;
    long lastBarkMs = -100000;
    long lastAttackerId;
    long lastAttackedMs = -100000;

    public EntityBehaviorLivestockDog(Entity entity) : base(entity) { }

    public override string PropertyName() => Code;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        baby = attributes["baby"].AsBool(false);
        walkSpeed = attributes["runSpeed"].AsFloat(0.03f);
        restWalkSpeed = attributes["walkSpeed"].AsFloat(0.008f);
    }

    static DogSettings? Settings =>
        livestockwelfareModSystem.Config?.Dogs is { Enabled: true } settings ? settings : null;

    // Above Pet AI's "follow" (2.1), so a dog walking with its owner still runs off to guard and
    // comes back after; below playing fetch (2.5) and attacks on command (3). Sit, lie down and
    // stay could be cancelled from 1.9 on, so CanGuard is false while one of those is active.
    const float GuardPriority = 2.2f;
    // Pet AI keeps the current command here; while one of these the dog keeps still.
    static readonly string[] StillCommands = { "sit", "lay", "stay" };

    public override void AfterInitialized(bool onFirstSpawn)
    {
        base.AfterInitialized(onFirstSpawn);
        Slim();
        if (entity.World.Side != EnumAppSide.Server || entity is not EntityAgent agent) return;

        if (entity.GetBehavior<EntityBehaviorTaskAI>() is { } taskAi)
        {
            taskManager = taskAi.TaskManager;
            // The target of a hunt is chosen in ShouldExecute, which runs before this check. Tasks
            // started another way (a herd alarm) pass OnExecuteTask; Pet AI's hunting can also
            // switch targets on the way, which StopSparedHunts catches.
            taskManager.OnShouldExecuteTask += task => !HuntsSpared(task);
            taskManager.OnExecuteTask += (task, _) => !HuntsSpared(task);
            AddBowlEating(agent, taskManager);
            if (baby) AddPupLure(agent, taskManager);
        }

        if (baby || Settings is not { } settings) return;
        if (settings.PoopChancePerDay > 0)
        {
            LivestockUtil.AddAiTask(agent, AiTaskLivestockDogPotty.Code, PottyPriority, restWalkSpeed, 0, 0,
                (taskConfig, aiConfig) => new AiTaskLivestockDogPotty(agent, taskConfig, aiConfig));
        }
        if (settings.TripAftermath || settings.SleepInBedAtNight)
        {
            LivestockUtil.AddAiTask(agent, AiTaskLivestockDogRest.Code, RestPriority, restWalkSpeed, 0, 0,
                (taskConfig, aiConfig) => new AiTaskLivestockDogRest(agent, taskConfig, aiConfig));
        }
        if (!settings.AnyGuarding) return;
        LivestockUtil.AddAiTask(agent, AiTaskLivestockDogGuard.Code, GuardPriority, walkSpeed, 0, 0,
            (taskConfig, aiConfig) => new AiTaskLivestockDogGuard(agent, taskConfig, aiConfig), "run");
    }

    // Above staying close to its mother (1.4) and below getting out of water (1.45). The vanilla
    // run from players (1.5) is held back meanwhile.
    const float PupLurePriority = 1.42f;

    // Pups follow a player holding meat or a bone (DogSettings.LurePups), so they can be fed by hand.
    void AddPupLure(EntityAgent agent, AiTaskManager manager)
    {
        if (Settings is not { LurePups: true } settings || settings.PupLureCodes.Length == 0) return;
        var codes = settings.PupLureCodes.Select(code => new AssetLocation(code)).ToArray();
        AiTaskLivestockFollowFood? follow = null;
        LivestockUtil.AddAiTask(agent, AiTaskLivestockFollowFood.Code, PupLurePriority, walkSpeed * 0.6f, 0, 0,
            (taskConfig, aiConfig) => follow = new AiTaskLivestockFollowFood(agent, taskConfig, aiConfig)
            {
                Lures = stack => codes.Any(code => WildcardUtil.Match(code, stack.Collectible.Code)),
            }, "run");
        manager.OnShouldExecuteTask += task => task is not (AiTaskFleeEntity or AiTaskFleeEntityR) || follow?.KeepsFromFleeing != true;
    }

    // Vanilla resets the collision box (on load, death, revival); this runs right after.
    public override void UpdateColSelBoxes() => Slim();

    // Wolves and most Wolf taming dogs have a 1.2 block square collision box (as long as the body,
    // and as wide), and the pathfinder tries each block at a random point inside it, so they rarely
    // find a way through a door, even a two-block one. DogSettings.Width narrows it; the selection
    // box stays as it is.
    void Slim()
    {
        if (squeezing || Settings is not { Width: > 0 } settings || entity.CollisionBox.XSize <= settings.Width) return;
        entity.SetCollisionBox((float)settings.Width, entity.CollisionBox.YSize);
    }

    // A dog going out of the house is made narrower still, until it is through and has room again.
    // Thinner than a player (0.6), so it slips past the open door leaf without catching on it.
    const float DoorWidth = 0.5f;
    float fullWidth;
    bool squeezing;
    bool squeezeHeld;

    /// <summary>Makes the dog narrow enough to pass a door; StopSqueezing lets it grow back once it fits.</summary>
    public void SqueezeThroughDoor()
    {
        squeezeHeld = true;
        if (squeezing || entity.CollisionBox.XSize <= DoorWidth) return;
        fullWidth = entity.CollisionBox.XSize;
        squeezing = true;
        entity.SetCollisionBox(DoorWidth, entity.CollisionBox.YSize);
    }

    public void StopSqueezing()
    {
        squeezeHeld = false;
        if (squeezing) TryStopSqueezing();
    }

    // Still in the doorway, it stays narrow (checked again on the next tick), or it would be stuck in the wall.
    void TryStopSqueezing()
    {
        var box = entity.CollisionBox;
        var full = new Cuboidf(-fullWidth / 2, box.Y1, -fullWidth / 2, fullWidth / 2, box.Y2, fullWidth / 2);
        if (entity.World.CollisionTester.IsColliding(entity.World.BlockAccessor, full, entity.Pos.XYZ, false)) return;
        entity.SetCollisionBox(fullWidth, box.YSize);
        squeezing = false;
    }

    // A tired dog goes to bed before drinking, eating or wandering, but guards (above) and obeys commands.
    const float RestPriority = 1.4f;
    // Above following (2.1): a dog walking with its owner still asks to go out. Below guarding.
    const float PottyPriority = 2.15f;

    /// <summary>Resting after a trip (see DogSettings.TripAftermath).</summary>
    public bool IsTired => entity.WatchedAttributes.GetDouble(TiredUntilAttr) > entity.World.Calendar.TotalHours;

    // Needs to go since this in-game hour (see AiTaskLivestockDogPotty); absent when it doesn't.
    const string UrgeSinceAttr = "lwPottyUrgeSinceHours";

    public bool NeedsToGo => entity.WatchedAttributes.HasAttribute(UrgeSinceAttr);
    public double UrgeHours => NeedsToGo ? entity.World.Calendar.TotalHours - entity.WatchedAttributes.GetDouble(UrgeSinceAttr) : 0;

    /// <summary>The dog needs to go (called by the hygiene behavior when it is time).</summary>
    public void FeelUrge()
    {
        // Asleep, it holds on till morning rather than waking the house.
        if (Settings is { SleepInBedAtNight: true } settings && settings.IsNight(entity.World.Calendar)) return;
        if (!NeedsToGo) entity.WatchedAttributes.SetDouble(UrgeSinceAttr, entity.World.Calendar.TotalHours);
    }

    /// <summary>Done: leaves the poop (where it stands) and is relieved. False if there was no free spot.</summary>
    public bool Relieve()
    {
        if (entity.GetBehavior<EntityBehaviorLivestockHygiene>()?.PlaceDogPoop() == false) return false;
        entity.WatchedAttributes.RemoveAttribute(UrgeSinceAttr);
        return true;
    }

    /// <summary>Sitting, lying down or staying on Pet AI's command: it keeps still for nothing else.</summary>
    public bool KeepsStillOnCommand => Array.IndexOf(StillCommands, entity.WatchedAttributes.GetString("activeCommand")) >= 0;

    // Watches the Feral Kinship Companions "away" flag: when it goes off, the dog is home again.
    void CheckTrip(DogSettings settings)
    {
        string? phase = entity.WatchedAttributes.GetTreeAttribute(FkcTripAttr)?.GetString("phase");
        bool away = entity.WatchedAttributes.GetTreeAttribute("domesticationstatus")?.GetBool(FkcAwayKey) == true
            || (phase != null && Array.IndexOf(FkcAwayPhases, phase) >= 0);
        bool wasAway = entity.WatchedAttributes.GetBool(OnTripAttr);
        if (away == wasAway) return;
        entity.WatchedAttributes.SetBool(OnTripAttr, away);
        if (away || !settings.TripAftermath) return;

        entity.GetBehavior<EntityBehaviorLivestockHygiene>()?.Soil((float)settings.TripFilth);
        entity.GetBehavior<EntityBehaviorLivestockShelter>()?.AddStress((float)settings.TripStress);
        entity.WatchedAttributes.SetDouble(TiredUntilAttr, entity.World.Calendar.TotalHours + settings.TripRestHours);
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1} is back from a trip: dirty, stressed, tired for {2} h", entity.Code.Path, entity.EntityId, settings.TripRestHours);
    }

    AiTaskManager? taskManager;

    // Wolf taming's and Feral Kinship's dogs only eat what is put in their mouth. Given a diet
    // (patches/dogs.json), this teaches them to walk to a food bowl (or any trough that suits them)
    // and eat, like vanilla wolves do. Only from bowls: no picking up loose meat.
    static void AddBowlEating(EntityAgent agent, AiTaskManager manager)
    {
        if (manager.AllTasks.Any(t => t is AiTaskSeekFoodAndEat) || agent.Properties.Attributes?["creatureDiet"].Exists != true) return;

        var config = JsonObject.FromJson("{ \"code\": \"seekfoodandeat\", \"animation\": \"walk\", \"movespeed\": 0.008, \"priority\": 1.3, "
            + "\"eatTime\": 1.5, \"eatAnimation\": \"eat\", \"eatSound\": \"creature/animal-eat1\", \"eatLooseItems\": false, "
            + "\"mincooldownHours\": 1, \"maxcooldownHours\": 4 }");
        var task = new AiTaskSeekFoodAndEat(agent, config, JsonObject.FromJson("{}"));
        manager.AddTask(task);
        task.AfterInitialize();
    }

    bool HuntsSpared(IAiTask task) =>
        task is AiTaskSeekEntity or AiTaskMeleeAttack && task is AiTaskBaseTargetable { TargetEntity: { } target } && Spares(target);

    void StopSparedHunts()
    {
        if (taskManager == null) return;
        var slots = taskManager.ActiveTasksBySlot;
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (slots[slot] is { } task && HuntsSpared(task)) taskManager.StopTask(slot);
        }
    }

    public float Bond => entity.WatchedAttributes.GetFloat(BondAttr, (float)(Settings?.StartBond ?? 0.5));
    public bool IsLonely => entity.WatchedAttributes.GetBool(LonelyAttr);
    public bool IsKept => LivestockUtil.IsKept(entity);

    /// <summary>A player is close enough to keep the dog company (it then needs no kennel at night).</summary>
    public bool HasCompany
    {
        get
        {
            if (Settings is not { } settings) return false;
            var pos = entity.Pos;
            var player = entity.World.NearestPlayer(pos.X, pos.Y, pos.Z);
            return player?.Entity != null && player.Entity.Pos.SquareDistanceTo(pos) <= (double)settings.CompanyRadius * settings.CompanyRadius;
        }
    }

    /// <summary>Whether this dog guards the herd and the garden or helps hunt now: kept, grown, not lonely, sick or hurt.</summary>
    public bool CanGuard
    {
        get
        {
            if (baby || Settings is not { } settings || !settings.AnyGuarding || !IsKept || IsLonely) return false;
            if (KeepsStillOnCommand) return false;
            if (entity.GetBehavior<EntityBehaviorLivestockHygiene>() is { IsSick: true } or { IsCollapsed: true }) return false;
            return entity.GetBehavior<EntityBehaviorHealth>() is not { } health || health.Health > health.MaxHealth * 0.4f;
        }
    }

    /// <summary>
    /// Whether a kept dog leaves this entity alone: the player's animals, the player, and wild game
    /// nobody wounded. What attacked the dog may always be bitten back.
    /// </summary>
    public bool Spares(Entity target)
    {
        if (Settings is not { } settings || !IsKept) return false;
        bool attackedByIt = target.EntityId == lastAttackerId && entity.World.ElapsedMilliseconds - lastAttackedMs < RetaliateMs;
        if (attackedByIt) return false;
        if (target is EntityPlayer) return settings.SparePlayers;
        if (LivestockUtil.IsKept(target)) return settings.SpareKeptAnimals;
        return settings.HuntOnlyWoundedGame && !IsChaseTarget(target, settings) && DogHunting.WoundedBy(target) == null;
    }

    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        if (damageSource.Type == EnumDamageType.Heal || damageSource.GetCauseEntity() is not { } attacker) return;
        lastAttackerId = attacker.EntityId;
        lastAttackedMs = entity.World.ElapsedMilliseconds;
    }

    /// <summary>A wild predator the dog chases (ChaseCodes).</summary>
    public static bool IsChaseTarget(Entity e, DogSettings settings) =>
        e.Alive && !LivestockUtil.IsKept(e) && Matches(e, settings.ChaseCodes);

    /// <summary>A wild animal that eats crops (GardenPestCodes).</summary>
    public static bool IsGardenPest(Entity e, DogSettings settings) =>
        e.Alive && !LivestockUtil.IsKept(e) && Matches(e, settings.GardenPestCodes);

    /// <summary>Anything the dog barks at: chased predators, and monsters too dangerous to chase.</summary>
    static bool IsThreat(Entity e, DogSettings settings) =>
        IsChaseTarget(e, settings) || (e.Alive && !LivestockUtil.IsKept(e) && Matches(e, settings.BarkCodes));

    static bool Matches(Entity e, string[] codes)
    {
        foreach (string code in codes)
        {
            if (WildcardUtil.Match(new AssetLocation(code), e.Code)) return true;
        }
        return false;
    }

    public override void OnGameTick(float deltaTime)
    {
        if (entity.World.Side != EnumAppSide.Server || !entity.Alive) return;

        accum += deltaTime;
        if (accum < CheckIntervalSeconds) return;
        accum = 0;

        var settings = Settings;
        if (settings == null || !IsKept)
        {
            if (IsLonely) entity.WatchedAttributes.SetBool(LonelyAttr, false);
            return;
        }

        if (squeezing && !squeezeHeld) TryStopSqueezing();
        StopSparedHunts();
        CheckTrip(settings);
        if (!baby && settings.Bark) TryBark(settings);
        if (IsLonely) Mope();

        var attrs = entity.WatchedAttributes;
        double nowHours = entity.World.Calendar.TotalHours;
        if (!attrs.HasAttribute(BondAttr)) attrs.SetFloat(BondAttr, (float)settings.StartBond);
        if (!attrs.HasAttribute(LastUpdateAttr))
        {
            attrs.SetDouble(LastUpdateAttr, nowHours);
            return;
        }

        double hours = Math.Min(nowHours - attrs.GetDouble(LastUpdateAttr), MaxCatchUpHours);
        if (hours < 1) return;
        attrs.SetDouble(LastUpdateAttr, nowHours);
        // Nobody around (option): the time passes without the dog missing anyone.
        if (!LivestockUtil.ShouldSimulate(entity)) return;

        double loss = settings.BondLossPerDay * hours / 24 * (HasCompany ? 0.5 : 1);
        SetBond(settings, Bond - loss);
        if (IsLonely) LivestockUtil.BlockBreeding(entity);
    }

    void SetBond(DogSettings settings, double bond)
    {
        bond = Math.Clamp(bond, 0, 1);
        entity.WatchedAttributes.SetFloat(BondAttr, (float)bond);
        bool lonely = bond < settings.LonelyBelowBond;
        if (lonely != IsLonely) entity.WatchedAttributes.SetBool(LonelyAttr, lonely);
    }

    void TryBark(DogSettings settings)
    {
        long now = entity.World.ElapsedMilliseconds;
        if (now - lastBarkMs < settings.BarkCooldownSeconds * 1000 || settings.BarkRadius <= 0) return;
        if (entity.GetBehavior<EntityBehaviorLivestockHygiene>() is { IsCollapsed: true }) return;

        var threat = entity.World.GetNearestEntity(entity.Pos.XYZ, settings.BarkRadius, 6, e => e != entity && IsThreat(e, settings));
        if (threat == null) return;

        lastBarkMs = now;
        // Face the threat, so it is easy to see where the danger is.
        double dx = threat.Pos.X - entity.Pos.X, dz = threat.Pos.Z - entity.Pos.Z;
        entity.Pos.Yaw = (float)Math.Atan2(dx, dz);
        PlaySound(BarkSound, BarkSounds, 32);
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1} barks at {2} #{3}", entity.Code.Path, entity.EntityId, threat.Code, threat.EntityId);
    }

    void PlaySound(string path, int variants, float range)
    {
        var sound = new AssetLocation("game", path + (1 + entity.World.Rand.Next(variants)));
        entity.World.PlaySoundAt(sound, entity, null, randomizePitch: true, range: range);
    }

    // A lonely dog is listless: it lies down now and then and whines at night.
    void Mope()
    {
        var rand = entity.World.Rand;
        var hygiene = entity.GetBehavior<EntityBehaviorLivestockHygiene>();
        if (hygiene is { IsCollapsed: false, IsHeldStill: false } && rand.NextDouble() < LonelyMopeChance)
        {
            hygiene.HoldStill(MopeMinSeconds + (float)rand.NextDouble() * (MopeMaxSeconds - MopeMinSeconds));
        }
        if (entity.GetBehavior<EntityBehaviorLivestockShelter>() is { Night: true } && rand.NextDouble() < LonelyWhineChance)
        {
            PlaySound(WhineSound, WhineSounds, 24);
        }
    }

    // Petting: right click with an empty hand (the shelter behavior shows the hearts and calms it).
    public override void OnInteract(EntityAgent byEntity, ItemSlot itemslot, Vec3d hitPosition, EnumInteractMode mode, ref EnumHandling handled)
    {
        if (mode != EnumInteractMode.Interact || byEntity.Controls.ShiftKey || !itemslot.Empty) return;
        if (entity.World.Side != EnumAppSide.Server || !entity.Alive || Settings is not { } settings || !IsKept) return;

        ShowJoy();
        double now = entity.World.Calendar.TotalHours;
        var attrs = entity.WatchedAttributes;
        if (attrs.HasAttribute(LastPettedAttr) && now - attrs.GetDouble(LastPettedAttr) < settings.PetCooldownHours) return;
        attrs.SetDouble(LastPettedAttr, now);
        SetBond(settings, Bond + settings.BondPerPet);
    }

    // Holding the button repeats the interaction; a new pat (after this long) yaps and wags again.
    const long JoyRepeatMs = 3000;
    const long WagMs = 3000;
    // Our happy animation (wagging tail, panting), patched into the wolf and dog shapes (patches/dogshapes.json).
    const string WagAnimation = "lwhappy";
    const string SitAnimation = "sit";
    static readonly AssetLocation YapSound = new("game", "sounds/creature/wolf/pup-bark");
    long lastJoyMs = -JoyRepeatMs;

    // Petted: a happy yap (lower for grown dogs), and it sits down panting and wagging its tail for a few seconds.
    void ShowJoy()
    {
        long now = entity.World.ElapsedMilliseconds;
        if (now - lastJoyMs < JoyRepeatMs) return;
        lastJoyMs = now;

        float pitch = (baby ? 1.15f : 0.8f) + (float)entity.World.Rand.NextDouble() * 0.1f;
        entity.World.PlaySoundAt(YapSound, entity, null, pitch, 16);

        // Sitting, the wagging tail is off the ground. The happy animation is added on top of it.
        if (entity.GetBehavior<EntityBehaviorLivestockHygiene>() is { IsCollapsed: false } hygiene)
        {
            hygiene.HoldStill(WagMs / 1000f, SitAnimation);
        }
        entity.AnimManager.StartAnimation(new AnimationMetaData
        {
            Code = WagAnimation,
            Animation = WagAnimation,
            AnimationSpeed = 1.5f,
            BlendMode = EnumAnimationBlendMode.Add
        }.Init());
        entity.World.RegisterCallback(_ => entity.AnimManager?.StopAnimation(WagAnimation), (int)WagMs);
    }

    /// <summary>Bond status for the info text and chat, e.g. "Bond: 60% (loyal)".</summary>
    /// <param name="langCode">Language to use, or null for the local game language.</param>
    public string StatusText(string? langCode = null)
    {
        string text = LivestockUtil.Translate(langCode, "livestockwelfare:dog-bond", (int)Math.Round(Bond * 100));
        string? mood = IsLonely ? "livestockwelfare:dog-lonely"
            : Settings is { } settings && Bond >= settings.LoyalAboveBond ? "livestockwelfare:dog-loyal" : null;
        // The client has no config, so it only knows "lonely" (synced).
        if (entity.World.Side == EnumAppSide.Client && !IsLonely) mood = null;
        if (mood != null) text += " (" + LivestockUtil.Translate(langCode, mood) + ")";
        if (IsTired) text += ", " + LivestockUtil.Translate(langCode, "livestockwelfare:dog-tired");
        return text;
    }

    public override void GetInfoText(StringBuilder infotext)
    {
        if (!entity.Alive || !entity.WatchedAttributes.HasAttribute(BondAttr)) return;
        infotext.AppendLine(StatusText());
    }
}
