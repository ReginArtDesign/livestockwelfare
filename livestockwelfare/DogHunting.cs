using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Remembers which animals a player wounded recently, so kept dogs hunt together with their
/// people: they leave wild game alone unless a player hurt it (DogSettings.HuntOnlyWoundedGame).
/// Server side only; the memory is short and not saved.
/// </summary>
public static class DogHunting
{
    // How long a wounded animal stays fair game (ms).
    const long WoundedMs = 30000;

    static readonly Dictionary<long, (string PlayerUid, long Ms)> wounded = new();

    public static void Start(ICoreServerAPI api)
    {
        api.Event.OnEntitySpawn += Watch;
        api.Event.OnEntityLoaded += Watch;
        api.Event.OnEntityDespawn += (entity, _) => wounded.Remove(entity.EntityId);
    }

    public static void Clear() => wounded.Clear();

    static void Watch(Entity entity)
    {
        if (entity is EntityPlayer || entity.GetBehavior<EntityBehaviorHealth>() is not { } health) return;
        health.onDamaged += (damage, source) =>
        {
            if (damage > 0 && source.GetCauseEntity() is EntityPlayer player)
            {
                wounded[entity.EntityId] = (player.PlayerUID, entity.World.ElapsedMilliseconds);
            }
            return damage;
        };
    }

    /// <summary>The player who wounded this animal in the last half minute, or null.</summary>
    public static string? WoundedBy(Entity entity)
    {
        if (!wounded.TryGetValue(entity.EntityId, out var hit)) return null;
        if (entity.World.ElapsedMilliseconds - hit.Ms <= WoundedMs) return hit.PlayerUid;
        wounded.Remove(entity.EntityId);
        return null;
    }
}
