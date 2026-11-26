using System;
using System.Linq;

namespace livestockwelfare;

/// <summary>
/// Treats: animals that follow a player holding food they eat, and take it from the hand (sneak +
/// hold right click). Chickens by default, so they can be led into the coop with a handful of grain.
/// </summary>
public class TreatSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Animals that follow food and take it from the hand (wildcards).</summary>
    public string[] AnimalCodes { get; set; } = { "game:chicken-*" };
    /// <summary>
    /// Kept animals (not wild ones) follow a player holding their ground food, so they can be led from
    /// pen to pen and fed where it is set down: scattered grain for chickens, a hay bale (or dry grass)
    /// for sheep, goats and cattle, a vegetable pile for pigs. They don't take it from the hand.
    /// </summary>
    public LureFood[] LureFoods { get; set; } =
    {
        new() { AnimalCodes = new[] { "game:chicken-*" }, FoodCodes = new[] { "livestockwelfare:scatteredgrain-*" }, TameFoodCodes = new[] { "game:grain-*" } },
        new()
        {
            AnimalCodes = new[] { "game:goat-*", "game:sheep-*", "tradlivestock:sheep-*", "tradlivestock:cow-*", "bovinae:*", "tamablesfotsa:*bovinae*" },
            FoodCodes = new[] { "livestockwelfare:haybale-*", "game:drygrass" },
            TameFoodCodes = new[] { "game:drygrass" },
        },
        new()
        {
            AnimalCodes = new[] { "game:pig-*", "tradlivestock:pig-*", "cervinae:*", "tamablesfotsa:*cervini*" },
            FoodCodes = new[] { "livestockwelfare:vegetablepile-*" },
            TameFoodCodes = new[] { "game:vegetable-*" },
        },
    };
    /// <summary>
    /// Taming wild animals (those in LureFoods): crouch near one with its food in hand (LureFood.TameFoodCodes)
    /// and it stops running away; stay crouched and still, and after a while it comes to take the food
    /// from the hand. Fed like that once a day, it stays around the spot and trusts the player more.
    /// </summary>
    public bool Taming { get; set; } = true;
    /// <summary>Seconds (on average) a crouching player must wait before the animal comes to eat.</summary>
    public double TamePatienceSeconds { get; set; } = 10;
    /// <summary>Daily feedings that make it one generation tamer (generation 1: no longer runs away; 2: can be roped).</summary>
    public int TameFeedingsPerGeneration { get; set; } = 3;
    /// <summary>Taming by hand goes up to this generation.</summary>
    public int TameMaxGeneration { get; set; } = 2;
    /// <summary>Once fed by hand, the animal stays within this many blocks of the spot until tame.</summary>
    public int TameHomeRadius { get; set; } = 16;
    /// <summary>Distance (blocks) from which they notice the food in a player's hand.</summary>
    public int FollowRadius { get; set; } = 10;
    /// <summary>How close (blocks) they come to the player.</summary>
    public double FollowDistance { get; set; } = 1.5;

    /// <summary>After a treat from the hand, the animal takes the next one only after this many in-game hours.</summary>
    public double HandFeedCooldownHours { get; set; } = 1;

    public void Sanitize()
    {
        AnimalCodes ??= Array.Empty<string>();
        LureFoods = (LureFoods ?? Array.Empty<LureFood>()).Where(lure => lure != null).ToArray();
        foreach (var lure in LureFoods)
        {
            lure.AnimalCodes ??= Array.Empty<string>();
            lure.FoodCodes ??= Array.Empty<string>();
            lure.TameFoodCodes ??= Array.Empty<string>();
        }
        FollowRadius = Math.Clamp(FollowRadius, 0, 32);
        FollowDistance = Math.Clamp(FollowDistance, 0.5, 8);
        if (HandFeedCooldownHours < 0) HandFeedCooldownHours = 0;
        TamePatienceSeconds = Math.Clamp(TamePatienceSeconds, 0, 600);
        TameFeedingsPerGeneration = Math.Clamp(TameFeedingsPerGeneration, 1, 100);
        TameMaxGeneration = Math.Clamp(TameMaxGeneration, 0, 10);
        TameHomeRadius = Math.Clamp(TameHomeRadius, 4, 64);
    }
}

/// <summary>Kept animals (wildcards) and the ground foods (item or block wildcards) that lure them.</summary>
public class LureFood
{
    public string[] AnimalCodes { get; set; } = Array.Empty<string>();
    public string[] FoodCodes { get; set; } = Array.Empty<string>();
    /// <summary>Food a wild one of these takes from the hand to be tamed (see TreatSettings.Taming).</summary>
    public string[] TameFoodCodes { get; set; } = Array.Empty<string>();
}
