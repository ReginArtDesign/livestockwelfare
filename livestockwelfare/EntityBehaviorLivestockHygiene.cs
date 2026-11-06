using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Dirty, crowded surroundings raise the animal's filth level; filthy animals fall sick.
/// Sick animals do not breed or regenerate, slowly lose health, can infect others of their
/// species and recover over time in clean surroundings.
/// All state lives in WatchedAttributes, so it survives saving and is synced to the client.
/// </summary>
public class EntityBehaviorLivestockHygiene : EntityBehavior
{
    public const string Code = "livestockwelfare.hygiene";

    const string FilthAttr = "lwFilth";
    const string SickAttr = "lwSick";
    // Why it fell sick ("filth", "weather" or "contagion"), shown with the sickness.
    const string SickCauseAttr = "lwSickCause";
    const string ImmuneUntilAttr = "lwImmuneUntilDays";
    const string LastUpdateAttr = "lwHygieneLastHours";
    // Badly sick animal lying on the ground until it recovers or dies.
    const string CollapsedAttr = "lwCollapsed";

    const float CheckIntervalSeconds = 5;
    // Longer gaps (e.g. the chunk was unloaded) are not simulated: the surroundings back then are unknown.
    const double MaxCatchUpHours = 24;

    string species = "";
    string collapseAnimation = "sleep";
    // Babies don't get dirty while they have a mother to keep them clean, but can still fall sick.
    // Orphans (see EntityBehaviorLivestockNursing) get dirty and need grooming like adults.
    bool baby;
    float accum;

    public EntityBehaviorLivestockHygiene(Entity entity) : base(entity) { }

    public override string PropertyName() => Code;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        species = attributes["species"].AsString("");
        baby = attributes["baby"].AsBool(false);
        collapseAnimation = attributes["collapseAnimation"].AsString("sleep");
        walkSpeed = attributes["walkSpeed"].AsFloat(0.006f);
        entity.AnimManager.OnStartAnimation += OnStartAnimation;

        if (entity.World.Side == EnumAppSide.Client)
        {
            shownDirtStage = DirtStage;
            // Re-render with a dirtier or cleaner coat when the synced filth crosses a stage.
            entity.WatchedAttributes.RegisterModifiedListener(FilthAttr, () =>
            {
                if (DirtStage == shownDirtStage) return;
                shownDirtStage = DirtStage;
                entity.MarkShapeModified();
            });
        }
    }

    // Flies buzz around very dirty animals (client side only).
    const float FliesFromFilth = 0.66f;
    float fliesAccum;
    static SimpleParticleProperties? flies;

    void SpawnFlies(float deltaTime)
    {
        fliesAccum += deltaTime;
        if (fliesAccum < 0.3f) return;
        fliesAccum = 0;
        // Only dirt draws flies; a sick animal shows it by being listless instead (see UpdateListless).
        if (Filth < FliesFromFilth) return;

        flies ??= new SimpleParticleProperties(
            1, 2, ColorUtil.ToRgba(255, 20, 20, 15),
            new Vec3d(), new Vec3d(),
            new Vec3f(-0.6f, -0.2f, -0.6f), new Vec3f(0.6f, 0.4f, 0.6f),
            0.9f, 0f, 0.08f, 0.12f, EnumParticleModel.Cube)
        {
            RandomVelocityChange = true,
            WithTerrainCollision = false
        };

        var box = entity.SelectionBox;
        var pos = entity.Pos;
        flies.MinPos.Set(pos.X + box.X1 - 0.3, pos.Y + box.Y2 * 0.5, pos.Z + box.Z1 - 0.3);
        flies.AddPos.Set(box.Width + 0.6, box.Height * 0.8, box.Length + 0.6);
        entity.World.SpawnParticles(flies);
    }

    // Filth at which the coat shows mud stage 1, 2 and 3.
    static readonly float[] DirtStageThresholds = { 0.33f, 0.66f, 0.9f };
    int shownDirtStage;

    int DirtStage
    {
        get
        {
            float filth = Filth;
            int stage = 0;
            while (stage < DirtStageThresholds.Length && filth >= DirtStageThresholds[stage]) stage++;
            return stage;
        }
    }

    // Client side: swap the coat textures for muddy copies (see DirtTextures). Animals whose
    // textures another mod already replaces (e.g. genelib) keep that mod's look.
    public override ITexPositionSource? GetTextureSource(ref EnumHandling handling)
    {
        int stage = DirtStage;
        if (stage == 0 || entity.Api is not ICoreClientAPI capi) return null;

        var textures = entity.Properties.Client?.Textures;
        if (textures == null || textures.Count == 0) return null;

        int textureIndex = entity.WatchedAttributes.GetInt("textureIndex");
        var subIds = new Dictionary<string, int>();
        foreach (var (code, texture) in textures)
        {
            var baked = texture.Baked;
            if (baked?.BakedName == null) continue;
            // Same alternate selection as the vanilla tesselator.
            if (baked.BakedVariants is { Length: > 1 } variants) baked = variants[textureIndex % variants.Length];
            subIds[code] = DirtTextures.GetSubId(capi, baked.BakedName, stage);
        }

        var fallback = capi.Tesselator.GetTextureSource(entity, null, textureIndex);
        // Stop here: a later behavior returning null would otherwise wipe out our result.
        handling = EnumHandling.PreventSubsequent;
        return new DirtyTextureSource(subIds, capi.EntityTextureAtlas, fallback);
    }

    public override void AfterInitialized(bool onFirstSpawn)
    {
        base.AfterInitialized(onFirstSpawn);
        if (entity.World.Side != EnumAppSide.Server) return;
        UpdateSelfCleaning();
        if (SelfCleaning && entity is EntityAgent agent && Settings is { DustBathing: true })
        {
            if (!entity.WatchedAttributes.HasAttribute(LastDustBathAttr)) entity.WatchedAttributes.SetDouble(LastDustBathAttr, entity.World.Calendar.TotalHours);
            LivestockUtil.AddAiTask(agent, AiTaskLivestockDustBath.Code, DustBathPriority, walkSpeed, 0.5, 1,
                (taskConfig, aiConfig) => new AiTaskLivestockDustBath(agent, taskConfig, aiConfig));
        }

        // Same approach as vanilla EntityBehaviorMortallyWoundable: no AI tasks while lying down.
        var taskAi = entity.GetBehavior<EntityBehaviorTaskAI>();
        if (taskAi != null) taskAi.TaskManager.OnShouldExecuteTask += _ => !IsCollapsed && !IsHeldStill;
        // The player's animals leave the player's dog alone (Wolf taming makes sheep attack dogs).
        if (taskAi != null)
        {
            taskAi.TaskManager.OnShouldExecuteTask += task => !AttacksKeptDog(task);
            taskAi.TaskManager.OnExecuteTask += (task, _) => !AttacksKeptDog(task);
        }
        if (taskAi != null && entity is EntityAgent luredAgent && LivestockTreats.Setup(entity)) AddFollowFood(luredAgent, taskAi);
        if (IsCollapsed) LieDown();
    }

    // While collapsed, only the lying animation (and dying) may play.
    bool OnStartAnimation(ref AnimationMetaData animationMeta, ref EnumHandling handling)
    {
        if (IsCollapsed && entity.Alive && animationMeta.Code != collapseAnimation && animationMeta.Code != "die")
        {
            handling = EnumHandling.PreventDefault;
            return true;
        }
        return false;
    }

    public string Species => species;
    public bool IsBaby => baby;

    // A baby with a mother (or a chick, which has no nursing) is kept clean; an orphan is not.
    bool KeptClean => baby && !entity.WatchedAttributes.GetBool(EntityBehaviorLivestockNursing.OrphanAttr);
    public float Filth => entity.WatchedAttributes.GetFloat(FilthAttr);

    /// <summary>Makes the coat dirtier at once (e.g. a dog back from a trip), up to fully filthy.</summary>
    public void Soil(float amount) => entity.WatchedAttributes.SetFloat(FilthAttr, Math.Clamp(Filth + amount, 0, 1));

    /// <summary>
    /// Chickens and other self-cleaning species get clean on their own; like babies they need no grooming.
    /// Set by the server from the config and synced, so the client knows not to start grooming them.
    /// </summary>
    bool SelfCleaning => entity.WatchedAttributes.GetBool(SelfCleaningAttr);
    const string SelfCleaningAttr = "lwSelfCleaning";

    void UpdateSelfCleaning()
    {
        bool selfCleaning = livestockwelfareModSystem.Config?.Hygiene.SelfCleaningSpecies is { } list && Array.IndexOf(list, species) >= 0;
        if (selfCleaning != SelfCleaning) entity.WatchedAttributes.SetBool(SelfCleaningAttr, selfCleaning);
    }

    /// <summary>Whether grooming can help this animal (adults of species that don't clean themselves).</summary>
    public bool CanBeGroomed => !KeptClean && !SelfCleaning;
    public bool IsSick => entity.WatchedAttributes.GetBool(SickAttr);
    public bool IsCollapsed => entity.WatchedAttributes.GetBool(CollapsedAttr);

    float WeatherStress => entity.GetBehavior<EntityBehaviorLivestockShelter>()?.Stress ?? 0;

    bool IsImmune => entity.WatchedAttributes.GetDouble(ImmuneUntilAttr) > entity.World.Calendar.TotalDays;

    HygieneSettings? Settings =>
        livestockwelfareModSystem.Config?.Hygiene is { Enabled: true } settings && (!baby || LivestockUtil.BabySettings != null) ? settings : null;

    public override void OnEntityLoaded()
    {
        base.OnEntityLoaded();
        if (entity.World.Side != EnumAppSide.Server) return;
        // Also sets the "sick" reason for animals saved by versions that had no reasons yet.
        LivestockUtil.SetRegenBlocked(entity, "sick", IsSick);
    }

    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        base.OnEntityDespawn(despawn);
        if (entity.World.Side == EnumAppSide.Server && entity.Alive) LivestockUtil.ReleaseRegen(entity);
    }

    public override void OnGameTick(float deltaTime)
    {
        if (!entity.Alive) return;
        if (entity.World.Side == EnumAppSide.Client)
        {
            SpawnFlies(deltaTime);
            return;
        }

        if (holdStillAnimation != null && !IsHeldStill) StopHoldingStill();

        accum += deltaTime;
        if (accum < CheckIntervalSeconds) return;
        accum = 0;

        // Something (e.g. being pushed or hit) may have started other movement or animations.
        if (IsCollapsed) LieDown();
        UpdateListless();

        var settings = Settings;
        if (settings == null)
        {
            if (IsSick) Recover(0);
            return;
        }

        var attrs = entity.WatchedAttributes;
        double nowHours = entity.World.Calendar.TotalHours;
        if (!attrs.HasAttribute(LastUpdateAttr))
        {
            attrs.SetDouble(LastUpdateAttr, nowHours);
            return;
        }

        double hours = Math.Min(nowHours - attrs.GetDouble(LastUpdateAttr), MaxCatchUpHours);
        if (hours < 1) return;
        attrs.SetDouble(LastUpdateAttr, nowHours);
        // Nobody around (option): the time passes without the animal getting dirtier, sicker, thirstier.
        if (!LivestockUtil.ShouldSimulate(entity)) return;

        // Time that passed at once (a time skip, a reloaded chunk): it would have taken its dust bath
        // in between, if it could get to dry sand.
        if (DustBathes(settings) && hours >= CatchUpBathHours && WantsDustBath(settings) && FindDustBath(settings, _ => false) != null) DustBathed();
        if (DustBathes(settings)) UpdateNoDustBath(settings);

        if (!KeptClean)
        {
            int extraAnimals = ExtraAnimalsNearby(settings);
            TryDepositMuck(settings, hours, extraAnimals);
            UpdateFilth(settings, hours, extraAnimals);
        }
        // Her mother keeps her clean: any dirt (e.g. from before, or set by a command) is gone.
        else if (Filth > 0) entity.WatchedAttributes.SetFloat(FilthAttr, 0);
        if (IsSick) UpdateSick(settings, hours);
        else TryFallSick(settings, hours);
    }

    void UpdateFilth(HygieneSettings settings, double hours, int extraAnimals)
    {
        double dirt = Math.Min(1, SurroundingsDirt(settings) + extraAnimals * settings.DirtPerExtraAnimal);
        double filth = Filth;
        // Moves towards the current dirt level; after the adapt time about 63% of the way.
        // Getting dirty is quick, getting clean on its own is slow: that is what grooming is for.
        // Self-cleaning species (chickens) get clean by dust bathing, or without it just in clean surroundings.
        // Too long without a bath, they get scruffy.
        if (DustBathes(settings) && HoursSinceDustBath > settings.UnbathedDays * 24) dirt = Math.Max(dirt, settings.UnbathedFilth);
        double adaptHours = dirt > filth ? settings.FilthAdaptHours : SelfCleaning && !settings.DustBathing ? settings.SelfCleaningHours : settings.FilthCleanHours;
        filth += (dirt - filth) * (1 - Math.Exp(-hours / adaptHours));
        entity.WatchedAttributes.SetFloat(FilthAttr, (float)Math.Clamp(filth, 0, 1));
    }

    /// <summary>Dirt from droppings, muck and rot lying around the animal.</summary>
    double SurroundingsDirt(HygieneSettings settings)
    {
        int r = settings.DroppingScanRadius;
        if (r <= 0) return 0;

        var pos = entity.Pos.AsBlockPos;
        var accessor = entity.World.BlockAccessor;
        var tmpPos = pos.Copy();
        int droppings = 0, muckLevels = 0, rotItems = 0;

        accessor.WalkBlocks(pos.AddCopy(-r, -1, -r), pos.AddCopy(r, 1, r), (block, x, y, z) =>
        {
            if (block.Code == null || block.Id == 0) return;

            if (block.Code.Domain == LivestockUtil.Domain && block.Code.Path.StartsWith(MuckCode))
            {
                muckLevels += DroppingsInBlock(block);
            }
            else if (block is BlockGroundStorage)
            {
                // Rot piled on the ground, e.g. spoiled feed.
                var storage = accessor.GetBlockEntity<BlockEntityGroundStorage>(tmpPos.Set(x, y, z));
                if (storage == null) return;
                foreach (var slot in storage.Inventory)
                {
                    if (IsRot(slot.Itemstack)) rotItems += slot.StackSize;
                }
            }
            else if (WildcardUtil.Match(settings.DroppingBlockCodes, block.Code.ToString()))
            {
                droppings += DroppingsInBlock(block);
            }
        });

        // Loose rot items dropped on the ground.
        foreach (var e in entity.World.GetEntitiesAround(entity.Pos.XYZ, r, 1.5f, e => e is EntityItem))
        {
            var stack = ((EntityItem)e).Itemstack;
            if (IsRot(stack)) rotItems += stack.StackSize;
        }

        return droppings * settings.DirtPerDropping + muckLevels * settings.DirtPerMuckLevel + rotItems * settings.DirtPerRotItem;
    }

    static bool IsRot(ItemStack? stack) => stack?.Collectible?.Code is { Domain: "game", Path: "rot" };

    // Natural Fertilizer stacks droppings in one block ("dung-3a" holds three); muck has levels ("muck-2").
    static int DroppingsInBlock(Block block)
    {
        string last = block.LastCodePart();
        return last.Length > 0 && char.IsDigit(last[0]) ? last[0] - '0' : 1;
    }

    const string DogSpecies = "dog";

    /// <summary>
    /// Other livestock nearby beyond the number that is fine (0 if not crowded). Babies don't count,
    /// and a dog guarding the herd doesn't crowd it (dogs only count for other dogs).
    /// </summary>
    int ExtraAnimalsNearby(HygieneSettings settings)
    {
        if (settings.CrowdingRadius <= 0) return 0;

        bool isDog = species == DogSpecies;
        int others = entity.World.GetEntitiesAround(entity.Pos.XYZ, settings.CrowdingRadius, 2,
            e => e != entity && e.Alive && e.GetBehavior<EntityBehaviorLivestockHygiene>() is { IsBaby: false } other
                && (other.Species == DogSpecies) == isDog).Length;
        return Math.Max(0, others - settings.CrowdingFreeAnimals);
    }

    const string MuckCode = "muck";
    const int MaxMuckLevel = 4;

    // Kept animals slowly soil the ground they stand on, faster when crowded. Wild animals don't,
    // so the world doesn't fill up with muck.
    void TryDepositMuck(HygieneSettings settings, double hours, int extraAnimals)
    {
        if (settings.MuckChancePerDay <= 0) return;

        if (!LivestockUtil.IsKept(entity)) return;
        if (species == DogSpecies && livestockwelfareModSystem.Config?.Dogs.MakesMuck != true) return;

        double chancePerDay = settings.MuckChancePerDay * (1 + extraAnimals * settings.MuckCrowdingBonus);
        if (!Roll(chancePerDay, hours)) return;

        var accessor = entity.World.BlockAccessor;
        var rand = entity.World.Rand;
        var feet = entity.Pos.AsBlockPos;

        // Try a few spots next to the animal; give up if none is free.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var pos = feet.AddCopy(rand.Next(3) - 1, 0, rand.Next(3) - 1);
            var block = accessor.GetBlock(pos);

            int level = 0;
            if (block.Code?.Domain == LivestockUtil.Domain && block.Code.Path.StartsWith(MuckCode))
            {
                level = DroppingsInBlock(block);
                if (level >= MaxMuckLevel) continue;
            }
            else
            {
                // Only on open ground: air or trampled-down plants (not droppings from other mods,
                // which are also replaceable), on top of a solid block.
                bool open = block.Id == 0 || (block.BlockMaterial == EnumBlockMaterial.Plant && block.Replaceable >= 5000 && block.EntityClass == null);
                if (!open || !accessor.GetBlock(pos.DownCopy()).SideSolid[BlockFacing.UP.Index]) continue;
            }

            var muck = entity.World.GetBlock(new AssetLocation(LivestockUtil.Domain, MuckCode + "-" + (level + 1)));
            if (muck == null) return;
            accessor.SetBlock(muck.Id, pos);
            return;
        }
    }

    void TryFallSick(HygieneSettings settings, double hours)
    {
        if (IsImmune) return;

        double filth = Filth;
        // Bad weather counts too: a cold, wet animal falls sick even in a clean pen.
        double weather = WeatherStress * (livestockwelfareModSystem.Config?.Weather.SicknessWeight ?? 0);
        double chancePerDay = settings.SicknessChancePerDay * (filth * filth + weather * weather);
        if (entity.GetBehavior<EntityBehaviorLivestockAge>()?.IsOld == true)
        {
            chancePerDay *= settings.OldAgeSicknessMultiplier;
        }

        if (Roll(chancePerDay, hours)) FallSick(weather > filth ? "weather" : "filth");
    }

    void UpdateSick(HygieneSettings settings, double hours)
    {
        if (Roll(settings.RecoveryChancePerDay * (1 - Filth) * (1 - WeatherStress), hours))
        {
            Recover(settings.ImmunityDays);
            return;
        }

        LivestockUtil.BlockBreeding(entity);
        SpreadDisease(settings, hours);

        // A sick wild animal can still pass the illness on, but it doesn't die of it.
        var health = entity.GetBehavior<EntityBehaviorHealth>();
        if (health == null || !LivestockUtil.IsKept(entity)) return;

        LivestockUtil.Harm(entity, health, settings.SickDamagePerDay * hours / 24, LivestockUtil.DiseaseDeathCause, $"disease (filth {Filth:0.00})");
        if (entity.Alive && !IsCollapsed && health.Health <= health.MaxHealth * settings.CollapseHealthFraction)
        {
            entity.WatchedAttributes.SetBool(CollapsedAttr, true);
            LieDown();
        }
    }

    void SpreadDisease(HygieneSettings settings, double hours)
    {
        if (settings.ContagionRadius <= 0 || settings.ContagionChancePerDay <= 0) return;

        var others = entity.World.GetEntitiesAround(entity.Pos.XYZ, settings.ContagionRadius, 2, e => e != entity && e.Alive);
        foreach (var other in others)
        {
            var hygiene = other.GetBehavior<EntityBehaviorLivestockHygiene>();
            if (hygiene == null || hygiene.species != species) continue;
            if (Roll(settings.ContagionChancePerDay, hours)) hygiene.Infect();
        }
    }

    bool AttacksKeptDog(IAiTask task) =>
        task is AiTaskMeleeAttack or AiTaskSeekEntity && task is AiTaskBaseTargetable { TargetEntity: { } target }
        && !entity.HasBehavior<EntityBehaviorLivestockDog>() && target.HasBehavior<EntityBehaviorLivestockDog>()
        && LivestockUtil.IsKept(entity) && LivestockUtil.IsKept(target);

    // This animal is a kept dog, and the attacker is one of the player's (non-dog) animals.
    bool IsKeptAnimalHittingKeptDog(Entity attacker) =>
        entity.HasBehavior<EntityBehaviorLivestockDog>() && !attacker.HasBehavior<EntityBehaviorLivestockDog>()
        && attacker.HasBehavior<EntityBehaviorLivestockAge>() && LivestockUtil.IsKept(entity) && LivestockUtil.IsKept(attacker);

    bool Roll(double chancePerDay, double hours) => LivestockUtil.Roll(entity, chancePerDay, hours);

    /// <summary>Makes the animal sick unless it already is, is immune, or sickness is switched off.</summary>
    public void Infect()
    {
        if (Settings == null || IsSick || IsImmune) return;
        FallSick("contagion");
    }

    // A sick animal is listless: it often lies down for a while.
    // Chance per check (every CheckIntervalSeconds) to lie down; with the rest times below a sick
    // animal spends about a third of its time lying.
    const double SickRestChance = 0.07;
    const float SickRestMinSeconds = 20, SickRestMaxSeconds = 60;

    void UpdateListless()
    {
        if (IsSick && entity.Alive && !IsCollapsed && !IsHeldStill && entity.World.Rand.NextDouble() < SickRestChance)
        {
            HoldStill(SickRestMinSeconds + (float)entity.World.Rand.NextDouble() * (SickRestMaxSeconds - SickRestMinSeconds));
        }
    }

    void FallSick(string cause)
    {
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1} fell sick from {2} (filth {3:0.00}, weather stress {4:0.00})",
            entity.Code.Path, entity.EntityId, cause, Filth, WeatherStress);
        entity.WatchedAttributes.SetBool(SickAttr, true);
        entity.WatchedAttributes.SetString(SickCauseAttr, cause);
        LivestockUtil.SetRegenBlocked(entity, "sick", true);
        LivestockUtil.BlockBreeding(entity);
    }

    void Recover(double immunityDays)
    {
        entity.WatchedAttributes.SetBool(SickAttr, false);
        entity.WatchedAttributes.RemoveAttribute(SickCauseAttr);
        entity.WatchedAttributes.SetDouble(ImmuneUntilAttr, entity.World.Calendar.TotalDays + immunityDays);
        LivestockUtil.SetRegenBlocked(entity, "sick", false);
        if (IsCollapsed) StandUp();
    }

    // Shovelling muck next to animals easily hits them instead. This behavior is patched in first,
    // before vanilla "health", so zeroing the damage here means it is never applied. Instead the
    // animal gets shoved aside, so the muck it stands on can be reached.
    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
        // A kept dog never hurts the player's animals, whichever AI (vanilla, Pet AI) made it bite,
        // and the player's animals never hurt the player's dog.
        if (damageSource.Type != EnumDamageType.Heal && damageSource.GetCauseEntity() is { } cause
            && (cause.GetBehavior<EntityBehaviorLivestockDog>() is { } dog && dog.Spares(entity) || IsKeptAnimalHittingKeptDog(cause)))
        {
            damage = 0;
            return;
        }
        if (Settings is not { ShovelsHarmless: true } || damageSource.Type == EnumDamageType.Heal) return;
        if (damageSource.GetCauseEntity() is not EntityPlayer player) return;
        if (player.RightHandItemSlot?.Itemstack?.Collectible?.Tool != EnumTool.Shovel) return;

        damage = 0;
        damageSource.KnockbackStrength = 0;

        var push = entity.Pos.XYZ.Sub(player.Pos.XYZ);
        push.Y = 0;
        if (push.Length() < 0.01) return;
        push.Normalize();
        entity.Pos.Motion.Add(push.X * ShovePower, 0.08, push.Z * ShovePower);
    }

    const double ShovePower = 0.25;

    // Dust bathing (chickens): see HygieneSettings.DustBathing and AiTaskLivestockDustBath.
    const string LastDustBathAttr = "lwLastDustBathHours";
    // Synced, so the info text can say why it doesn't bathe.
    const string NoDustBathAttr = "lwNoDustBath";
    const double CatchUpBathHours = 3;
    // Below eating, drinking and shelter (1.25-1.36), above idling and wandering.
    const float DustBathPriority = 1.2f;
    // Above sheltering, laying and eating, below being led on a rope (1.45) and getting out of
    // water (1.4). The vanilla run from players (1.5) is held back meanwhile.
    const float FollowFoodPriority = 1.39f;

    // Treats (see TreatSettings): it walks after a player holding grain, at a run if it has one.
    void AddFollowFood(EntityAgent agent, EntityBehaviorTaskAI taskAi)
    {
        bool canRun = entity.Properties.Client?.Animations?.Any(a => a.Code == "run") == true;
        AiTaskLivestockFollowFood? follow = null;
        LivestockUtil.AddAiTask(agent, AiTaskLivestockFollowFood.Code, FollowFoodPriority, canRun ? 0.015f : walkSpeed * 2, 0, 0,
            (taskConfig, aiConfig) => follow = new AiTaskLivestockFollowFood(agent, taskConfig, aiConfig), canRun ? "run" : "walk");
        taskAi.TaskManager.OnShouldExecuteTask += task => task is not (AiTaskFleeEntity or AiTaskFleeEntityR) || follow?.KeepsFromFleeing != true;
    }
    float walkSpeed;

    bool DustBathes(HygieneSettings settings) => SelfCleaning && settings.DustBathing && !KeptClean;

    double HoursSinceDustBath => entity.World.Calendar.TotalHours - entity.WatchedAttributes.GetDouble(LastDustBathAttr, entity.World.Calendar.TotalHours);

    /// <summary>Whether it is time for a dust bath: a little dirty, or long enough since the last one. Only by day.</summary>
    public bool WantsDustBath(HygieneSettings settings) =>
        DustBathes(settings) && (Filth >= settings.DustBathAboveFilth || HoursSinceDustBath >= settings.DustBathEveryHours)
        && entity.World.Calendar.GetDayLightStrength(entity.Pos.AsBlockPos) > 0.5f;

    /// <summary>
    /// The nearest dry sand within the radius with room above it to bathe, skipping those isFailed
    /// rejects. Sand open to the sky while it rains is mud. Server side.
    /// </summary>
    public BlockPos? FindDustBath(HygieneSettings settings, System.Func<BlockPos, bool> isFailed)
    {
        int r = settings.DustBathRadius;
        var pos = entity.Pos.AsBlockPos;
        var accessor = entity.World.BlockAccessor;
        bool raining = accessor.GetClimateAt(pos, EnumGetClimateMode.NowValues)?.Rainfall > 0.05f;
        var tmpPos = pos.Copy();
        BlockPos? best = null;
        double bestDistance = double.MaxValue;

        accessor.WalkBlocks(pos.AddCopy(-r, -2, -r), pos.AddCopy(r, 1, r), (block, x, y, z) =>
        {
            if (block.Code == null || !WildcardUtil.Match(settings.DustBathBlockCodes, block.Code.ToString())) return;
            if (accessor.GetBlock(tmpPos.Set(x, y + 1, z)).Id != 0) return;
            if (raining && accessor.GetRainMapHeightAt(x, z) <= y) return;

            tmpPos.Set(x, y, z);
            double distance = tmpPos.DistanceSqTo(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
            if (distance >= bestDistance || isFailed(tmpPos)) return;
            bestDistance = distance;
            best = tmpPos.Copy();
        });
        return best;
    }

    /// <summary>After a dust bath: clean, and the clock for the next one starts again.</summary>
    public void DustBathed()
    {
        entity.WatchedAttributes.SetFloat(FilthAttr, 0);
        entity.WatchedAttributes.SetDouble(LastDustBathAttr, entity.World.Calendar.TotalHours);
        entity.WatchedAttributes.SetBool(NoDustBathAttr, false);
    }

    // Only worth telling once it is overdue: then the player should put down some sand.
    void UpdateNoDustBath(HygieneSettings settings)
    {
        bool none = HoursSinceDustBath > settings.UnbathedDays * 24 && FindDustBath(settings, _ => false) == null;
        if (none != entity.WatchedAttributes.GetBool(NoDustBathAttr)) entity.WatchedAttributes.SetBool(NoDustBathAttr, none);
    }

    /// <summary>Rubs off some of the dirt (server side). Returns false if the animal was already clean.</summary>
    public bool Groom(double amount)
    {
        float filth = Filth;
        if (filth <= 0) return false;
        entity.WatchedAttributes.SetFloat(FilthAttr, (float)Math.Max(0, filth - amount));
        return true;
    }

    // Server side: while being groomed or fed by hand, the animal stays put (no AI tasks) and plays
    // an animation, until this time.
    long holdStillUntilMs;
    string? holdStillAnimation;

    /// <summary>
    /// Resting with the legs folded under the body ("sit" in every vanilla livestock shape): used
    /// while groomed or petted, so both sides and the back stay within reach. Lying on the side
    /// ("sleep") is kept for collapsing.
    /// </summary>
    public const string RestAnimation = "sit";

    public bool IsHeldStill => holdStillUntilMs > entity.World.ElapsedMilliseconds;

    /// <summary>
    /// Keeps the animal still for the given time, playing the animation (null: lie down, as when
    /// collapsed). Called again while the player keeps going, to extend the time.
    /// </summary>
    public void HoldStill(float seconds, string? animation = null)
    {
        animation ??= collapseAnimation;
        bool already = IsHeldStill && holdStillAnimation == animation;
        holdStillUntilMs = entity.World.ElapsedMilliseconds + (long)(seconds * 1000);
        if (already) return;

        if (holdStillAnimation != null) entity.AnimManager.StopAnimation(holdStillAnimation);
        holdStillAnimation = animation;
        entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager.StopTasks();
        (entity as EntityAgent)?.Controls.StopAllMovement();
        if (!IsCollapsed) StartAnimation(animation);
    }

    // Plays one of the shape's animations, also those the entity type lists no settings for (e.g.
    // "eat", which vanilla only plays through an AI task).
    void StartAnimation(string code)
    {
        if (entity.AnimManager.StartAnimation(code)) return;
        entity.AnimManager.StartAnimation(new AnimationMetaData { Code = code, Animation = code }.Init());
    }

    /// <summary>Lets the animal move on (e.g. once it is clean).</summary>
    public void StopHoldingStill()
    {
        holdStillUntilMs = 0;
        if (holdStillAnimation == null) return;
        if (!IsCollapsed)
        {
            entity.AnimManager.StopAnimation(holdStillAnimation);
            entity.AnimManager.StartAnimation("idle");
        }
        holdStillAnimation = null;
    }

    void LieDown()
    {
        entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager.StopTasks();
        (entity as EntityAgent)?.Controls.StopAllMovement();
        entity.GetBehavior<EntityBehaviorEmotionStates>()?.ClearStates();
        if (!entity.AnimManager.IsAnimationActive(collapseAnimation)) entity.AnimManager.StartAnimation(collapseAnimation);
    }

    void StandUp()
    {
        entity.WatchedAttributes.SetBool(CollapsedAttr, false);
        entity.AnimManager.StopAnimation(collapseAnimation);
        entity.AnimManager.StartAnimation("idle");
    }

    /// <summary>Sickness and filth status for the info text and chat, e.g. "Sick, filth: 45% (dirty surroundings)".</summary>
    /// <param name="langCode">Language to use, or null for the local game language.</param>
    public string StatusText(string? langCode = null)
    {
        var parts = new StringBuilder();
        if (IsSick)
        {
            parts.Append(LivestockUtil.Translate(langCode, "livestockwelfare:sick"));
            if (entity.WatchedAttributes.GetString(SickCauseAttr) is { Length: > 0 } cause)
            {
                parts.Append(" (").Append(LivestockUtil.Translate(langCode, "livestockwelfare:sick-" + cause)).Append(')');
            }
            parts.Append(", ");
        }
        if (IsCollapsed) parts.Append(LivestockUtil.Translate(langCode, "livestockwelfare:collapsed")).Append(", ");
        // Babies with a mother are never dirty, so only their sickness is worth showing.
        if (KeptClean) return parts.Length == 0 ? "" : parts.ToString(0, parts.Length - 2);

        float filth = Filth;
        parts.Append(LivestockUtil.Translate(langCode, "livestockwelfare:filth-percent", (int)Math.Round(filth * 100)));

        string? filthKey = filth >= 0.66f ? "livestockwelfare:filth-high" : filth >= 0.33f ? "livestockwelfare:filth-medium" : null;
        if (filthKey != null) parts.Append(" (").Append(LivestockUtil.Translate(langCode, filthKey)).Append(')');
        if (entity.WatchedAttributes.GetBool(NoDustBathAttr)) parts.Append(", ").Append(LivestockUtil.Translate(langCode, "livestockwelfare:dustbath-none"));

        return parts.ToString();
    }

    // The carcass text ("Died of disease") comes from vanilla, see LivestockUtil.DeathByEntityAttr.
    public override void GetInfoText(StringBuilder infotext)
    {
        if (!entity.Alive) return;

        if (StatusText() is { Length: > 0 } status) infotext.AppendLine(status);
    }
}
