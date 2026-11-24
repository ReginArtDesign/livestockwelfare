using System;
using System.Collections.Generic;

namespace livestockwelfare;

/// <summary>
/// Kept predators (dogs, raised wolves) eat skinned carcasses left on the ground: a corpse the player
/// has cut open with a knife (vanilla harvesting), or a skinned or bled-out body of the Butchering mod
/// put down or thrown on the ground. The carcass does not shrink as it is eaten; when the last meal
/// is gone, the vanilla bone carcass ("skeleton") of the animal's size is left in its place.
/// </summary>
public class CarcassSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Wild predators eat carcasses too (wolves, foxes ...), not only kept ones. Off by default, so
    /// wild wolves are not drawn to the farm.
    /// </summary>
    public bool WildPredatorsToo { get; set; } = false;

    /// <summary>
    /// Butchering mod bodies in these states can be eaten ("dead", "skinned", "bledout", "rotten"),
    /// whichever mod the animal comes from (Butchering names e.g. Traditional Livestock sheep
    /// "tradlivestock:deadsheep-..."). Rotten ones are left alone.
    /// </summary>
    public string[] ButcheringStates { get; set; } = { "skinned", "bledout" };

    /// <summary>
    /// A predator that has not eaten for this many in-game hours eats again even if vanilla says it
    /// is full: vanilla females (dogs, wolves) stop eating once they have eaten enough to breed, until
    /// they have had young. From a carcass, a food bowl or a trough. 0: vanilla rule only.
    /// </summary>
    public double HungryAfterHours { get; set; } = 12;

    /// <summary>How many meals a carcass gives, by the size of the skeleton it leaves (vanilla carcass-{size}).</summary>
    public Dictionary<string, int> MealsBySize { get; set; } = new()
    {
        ["tiny"] = 1,
        ["small"] = 1,
        ["medium"] = 3,
        ["large"] = 6,
    };

    public void Sanitize()
    {
        ButcheringStates ??= Array.Empty<string>();
        HungryAfterHours = Math.Clamp(HungryAfterHours, 0, 1000);
        MealsBySize ??= new();
        foreach (string size in new List<string>(MealsBySize.Keys)) MealsBySize[size] = Math.Clamp(MealsBySize[size], 1, 100);
    }
}
