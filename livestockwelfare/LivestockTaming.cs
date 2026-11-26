using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace livestockwelfare;

/// <summary>
/// Taming wild animals by hand (TreatSettings.Taming): a wild animal takes its food from a crouching
/// player's hand once a day. Every few daily feedings it gets one generation tamer, as if born in the
/// player's care (vanilla: generation 1 no longer runs away, 2 can be roped and doesn't attack). From
/// the first feeding on it stays around the spot where it was fed. AiTaskLivestockFollowFood does
/// the waiting and walking up, AiTaskLivestockStayNearHome the staying around.
/// </summary>
public static class LivestockTaming
{
    // Feedings since the last generation step, and how many a step takes (synced, for the info text).
    const string TrustAttr = "lwTameTrust";
    const string TrustNeededAttr = "lwTameTrustNeeded";
    const string FedDayAttr = "lwTameFedDay";
    // Set once it has reached TameMaxGeneration.
    const string DoneAttr = "lwTameDone";
    // Where it was first fed: it stays around there until tame.
    const string HomeAttr = "lwTameHome";
    const float ChewSeconds = 2.5f;
    static readonly AssetLocation EatSound = new("game", "sounds/creature/animal-eat-small");
    static readonly Dictionary<AssetLocation, AssetLocation[]> tameFoods = new();

    static TreatSettings? Settings => LivestockTreats.Settings is { Taming: true } settings ? settings : null;

    // The foods a wild one of this kind takes from the hand.
    static AssetLocation[] TameFoods(Entity entity)
    {
        if (!tameFoods.TryGetValue(entity.Code, out var codes))
        {
            codes = Settings is { } settings
                ? settings.LureFoods.Where(lure => lure.AnimalCodes.Any(code => WildcardUtil.Match(new AssetLocation(code), entity.Code)))
                    .SelectMany(lure => lure.TameFoodCodes).Select(code => new AssetLocation(code)).ToArray()
                : Array.Empty<AssetLocation>();
            tameFoods[entity.Code] = codes;
        }
        return codes;
    }

    /// <summary>Whether this kind of animal can be tamed by hand at all.</summary>
    public static bool CanBeTamed(Entity entity) =>
        Settings != null && LivestockTreats.Diet(entity) != null && !entity.HasBehavior<EntityBehaviorLivestockNursing>() && TameFoods(entity).Length > 0;

    /// <summary>
    /// Server: whether it is (still) being tamed by hand: a wild one, or one already being tamed that
    /// hasn't reached the last generation yet.
    /// </summary>
    public static bool IsTamable(Entity entity) =>
        entity.Alive && Settings is { } settings && CanBeTamed(entity)
        && entity.WatchedAttributes.GetInt("generation") < settings.TameMaxGeneration
        && (HasHome(entity) || !LivestockUtil.IsKept(entity));

    /// <summary>Food in hand that this animal comes to take while being tamed.</summary>
    public static bool Tempts(Entity entity, ItemStack? stack) =>
        stack != null && TameFoods(entity).Any(code => WildcardUtil.Match(code, stack.Collectible.Code)) && LivestockTreats.Eats(entity, stack);

    static int Today(Entity entity) => (int)Math.Floor(entity.World.Calendar.TotalDays);

    /// <summary>Fed by hand today already: it won't come again until tomorrow.</summary>
    public static bool FedToday(Entity entity) =>
        entity.WatchedAttributes.HasAttribute(FedDayAttr) && entity.WatchedAttributes.GetInt(FedDayAttr) >= Today(entity);

    public static bool HasHome(Entity entity) => entity.WatchedAttributes.HasAttribute(HomeAttr + "X");

    /// <summary>Where it was first fed, or null.</summary>
    public static Vec3d? Home(Entity entity)
    {
        var attrs = entity.WatchedAttributes;
        return HasHome(entity) ? new Vec3d(attrs.GetDouble(HomeAttr + "X"), attrs.GetDouble(HomeAttr + "Y"), attrs.GetDouble(HomeAttr + "Z")) : null;
    }

    /// <summary>Server: the animal takes one portion of its food from the player's hand and eats it.</summary>
    public static bool FeedFromHand(Entity entity, EntityPlayer player)
    {
        var slot = Tempts(entity, player.RightHandItemSlot?.Itemstack) ? player.RightHandItemSlot
            : Tempts(entity, player.LeftHandItemSlot?.Itemstack) ? player.LeftHandItemSlot : null;
        return slot != null && Feed(entity, slot, player.Player as IServerPlayer, player.Player?.WorldData.CurrentGameMode == EnumGameMode.Creative);
    }

    // The feeding itself (also used by the self-test, with no player).
    internal static bool Feed(Entity entity, ItemSlot slot, IServerPlayer? player, bool creative)
    {
        if (Settings == null || !IsTamable(entity) || FedToday(entity) || !Tempts(entity, slot.Itemstack)) return false;

        entity.GetBehavior<EntityBehaviorLivestockMouth>()?.Hold(slot.Itemstack!, ChewSeconds);
        entity.GetBehavior<EntityBehaviorLivestockHygiene>()?.HoldStill(ChewSeconds, "eat");
        if (!creative)
        {
            slot.TakeOut(1);
            slot.MarkDirty();
        }
        entity.World.PlaySoundAt(EatSound, entity, null, randomizePitch: true, range: 12);

        // A meal, as one portion from a trough (vanilla AiTaskSeekFoodAndEat).
        var attrs = entity.WatchedAttributes;
        var hunger = attrs.GetOrAddTreeAttribute("hunger");
        hunger.SetFloat("saturation", hunger.GetFloat("saturation") + 1);
        attrs.SetDouble("lastMealEatenTotalHours", entity.World.Calendar.TotalHours);
        attrs.MarkPathDirty("hunger");

        Trust(entity, player);
        return true;
    }

    /// <summary>
    /// Server: a wild animal fed by hand today (by LivestockTaming or as a treat, see LivestockTreats)
    /// trusts the player a little more, the first time each day.
    /// </summary>
    public static void Trust(Entity entity, IServerPlayer? player)
    {
        if (Settings is not { } settings || !IsTamable(entity) || FedToday(entity)) return;
        var attrs = entity.WatchedAttributes;
        attrs.SetInt(FedDayAttr, Today(entity));
        if (!HasHome(entity))
        {
            attrs.SetDouble(HomeAttr + "X", entity.Pos.X);
            attrs.SetDouble(HomeAttr + "Y", entity.Pos.Y);
            attrs.SetDouble(HomeAttr + "Z", entity.Pos.Z);
        }
        int trust = attrs.GetInt(TrustAttr) + 1;
        string message = "livestockwelfare:tame-fed";
        if (trust >= settings.TameFeedingsPerGeneration)
        {
            int generation = attrs.GetInt("generation") + 1;
            attrs.SetInt("generation", generation);
            trust = 0;
            bool done = generation >= settings.TameMaxGeneration;
            if (done) attrs.SetBool(DoneAttr, true);
            message = done ? "livestockwelfare:tame-done" : "livestockwelfare:tame-step";
        }
        attrs.SetInt(TrustAttr, trust);
        attrs.SetInt(TrustNeededAttr, settings.TameFeedingsPerGeneration);
        player?.SendMessage(GlobalConstants.GeneralChatGroup, LivestockUtil.Translate(player.LanguageCode, message), EnumChatType.Notification);
    }

    /// <summary>Self-test: as if the day it was last fed were over.</summary>
    internal static void ForgetFedDay(Entity entity) => entity.WatchedAttributes.RemoveAttribute(FedDayAttr);

    /// <summary>Info text while being tamed: how far along it is.</summary>
    public static string? InfoText(Entity entity)
    {
        var attrs = entity.WatchedAttributes;
        if (!HasHome(entity) || !attrs.HasAttribute(TrustNeededAttr) || attrs.GetBool(DoneAttr)) return null;
        string text = Lang.Get("livestockwelfare:tame-progress", attrs.GetInt(TrustAttr), attrs.GetInt(TrustNeededAttr));
        return FedToday(entity) ? text + ", " + Lang.Get("livestockwelfare:tame-fedtoday") : text;
    }
}
