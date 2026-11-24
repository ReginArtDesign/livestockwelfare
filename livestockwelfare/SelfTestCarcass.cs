using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Development aid, like SelfTest (LW_SELFTEST=1): a sheep corpse cut open with a knife, and a
/// Butchering bled-out body thrown down (when that mod is installed), are offered as food to a kept
/// wolf but not to a wild one; eaten up, they leave a skeleton. Logs "[lwselftest] carcass" lines.
/// (The food source is eaten directly: without a player nearby the server leaves animals inactive,
/// so the wolf would not walk there anyway.)
/// </summary>
public class SelfTestCarcass : ModSystem
{
    ICoreServerAPI sapi = null!;

    public override bool ShouldLoad(EnumAppSide forSide) =>
        forSide == EnumAppSide.Server && Environment.GetEnvironmentVariable("LW_SELFTEST") == "1";

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () =>
        {
            var spawn = api.World.DefaultSpawnPosition.AsBlockPos;
            api.WorldManager.LoadChunkColumnPriority(spawn.X / GlobalConstants.ChunkSize, spawn.Z / GlobalConstants.ChunkSize,
                new ChunkLoadOptions { KeepLoaded = true, OnLoaded = () => api.Event.EnqueueMainThreadTask(() => Check(spawn), "lwselftestcarcass") });
        });
    }

    Entity Spawn(string code, BlockPos pos)
    {
        var entity = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation(code)));
        entity.Pos.SetPos(pos.X + 0.5, pos.Y, pos.Z + 0.5);
        sapi.World.SpawnEntity(entity);
        return entity;
    }

    void Check(BlockPos spawn)
    {
        var settings = livestockwelfareModSystem.Config?.Carcasses;
        var feeding = sapi.ModLoader.GetModSystem<CarcassFeeding>();
        var pois = sapi.ModLoader.GetModSystem<POIRegistry>();
        var wolfType = sapi.World.GetEntityType(new AssetLocation("game:wolf-eurasian-adult-male"));
        if (settings == null || wolfType == null)
        {
            sapi.Logger.Notification("[lwselftest] carcass: FAILED (no config or no wolf)");
            return;
        }

        // A stone floor high in the air, so the skeleton has solid ground and nothing is in the way.
        var floor = new BlockPos(spawn.X + 6, Math.Min(spawn.Y + 40, sapi.WorldManager.MapSizeY - 10), spawn.Z + 6);
        var stone = sapi.World.GetBlock(new AssetLocation("game:rock-granite"));
        for (int x = -3; x <= 3; x++)
        for (int z = -3; z <= 3; z++)
        {
            sapi.World.BlockAccessor.SetBlock(stone.BlockId, floor.AddCopy(x, 0, z));
            sapi.World.BlockAccessor.SetBlock(0, floor.AddCopy(x, 1, z));
        }
        var ground = floor.UpCopy();

        var diet = wolfType.Attributes["creatureDiet"].AsObject<CreatureDiet>();
        var wild = Spawn("game:wolf-eurasian-adult-male", ground.AddCopy(-2, 0, -2));
        var kept = Spawn("game:wolf-eurasian-adult-male", ground.AddCopy(-2, 0, 2));
        kept.WatchedAttributes.SetInt("generation", 2);

        // Vanilla: a sheep corpse cut open (harvested) with something left in it.
        var sheep = Spawn("game:sheep-bighorn-adult-female", ground);
        sheep.Die(EnumDespawnReason.Death);
        sheep.WatchedAttributes.SetBool("harvested", true);
        feeding.ScanAround(ground.ToVec3d(), settings);
        var corpse = pois.GetNearestPoi(ground.ToVec3d(), 3, p => p is SkinnedCorpse) as SkinnedCorpse;
        Report("vanilla skinned sheep", corpse, wild, kept, diet, ground, () => sheep.GetBehavior<EntityBehaviorDeadDecay>() is null || ((EntityAgent)sheep).AllowDespawn);

        // Butchering: a bled-out sheep thrown on the ground.
        var bodyItem = sapi.World.GetItem(new AssetLocation("butchering:deadsheep-female-bighorn-1-bledout"));
        if (bodyItem == null)
        {
            sapi.Logger.Notification("[lwselftest] carcass butchering: not installed");
        }
        else
        {
            var at = ground.AddCopy(2, 0, 0);
            var stack = new ItemStack(bodyItem);
            stack.Attributes.SetString("AnimalCarcass", "game:carcass-small");
            var thrown = sapi.World.SpawnItemEntity(stack, at.ToVec3d().Add(0.5, 0.1, 0.5));
            feeding.ScanAround(at.ToVec3d(), settings);
            var loose = pois.GetNearestPoi(at.ToVec3d(), 3, p => p is LooseCarcass) as LooseCarcass;
            Report("butchering bled-out sheep", loose, wild, kept, diet, at, () => !thrown.Alive);

            var rotten = sapi.World.GetItem(new AssetLocation("butchering:deadsheep-female-bighorn-1-rotten"));
            sapi.Logger.Notification("[lwselftest] carcass butchering rotten body eaten: {0} (expect False) — {1}",
                CarcassFeeding.IsCarcassItem(rotten == null ? null : new ItemStack(rotten), settings),
                rotten != null && !CarcassFeeding.IsCarcassItem(new ItemStack(rotten), settings) ? "OK" : "FAILED");

            // Butchering names other mods' bodies in their own domain (Traditional Livestock sheep).
            var tradBody = sapi.World.GetItem(new AssetLocation("tradlivestock:deadsheep-female-1-bledout"));
            if (tradBody == null) sapi.Logger.Notification("[lwselftest] carcass traditional livestock body: not installed");
            else sapi.Logger.Notification("[lwselftest] carcass traditional livestock bled-out sheep is a carcass: {0} — {1}",
                CarcassFeeding.IsCarcassItem(new ItemStack(tradBody), settings), CarcassFeeding.IsCarcassItem(new ItemStack(tradBody), settings) ? "OK" : "FAILED");
        }

        CheckFullFemale(ground.AddCopy(2, 0, 2));
        wild.Die(EnumDespawnReason.Removed);
        kept.Die(EnumDespawnReason.Removed);
    }

    // A kept she-wolf full enough to breed: vanilla says she doesn't eat. Just after a meal that
    // stays so; after HungryAfterHours without one she eats again.
    void CheckFullFemale(BlockPos pos)
    {
        var female = Spawn("game:wolf-eurasian-adult-female", pos);
        female.WatchedAttributes.SetInt("generation", 2);
        if (female.GetBehavior<EntityBehaviorMultiplyBase>() is not { } multiply)
        {
            sapi.Logger.Notification("[lwselftest] carcass full she-wolf: no multiply behavior — FAILED");
            return;
        }
        female.WatchedAttributes.GetOrAddTreeAttribute("hunger").SetFloat("saturation", 100);
        double now = sapi.World.Calendar.TotalHours;
        double after = livestockwelfareModSystem.Config?.Carcasses.HungryAfterHours ?? 12;
        female.WatchedAttributes.SetDouble("lastMealEatenTotalHours", now - 1);
        bool justAte = multiply.ShouldEat;
        female.WatchedAttributes.SetDouble("lastMealEatenTotalHours", now - after - 1);
        bool hungry = multiply.ShouldEat;
        sapi.Logger.Notification("[lwselftest] carcass full she-wolf eats: just after a meal {0} (expect False), {1} h later {2} (expect True) — {3}",
            justAte, after + 1, hungry, !justAte && hungry ? "OK" : "FAILED");
        female.Die(EnumDespawnReason.Removed);
    }

    void Report(string what, CarcassFoodSource? source, Entity wild, Entity kept, CreatureDiet diet, BlockPos at, Func<bool> carcassGone)
    {
        if (source == null)
        {
            sapi.Logger.Notification("[lwselftest] carcass {0}: not registered as food — FAILED", what);
            return;
        }
        bool wildEats = source.IsSuitableFor(wild, diet);
        bool keptEats = source.IsSuitableFor(kept, diet);
        int meals = source.Meals;
        string skeleton = source.SkeletonCode;
        int eaten = 0;
        while (source.IsSuitableFor(kept, diet) && eaten < 20)
        {
            source.ConsumeOnePortion(kept);
            eaten++;
        }

        // The skeleton is at the carcass's block or right next to it.
        var accessor = sapi.World.BlockAccessor;
        var found = new[] { at }.Concat(BlockFacing.HORIZONTALS.Select(f => at.AddCopy(f)))
            .Select(p => accessor.GetBlock(p).Code?.ToString()).FirstOrDefault(c => c == skeleton);
        bool gone = carcassGone() && !source.Exists;
        bool ok = !wildEats && keptEats && eaten == meals && found != null && gone;
        sapi.Logger.Notification("[lwselftest] carcass {0}: wild wolf eats {1} (expect False), kept wolf eats {2}, meals {3} eaten {4}, skeleton {5} {6}, carcass gone {7} — {8}",
            what, wildEats, keptEats, meals, eaten, skeleton, found != null ? "placed" : "MISSING", gone, ok ? "OK" : "FAILED");
    }
}
