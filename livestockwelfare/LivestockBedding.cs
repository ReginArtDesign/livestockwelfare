using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Straw bedding for breeding (BreedingSettings.RequireBedding): kept animals only get pregnant
/// after the pair has lain together on clean bedding under a roof (see AiTaskLivestockBedding),
/// and a birth on the bedding soils it. Vanilla EntityBehaviorMultiply is patched for both.
/// </summary>
public static class LivestockBedding
{
    // Set on the female when the pair has lain together; vanilla may then make her pregnant.
    const string MatedAttr = "lwMatedTotalHours";
    // Vanilla rolls 6% every 3 seconds, so she is usually pregnant within a minute or two.
    const double MatedValidHours = 3;
    /// <summary>Set (watched) on a female that is ready to mate but finds no bedding, for the info text.</summary>
    public const string NeedsBeddingAttr = "lwNeedsBedding";

    // On the mother after a birth on the bedding: until when she stays there with her young, and where.
    const string NewbornUntilAttr = "lwNewbornUntilHours";
    const string NewbornBedAttr = "lwNewbornBed";

    const string SoiledCode = "livestockwelfare:strawbedding-soiled";
    const string CleanCode = "livestockwelfare:strawbedding-clean";

    public static BreedingSettings? Settings =>
        livestockwelfareModSystem.Config?.Breeding is { RequireBedding: true } settings ? settings : null;

    /// <summary>Kept livestock of this mod; wild animals and other mods' animals breed as before.</summary>
    public static bool Applies(Entity entity) =>
        Settings != null && entity.HasBehavior<EntityBehaviorLivestockShelter>() && LivestockUtil.IsKept(entity);

    public static bool IsCleanBedding(Block block)
    {
        if (block?.Code == null || Settings is not { } settings) return false;
        foreach (string code in settings.BeddingBlockCodes)
        {
            if (WildcardUtil.Match(new AssetLocation(code), block.Code)) return true;
        }
        return false;
    }

    /// <summary>Clean bedding with a roof above it.</summary>
    public static bool IsUsableBedding(IBlockAccessor accessor, BlockPos pos) =>
        IsCleanBedding(accessor.GetBlock(pos)) && accessor.GetRainMapHeightAt(pos) > pos.Y;

    /// <summary>Straw bedding, clean or soiled: a mother stays on hers after the birth soiled it.</summary>
    public static bool IsAnyBedding(Block block) =>
        block?.Code is { } code && (code.ToString() == SoiledCode || IsCleanBedding(block));

    /// <summary>The bedding a mother stays on with her newborns, while that time lasts.</summary>
    public static BlockPos? NewbornBed(Entity mother)
    {
        var attrs = mother.Attributes;
        if (!attrs.HasAttribute(NewbornUntilAttr) || mother.World.Calendar.TotalHours > attrs.GetDouble(NewbornUntilAttr)) return null;
        return new BlockPos(attrs.GetInt(NewbornBedAttr + "X"), attrs.GetInt(NewbornBedAttr + "Y"), attrs.GetInt(NewbornBedAttr + "Z"), mother.Pos.Dimension);
    }

    public static void MarkMated(Entity female) =>
        female.Attributes.SetDouble(MatedAttr, female.World.Calendar.TotalHours);

    public static bool RecentlyMated(Entity female) =>
        female.Attributes.HasAttribute(MatedAttr)
        && female.World.Calendar.TotalHours - female.Attributes.GetDouble(MatedAttr) <= MatedValidHours;

    public static void ClearMated(Entity female) => female.Attributes.RemoveAttribute(MatedAttr);

    /// <summary>Ready to mate as far as vanilla is concerned: rested, fed and not pregnant.</summary>
    public static bool ReadyToMate(EntityBehaviorMultiply multiply)
    {
        var entity = multiply.entity;
        if (multiply.IsPregnant || multiply.TotalDaysCooldownUntil > entity.World.Calendar.TotalDays) return false;
        float saturation = entity.WatchedAttributes.GetTreeAttribute("hunger")?.GetFloat("saturation") ?? 0;
        return saturation >= multiply.PortionsEatenForMultiply;
    }

    /// <summary>The nearest male she can mate with (vanilla's "requiresNearbyEntityCode"), or null.
    /// Like vanilla, a male that eats must have eaten something.</summary>
    public static Entity? FindMale(EntityBehaviorMultiply multiply)
    {
        var codes = multiply.RequiresNearbyEntityCodes;
        var entity = multiply.entity;
        if (codes == null || codes.Length == 0) return null;
        float range = Math.Max(multiply.RequiresNearbyEntityRange, 5);
        return entity.World.GetNearestEntity(entity.Pos.XYZ, range, range, e =>
            e != entity && e.Alive && Array.Exists(codes, code => WildcardUtil.Match(code, e.Code)) && HasEaten(e));
    }

    static bool HasEaten(Entity male) =>
        !male.WatchedAttributes.GetBool("doesEat")
        || (male.WatchedAttributes.GetTreeAttribute("hunger")?.GetFloat("saturation") ?? 0) >= 1;


    /// <summary>Patches vanilla; the caller unpatches on dispose, as a singleplayer game may load several worlds.</summary>
    public static Harmony Patch(string harmonyId)
    {
        var harmony = new Harmony(harmonyId);
        harmony.Patch(AccessTools.Method(typeof(EntityBehaviorMultiply), "TryGetPregnant"),
            prefix: new HarmonyMethod(typeof(LivestockBedding), nameof(TryGetPregnantPrefix)));
        harmony.Patch(AccessTools.Method(typeof(EntityBehaviorMultiply), "GiveBirth"),
            prefix: new HarmonyMethod(typeof(LivestockBedding), nameof(GiveBirthPrefix)));
        return harmony;
    }

    // No pregnancy for kept animals that have not lain together on bedding just now.
    static bool TryGetPregnantPrefix(EntityBehaviorMultiply __instance, ref bool __result)
    {
        var entity = __instance.entity;
        if (!Applies(entity) || RecentlyMated(entity)) return true;
        __result = false;
        return false;
    }

    // A birth soils the bedding she lies on.
    static void GiveBirthPrefix(EntityBehaviorMultiply __instance)
    {
        var entity = __instance.entity;
        ClearMated(entity);
        if (Settings == null) return;

        var accessor = entity.World.BlockAccessor;
        var pos = entity.Pos.AsBlockPos;
        if (!IsCleanBedding(accessor.GetBlock(pos))) pos = pos.DownCopy();
        if (!IsCleanBedding(accessor.GetBlock(pos)) || accessor.GetBlock(pos).Code.ToString() != CleanCode) return;

        var soiled = entity.World.GetBlock(new AssetLocation(SoiledCode));
        if (soiled == null) return;
        accessor.SetBlock(soiled.Id, pos);

        if (Settings is { NewbornDays: > 0 } settings)
        {
            var attrs = entity.Attributes;
            attrs.SetDouble(NewbornUntilAttr, entity.World.Calendar.TotalHours + settings.NewbornDays * 24);
            attrs.SetInt(NewbornBedAttr + "X", pos.X);
            attrs.SetInt(NewbornBedAttr + "Y", pos.Y);
            attrs.SetInt(NewbornBedAttr + "Z", pos.Z);
        }
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1} gave birth on the bedding at {2}, now soiled", entity.Code.Path, entity.EntityId, pos);
    }
}
