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

    public static TreatSettings? Settings =>
        livestockwelfareModSystem.Config?.Treats is { Enabled: true } settings ? settings : null;

    /// <summary>
    /// Server: whether this animal takes treats (TreatSettings.AnimalCodes). Orphaned babies are fed
    /// by hand their own way (EntityBehaviorLivestockNursing).
    /// </summary>
    public static bool Setup(Entity entity)
    {
        bool takes = Settings is { } settings && Diet(entity) != null && !entity.HasBehavior<EntityBehaviorLivestockNursing>()
            && settings.AnimalCodes.Any(code => WildcardUtil.Match(new AssetLocation(code), entity.Code));
        if (entity.WatchedAttributes.GetBool(TakesTreatsAttr) != takes) entity.WatchedAttributes.SetBool(TakesTreatsAttr, takes);
        return takes;
    }

    public static bool TakesTreats(Entity entity) => entity.Alive && entity.WatchedAttributes.GetBool(TakesTreatsAttr);

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
    }
}
