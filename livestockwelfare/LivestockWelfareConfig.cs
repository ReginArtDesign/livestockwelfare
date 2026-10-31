using System;
using System.Collections.Generic;

namespace livestockwelfare;

/// <summary>
/// Server-side settings, stored in ModConfig/livestockwelfare.json.
/// Ages are in in-game years, so they scale with the world's month length.
/// </summary>
public class LivestockWelfareConfig
{
    public const string FileName = "livestockwelfare.json";

    /// <summary>Animals count as old after this fraction of their lifespan.</summary>
    public double OldAgeFraction { get; set; } = 0.75;

    /// <summary>Old females can no longer get pregnant.</summary>
    public bool OldAnimalsInfertile { get; set; } = true;

    /// <summary>Animals die when they reach the end of their lifespan.</summary>
    public bool DieOfOldAge { get; set; } = true;

    /// <summary>
    /// Lifespan per species. The key is the "species" set in the entity patch.
    /// Each animal gets a random lifespan between min and max when it is first seen.
    /// </summary>
    public Dictionary<string, SpeciesAging> Species { get; set; } = new()
    {
        // Shorter than real life on purpose: with the default 9-day months an in-game year
        // is ~86 real hours, so realistic lifespans would never be reached in most worlds.
        ["sheep"] = new SpeciesAging { LifespanYearsMin = 3, LifespanYearsMax = 5 },
        ["goat"] = new SpeciesAging { LifespanYearsMin = 4, LifespanYearsMax = 6 },
        ["pig"] = new SpeciesAging { LifespanYearsMin = 3, LifespanYearsMax = 4 },
        ["chicken"] = new SpeciesAging { LifespanYearsMin = 2, LifespanYearsMax = 3 },
    };

    public HygieneSettings Hygiene { get; set; } = new();

    public void Sanitize()
    {
        if (OldAgeFraction is < 0.1 or > 1) OldAgeFraction = 0.75;
        foreach (var aging in Species.Values)
        {
            if (aging.LifespanYearsMin < 0.1) aging.LifespanYearsMin = 0.1;
            if (aging.LifespanYearsMax < aging.LifespanYearsMin) aging.LifespanYearsMax = aging.LifespanYearsMin;
        }
        Hygiene.Sanitize();
    }
}

/// <summary>
/// Dirt comes from droppings near the animal (if a droppings mod such as Natural Fertilizer is installed)
/// and from overcrowding. Dirty surroundings slowly raise the animal's filth level, which makes it
/// more likely to get sick. Chances are per in-game day.
/// </summary>
public class HygieneSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Blocks that count as droppings (wildcards allowed). If the variant starts with a number
    /// (e.g. "dung-3a"), it is taken as the number of droppings in the block.
    /// </summary>
    public string[] DroppingBlockCodes { get; set; } =
    {
        "naturalfertilizer:dung-*",
        "naturalfertilizer:smallpoop-*",
        "naturalfertilizer:chickenpoop-*",
    };

    /// <summary>Horizontal distance (blocks) searched for droppings, muck and rot around the animal.</summary>
    public int DroppingScanRadius { get; set; } = 3;
    /// <summary>Dirt added per dropping. 1.0 dirt is the worst possible surroundings.</summary>
    public double DirtPerDropping { get; set; } = 0.05;
    /// <summary>Dirt added per level of muck (a muck block has 1-4 levels).</summary>
    public double DirtPerMuckLevel { get; set; } = 0.03;
    /// <summary>Dirt added per rot item lying around (loose or piled on the ground).</summary>
    public double DirtPerRotItem { get; set; } = 0.01;

    /// <summary>
    /// Daily chance that a kept animal adds a level of muck to the ground next to it.
    /// Wild animals never do. 0 turns muck off.
    /// </summary>
    // Tuned so an uncleaned pen of 10 sheep turns dangerous after roughly two weeks, not one.
    public double MuckChancePerDay { get; set; } = 0.15;
    /// <summary>Each crowding animal above the limit raises the muck chance by this fraction.</summary>
    public double MuckCrowdingBonus { get; set; } = 0.15;
    /// <summary>Animals of at least this generation count as kept (player-placed ones always do).</summary>
    public int MuckMinGeneration { get; set; } = 1;

    /// <summary>Distance (blocks) in which other livestock count as crowding.</summary>
    public int CrowdingRadius { get; set; } = 4;
    /// <summary>This many other animals nearby are fine; each one above it adds dirt.</summary>
    public int CrowdingFreeAnimals { get; set; } = 6;
    public double DirtPerExtraAnimal { get; set; } = 0.1;

    /// <summary>How quickly an animal gets dirty in dirtier surroundings, in in-game hours.</summary>
    public double FilthAdaptHours { get; set; } = 24;
    /// <summary>How slowly an animal gets clean on its own in cleaner surroundings, in in-game hours.</summary>
    public double FilthCleanHours { get; set; } = 96;

    /// <summary>Hitting livestock with a shovel does no damage, so muck can be cleaned up around them.</summary>
    public bool ShovelsHarmless { get; set; } = true;

    /// <summary>Filth removed by one grooming (sneak + hold right click with dry grass).</summary>
    public double GroomFilthReduction { get; set; } = 0.25;
    /// <summary>Chance that a grooming uses up one dry grass.</summary>
    public double GroomItemUseChance { get; set; } = 0.5;

    /// <summary>Daily chance to fall sick at 100% filth (scales with filth squared).</summary>
    public double SicknessChancePerDay { get; set; } = 0.25;
    /// <summary>Old animals get sick this many times more easily.</summary>
    public double OldAgeSicknessMultiplier { get; set; } = 2;

    /// <summary>Distance (blocks) in which a sick animal can infect healthy animals of its species.</summary>
    public int ContagionRadius { get; set; } = 3;
    // Per sick-healthy pair: in a dense flock a sick animal has many neighbours, so keep it low.
    public double ContagionChancePerDay { get; set; } = 0.1;

    /// <summary>Daily chance to recover in perfectly clean surroundings (scales down with filth).</summary>
    public double RecoveryChancePerDay { get; set; } = 0.35;
    /// <summary>After recovering, the animal cannot get sick again for this many days.</summary>
    public double ImmunityDays { get; set; } = 6;

    /// <summary>Health lost per in-game day while sick. Sick animals also stop regenerating.</summary>
    public double SickDamagePerDay { get; set; } = 3;

    /// <summary>
    /// A sick animal whose health drops to this fraction of its maximum lies down and stays down
    /// until it recovers or dies (0.3 = 5 of a sheep's 17 HP). 0 turns this off.
    /// </summary>
    public double CollapseHealthFraction { get; set; } = 0.3;

    public void Sanitize()
    {
        DroppingScanRadius = Math.Clamp(DroppingScanRadius, 0, 8);
        CrowdingRadius = Math.Clamp(CrowdingRadius, 0, 16);
        ContagionRadius = Math.Clamp(ContagionRadius, 0, 16);
        if (FilthAdaptHours < 1) FilthAdaptHours = 1;
        if (FilthCleanHours < 1) FilthCleanHours = 1;
        GroomFilthReduction = Math.Clamp(GroomFilthReduction, 0, 1);
        GroomItemUseChance = Math.Clamp(GroomItemUseChance, 0, 1);
        SicknessChancePerDay = Math.Clamp(SicknessChancePerDay, 0, 1);
        ContagionChancePerDay = Math.Clamp(ContagionChancePerDay, 0, 1);
        RecoveryChancePerDay = Math.Clamp(RecoveryChancePerDay, 0, 1);
        if (OldAgeSicknessMultiplier < 0) OldAgeSicknessMultiplier = 0;
        if (SickDamagePerDay < 0) SickDamagePerDay = 0;
        if (ImmunityDays < 0) ImmunityDays = 0;
        CollapseHealthFraction = Math.Clamp(CollapseHealthFraction, 0, 1);
        MuckChancePerDay = Math.Clamp(MuckChancePerDay, 0, 1);
        if (MuckCrowdingBonus < 0) MuckCrowdingBonus = 0;
        DroppingBlockCodes ??= Array.Empty<string>();
    }
}

public class SpeciesAging
{
    public double LifespanYearsMin { get; set; }
    public double LifespanYearsMax { get; set; }
}
