using System;

namespace livestockwelfare;

/// <summary>
/// Scattered grain: a handful of grain on the ground that chickens and other grain-eating birds
/// peck up, portion by portion, like from a trough.
/// </summary>
public class ScatteredGrainSettings
{
    /// <summary>Animals that eat it (wildcards), if their diet takes grain. Pigs are left out on purpose.</summary>
    public string[] AnimalCodes { get; set; } = { "game:chicken-*", "dinornithidae:*" };

    /// <summary>In-game hours of rain under the open sky before it spoils. 0: never spoils.</summary>
    public double SpoilAfterRainHours { get; set; } = 6;

    public void Sanitize()
    {
        AnimalCodes ??= Array.Empty<string>();
        SpoilAfterRainHours = Math.Clamp(SpoilAfterRainHours, 0, 1000);
    }
}
