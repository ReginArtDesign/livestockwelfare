using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>Helpers shared by the livestock behaviors.</summary>
public static class LivestockUtil
{
    public const string Domain = "livestockwelfare";

    // Vanilla EntityBehaviorHarvestable reads the killer's entity type from this attribute: its
    // "killedByInfoText" is shown on the carcass and its "deathByMultiplier" scales the drops.
    // The codes below are never-spawned entity types from entities/deathcause.json.
    public const string DeathByEntityAttr = "deathByEntity";
    public const string OldAgeDeathCause = "livestockwelfare:deathcause-oldage";
    public const string DiseaseDeathCause = "livestockwelfare:deathcause-disease";
    public const string ExposureDeathCause = "livestockwelfare:deathcause-exposure";
    public const string ThirstDeathCause = "livestockwelfare:deathcause-thirst";

    /// <summary>
    /// Keeps the vanilla mating cooldown just ahead of the current time, so EntityBehaviorMultiply
    /// never lets the animal get pregnant. An already pregnant animal still gives birth.
    /// </summary>
    public static void BlockBreeding(Entity entity)
    {
        var multiply = entity.WatchedAttributes.GetTreeAttribute("multiply");
        if (multiply == null || multiply.GetBool("isPregnant")) return;

        double blockUntil = entity.World.Calendar.TotalDays + 1;
        if (multiply.GetDouble("totalDaysCooldownUntil") >= blockUntil) return;

        multiply.SetDouble("totalDaysCooldownUntil", blockUntil);
        entity.WatchedAttributes.MarkPathDirty("multiply");
    }

    /// <summary>Whether the given death cause is allowed to kill (see SafetySettings).</summary>
    public static bool CanKill(string cause)
    {
        var safety = livestockwelfareModSystem.Config?.Safety;
        if (safety == null) return true;
        if (!safety.AnimalsCanDie) return false;
        return cause switch
        {
            OldAgeDeathCause => safety.OldAgeCanKill,
            DiseaseDeathCause => safety.DiseaseCanKill,
            ExposureDeathCause => safety.ExposureCanKill,
            ThirstDeathCause => safety.ThirstCanKill,
            _ => true
        };
    }

    /// <summary>
    /// Takes health from a suffering animal. If that empties its health it dies of the given cause,
    /// unless that cause may not kill; then it is left at 1 health.
    /// </summary>
    public static void Harm(Entity entity, EntityBehaviorHealth health, double amount, string cause, string logReason)
    {
        float floor = CanKill(cause) ? 0 : Math.Min(1, health.Health);
        health.Health = Math.Max(floor, health.Health - (float)amount);
        if (health.Health > 0) return;

        entity.World.Logger.Notification("[livestockwelfare] {0} #{1} died of {2}", entity.Code, entity.EntityId, logReason);
        Kill(entity, cause);
    }

    /// <summary>
    /// False while the animal's needs should be paused because no player is near
    /// (SafetySettings.PauseWhenNoPlayerNearby).
    /// </summary>
    public static bool ShouldSimulate(Entity entity)
    {
        var safety = livestockwelfareModSystem.Config?.Safety;
        if (safety is not { PauseWhenNoPlayerNearby: true }) return true;

        var pos = entity.Pos;
        var player = entity.World.NearestPlayer(pos.X, pos.Y, pos.Z);
        return player?.Entity != null && player.Entity.Pos.SquareDistanceTo(pos) <= (double)safety.PlayerRadius * safety.PlayerRadius;
    }

    /// <summary>Kills the animal without a killer, remembering the cause for the info text and drops.</summary>
    public static void Kill(Entity entity, string cause)
    {
        entity.WatchedAttributes.SetString(DeathByEntityAttr, cause);
        entity.Die(EnumDespawnReason.Death, new DamageSource
        {
            Source = EnumDamageSource.Internal,
            Type = EnumDamageType.Injury
        });
    }

    // Several things (sickness, bad weather) can stop an animal's vanilla health regeneration.
    // Each sets its own reason; regeneration is off while any reason is set. We only write
    // "regenSpeed" when our overall state changes, so other mods that use it are left alone.
    const string RegenBlockedAttr = "lwRegenSuppressed";
    const string RegenReasonPrefix = "lwRegenBlocked-";
    const string VanillaRegenSpeedAttr = "regenSpeed";
    static readonly string[] RegenReasons = { "sick", "weather", "thirst" };

    public static void SetRegenBlocked(Entity entity, string reason, bool blocked)
    {
        entity.WatchedAttributes.SetBool(RegenReasonPrefix + reason, blocked);
        ApplyRegen(entity);
    }

    /// <summary>Re-applies the regeneration state, e.g. after the animal was loaded.</summary>
    public static void ApplyRegen(Entity entity)
    {
        bool blocked = false;
        foreach (string reason in RegenReasons) blocked |= entity.WatchedAttributes.GetBool(RegenReasonPrefix + reason);
        SetRegen(entity, blocked);
    }

    /// <summary>
    /// Turns regeneration back on while the animal is unloaded (reasons are kept), so removing
    /// the mod never leaves animals that can no longer heal.
    /// </summary>
    public static void ReleaseRegen(Entity entity) => SetRegen(entity, false);

    static void SetRegen(Entity entity, bool blocked)
    {
        var attrs = entity.WatchedAttributes;
        if (attrs.GetBool(RegenBlockedAttr) == blocked) return;
        attrs.SetFloat(VanillaRegenSpeedAttr, blocked ? 0 : 1);
        attrs.SetBool(RegenBlockedAttr, blocked);
    }

    /// <param name="langCode">Language to use, or null for the local game language.</param>
    public static string Translate(string? langCode, string key, params object[] args) =>
        langCode == null ? Lang.Get(key, args) : Lang.GetL(langCode, key, args);
}
