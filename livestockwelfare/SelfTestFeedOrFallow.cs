using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Development aid, like SelfTest (LW_SELFTEST=1), needing Feed or Fallow installed: a pregnant sow
/// and a full hen, vanilla says they don't eat; after HungryEatAfterHours without a meal they should.
/// Logs "[lwselftest] feedorfallow" lines. (Only the decision is checked: without a player nearby
/// the server leaves animals inactive, so they would not walk to a trough anyway.)
/// </summary>
public class SelfTestFeedOrFallow : ModSystem
{
    ICoreServerAPI sapi = null!;

    public override bool ShouldLoad(EnumAppSide forSide) =>
        forSide == EnumAppSide.Server && Environment.GetEnvironmentVariable("LW_SELFTEST") == "1";

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ => Check(), 2000));
    }

    void Check()
    {
        if (!sapi.ModLoader.IsModEnabled(FeedOrFallowCompat.ModId))
        {
            sapi.Logger.Notification("[lwselftest] feedorfallow: not installed");
            return;
        }
        Test("game:pig-eurasian-adult-female", pregnant: true);
        Test("game:chicken-hen", pregnant: false);
    }

    void Test(string code, bool pregnant)
    {
        var type = sapi.World.GetEntityType(new AssetLocation(code));
        if (type == null)
        {
            sapi.Logger.Notification("[lwselftest] feedorfallow {0}: no such entity", code);
            return;
        }
        var entity = sapi.World.ClassRegistry.CreateEntity(type);
        var pos = sapi.World.DefaultSpawnPosition.AsBlockPos;
        entity.Pos.SetPos(pos.X + 0.5, sapi.World.BlockAccessor.GetRainMapHeightAt(pos) + 1, pos.Z + 0.5);
        entity.WatchedAttributes.SetInt("generation", 2);
        sapi.World.SpawnEntity(entity);

        var multiply = entity.GetBehavior<EntityBehaviorMultiplyBase>();
        if (multiply == null || entity.GetBehavior("feedorfallow") == null)
        {
            sapi.Logger.Notification("[lwselftest] feedorfallow {0}: multiply {1}, feedorfallow {2}", code, multiply != null, entity.GetBehavior("feedorfallow") != null);
            entity.Die(EnumDespawnReason.Removed);
            return;
        }

        // Full (or pregnant): vanilla would not send it to the trough.
        entity.WatchedAttributes.GetOrAddTreeAttribute("hunger").SetFloat("saturation", 100);
        if (pregnant) entity.WatchedAttributes.GetOrAddTreeAttribute("multiply").SetBool("isPregnant", true);

        double now = sapi.World.Calendar.TotalHours;
        double after = livestockwelfareModSystem.Config?.Breeding.HungryEatAfterHours ?? 24;
        entity.WatchedAttributes.SetDouble("lastMealEatenTotalHours", now - after / 2);
        bool recent = multiply.ShouldEat;
        entity.WatchedAttributes.SetDouble("lastMealEatenTotalHours", now - after - 1);
        bool hungry = multiply.ShouldEat;

        sapi.Logger.Notification("[lwselftest] feedorfallow {0} (pregnant {1}): ate recently -> eats {2} (want False), {3} h without a meal -> eats {4} (want True) — {5}",
            code, (multiply as EntityBehaviorMultiply)?.IsPregnant, recent, after + 1, hungry, !recent && hungry ? "OK" : "FAIL");
        entity.Die(EnumDespawnReason.Removed);
    }
}
