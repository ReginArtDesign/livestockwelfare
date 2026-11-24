using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Finds skinned carcasses near players (CarcassSettings) and offers them to predators as food, the
/// way a trough is offered: vanilla AiTaskSeekFoodAndEat walks to them and eats one meal at a time.
/// Every few seconds the area around each player is looked through, new carcasses are registered as
/// food and gone ones removed.
/// </summary>
public class CarcassFeeding : ModSystem
{
    const int ScanIntervalMs = 5000;
    // As far as vanilla animals look for food (AiTaskSeekFoodAndEat).
    const int ScanRadius = 48;

    ICoreServerAPI sapi = null!;
    POIRegistry poiRegistry = null!;
    readonly Dictionary<string, CarcassFoodSource> registered = new();

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        poiRegistry = api.ModLoader.GetModSystem<POIRegistry>();
        api.Event.RegisterGameTickListener(_ => Scan(), ScanIntervalMs, 1000);
        hungerPatches = new HarmonyLib.Harmony(Mod.Info.ModID + ".carcasshunger");
        var postfix = new HarmonyLib.HarmonyMethod(typeof(CarcassFeeding), nameof(ShouldEatPostfix));
        // EntityBehaviorMultiply (wolves, dogs) overrides the base getter, so both are patched.
        hungerPatches.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(EntityBehaviorMultiplyBase), nameof(EntityBehaviorMultiplyBase.ShouldEat)), postfix: postfix);
        hungerPatches.Patch(HarmonyLib.AccessTools.PropertyGetter(typeof(EntityBehaviorMultiply), nameof(EntityBehaviorMultiply.ShouldEat)), postfix: postfix);
    }

    HarmonyLib.Harmony? hungerPatches;

    /// <summary>
    /// Vanilla lets a female stop eating once she has eaten enough to breed, until she has had young.
    /// A kept dog walked to the carcass on the rare chance vanilla still gives her and then turned
    /// away without eating. A predator that has gone CarcassSettings.HungryAfterHours without a meal
    /// eats again (carcass, bowl or trough).
    /// </summary>
    static void ShouldEatPostfix(EntityBehavior __instance, ref bool __result)
    {
        if (__result || livestockwelfareModSystem.Config?.Carcasses is not { Enabled: true, HungryAfterHours: > 0 } settings) return;
        var entity = __instance.entity;
        if (entity?.World == null || !IsCarcassEater(entity, DietOf(entity))) return;
        double lastMeal = entity.WatchedAttributes.GetDouble(LastMealAttr, double.NaN);
        if (!double.IsNaN(lastMeal) && entity.World.Calendar.TotalHours - lastMeal < settings.HungryAfterHours) return;
        __result = true;
    }

    // Vanilla AiTaskSeekFoodAndEat notes each meal here.
    const string LastMealAttr = "lastMealEatenTotalHours";

    static readonly Dictionary<string, CreatureDiet?> diets = new();

    static CreatureDiet? DietOf(Entity entity)
    {
        string code = entity.Code.ToString();
        if (!diets.TryGetValue(code, out var diet))
        {
            diet = entity.Properties.Attributes?["creatureDiet"].AsObject<CreatureDiet>();
            diets[code] = diet;
        }
        return diet;
    }

    public override void Dispose()
    {
        registered.Clear();
        diets.Clear();
        hungerPatches?.UnpatchAll(hungerPatches.Id);
    }

    /// <summary>Looks for carcasses around the online players. Public for the self-test.</summary>
    public void Scan()
    {
        var settings = livestockwelfareModSystem.Config?.Carcasses;
        foreach (var gone in registered.Where(r => settings?.Enabled != true || !r.Value.Exists).ToList())
        {
            poiRegistry.RemovePOI(gone.Value);
            registered.Remove(gone.Key);
        }
        if (settings?.Enabled != true) return;

        foreach (var player in sapi.World.AllOnlinePlayers)
        {
            if (player.Entity is not { } playerEntity) continue;
            ScanAround(playerEntity.Pos.XYZ, settings);
        }
    }

    /// <summary>Registers the carcasses within ScanRadius of this position. Public for the self-test.</summary>
    public void ScanAround(Vec3d center, CarcassSettings settings)
    {
        foreach (var entity in sapi.World.GetEntitiesAround(center, ScanRadius, ScanRadius / 2, e => !e.Alive || e is EntityItem))
        {
            string key = "e" + entity.EntityId;
            if (registered.ContainsKey(key)) continue;
            CarcassFoodSource? source = entity switch
            {
                EntityItem item when IsCarcassItem(item.Itemstack, settings) => new LooseCarcass(item),
                not EntityItem when IsSkinnedCorpse(entity) => new SkinnedCorpse(entity),
                _ => null,
            };
            if (source != null) Register(key, source);
        }

        const int size = GlobalConstants.ChunkSize;
        int radius = ScanRadius / size + 1;
        int cx = (int)center.X / size, cy = (int)center.Y / size, cz = (int)center.Z / size;
        for (int x = cx - radius; x <= cx + radius; x++)
        for (int y = Math.Max(0, cy - 1); y <= cy + 1; y++)
        for (int z = cz - radius; z <= cz + radius; z++)
        {
            if (sapi.WorldManager.GetChunk(x, y, z)?.BlockEntities is not { } blockEntities) continue;
            foreach (var be in blockEntities.Values)
            {
                if (be is not BlockEntityGroundStorage storage) continue;
                string key = "b" + storage.Pos;
                if (registered.ContainsKey(key) || !storage.Inventory.Any(slot => IsCarcassItem(slot.Itemstack, settings))) continue;
                Register(key, new StoredCarcass(storage));
            }
        }
    }

    void Register(string key, CarcassFoodSource source)
    {
        registered[key] = source;
        poiRegistry.AddPOI(source);
    }

    static bool IsSkinnedCorpse(Entity entity) =>
        !entity.Alive && entity.WatchedAttributes.GetBool("harvested") && entity.State != EnumEntityState.Despawned
        && entity.Properties.Attributes?["isMechanical"].AsBool() != true;

    // A Butchering mod body ("butcherable" item class), in any domain: Butchering adds bodies for
    // other mods' animals in their domains, e.g. "tradlivestock:deadsheep-female-1-bledout".
    public static bool IsCarcassItem(ItemStack? stack, CarcassSettings settings) =>
        stack?.Collectible is { } collectible && collectible.GetType().Name == "ItemButcherable"
        && collectible.Variant["state"] is { } state && settings.ButcheringStates.Contains(state);

    /// <summary>A predator that may eat carcasses: a meat eater, kept by the player unless wild ones are allowed too.</summary>
    public static bool IsCarcassEater(Entity entity, CreatureDiet? diet)
    {
        var settings = livestockwelfareModSystem.Config?.Carcasses;
        if (settings?.Enabled != true || diet == null || !entity.Alive) return false;
        meat ??= entity.World.GetItem(new AssetLocation("game:bushmeat-raw")) is { } item ? new ItemStack(item) : null;
        return meat != null && diet.Matches(meat) && (settings.WildPredatorsToo || LivestockUtil.IsKept(entity));
    }

    static ItemStack? meat;

    /// <summary>
    /// Puts the skeleton block down at this position, or next to it if that is taken, like vanilla
    /// EntityBehaviorDeadDecay does when a corpse has rotted away.
    /// </summary>
    public static void PlaceSkeleton(IWorldAccessor world, BlockPos pos, string skeletonCode)
    {
        var block = world.GetBlock(new AssetLocation(skeletonCode)) ?? world.GetBlock(new AssetLocation(CarcassFoodSource.DefaultSkeleton));
        if (block == null) return;
        var accessor = world.BlockAccessor;
        foreach (var candidate in new[] { pos }.Concat(BlockFacing.HORIZONTALS.Select(f => pos.AddCopy(f))))
        {
            if (!accessor.GetBlock(candidate).IsReplacableBy(block)) continue;
            accessor.SetBlock(block.BlockId, candidate);
            accessor.MarkBlockDirty(candidate);
            return;
        }
    }
}

/// <summary>A carcass as a food source: so many meals, then a skeleton.</summary>
public abstract class CarcassFoodSource : IAnimalFoodSource
{
    public const string DefaultSkeleton = "game:carcass-medium";
    /// <summary>Meals eaten so far, kept on the carcass so it survives a restart.</summary>
    public const string EatenAttr = "lwCarcassMealsEaten";

    public abstract Vec3d Position { get; }
    public string Type => "food";

    /// <summary>Whether the carcass is still there to eat.</summary>
    public abstract bool Exists { get; }
    /// <summary>The vanilla bone carcass block that is left, e.g. "game:carcass-medium".</summary>
    public abstract string SkeletonCode { get; }
    protected abstract IWorldAccessor World { get; }
    protected abstract int Eaten { get; set; }
    /// <summary>Takes the eaten carcass away and leaves the skeleton.</summary>
    protected abstract void LeaveSkeleton();

    public int Meals
    {
        get
        {
            var meals = livestockwelfareModSystem.Config?.Carcasses.MealsBySize;
            string size = new AssetLocation(SkeletonCode).Path.Split('-').Last();
            return meals != null && meals.TryGetValue(size, out int count) ? count : 3;
        }
    }

    public int MealsLeft => Math.Max(0, Meals - Eaten);

    // A full animal is not sent: vanilla would walk it here and then turn away without eating.
    public bool IsSuitableFor(Entity entity, CreatureDiet diet) => Exists && MealsLeft > 0 && CarcassFeeding.IsCarcassEater(entity, diet)
        && entity.GetBehavior<EntityBehaviorMultiplyBase>() is not { ShouldEat: false };

    public float ConsumeOnePortion(Entity entity)
    {
        if (!Exists || MealsLeft <= 0) return 0;
        Eaten++;
        World.Logger.Debug("[livestockwelfare] {0} #{1} ate from a carcass at {2}, {3} of {4} meals left", entity.Code, entity.EntityId, Position, MealsLeft, Meals);
        if (MealsLeft <= 0)
        {
            World.Logger.Debug("[livestockwelfare] {0} #{1} finished a carcass at {2}, leaving {3}", entity.Code, entity.EntityId, Position, SkeletonCode);
            LeaveSkeleton();
        }
        return 1;
    }
}

/// <summary>A corpse cut open with a knife (vanilla harvesting) and left lying.</summary>
public class SkinnedCorpse : CarcassFoodSource
{
    readonly Entity corpse;

    public SkinnedCorpse(Entity corpse) => this.corpse = corpse;

    public override Vec3d Position => corpse.Pos.XYZ;
    public override bool Exists => !corpse.Alive && corpse.State != EnumEntityState.Despawned
        && corpse.World.GetEntityById(corpse.EntityId) == corpse && corpse.DespawnReason?.Reason != EnumDespawnReason.Removed
        && !(corpse.GetBehavior<EntityBehaviorDeadDecay>() is { } decay && Decayed(decay));
    protected override IWorldAccessor World => corpse.World;

    protected override int Eaten
    {
        get => corpse.WatchedAttributes.GetInt(EatenAttr);
        set => corpse.WatchedAttributes.SetInt(EatenAttr, value);
    }

    // DecayNow lets the corpse despawn, so it is gone (as far as eating goes) from then on.
    static bool Decayed(EntityBehaviorDeadDecay decay) => decay.entity is EntityAgent { AllowDespawn: true };

    /// <summary>The skeleton the animal leaves when it rots away (its "deaddecay" decayedBlock).</summary>
    public override string SkeletonCode
    {
        get
        {
            var decay = corpse.Properties.Server?.BehaviorsAsJsonObj?.FirstOrDefault(b => b["code"].AsString() == "deaddecay");
            if (decay?["decayedBlock"].AsString() is { } code) return new AssetLocation(code).ToString();
            // No skeleton of its own: one by its size.
            float width = corpse.Properties.DeadCollisionBoxSize.X;
            return "game:carcass-" + (width < 0.5f ? "small" : width < 1.3f ? "medium" : "large");
        }
    }

    protected override void LeaveSkeleton()
    {
        if (corpse.GetBehavior<EntityBehaviorDeadDecay>() is { } decay && corpse.Properties.Server?.BehaviorsAsJsonObj?
            .Any(b => b["code"].AsString() == "deaddecay" && b["decayedBlock"].Exists) == true)
        {
            decay.DecayNow();
            return;
        }
        CarcassFeeding.PlaceSkeleton(corpse.World, corpse.Pos.AsBlockPos, SkeletonCode);
        // Die() does nothing to a dead entity, so it is despawned directly.
        (corpse.World as IServerWorldAccessor)?.DespawnEntity(corpse, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
    }
}

/// <summary>A Butchering mod body thrown on the ground (an item entity).</summary>
public class LooseCarcass : CarcassFoodSource
{
    readonly EntityItem item;

    public LooseCarcass(EntityItem item) => this.item = item;

    public override Vec3d Position => item.Pos.XYZ;
    public override bool Exists => item.Alive && item.Itemstack != null && item.World.GetEntityById(item.EntityId) == item;
    protected override IWorldAccessor World => item.World;
    public override string SkeletonCode => item.Itemstack?.Attributes.GetString("AnimalCarcass") ?? DefaultSkeleton;

    protected override int Eaten
    {
        get => item.Itemstack?.Attributes.GetInt(EatenAttr) ?? 0;
        set => item.Itemstack?.Attributes.SetInt(EatenAttr, value);
    }

    protected override void LeaveSkeleton()
    {
        string skeleton = SkeletonCode;
        item.Die(EnumDespawnReason.Removed);
        CarcassFeeding.PlaceSkeleton(item.World, item.Pos.AsBlockPos, skeleton);
    }
}

/// <summary>A Butchering mod body put down on the ground (ground storage, Ctrl + right click).</summary>
public class StoredCarcass : CarcassFoodSource
{
    readonly BlockEntityGroundStorage storage;

    public StoredCarcass(BlockEntityGroundStorage storage) => this.storage = storage;

    ItemSlot? Slot => livestockwelfareModSystem.Config?.Carcasses is { } settings
        ? storage.Inventory.FirstOrDefault(slot => CarcassFeeding.IsCarcassItem(slot.Itemstack, settings))
        : null;

    public override Vec3d Position => storage.Pos.ToVec3d().Add(0.5, 0.1, 0.5);
    public override bool Exists => storage.Api?.World.BlockAccessor.GetBlockEntity(storage.Pos) == storage && Slot != null;
    protected override IWorldAccessor World => storage.Api.World;
    public override string SkeletonCode => Slot?.Itemstack?.Attributes.GetString("AnimalCarcass") ?? DefaultSkeleton;

    protected override int Eaten
    {
        get => Slot?.Itemstack?.Attributes.GetInt(EatenAttr) ?? 0;
        set
        {
            if (Slot is not { } slot) return;
            slot.Itemstack?.Attributes.SetInt(EatenAttr, value);
            storage.MarkDirty();
        }
    }

    protected override void LeaveSkeleton()
    {
        if (Slot is not { } slot) return;
        string skeleton = SkeletonCode;
        slot.Itemstack = null;
        slot.MarkDirty();
        var world = storage.Api.World;
        var pos = storage.Pos.Copy();
        if (storage.Inventory.Empty)
        {
            // The body was all there was: the skeleton takes its place.
            world.BlockAccessor.SetBlock(0, pos);
        }
        else
        {
            storage.MarkDirty(true);
        }
        CarcassFeeding.PlaceSkeleton(world, pos, skeleton);
    }
}
