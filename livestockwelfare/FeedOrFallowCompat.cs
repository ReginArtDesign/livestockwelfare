using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Feed or Fallow lets kept animals starve after a few days without a meal. Vanilla, though, sends
/// an animal to the trough only while it needs food to breed: a pregnant female never goes, and a
/// hen that is full but has nowhere to lay (nests full of eggs) doesn't either. With both mods a
/// pregnant sow on her bedding and hens beside full nests starved next to full troughs. So when
/// Feed or Fallow is installed, an animal that has gone a while without a meal goes to eat anyway.
/// </summary>
public static class FeedOrFallowCompat
{
    public const string ModId = "feedorfallow";
    // The Feed or Fallow behaviour; only animals that have it can starve.
    const string BehaviorName = "feedorfallow";
    // Vanilla notes each meal from a trough here, and Feed or Fallow reads it too.
    const string LastMealAttr = "lastMealEatenTotalHours";
    const string FedAttr = "ffLastFedHours";

    /// <summary>Patches vanilla; the caller unpatches on dispose, as a singleplayer game may load several worlds.</summary>
    public static Harmony Patch(string harmonyId)
    {
        var harmony = new Harmony(harmonyId);
        var postfix = new HarmonyMethod(typeof(FeedOrFallowCompat), nameof(ShouldEatPostfix));
        // EntityBehaviorMultiply (sheep, goats, pigs...) overrides the base getter (hens), so both are patched.
        harmony.Patch(AccessTools.PropertyGetter(typeof(EntityBehaviorMultiplyBase), nameof(EntityBehaviorMultiplyBase.ShouldEat)), postfix: postfix);
        harmony.Patch(AccessTools.PropertyGetter(typeof(EntityBehaviorMultiply), nameof(EntityBehaviorMultiply.ShouldEat)), postfix: postfix);
        return harmony;
    }

    static void ShouldEatPostfix(EntityBehavior __instance, ref bool __result)
    {
        if (__result || livestockwelfareModSystem.Config?.Breeding is not { HungryEatAfterHours: > 0 } settings) return;
        var entity = __instance.entity;
        if (entity?.World == null || entity.GetBehavior(BehaviorName) == null) return;
        // Feed or Fallow also notes when it last counted the animal as fed (by hand, grazing, or first seen).
        double lastMeal = entity.WatchedAttributes.GetDouble(LastMealAttr, entity.WatchedAttributes.GetDouble(FedAttr, double.NaN));
        if (double.IsNaN(lastMeal)) return;
        double hours = entity.World.Calendar.TotalHours - lastMeal;
        if (hours < settings.HungryEatAfterHours) return;
        __result = true;

        // Noted in server-debug.log once per meal, to see in a test world that it works.
        if (entity.Attributes.GetDouble(LoggedAttr) == lastMeal) return;
        entity.Attributes.SetDouble(LoggedAttr, lastMeal);
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1} goes to eat although vanilla says no ({2:0} h without a meal, Feed or Fallow)",
            entity.Code.Path, entity.EntityId, hours);
    }

    const string LoggedAttr = "lwHungryEatLogged";
}
