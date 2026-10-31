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

        GetThirsty(settings, hours);
        if (Thirst > settings.DrinkAboveThirst) TryDrink(settings);
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
        if (water.Container == null) return;

        var closestContainer = water.Container;
        var slot = WaterSlot(closestContainer)!;
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
    record WaterSearch(bool Natural, BlockEntityLiquidContainer? Container, bool SawUndrinkable);

    WaterSearch FindWater(ThirstSettings settings)
    {
        int r = settings.DrinkRadius;
        var pos = entity.Pos.AsBlockPos;
        var accessor = entity.World.BlockAccessor;
        var tmpPos = pos.Copy();

        bool natural = false, sawUndrinkable = false;
        BlockEntityLiquidContainer? closest = null;
        double closestDistance = double.MaxValue;

        accessor.WalkBlocks(pos.AddCopy(-r, -2, -r), pos.AddCopy(r, 1, r), (block, x, y, z) =>
        {
            if (block.IsLiquid())
            {
                if (block.LiquidCode == "water") natural |= settings.DrinkFromNaturalWater;
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

        return new WaterSearch(natural, closest, sawUndrinkable);
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
        if (water.Natural) return LivestockUtil.Translate(langCode, "livestockwelfare:water-natural");
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

        health.Health -= (float)(settings.HarmPerDay * hours / 24);
        if (health.Health <= 0)
        {
            health.Health = 0;
            entity.World.Logger.Notification("[livestockwelfare] {0} #{1} died of thirst", entity.Code, entity.EntityId);
            LivestockUtil.Kill(entity, LivestockUtil.ThirstDeathCause);
        }
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
