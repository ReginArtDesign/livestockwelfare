using System;
using System.Collections.Generic;
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
    const string ImmuneUntilAttr = "lwImmuneUntilDays";
    const string LastUpdateAttr = "lwHygieneLastHours";
    // Badly sick animal lying on the ground until it recovers or dies.
    const string CollapsedAttr = "lwCollapsed";

    const float CheckIntervalSeconds = 5;
    // Longer gaps (e.g. the chunk was unloaded) are not simulated: the surroundings back then are unknown.
    const double MaxCatchUpHours = 24;

    string species = "";
    string collapseAnimation = "sleep";
    float accum;

    public EntityBehaviorLivestockHygiene(Entity entity) : base(entity) { }

    public override string PropertyName() => Code;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        species = attributes["species"].AsString("");
        collapseAnimation = attributes["collapseAnimation"].AsString("sleep");
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

    // Flies buzz around very dirty and sick animals (client side only).
    const float FliesFromFilth = 0.66f;
    float fliesAccum;
    static SimpleParticleProperties? flies;

    void SpawnFlies(float deltaTime)
    {
        fliesAccum += deltaTime;
        if (fliesAccum < 0.3f) return;
        fliesAccum = 0;
        if (!IsSick && Filth < FliesFromFilth) return;

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

        // Same approach as vanilla EntityBehaviorMortallyWoundable: no AI tasks while lying down.
        var taskAi = entity.GetBehavior<EntityBehaviorTaskAI>();
        if (taskAi != null) taskAi.TaskManager.OnShouldExecuteTask += _ => !IsCollapsed;
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
    public float Filth => entity.WatchedAttributes.GetFloat(FilthAttr);
    public bool IsSick => entity.WatchedAttributes.GetBool(SickAttr);
    public bool IsCollapsed => entity.WatchedAttributes.GetBool(CollapsedAttr);

    float WeatherStress => entity.GetBehavior<EntityBehaviorLivestockShelter>()?.Stress ?? 0;

    bool IsImmune => entity.WatchedAttributes.GetDouble(ImmuneUntilAttr) > entity.World.Calendar.TotalDays;

    static HygieneSettings? Settings =>
        livestockwelfareModSystem.Config?.Hygiene is { Enabled: true } settings ? settings : null;

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

        accum += deltaTime;
        if (accum < CheckIntervalSeconds) return;
        accum = 0;

        // Something (e.g. being pushed or hit) may have started other movement or animations.
        if (IsCollapsed) LieDown();

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

        int extraAnimals = ExtraAnimalsNearby(settings);
        TryDepositMuck(settings, hours, extraAnimals);
        UpdateFilth(settings, hours, extraAnimals);
        if (IsSick) UpdateSick(settings, hours);
        else TryFallSick(settings, hours);
    }

    void UpdateFilth(HygieneSettings settings, double hours, int extraAnimals)
    {
        double dirt = Math.Min(1, SurroundingsDirt(settings) + extraAnimals * settings.DirtPerExtraAnimal);
        double filth = Filth;
        // Moves towards the current dirt level; after the adapt time about 63% of the way.
        // Getting dirty is quick, getting clean on its own is slow: that is what grooming is for.
        double adaptHours = dirt > filth ? settings.FilthAdaptHours : settings.FilthCleanHours;
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

    /// <summary>Other livestock nearby beyond the number that is fine (0 if not crowded).</summary>
    int ExtraAnimalsNearby(HygieneSettings settings)
    {
        if (settings.CrowdingRadius <= 0) return 0;

        int others = entity.World.GetEntitiesAround(entity.Pos.XYZ, settings.CrowdingRadius, 2,
            e => e != entity && e.Alive && e.HasBehavior<EntityBehaviorLivestockHygiene>()).Length;
        return Math.Max(0, others - settings.CrowdingFreeAnimals);
    }

    const string MuckCode = "muck";
    const int MaxMuckLevel = 4;

    // Kept animals slowly soil the ground they stand on, faster when crowded. Wild animals don't,
    // so the world doesn't fill up with muck.
    void TryDepositMuck(HygieneSettings settings, double hours, int extraAnimals)
    {
        if (settings.MuckChancePerDay <= 0) return;

        bool kept = entity.WatchedAttributes.GetInt("generation") >= settings.MuckMinGeneration
            || entity.Attributes.GetString("origin") == "playerplaced";
        if (!kept) return;

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
                // Only on open ground: air or trampled-down plants, on top of a solid block.
                bool open = block.Id == 0 || (block.Replaceable >= 5000 && !block.IsLiquid());
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

        if (Roll(chancePerDay, hours)) FallSick();
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

        var health = entity.GetBehavior<EntityBehaviorHealth>();
        if (health == null) return;

        health.Health -= (float)(settings.SickDamagePerDay * hours / 24);
        if (!IsCollapsed && health.Health > 0 && health.Health <= health.MaxHealth * settings.CollapseHealthFraction)
        {
            entity.WatchedAttributes.SetBool(CollapsedAttr, true);
            LieDown();
        }

        if (health.Health <= 0)
        {
            health.Health = 0;
            entity.World.Logger.Notification("[livestockwelfare] {0} #{1} died of disease (filth {2:0.00})", entity.Code, entity.EntityId, Filth);
            LivestockUtil.Kill(entity, LivestockUtil.DiseaseDeathCause);
        }
    }

    void SpreadDisease(HygieneSettings settings, double hours)
    {
        if (settings.ContagionRadius <= 0 || settings.ContagionChancePerDay <= 0) return;

        var others = entity.World.GetEntitiesAround(entity.Pos.XYZ, settings.ContagionRadius, 2, e => e != entity && e.Alive);
        foreach (var other in others)
        {
            var hygiene = other.GetBehavior<EntityBehaviorLivestockHygiene>();
            if (hygiene == null || hygiene.species != species || hygiene.IsSick || hygiene.IsImmune) continue;
            if (Roll(settings.ContagionChancePerDay, hours)) hygiene.FallSick();
        }
    }

    // Converts a daily chance into the chance over the given number of in-game hours.
    bool Roll(double chancePerDay, double hours)
    {
        if (chancePerDay <= 0) return false;
        double chance = 1 - Math.Pow(1 - Math.Min(chancePerDay, 1), hours / 24);
        return entity.World.Rand.NextDouble() < chance;
    }

    void FallSick()
    {
        entity.WatchedAttributes.SetBool(SickAttr, true);
        LivestockUtil.SetRegenBlocked(entity, "sick", true);
        LivestockUtil.BlockBreeding(entity);
    }

    void Recover(double immunityDays)
    {
        entity.WatchedAttributes.SetBool(SickAttr, false);
        entity.WatchedAttributes.SetDouble(ImmuneUntilAttr, entity.World.Calendar.TotalDays + immunityDays);
        LivestockUtil.SetRegenBlocked(entity, "sick", false);
        if (IsCollapsed) StandUp();
    }

    // Shovelling muck next to animals easily hits them instead. This behavior is patched in first,
    // before vanilla "health", so zeroing the damage here means it is never applied. Instead the
    // animal gets shoved aside, so the muck it stands on can be reached.
    public override void OnEntityReceiveDamage(DamageSource damageSource, ref float damage)
    {
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

    /// <summary>Rubs off some of the dirt (server side). Returns false if the animal was already clean.</summary>
    public bool Groom(double amount)
    {
        float filth = Filth;
        if (filth <= 0) return false;
        entity.WatchedAttributes.SetFloat(FilthAttr, (float)Math.Max(0, filth - amount));
        return true;
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
        if (IsSick) parts.Append(LivestockUtil.Translate(langCode, "livestockwelfare:sick")).Append(", ");
        if (IsCollapsed) parts.Append(LivestockUtil.Translate(langCode, "livestockwelfare:collapsed")).Append(", ");

        float filth = Filth;
        parts.Append(LivestockUtil.Translate(langCode, "livestockwelfare:filth-percent", (int)Math.Round(filth * 100)));

        string? filthKey = filth >= 0.66f ? "livestockwelfare:filth-high" : filth >= 0.33f ? "livestockwelfare:filth-medium" : null;
        if (filthKey != null) parts.Append(" (").Append(LivestockUtil.Translate(langCode, filthKey)).Append(')');

        return parts.ToString();
    }

    // The carcass text ("Died of disease") comes from vanilla, see LivestockUtil.DeathByEntityAttr.
    public override void GetInfoText(StringBuilder infotext)
    {
        if (!entity.Alive) return;

        infotext.AppendLine(StatusText());
    }
}
