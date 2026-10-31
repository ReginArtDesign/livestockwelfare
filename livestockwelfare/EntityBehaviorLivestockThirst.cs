using System;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Thirst: grows over time, faster when warm and in the hot sun. A thirsty animal drinks from
/// fresh water nearby (free) or from a barrel or placed bucket with water (the water is used up).
/// Very thirsty animals don't breed; parched ones stop healing and slowly lose health.
/// </summary>
public class EntityBehaviorLivestockThirst : EntityBehavior
{
    public const string Code = "livestockwelfare.thirst";

    const string ThirstAttr = "lwThirst";
    const string LastUpdateAttr = "lwThirstLastHours";

    const float CheckIntervalSeconds = 5;
    // Longer gaps (e.g. the chunk was unloaded) are not simulated, like the other behaviors.
    const double MaxCatchUpHours = 24;
    static readonly AssetLocation WaterPortion = new("game", "waterportion");

    string species = "";
    float accum;

    public EntityBehaviorLivestockThirst(Entity entity) : base(entity) { }

    public override string PropertyName() => Code;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        species = attributes["species"].AsString("");
        walkSpeed = attributes["walkSpeed"].AsFloat(0.006f);
    }

    float walkSpeed;
    // Just above vanilla eating (1.25-1.35 depending on species): a thirsty animal goes to drink first.
    const float SeekWaterPriority = 1.36f;

    public override void AfterInitialized(bool onFirstSpawn)
    {
        base.AfterInitialized(onFirstSpawn);
        if (entity.World.Side != EnumAppSide.Server || entity is not EntityAgent agent) return;
        if (livestockwelfareModSystem.Config?.Thirst is not { Enabled: true, WalkToWater: true }) return;

        LivestockUtil.AddAiTask(agent, AiTaskLivestockSeekWater.Code, SeekWaterPriority, walkSpeed, 0.25, 0.5,
            (taskConfig, aiConfig) => new AiTaskLivestockSeekWater(agent, taskConfig, aiConfig));
    }

    public float Thirst => entity.WatchedAttributes.GetFloat(ThirstAttr);

    static ThirstSettings? Settings =>
        livestockwelfareModSystem.Config?.Thirst is { Enabled: true } settings ? settings : null;

    public override void OnGameTick(float deltaTime)
    {
        if (entity.World.Side != EnumAppSide.Server || !entity.Alive) return;

        accum += deltaTime;
        if (accum < CheckIntervalSeconds) return;
        accum = 0;

        var settings = Settings;
        if (settings == null)
        {
            if (Thirst > 0) entity.WatchedAttributes.SetFloat(ThirstAttr, 0);
            LivestockUtil.SetRegenBlocked(entity, "thirst", false);
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

        GetThirsty(settings, hours);
        // With walking to water, drinking from where they stand is only the fallback for animals
        // that can't get there (e.g. a barrel just outside the fence).
        double drinkAbove = settings.WalkToWater ? Math.Max(settings.DrinkAboveThirst, settings.RemoteDrinkAboveThirst) : settings.DrinkAboveThirst;
        if (Thirst > drinkAbove) TryDrink(settings);
        ApplyEffects(settings, hours);
    }

    void GetThirsty(ThirstSettings settings, double hours)
    {
        double rate = settings.ThirstPerDay;

        var climate = entity.World.BlockAccessor.GetClimateAt(entity.Pos.AsBlockPos, EnumGetClimateMode.NowValues);
        if (climate != null && climate.Temperature > settings.WarmAboveC)
        {
            rate *= 1 + (climate.Temperature - settings.WarmAboveC) * 0.1;
        }
        if (entity.GetBehavior<EntityBehaviorLivestockShelter>()?.InHotSun == true) rate *= settings.HotSunMultiplier;

        SetThirst(Thirst + rate * hours / 24);
    }

    void SetThirst(double thirst) => entity.WatchedAttributes.SetFloat(ThirstAttr, (float)Math.Clamp(thirst, 0, 1));

    void TryDrink(ThirstSettings settings)
    {
        var water = FindWater(settings);

        // Ponds and rivers never run dry, so prefer them and keep the barrels for when there is none.
        if (water.Natural)
        {
            SetThirst(0);
            return;
        }
        if (water.Container != null) DrinkFrom(water.Container);
    }

    /// <summary>Drinks from a container, or from fresh water if it is null.</summary>
    public void DrinkFrom(BlockEntityLiquidContainer? container)
    {
        var settings = Settings;
        if (settings == null) return;
        if (container == null)
        {
            SetThirst(0);
            return;
        }

        var closestContainer = container;
        var slot = WaterSlot(closestContainer);
        if (slot == null) return;
        int itemsPerLitre = BlockLiquidContainerBase.GetContainableProps(slot.Itemstack)?.ItemsPerLitre is > 0 and var ipl ? (int)ipl : 100;
        double thirst = Thirst;
        int wanted = (int)Math.Ceiling(thirst * settings.LitresFor(species) * itemsPerLitre);
        int taken = Math.Min(wanted, slot.StackSize);
        if (taken <= 0) return;

        slot.TakeOut(taken);
        slot.MarkDirty();
        closestContainer.MarkDirty(true);
        // Not enough water left: drink what there is and stay a bit thirsty.
        SetThirst(thirst * (1 - (double)taken / wanted));
    }

    /// <summary>What water the animal can reach: fresh water nearby, or the closest container with water.</summary>
    record WaterSearch(bool Natural, BlockEntityLiquidContainer? Container, bool SawUndrinkable, string? NaturalBlock = null, Vec3i? NaturalOffset = null);

    WaterSearch FindWater(ThirstSettings settings)
    {
        int r = settings.DrinkRadius;
        var pos = entity.Pos.AsBlockPos;
        var accessor = entity.World.BlockAccessor;
        var tmpPos = pos.Copy();

        bool natural = false, sawUndrinkable = false;
        string? naturalBlock = null;
        Vec3i? naturalOffset = null;
        BlockEntityLiquidContainer? closest = null;
        double closestDistance = double.MaxValue;

        accessor.WalkBlocks(pos.AddCopy(-r, -2, -r), pos.AddCopy(r, 1, r), (block, x, y, z) =>
        {
            if (block.IsLiquid())
            {
                if (block.LiquidCode == "water" && settings.DrinkFromNaturalWater)
                {
                    if (!natural) { naturalBlock = block.Code?.ToShortString(); naturalOffset = new Vec3i(x - pos.X, y - pos.Y, z - pos.Z); }
                    natural = true;
                }
                else if (UndrinkableLiquids.Contains(block.LiquidCode)) sawUndrinkable = true;
                return;
            }
            if (block.EntityClass == null) return;

            if (accessor.GetBlockEntity(tmpPos.Set(x, y, z)) is not BlockEntityLiquidContainer container) return;
            if (container is BlockEntityBarrel { Sealed: true }) return;
            if (WaterSlot(container) == null)
            {
                sawUndrinkable |= HoldsUndrinkable(container);
                return;
            }

            double distance = tmpPos.DistanceSqTo(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = container;
            }
        });

        return new WaterSearch(natural, closest, sawUndrinkable, naturalBlock, naturalOffset);
    }

    /// <summary>
    /// For the walk-to-water AI: the nearest place to drink within the radius, either surface fresh
    /// water or a container with water. Returns the block to walk to and the container (null = fresh water).
    /// </summary>
    public (BlockPos Pos, BlockEntityLiquidContainer? Container)? FindDrinkTarget(int radius, System.Func<BlockPos, bool> isFailed)
    {
        var settings = Settings;
        if (settings == null) return null;

        var pos = entity.Pos.AsBlockPos;
        var accessor = entity.World.BlockAccessor;
        var tmpPos = pos.Copy();
        BlockPos? bestPos = null;
        BlockEntityLiquidContainer? bestContainer = null;
        double bestDistance = double.MaxValue;

        accessor.WalkBlocks(pos.AddCopy(-radius, -3, -radius), pos.AddCopy(radius, 2, radius), (block, x, y, z) =>
        {
            tmpPos.Set(x, y, z);
            BlockEntityLiquidContainer? container = null;
            if (block.IsLiquid())
            {
                // Only surface water an animal can reach from the bank.
                if (!settings.DrinkFromNaturalWater || block.LiquidCode != "water") return;
                if (accessor.GetBlock(tmpPos.UpCopy()).Id != 0) return;
            }
            else if (block.EntityClass != null)
            {
                container = accessor.GetBlockEntity(tmpPos) as BlockEntityLiquidContainer;
                if (container == null || container is BlockEntityBarrel { Sealed: true } || WaterSlot(container) == null) return;
            }
            else return;

            double distance = tmpPos.DistanceSqTo(entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
            if (distance >= bestDistance || isFailed(tmpPos)) return;
            bestDistance = distance;
            bestPos = tmpPos.Copy();
            bestContainer = container;
        });

        return bestPos == null ? null : (bestPos, bestContainer);
    }

    // Hot spring water and sea water: animals refuse them, so tell the player why they don't drink.
    static readonly string[] UndrinkableLiquids = { "boilingwater", "saltwater" };

    static ItemSlot? WaterSlot(BlockEntityLiquidContainer container)
    {
        foreach (var slot in container.Inventory)
        {
            if (slot.Itemstack?.Collectible?.Code is { } code && code.Equals(WaterPortion) && slot.StackSize > 0) return slot;
        }
        return null;
    }

    static bool HoldsUndrinkable(BlockEntityLiquidContainer container)
    {
        foreach (var slot in container.Inventory)
        {
            string? path = slot.Itemstack?.Collectible?.Code?.Path;
            if (path is "boilingwaterportion" or "saltwaterportion") return true;
        }
        return false;
    }

    /// <summary>Where this animal can drink, for the status chat (server side, scans the area).</summary>
    public string WaterSourceText(string langCode)
    {
        if (Settings is not { } settings) return "";
        var water = FindWater(settings);
        // Says which block it found and where, so a stray water block is easy to find.
        if (water.Natural) return LivestockUtil.Translate(langCode, "livestockwelfare:water-natural", water.NaturalBlock ?? "?",
            water.NaturalOffset?.X ?? 0, water.NaturalOffset?.Y ?? 0, water.NaturalOffset?.Z ?? 0);
        if (water.Container != null)
        {
            var slot = WaterSlot(water.Container)!;
            int itemsPerLitre = BlockLiquidContainerBase.GetContainableProps(slot.Itemstack)?.ItemsPerLitre is > 0 and var ipl ? (int)ipl : 100;
            return LivestockUtil.Translate(langCode, "livestockwelfare:water-container", Math.Round((double)slot.StackSize / itemsPerLitre, 1));
        }
        return LivestockUtil.Translate(langCode, water.SawUndrinkable ? "livestockwelfare:water-undrinkable" : "livestockwelfare:water-none", settings.DrinkRadius);
    }

    void ApplyEffects(ThirstSettings settings, double hours)
    {
        float thirst = Thirst;
        if (thirst >= settings.NoBreedingAboveThirst) LivestockUtil.BlockBreeding(entity);

        bool harmed = thirst >= settings.HarmAboveThirst;
        LivestockUtil.SetRegenBlocked(entity, "thirst", harmed);
        if (!harmed) return;

        var health = entity.GetBehavior<EntityBehaviorHealth>();
        if (health == null) return;

        LivestockUtil.Harm(entity, health, settings.HarmPerDay * hours / 24, LivestockUtil.ThirstDeathCause, "thirst");
    }

    /// <summary>Thirst status for the info text and chat, e.g. "Thirst: 70% (very thirsty)".</summary>
    /// <param name="langCode">Language to use, or null for the local game language.</param>
    public string StatusText(string? langCode = null)
    {
        float thirst = Thirst;
        string text = LivestockUtil.Translate(langCode, "livestockwelfare:thirst-percent", (int)Math.Round(thirst * 100));
        string? key = thirst >= 0.95f ? "livestockwelfare:thirst-parched" : thirst >= 0.6f ? "livestockwelfare:thirst-very" : thirst >= 0.25f ? "livestockwelfare:thirst-some" : null;
        return key == null ? text : text + " (" + LivestockUtil.Translate(langCode, key) + ")";
    }

    public override void GetInfoText(StringBuilder infotext)
    {
        if (entity.Alive) infotext.AppendLine(StatusText());
    }
}
