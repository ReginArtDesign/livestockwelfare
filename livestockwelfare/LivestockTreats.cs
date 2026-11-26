using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Treats (see TreatSettings): which animals follow food and take it from the hand, and the feeding
/// itself. A treat counts as a meal from the trough, so it also helps them breed.
/// </summary>
public static class LivestockTreats
{
    // Set by the server on animals that take treats, so the client (which has no config) knows too.
    const string TakesTreatsAttr = "lwTakesTreats";
    const string NextTreatAttr = "lwNextTreatHours";
    const float ChewSeconds = 2.5f;
    static readonly AssetLocation EatSound = new("game", "sounds/creature/animal-eat-small");
    static readonly Dictionary<AssetLocation, CreatureDiet?> diets = new();
    // Server: the ground foods that lure each kind of animal (TreatSettings.LureFoods).
    static readonly Dictionary<AssetLocation, AssetLocation[]> lureFoods = new();

    public static TreatSettings? Settings =>
        livestockwelfareModSystem.Config?.Treats is { Enabled: true } settings ? settings : null;

    /// <summary>
    /// Server: whether this animal is lured by food at all: by any food it eats, which it also takes
    /// from the hand (TreatSettings.AnimalCodes), or, once kept, by a food to set down on the ground
    /// (TreatSettings.LureFoods). Orphaned babies are fed by hand their own way
    /// (EntityBehaviorLivestockNursing).
    /// </summary>
    public static bool Setup(Entity entity)
    {
        bool takes = false;
        lureFoods.Remove(entity.Code);
        if (Settings is { } settings && Diet(entity) != null && !entity.HasBehavior<EntityBehaviorLivestockNursing>())
        {
            takes = settings.AnimalCodes.Any(code => Matches(code, entity.Code));
        }
        if (entity.WatchedAttributes.GetBool(TakesTreatsAttr) != takes) entity.WatchedAttributes.SetBool(TakesTreatsAttr, takes);
        return takes || LureFoods(entity).Length > 0;
    }

    static bool Matches(string code, AssetLocation entityCode) => WildcardUtil.Match(new AssetLocation(code), entityCode);

    public static bool TakesTreats(Entity entity) => entity.Alive && entity.WatchedAttributes.GetBool(TakesTreatsAttr);

    // The ground foods that lure this kind of animal, once kept.
    static AssetLocation[] LureFoods(Entity entity)
    {
        if (!lureFoods.TryGetValue(entity.Code, out var codes))
        {
            codes = Settings is { } settings && !entity.HasBehavior<EntityBehaviorLivestockNursing>()
                ? settings.LureFoods.Where(lure => lure.AnimalCodes.Any(code => Matches(code, entity.Code)))
                    .SelectMany(lure => lure.FoodCodes).Select(code => new AssetLocation(code)).ToArray()
                : Array.Empty<AssetLocation>();
            lureFoods[entity.Code] = codes;
        }
        return codes;
    }

    /// <summary>Server: whether a player holding food would lure this animal now (a wild one only by treats).</summary>
    public static bool IsLured(Entity entity) =>
        entity.Alive && (TakesTreats(entity) || LureFoods(entity).Length > 0 && LivestockUtil.IsKept(entity));

    /// <summary>
    /// Server: food in a player's hand that lures this animal: a treat it takes from the hand, or,
    /// once kept, its ground food (scattered grain for chickens, a hay bale for sheep).
    /// </summary>
    public static bool Lures(Entity entity, ItemStack? stack)
    {
        if (stack == null) return false;
        if (TakesTreats(entity) && Eats(entity, stack)) return true;
        var foods = LureFoods(entity);
        return foods.Length > 0 && foods.Any(code => WildcardUtil.Match(code, stack.Collectible.Code)) && LivestockUtil.IsKept(entity);
    }

    public static CreatureDiet? Diet(Entity entity)
    {
        if (!diets.TryGetValue(entity.Code, out var diet))
        {
            diet = entity.Properties.Attributes?["creatureDiet"].AsObject<CreatureDiet?>(null);
            diets[entity.Code] = diet;
        }
        return diet;
    }

    /// <summary>Food this animal eats from a trough (grain for chickens).</summary>
    public static bool Eats(Entity entity, ItemStack? stack) =>
        stack != null && stack.Collectible is not BlockLiquidContainerBase && Diet(entity)?.Matches(stack) == true;

    /// <summary>Why the animal turns down a treat now (a lang key), or null if it would take it.</summary>
    public static string? Refusal(Entity entity) =>
        entity.World.Calendar.TotalHours < entity.WatchedAttributes.GetDouble(NextTreatAttr) ? "livestockwelfare:handfeed-full" : null;

    /// <summary>Server: the animal takes one item from the player's hand and eats it.</summary>
    public static void HandFeed(Entity entity, ItemSlot slot, EntityAgent byEntity)
    {
        // Refused after the first one, so a held use ended twice by the game feeds only once.
        if (Settings is not { } settings || !TakesTreats(entity) || !Eats(entity, slot.Itemstack) || Refusal(entity) != null) return;

        entity.GetBehavior<EntityBehaviorLivestockMouth>()?.Hold(slot.Itemstack!, ChewSeconds);
        entity.GetBehavior<EntityBehaviorLivestockHygiene>()?.HoldStill(ChewSeconds, "eat");
        if ((byEntity as EntityPlayer)?.Player?.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            slot.TakeOut(1);
            slot.MarkDirty();
        }

        // The same as one portion from a trough (vanilla AiTaskSeekFoodAndEat).
        var attrs = entity.WatchedAttributes;
        double now = entity.World.Calendar.TotalHours;
        var hunger = attrs.GetOrAddTreeAttribute("hunger");
        hunger.SetFloat("saturation", hunger.GetFloat("saturation") + 1);
        attrs.SetDouble("lastMealEatenTotalHours", now);
        attrs.MarkPathDirty("hunger");
        attrs.SetDouble(NextTreatAttr, now + Math.Max(settings.HandFeedCooldownHours, 0.05));
        entity.World.PlaySoundAt(EatSound, entity, null, randomizePitch: true, range: 12);
        // A wild one (a hen) gets tamer too, a little each day.
        LivestockTaming.Trust(entity, (byEntity as EntityPlayer)?.Player as Vintagestory.API.Server.IServerPlayer);
    }
}
