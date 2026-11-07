using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace livestockwelfare;

/// <summary>
/// Taming mods such as Feral Kinship turn a tamed animal into a new entity type (sheep-adult becomes
/// feralkinship:tamesheep): Pet AI removes the old animal and spawns the tame one in its place,
/// copying only its name and taming data. This carries our state (age, dirt, sickness, thirst...)
/// over, so taming does not make an animal young, clean and healthy again.
/// </summary>
public class TamingCarryOver : ModSystem
{
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.Event.OnEntitySpawn += OnSpawn;
    }

    // Pet AI calls Die(Expire) on the old animal right before spawning the new one at the same
    // position, so the old one is still in its chunk (not alive) when the new one spawns.
    static void OnSpawn(Entity entity)
    {
        if (entity.GetBehavior<EntityBehaviorLivestockAge>() == null) return;

        var old = entity.World.GetEntitiesAround(entity.Pos.XYZ, 0.2f, 0.2f, e =>
            e != entity && !e.Alive && e.Code != entity.Code
            && !e.WatchedAttributes.HasAttribute("deathTotalHours")
            && e.WatchedAttributes.HasAttribute("lwBirthTotalDays")).FirstOrDefault();
        if (old == null) return;

        var from = (TreeAttribute)old.WatchedAttributes;
        var to = entity.WatchedAttributes;
        var keys = from.Keys.Where(k => k.StartsWith("lw")).ToArray();
        foreach (var key in keys)
        {
            to[key] = from[key].Clone();
            to.MarkPathDirty(key);
        }
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1}: took over {2} values from {3} #{4} (tamed)",
            entity.Code, entity.EntityId, keys.Length, old.Code, old.EntityId);
    }
}
