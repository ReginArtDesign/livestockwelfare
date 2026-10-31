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

    public WeatherSettings Weather { get; set; } = new();

    public ThirstSettings Thirst { get; set; } = new();

    public SafetySettings Safety { get; set; } = new();

    public void Sanitize()
    {
        if (OldAgeFraction is < 0.1 or > 1) OldAgeFraction = 0.75;
        foreach (var aging in Species.Values)
        {
            if (aging.LifespanYearsMin < 0.1) aging.LifespanYearsMin = 0.1;
            if (aging.LifespanYearsMax < aging.LifespanYearsMin) aging.LifespanYearsMax = aging.LifespanYearsMin;
        }
        Hygiene.Sanitize();
        Weather.Sanitize();
        Thirst.Sanitize();
        Safety.Sanitize();
    }
}

/// <summary>
/// Rain, snow, cold and strong sun stress animals that have no shelter. A roof keeps off rain,
/// snow and sun; a closed room (no open exits) also keeps out the cold. Stress makes animals
/// sick more easily, stops breeding and, when severe, slowly hurts them.
/// </summary>
public class WeatherSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Stress while standing in rain (scaled by how hard it rains).</summary>
    public double RainStress { get; set; } = 0.35;
    /// <summary>Stress while standing in snowfall (scaled by how hard it snows).</summary>
    public double SnowStress { get; set; } = 0.45;
    /// <summary>Stress per degree below the species' cold limit, outside a closed room.</summary>
    public double ColdStressPerDegree { get; set; } = 0.05;
    /// <summary>Under a roof but not in a closed room, cold stress is multiplied by this.</summary>
    public double RoofColdFactor { get; set; } = 0.5;
    /// <summary>Stress per degree above the species' heat limit, in direct daytime sun.</summary>
    public double HeatStressPerDegree { get; set; } = 0.06;

    /// <summary>How quickly stress follows the weather, in in-game hours.</summary>
    public double StressAdaptHours { get; set; } = 12;

    /// <summary>
    /// How much weather stress counts towards falling sick, compared to filth: the daily sickness
    /// chance scales with filth² + (stress × this)². 1 = stress is as bad as dirt.
    /// </summary>
    public double SicknessWeight { get; set; } = 1;
    /// <summary>Animals this stressed do not breed.</summary>
    public double NoBreedingAboveStress { get; set; } = 0.5;
    /// <summary>Animals this stressed stop healing and slowly lose health.</summary>
    public double HarmAboveStress { get; set; } = 0.8;
    public double HarmPerDay { get; set; } = 2;

    /// <summary>In bad weather, animals in the open walk to the nearest spot under a roof.</summary>
    public bool SeekShelter { get; set; } = true;
    /// <summary>How far (blocks) animals look for a roof.</summary>
    public int SeekShelterRadius { get; set; } = 12;
    /// <summary>At night animals also go under the roof and sleep there until morning.</summary>
    public bool SleepUnderRoof { get; set; } = true;

    /// <summary>Stress removed by petting (right click with an empty hand).</summary>
    public double PetStressReduction { get; set; } = 0.2;
    /// <summary>An animal can be petted once per this many in-game hours.</summary>
    public double PetCooldownHours { get; set; } = 2;

    /// <summary>Temperatures (°C) each species copes with; "default" is used for others.</summary>
    public Dictionary<string, WeatherTolerance> Species { get; set; } = new()
    {
        // Wool and thick coats handle cold well; pigs and chickens much less.
        ["default"] = new WeatherTolerance { ColdBelowC = -2, HotAboveC = 30 },
        ["sheep"] = new WeatherTolerance { ColdBelowC = -10, HotAboveC = 27 },
        ["goat"] = new WeatherTolerance { ColdBelowC = -6, HotAboveC = 32 },
        ["pig"] = new WeatherTolerance { ColdBelowC = 2, HotAboveC = 26 },
        ["chicken"] = new WeatherTolerance { ColdBelowC = 0, HotAboveC = 30 },
    };

    public WeatherTolerance ToleranceFor(string species) =>
        Species.TryGetValue(species, out var t) ? t : Species.TryGetValue("default", out var d) ? d : new WeatherTolerance();

    public void Sanitize()
    {
        if (StressAdaptHours < 1) StressAdaptHours = 1;
        RoofColdFactor = Math.Clamp(RoofColdFactor, 0, 1);
        if (SicknessWeight < 0) SicknessWeight = 0;
        PetStressReduction = Math.Clamp(PetStressReduction, 0, 1);
        SeekShelterRadius = Math.Clamp(SeekShelterRadius, 2, 24);
        if (PetCooldownHours < 0) PetCooldownHours = 0;
        if (HarmPerDay < 0) HarmPerDay = 0;
        Species ??= new();
    }
}

/// <summary>
/// Animals get thirsty over time, faster in warm weather and much faster in the hot sun. They drink
/// for free from fresh water (ponds, rivers) nearby; from barrels and placed buckets the water is
/// really used up, so they have to be refilled.
/// </summary>
public class ThirstSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Thirst gained per in-game day at mild temperatures (1.0 = fully parched).</summary>
    public double ThirstPerDay { get; set; } = 0.8;
    /// <summary>Above this temperature (°C) thirst grows faster: +10% per degree.</summary>
    public double WarmAboveC { get; set; } = 20;
    /// <summary>In the hot sun (see Weather) thirst grows this many times faster on top.</summary>
    public double HotSunMultiplier { get; set; } = 1.5;

    /// <summary>Animals drink once thirst is above this.</summary>
    public double DrinkAboveThirst { get; set; } = 0.25;

    /// <summary>Thirsty animals walk to water (like to a trough) and drink there.</summary>
    public bool WalkToWater { get; set; } = true;
    /// <summary>Animals start looking for water above this thirst.</summary>
    public double SeekWaterAboveThirst { get; set; } = 0.2;
    /// <summary>How far (blocks) animals look for water to walk to.</summary>
    public int SeekWaterRadius { get; set; } = 16;
    /// <summary>With WalkToWater, animals only drink without walking (within DrinkRadius) above this.</summary>
    public double RemoteDrinkAboveThirst { get; set; } = 0.6;
    /// <summary>Horizontal distance (blocks) an animal looks for water.</summary>
    public int DrinkRadius { get; set; } = 6;
    /// <summary>Animals may drink from ponds and rivers (never from salt water).</summary>
    public bool DrinkFromNaturalWater { get; set; } = true;

    /// <summary>Animals this thirsty do not breed.</summary>
    public double NoBreedingAboveThirst { get; set; } = 0.6;
    /// <summary>Animals this thirsty stop healing and slowly lose health.</summary>
    public double HarmAboveThirst { get; set; } = 0.95;
    public double HarmPerDay { get; set; } = 3;

    /// <summary>Litres of water a fully parched animal drinks from a barrel; "default" for others.</summary>
    // Less than real animals drink: at these amounts 10 sheep use about 8 litres a day, so a full
    // 50 litre barrel lasts roughly 6 in-game days in mild weather (half that in a heat wave).
    public Dictionary<string, double> LitresPerFullDrink { get; set; } = new()
    {
        ["default"] = 1,
        ["sheep"] = 1,
        ["goat"] = 1,
        ["pig"] = 1.5,
        ["chicken"] = 0.1,
    };

    public double LitresFor(string species) =>
        LitresPerFullDrink.TryGetValue(species, out double l) ? l : LitresPerFullDrink.TryGetValue("default", out double d) ? d : 2;

    public void Sanitize()
    {
        if (ThirstPerDay < 0) ThirstPerDay = 0;
        if (HotSunMultiplier < 1) HotSunMultiplier = 1;
        DrinkAboveThirst = Math.Clamp(DrinkAboveThirst, 0, 1);
        DrinkRadius = Math.Clamp(DrinkRadius, 1, 16);
        SeekWaterRadius = Math.Clamp(SeekWaterRadius, 2, 32);
        SeekWaterAboveThirst = Math.Clamp(SeekWaterAboveThirst, 0, 1);
        RemoteDrinkAboveThirst = Math.Clamp(RemoteDrinkAboveThirst, 0, 1);
        if (HarmPerDay < 0) HarmPerDay = 0;
        LitresPerFullDrink ??= new();
    }
}

/// <summary>
/// Ways to make the mod gentler, e.g. for multiplayer servers where owners are not always online.
/// </summary>
public class SafetySettings
{
    /// <summary>Master switch: false = no animal ever dies from this mod (they still get old, sick, weak).</summary>
    public bool AnimalsCanDie { get; set; } = true;
    /// <summary>Per cause. A cause that cannot kill leaves the animal at 1 health instead.</summary>
    public bool OldAgeCanKill { get; set; } = true;
    public bool DiseaseCanKill { get; set; } = true;
    public bool ExposureCanKill { get; set; } = true;
    public bool ThirstCanKill { get; set; } = true;

    /// <summary>
    /// Pause everything (aging, dirt, sickness, weather, thirst) for animals with no player within
    /// PlayerRadius blocks, so animals don't suffer or age while their owner is away or offline.
    /// </summary>
    public bool PauseWhenNoPlayerNearby { get; set; } = false;
    public int PlayerRadius { get; set; } = 64;

    public void Sanitize()
    {
        PlayerRadius = Math.Clamp(PlayerRadius, 8, 512);
    }
}

public class WeatherTolerance
{
    public double ColdBelowC { get; set; } = -2;
    public double HotAboveC { get; set; } = 30;
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
