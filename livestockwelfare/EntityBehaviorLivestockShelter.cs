using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Weather stress: rain, snow, cold and strong sun stress animals without shelter. A roof keeps
/// off rain, snow and sun; a closed room (vanilla room rules, as for the player) also keeps out
/// the cold. Stress counts towards sickness (see EntityBehaviorLivestockHygiene), stops breeding
/// and, when severe, stops healing and slowly hurts the animal.
/// </summary>
public class EntityBehaviorLivestockShelter : EntityBehavior
{
    public const string Code = "livestockwelfare.shelter";

    const string StressAttr = "lwWeatherStress";
    const string ConditionsAttr = "lwWeatherConditions";
    const string LastUpdateAttr = "lwWeatherLastHours";

    [Flags]
    enum Conditions
    {
        None = 0,
        Wet = 1,
        Snowed = 2,
        Cold = 4,
        Hot = 8,
        Roofed = 16,
        Enclosed = 32
    }

    const float CheckIntervalSeconds = 5;
    // Longer gaps (e.g. the chunk was unloaded) are not simulated: the weather back then is unknown.
    const double MaxCatchUpHours = 24;
    // Precipitation below this is just drizzle or a few flakes.
    const float MinPrecipitation = 0.05f;
    // Daylight strength above which the sun counts as strong (it ranges roughly 0..1.2).
    const float StrongSunlight = 0.7f;

    string species = "";
    float accum;
    double targetStress;

    public EntityBehaviorLivestockShelter(Entity entity) : base(entity) { }

    public override string PropertyName() => Code;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        species = attributes["species"].AsString("");
    }

    public float Stress => entity.WatchedAttributes.GetFloat(StressAttr);

    Conditions Current => (Conditions)entity.WatchedAttributes.GetInt(ConditionsAttr);

    /// <summary>Standing in strong sun above the species' heat limit (as of the last check).</summary>
    public bool InHotSun => (Current & Conditions.Hot) != 0;

    static WeatherSettings? Settings =>
        livestockwelfareModSystem.Config?.Weather is { Enabled: true } settings ? settings : null;

    public override void OnGameTick(float deltaTime)
    {
        if (entity.World.Side != EnumAppSide.Server || !entity.Alive) return;

        accum += deltaTime;
        if (accum < CheckIntervalSeconds) return;
        accum = 0;

        var settings = Settings;
        if (settings == null)
        {
            if (Stress > 0) entity.WatchedAttributes.SetFloat(StressAttr, 0);
            LivestockUtil.SetRegenBlocked(entity, "weather", false);
            return;
        }

        // Conditions are checked often so the info text stays current; stress changes hourly.
        CheckConditions(settings);

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

        UpdateStress(settings, hours);
        ApplyEffects(settings, hours);
    }

    void CheckConditions(WeatherSettings settings)
    {
        var world = entity.World;
        var accessor = world.BlockAccessor;
        var pos = entity.Pos.AsBlockPos;

        var climate = accessor.GetClimateAt(pos, EnumGetClimateMode.NowValues);
        if (climate == null) return;

        var conditions = Conditions.None;
        // Same checks vanilla uses for the player: anything above that stops rain is a roof;
        // a room with no open exits, or more roof than skylight, is closed.
        bool roofed = accessor.GetRainMapHeightAt(pos) > pos.Y;
        var room = world.Api.ModLoader.GetModSystem<RoomRegistry>()?.GetRoomForPosition(pos);
        bool enclosed = room != null && (room.ExitCount == 0 || room.SkylightCount < room.NonSkylightCount);
        if (roofed) conditions |= Conditions.Roofed;
        if (enclosed) conditions |= Conditions.Enclosed;

        double target = 0;
        float temperature = climate.Temperature;
        var tolerance = settings.ToleranceFor(species);

        if (!roofed && climate.Rainfall > MinPrecipitation)
        {
            double intensity = Math.Min(1, climate.Rainfall * 2);
            if (temperature < -1)
            {
                conditions |= Conditions.Snowed;
                target += settings.SnowStress * intensity;
            }
            else
            {
                conditions |= Conditions.Wet;
                target += settings.RainStress * intensity;
            }
        }

        if (temperature < tolerance.ColdBelowC && !enclosed)
        {
            conditions |= Conditions.Cold;
            double cold = (tolerance.ColdBelowC - temperature) * settings.ColdStressPerDegree;
            target += roofed ? cold * settings.RoofColdFactor : cold;
        }

        if (!roofed && temperature > tolerance.HotAboveC && world.Calendar.GetDayLightStrength(pos) > StrongSunlight)
        {
            conditions |= Conditions.Hot;
            target += (temperature - tolerance.HotAboveC) * settings.HeatStressPerDegree;
        }

        targetStress = Math.Min(1, target);
        if (conditions != Current) entity.WatchedAttributes.SetInt(ConditionsAttr, (int)conditions);
    }

    void UpdateStress(WeatherSettings settings, double hours)
    {
        double stress = Stress;
        // Moves towards the current weather; after StressAdaptHours about 63% of the way.
        stress += (targetStress - stress) * (1 - Math.Exp(-hours / settings.StressAdaptHours));
        entity.WatchedAttributes.SetFloat(StressAttr, (float)Math.Clamp(stress, 0, 1));
    }

    void ApplyEffects(WeatherSettings settings, double hours)
    {
        float stress = Stress;
        if (stress >= settings.NoBreedingAboveStress) LivestockUtil.BlockBreeding(entity);

        bool harmed = stress >= settings.HarmAboveStress;
        LivestockUtil.SetRegenBlocked(entity, "weather", harmed);
        if (!harmed) return;

        var health = entity.GetBehavior<EntityBehaviorHealth>();
        if (health == null) return;

        health.Health -= (float)(settings.HarmPerDay * hours / 24);
        if (health.Health <= 0)
        {
            health.Health = 0;
            entity.World.Logger.Notification("[livestockwelfare] {0} #{1} died of exposure (stress {2:0.00})", entity.Code, entity.EntityId, stress);
            LivestockUtil.Kill(entity, LivestockUtil.ExposureDeathCause);
        }
    }

    /// <summary>Weather status for the info text and chat, e.g. "Getting wet, cold (weather stress: 45%)".</summary>
    /// <param name="langCode">Language to use, or null for the local game language.</param>
    public string StatusText(string? langCode = null)
    {
        var conditions = Current;
        int stressPercent = (int)Math.Round(Stress * 100);

        var problems = new List<string>();
        if ((conditions & Conditions.Wet) != 0) problems.Add("livestockwelfare:weather-wet");
        if ((conditions & Conditions.Snowed) != 0) problems.Add("livestockwelfare:weather-snow");
        if ((conditions & Conditions.Cold) != 0) problems.Add("livestockwelfare:weather-cold");
        if ((conditions & Conditions.Hot) != 0) problems.Add("livestockwelfare:weather-hot");

        var text = new StringBuilder();
        if (problems.Count > 0)
        {
            for (int i = 0; i < problems.Count; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(LivestockUtil.Translate(langCode, problems[i]));
            }
            text.Append(" (").Append(LivestockUtil.Translate(langCode, "livestockwelfare:weather-stress", stressPercent)).Append(')');
            return text.ToString();
        }

        string shelter = (conditions & Conditions.Enclosed) != 0 ? "livestockwelfare:weather-enclosed"
            : (conditions & Conditions.Roofed) != 0 ? "livestockwelfare:weather-roofed"
            : "livestockwelfare:weather-open";
        text.Append(LivestockUtil.Translate(langCode, shelter));
        if (stressPercent > 0) text.Append(" (").Append(LivestockUtil.Translate(langCode, "livestockwelfare:weather-stress", stressPercent)).Append(')');
        return text.ToString();
    }

    public override void GetInfoText(StringBuilder infotext)
    {
        if (entity.Alive) infotext.AppendLine(StatusText());
    }
}
