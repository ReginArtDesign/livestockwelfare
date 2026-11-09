using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace livestockwelfare;

/// <summary>
/// Applies the breeding settings from the config to the game's "multiply" (mammals), "multiplybase"
/// (hens laying eggs) and "grow" behaviors. Those read their values from the entity type's JSON when
/// an animal is loaded, so changing the JSON once on the server is enough, for existing animals too.
/// </summary>
public static class LivestockBreeding
{
    const double HoursPerDay = 24;

    public static void Apply(ICoreAPI api, BreedingSettings settings)
    {
        if (!settings.Enabled) return;

        var types = api.World.EntityTypes.Where(t => t?.Code != null && t.Server?.BehaviorsAsJsonObj != null).ToList();
        var byCode = types.GroupBy(t => t.Code.ToString()).ToDictionary(g => g.Key, g => g.First());
        int multiplyCount = 0, growCount = 0;

        foreach (var group in types.GroupBy(SpeciesOf).Where(g => g.Key != null))
        {
            if (!settings.Species.TryGetValue(group.Key!, out var breeding) || breeding == null) continue;

            foreach (var type in group)
            {
                var multiply = Behavior(type, "multiply") ?? Behavior(type, "multiplybase");
                if (multiply == null) continue;
                Set(multiply, "portionsEatenForMultiply", breeding.PortionsToBreed);
                Set(multiply, "pregnancyDays", breeding.PregnancyDays);
                Set(multiply, "multiplyCooldownDaysMin", breeding.RestDaysMin);
                Set(multiply, "multiplyCooldownDaysMax", breeding.RestDaysMax);
                multiplyCount++;
            }

            if (breeding.GrowUpDays is not { } growUpDays) continue;
            // Chickens grow in two stages (chick, then young hen or rooster), so the time is shared out
            // between the stages in the game's own proportion, measured on the longest way to adulthood.
            var growing = group.Where(t => Behavior(t, "grow") != null).ToList();
            double longest = growing.Select(t => HoursToAdult(t, byCode, 0)).DefaultIfEmpty(0).Max();
            if (longest <= 0) continue;
            double factor = growUpDays * HoursPerDay / longest;
            api.Logger.Debug("[livestockwelfare] {0}: grows up in {1:0.#} days instead of {2:0.#}", group.Key, growUpDays, longest / HoursPerDay);
            foreach (var type in growing)
            {
                var grow = Behavior(type, "grow")!;
                grow.Token["hoursToGrow"] = Math.Max(1, grow["hoursToGrow"].AsDouble(96) * factor);
                growCount++;
            }
        }
        api.Logger.Notification("[livestockwelfare] Breeding settings applied to {0} breeding and {1} growing animal types", multiplyCount, growCount);
    }

    static string? SpeciesOf(EntityProperties type) =>
        Behavior(type, EntityBehaviorLivestockAge.Code)?["species"].AsString();

    // The type's server behavior with this code, unless it is switched off for this variant.
    static JsonObject? Behavior(EntityProperties type, string code) =>
        type.Server.BehaviorsAsJsonObj.FirstOrDefault(b => b["code"].AsString() == code && b["enabled"].AsBool(true));

    // Hours from this stage until the animal is fully grown, following "adultEntityCodes".
    static double HoursToAdult(EntityProperties type, Dictionary<string, EntityProperties> byCode, int depth)
    {
        var grow = Behavior(type, "grow");
        if (grow == null || depth > 5) return 0;
        double hours = grow["hoursToGrow"].AsDouble(96);
        string? next = grow["adultEntityCodes"].AsArray<string>(Array.Empty<string>()).FirstOrDefault();
        if (next == null) return hours;
        // Like vanilla, a code without a domain means "game:".
        return hours + (byCode.TryGetValue(new AssetLocation(next).ToString(), out var nextType) ? HoursToAdult(nextType, byCode, depth + 1) : 0);
    }

    static void Set(JsonObject behavior, string key, double? value)
    {
        if (value is { } v) behavior.Token[key] = v;
    }
}
