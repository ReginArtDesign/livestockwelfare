using System;

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
    /// <summary>Distance (blocks) from which they notice the food in a player's hand.</summary>
    public int FollowRadius { get; set; } = 10;
    /// <summary>How close (blocks) they come to the player.</summary>
    public double FollowDistance { get; set; } = 1.5;

    /// <summary>After a treat from the hand, the animal takes the next one only after this many in-game hours.</summary>
    public double HandFeedCooldownHours { get; set; } = 1;

    public void Sanitize()
    {
        AnimalCodes ??= Array.Empty<string>();
        FollowRadius = Math.Clamp(FollowRadius, 0, 32);
        FollowDistance = Math.Clamp(FollowDistance, 0.5, 8);
        if (HandFeedCooldownHours < 0) HandFeedCooldownHours = 0;
    }
}
