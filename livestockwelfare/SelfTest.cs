using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace livestockwelfare;

/// <summary>
/// Development aid for headless servers: when the LW_SELFTEST environment variable is set to 1,
/// spawns a crowded flock and a lamb near the world spawn, advances time a day at a time and
/// logs their state ("[lwselftest]" lines). Does nothing in normal play.
/// </summary>
public class SelfTest : ModSystem
{
    ICoreServerAPI sapi = null!;
    readonly List<Entity> watched = new();
    bool spawning;
    BlockPos spawn = null!;

    public override bool ShouldLoad(EnumAppSide forSide) =>
        forSide == EnumAppSide.Server && Environment.GetEnvironmentVariable("LW_SELFTEST") == "1";

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ => Run(), 3000));
        // Adults that grow up from the lamb are new entities; watch them too.
        api.Event.OnEntitySpawn += e =>
        {
            if (!spawning && e.Code.Path.StartsWith("sheep") && watched.Count > 0)
            {
                watched.Add(e);
                sapi.Logger.Notification("[lwselftest] new entity {0} #{1}", e.Code.Path, e.EntityId);
            }
        };
    }

    void Run()
    {
        foreach (var cause in new[] { LivestockUtil.OldAgeDeathCause, LivestockUtil.DiseaseDeathCause })
        {
            var type = sapi.World.GetEntityType(new AssetLocation(cause));
            sapi.Logger.Notification("[lwselftest] death cause type {0}: {1}, killedByInfoText={2}",
                cause, type != null ? "found" : "MISSING", type?.Attributes?["killedByInfoText"].AsString());
        }

        ExportDirtPreviews();

        var drygrass = sapi.World.GetItem(new AssetLocation("game:drygrass"));
        sapi.Logger.Notification("[lwselftest] dry grass grooming behavior: {0}",
            drygrass?.CollectibleBehaviors.Any(b => b is CollectibleBehaviorGroom) == true ? "found" : "MISSING");

        var spawn = sapi.World.DefaultSpawnPosition.AsBlockPos;
        sapi.WorldManager.LoadChunkColumnPriority(spawn.X / GlobalConstants.ChunkSize, spawn.Z / GlobalConstants.ChunkSize,
            new ChunkLoadOptions { OnLoaded = () => sapi.Event.EnqueueMainThreadTask(() => SpawnAndWatch(spawn), "lwselftest") });
    }

    // Paints a few vanilla textures with every mud stage and saves them as PNG into the folder in
    // LW_SELFTEST_OUT, to check how the dirty coats look without starting a client.
    void ExportDirtPreviews()
    {
        string? outDir = Environment.GetEnvironmentVariable("LW_SELFTEST_OUT");
        if (string.IsNullOrEmpty(outDir)) return;

        foreach (string path in new[] {
            "textures/entity/animal/mammal/hooved/sheep/bighorn/female.png",
            "textures/entity/animal/mammal/hooved/goat/mountain-adult.png",
            "textures/entity/animal/mammal/hooved/pig/eurasian-female1.png",
            "textures/entity/animal/bird/chicken/hen1.png" })
        {
            var asset = sapi.Assets.TryGet(new AssetLocation("game", path));
            if (asset == null) { sapi.Logger.Warning("[lwselftest] preview texture {0} not found", path); continue; }

            var source = new BitmapExternal(asset.Data, asset.Data.Length, sapi.Logger);
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            for (int stage = 0; stage <= DirtTextures.Stages; stage++)
            {
                using var bmp = DirtTextures.PaintBitmap(source, stage);
                using var data = SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, $"{name}-{stage}.png"), data.ToArray());
            }
            sapi.Logger.Notification("[lwselftest] exported dirt previews for {0}", name);
        }
    }

    void SpawnAndWatch(BlockPos spawn)
    {
        int y = sapi.World.BlockAccessor.GetTerrainMapheightAt(spawn) + 1;
        // 10 ewes close together: mildly crowded, so they soil the ground and some get sick.
        for (int i = 0; i < 10; i++) watched.Add(Spawn("game:sheep-bighorn-adult-female", spawn, i % 4, y, i / 4));
        watched.Add(Spawn("game:sheep-bighorn-baby-female", spawn, 0, y, 4));
        this.spawn = spawn;

        // A small closed plank hut with one sheep, to check shelter detection.
        BuildHut(spawn.AddCopy(10, 0, 0).Set(spawn.X + 10, y, spawn.Z));
        hutSheep = Spawn("game:sheep-bighorn-adult-female", spawn, 12, y, 2);
        Log("spawned");

        int step = 0;
        sapi.Event.RegisterGameTickListener(_ =>
        {
            step++;
            // Lambs grow up after 14 days.
            if (step > 16) return;
            sapi.World.Calendar.Add(24);
            Log("day +" + step);
            if (step == 6 && watched.FirstOrDefault(e => e.Alive)?.GetBehavior<EntityBehaviorLivestockHygiene>() is { } h)
            {
                float before = h.Filth;
                h.Groom(0.25);
                sapi.Logger.Notification("[lwselftest] groomed: filth {0:0.00} -> {1:0.00}", before, h.Filth);
            }
        }, 7000);
    }

    Entity? hutSheep;

    // Walls, floor and roof of planks around a 3x3x2 inside space starting at corner + (1, 0, 1).
    void BuildHut(BlockPos corner)
    {
        var planks = sapi.World.GetBlock(new AssetLocation("game:planks-aged-ud"));
        var accessor = sapi.World.BlockAccessor;
        for (int dx = 0; dx <= 4; dx++)
        for (int dy = -1; dy <= 2; dy++)
        for (int dz = 0; dz <= 4; dz++)
        {
            bool inside = dx is >= 1 and <= 3 && dz is >= 1 and <= 3 && dy is >= 0 and <= 1;
            accessor.SetBlock(inside ? 0 : planks.Id, corner.AddCopy(dx, dy, dz));
        }
    }

    Entity Spawn(string code, BlockPos at, int dx, int y, int dz)
    {
        var e = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation(code)));
        e.Pos.SetPos(at.X + 0.5 + dx, y, at.Z + 0.5 + dz);
        // Like animals placed by a player, so they count as kept livestock.
        e.Attributes.SetString("origin", "playerplaced");
        spawning = true;
        sapi.World.SpawnEntity(e);
        spawning = false;
        return e;
    }

    void Log(string label)
    {
        int alive = watched.Count(e => e.Alive);
        int sick = watched.Count(e => e.Alive && e.WatchedAttributes.GetBool("lwSick"));
        int muckBlocks = 0, muckLevels = 0;
        sapi.World.BlockAccessor.WalkBlocks(spawn.AddCopy(-10, -2, -10), spawn.AddCopy(14, 3, 14), (block, x, y, z) =>
        {
            if (block.Code?.Domain != LivestockUtil.Domain) return;
            muckBlocks++;
            muckLevels += block.LastCodePart()[0] - '0';
        });
        sapi.Logger.Notification("[lwselftest] {0}: day {1:0.0}, {2}/{3} alive, {4} sick, muck: {5} blocks / {6} levels",
            label, sapi.World.Calendar.TotalDays, alive, watched.Count, sick, muckBlocks, muckLevels);

        var climate = sapi.World.BlockAccessor.GetClimateAt(spawn, EnumGetClimateMode.NowValues);
        foreach (var (where, e) in new[] { ("open", watched.FirstOrDefault(w => w.Alive)), ("hut", hutSheep) })
        {
            if (e?.GetBehavior<EntityBehaviorLivestockShelter>() is not { } shelter) continue;
            sapi.Logger.Notification("[lwselftest]   weather {0}: {1}°C rain {2:0.00} -> {3}",
                where, climate?.Temperature, climate?.Rainfall, shelter.StatusText("en"));
        }
        foreach (var e in watched.Where(e => e.Alive && (e == watched[0] || !e.Code.Path.Contains("female-") || e.Code.Path.Contains("baby") || e.WatchedAttributes.GetBool("lwSick"))).Take(4))
        {
            var a = e.WatchedAttributes;
            double days = sapi.World.Calendar.TotalDays - a.GetDouble("lwBirthTotalDays");
            sapi.Logger.Notification("[lwselftest]   {0} #{1}: age {2:0.00} y, lifespan {3:0.00} y, filth {4:0.00}, sick {5}, health {6:0.0}, regenSpeed {7}",
                e.Code.Path, e.EntityId, days / sapi.World.Calendar.DaysPerYear, a.GetDouble("lwLifespanYears"),
                a.GetFloat("lwFilth"), a.GetBool("lwSick") + (a.GetBool("lwCollapsed") ? " COLLAPSED sleepAnim=" + e.AnimManager.IsAnimationActive("sleep") : ""), (a["health"] as Vintagestory.API.Datastructures.ITreeAttribute)?.GetFloat("currenthealth"),
                a.HasAttribute("regenSpeed") ? a.GetFloat("regenSpeed").ToString() : "-");
        }
    }
}
