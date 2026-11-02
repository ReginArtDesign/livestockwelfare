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
        CheckSchematic();

        if (Environment.GetEnvironmentVariable("LW_SELFTEST_NODEATH") == "1" && livestockwelfareModSystem.Config is { } config)
        {
            config.Safety.AnimalsCanDie = false;
            sapi.Logger.Notification("[lwselftest] death disabled for this run");
        }

        var drygrass = sapi.World.GetItem(new AssetLocation("game:drygrass"));
        sapi.Logger.Notification("[lwselftest] dry grass grooming behavior: {0}",
            drygrass?.CollectibleBehaviors.Any(b => b is CollectibleBehaviorGroom) == true ? "found" : "MISSING");

        var spawn = sapi.World.DefaultSpawnPosition.AsBlockPos;
        // The test animals stand up to 16 blocks from the spawn, possibly in the next chunk.
        for (int cx = -1; cx <= 1; cx++)
        for (int cz = -1; cz <= 1; cz++)
        {
            if (cx != 0 || cz != 0) sapi.WorldManager.LoadChunkColumnPriority(spawn.X / GlobalConstants.ChunkSize + cx, spawn.Z / GlobalConstants.ChunkSize + cz, new ChunkLoadOptions { KeepLoaded = true });
        }
        sapi.WorldManager.LoadChunkColumnPriority(spawn.X / GlobalConstants.ChunkSize, spawn.Z / GlobalConstants.ChunkSize,
            new ChunkLoadOptions { KeepLoaded = true, OnLoaded = () => sapi.Event.EnqueueMainThreadTask(() =>
            {
                // LW_SELFTEST_POND=1: only the pond test, in real time (the main test skips whole
                // days, and after a time skip animals drink without walking).
                if (Environment.GetEnvironmentVariable("LW_SELFTEST_POND") == "1") PondTest(spawn);
                else SpawnAndWatch(spawn);
            }, "lwselftest") });
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

    // Loads the schematic in LW_SELFTEST_SCHEMATIC and reports block codes the game doesn't know.
    void CheckSchematic()
    {
        string? path = Environment.GetEnvironmentVariable("LW_SELFTEST_SCHEMATIC");
        if (string.IsNullOrEmpty(path)) return;

        string error = "";
        var schematic = BlockSchematic.LoadFromFile(path, ref error);
        if (schematic == null)
        {
            sapi.Logger.Warning("[lwselftest] schematic failed to load: {0}", error);
            return;
        }
        var missing = schematic.BlockCodes.Values.Where(code => sapi.World.GetBlock(code) == null).ToList();
        sapi.Logger.Notification("[lwselftest] schematic {0}x{1}x{2}, {3} blocks, unknown codes: {4}",
            schematic.SizeX, schematic.SizeY, schematic.SizeZ, schematic.Indices.Count, missing.Count == 0 ? "none" : string.Join(", ", missing));
    }

    void SpawnAndWatch(BlockPos spawn)
    {
        int y = sapi.World.BlockAccessor.GetTerrainMapheightAt(spawn) + 1;
        // 10 ewes close together: mildly crowded, so they soil the ground and some get sick.
        for (int i = 0; i < 10; i++) watched.Add(Spawn("game:sheep-bighorn-adult-female", spawn, i % 4, y, i / 4));
        watched.Add(Spawn("game:sheep-bighorn-baby-female", spawn, 0, y, 4));
        this.spawn = spawn;

        var tasks = watched[0].GetBehavior<Vintagestory.GameContent.EntityBehaviorTaskAI>()?.TaskManager.AllTasks;
        sapi.Logger.Notification("[lwselftest] AI tasks: seek water {0}, seek shelter {1}",
            tasks?.Any(t => t is AiTaskLivestockSeekWater) == true ? "added" : "MISSING",
            tasks?.Any(t => t is AiTaskLivestockSeekShelter) == true ? "added" : "MISSING");

        // A full barrel (50 litres) of water next to the flock; the sheep in the hut has none.
        // 9 blocks away by default: beyond the 6-block radius for drinking without walking.
        PlaceWaterBarrel(spawn.AddCopy(0, 0, 0).Set(spawn.X - BarrelDistance, y, spawn.Z), 50);
        // Control: an identical barrel far from any animal, to tell apart drinking from other effects.
        controlBarrel = spawn.Copy().Set(spawn.X - 25, sapi.World.BlockAccessor.GetTerrainMapheightAt(spawn.Copy().Set(spawn.X - 25, y, spawn.Z)) + 1, spawn.Z);
        sapi.World.BlockAccessor.SetBlock(sapi.World.GetBlock(new AssetLocation("game:barrel")).Id, controlBarrel);

        // A small closed plank hut with one sheep, to check shelter detection.
        BuildHut(spawn.AddCopy(10, 0, 0).Set(spawn.X + 10, y, spawn.Z));
        hutSheep = Spawn("game:sheep-bighorn-adult-female", spawn, 12, y, 2);

        // Babies, away from the flock: a ewe gives "birth" (spawned like vanilla does, at her exact
        // spot), a second ewe will die so her lamb becomes an orphan, and a chick and a dirty hen
        // stand next to muck to check that chicks never get dirty and hens clean themselves.
        nursingEwe = Spawn("game:sheep-bighorn-adult-female", spawn, -6, y, 8);
        newborn = SpawnBorn("game:sheep-bighorn-baby-male", nursingEwe);
        doomedEwe = Spawn("game:sheep-bighorn-adult-female", spawn, -16, y, 8);
        orphan = SpawnBorn("game:sheep-bighorn-baby-female", doomedEwe);
        chick = Spawn("game:chicken-baby", spawn, 6, y, 10);
        hen = Spawn("game:chicken-hen", spawn, 8, y, 10);
        hen.WatchedAttributes.SetFloat("lwFilth", 0.8f);
        var muck = sapi.World.GetBlock(new AssetLocation(LivestockUtil.Domain, "muck-4"));
        sapi.World.BlockAccessor.SetBlock(muck.Id, spawn.Copy().Set(spawn.X + 6, y, spawn.Z + 11));
        // Muck must not float: dig out the ground under a second muck block.
        var floatingMuck = spawn.Copy().Set(spawn.X - 20, y, spawn.Z - 10);
        sapi.World.BlockAccessor.SetBlock(muck.Id, floatingMuck);
        sapi.World.BlockAccessor.BreakBlock(floatingMuck.DownCopy(), null);
        sapi.Logger.Notification("[lwselftest] birth: newborn mother {0} (ewe #{1}), orphan-to-be mother {2} (ewe #{3})",
            newborn.WatchedAttributes.GetLong("lwMotherId"), nursingEwe.EntityId, orphan.WatchedAttributes.GetLong("lwMotherId"), doomedEwe.EntityId);
        var groomedChicken = hen.GetBehavior<EntityBehaviorLivestockHygiene>();
        sapi.Logger.Notification("[lwselftest] groomable: hen {0}, chick {1}, ewe {2}", groomedChicken?.CanBeGroomed,
            chick.GetBehavior<EntityBehaviorLivestockHygiene>()?.CanBeGroomed, nursingEwe.GetBehavior<EntityBehaviorLivestockHygiene>()?.CanBeGroomed);
        // Hot spring water in the hut: not drinkable, the status should say so.
        sapi.World.BlockAccessor.SetBlock(sapi.World.GetBlock(new AssetLocation("game:boilingwater-still-7")).Id, spawn.Copy().Set(spawn.X + 11, y, spawn.Z + 1));
        Log("spawned");

        int step = 0;
        sapi.Event.RegisterGameTickListener(_ =>
        {
            step++;
            if (step == 1) sapi.Logger.Notification("[lwselftest] muck after digging out its ground: {0}", sapi.World.BlockAccessor.GetBlock(floatingMuck).Code);
            if (step == 2) HeatTest();
            if (step == 3) NightTest();
            if (step == 4) HydrateOrDiedrateTest();
            // Lambs grow up after 14 days.
            if (step > Steps) return;
            sapi.World.Calendar.Add(StepHours);
            Log("day +" + step);
            foreach (var sick in watched.Where(e => e.Alive && e.WatchedAttributes.GetBool("lwSick")).Take(2))
            {
                sapi.Logger.Notification("[lwselftest]   sick #{0}: walkspeed {1:0.00}, lying {2}", sick.EntityId,
                    sick.Stats.GetBlended("walkspeed"), sick.AnimManager.IsAnimationActive("sleep"));
            }
            if (step == 2) LivestockUtil.Kill(doomedEwe!, LivestockUtil.DiseaseDeathCause);
            // A thirsty mother has less milk, a sick one may pass on her illness.
            if (step == 3 && nursingEwe!.Alive)
            {
                nursingEwe.WatchedAttributes.SetFloat("lwThirst", 0.9f);
                nursingEwe.GetBehavior<EntityBehaviorLivestockHygiene>()?.Infect();
            }
            // The orphan cannot eat from a trough yet: feed it by hand, milk twice and grain once.
            if (step == 3 && orphan!.Alive) HeldFeedCarrot();
            if (step >= 3 && step <= 5 && orphan!.Alive) HandFeedOrphan(step == 4 ? "game:grain-flax" : null);
            if (step == 6 && watched.FirstOrDefault(e => e.Alive)?.GetBehavior<EntityBehaviorLivestockHygiene>() is { } h)
            {
                float before = h.Filth;
                h.Groom(0.25);
                sapi.Logger.Notification("[lwselftest] groomed: filth {0:0.00} -> {1:0.00}", before, h.Filth);
            }
        }, 7000);
    }

    // Feeds the orphan like a player would: grain, or milk from a bucket when code is null.
    // The whole held-use sequence as the server runs it for a player, with a sheep standing in for
    // the player (sneaking): start, steps every 0.1 s, then stop.
    void HeldFeedCarrot()
    {
        var feeder = (EntityAgent)watched.First(e => e.Alive && e != orphan && e.Code.Path.Contains("adult"));
        feeder.Controls.ShiftKey = true;
        var slot = new DummySlot(new ItemStack(sapi.World.GetItem(new AssetLocation("game:vegetable-carrot")), 5));
        var sel = new EntitySelection { Entity = orphan, Position = orphan!.Pos.XYZ };
        var nursing = orphan.GetBehavior<EntityBehaviorLivestockNursing>()!;
        float before = nursing.Nourished;
        var handling = EnumHandHandling.NotHandled;
        slot.Itemstack.Collectible.OnHeldUseStart(slot, feeder, null, sel, EnumHandInteract.HeldItemInteract, true, ref handling);
        var steps = new System.Text.StringBuilder();
        float t = 0;
        var use = handling == EnumHandHandling.NotHandled ? EnumHandInteract.None : EnumHandInteract.HeldItemInteract;
        while (use != EnumHandInteract.None && t < 3)
        {
            t += 0.1f;
            feeder.Controls.HandUse = use;
            use = slot.Itemstack.Collectible.OnHeldUseStep(t, slot, feeder, null, sel);
            steps.Append($"{t:0.0}:{use} ");
        }
        slot.Itemstack?.Collectible.OnHeldUseStop(t, slot, feeder, null, sel, EnumHandInteract.HeldItemInteract);
        sapi.Logger.Notification("[lwselftest] held carrot feed: start {0}, steps {1}, nourished {2:0.00} -> {3:0.00}, carrots left {4}",
            handling, steps, before, nursing.Nourished, slot.StackSize);
        feeder.Controls.ShiftKey = false;
        feeder.Controls.HandUse = EnumHandInteract.None;
    }

    void HandFeedOrphan(string? code)
    {
        var nursing = orphan!.GetBehavior<EntityBehaviorLivestockNursing>()!;
        ItemStack stack;
        if (code == null)
        {
            var bucket = (Vintagestory.GameContent.BlockLiquidContainerBase)sapi.World.GetBlock(new AssetLocation("game:woodbucket"));
            stack = new ItemStack(bucket);
            bucket.TryPutLiquid(stack, new ItemStack(sapi.World.GetItem(new AssetLocation("game:milkportion")), 10000), 10);
        }
        else stack = new ItemStack(sapi.World.GetItem(new AssetLocation(code)), 4);

        var slot = new DummySlot(stack);
        var task = orphan.GetBehavior<Vintagestory.GameContent.EntityBehaviorTaskAI>()?.TaskManager.AllTasks.FirstOrDefault(t => t is Vintagestory.GameContent.AiTaskSeekFoodAndEat);
        bool hasBehavior = stack.Collectible.CollectibleBehaviors.Any(b => b is CollectibleBehaviorHandFeed);
        string before = nursing.StatusText("en");
        bool wants = nursing.WantsHandFeeding, accepts = nursing.CanHandFeedWith(stack);
        nursing.HandFeed(slot, (EntityAgent)orphan);
        sapi.Logger.Notification("[lwselftest] mouth after feeding: {0}, LeftHand attachment point {1}, eat anim {2}",
            ((EntityAgent)orphan).LeftHandItemSlot?.Itemstack?.Collectible.Code?.ToString() ?? "empty",
            orphan.AnimManager.Animator?.GetAttachmentPointPose("LeftHand") != null ? "found" : "MISSING",
            orphan.AnimManager.IsAnimationActive("eat"));
        sapi.Logger.Notification("[lwselftest] hand feed {0}: behavior {1}, wants {2}, accepts {3}, knew trough before {4}; {5} -> {6}; left {7}; trough task {8}",
            code ?? "milk", hasBehavior, wants, accepts, before, nursing.StatusText("en"), orphan.WatchedAttributes.GetBool("lwKnowsTrough"),
            code == null ? ((Vintagestory.GameContent.BlockLiquidContainerBase)stack.Collectible).GetCurrentLitres(slot.Itemstack) + " L" : slot.StackSize + " grain",
            task == null ? "none" : "allowed " + typeof(Vintagestory.GameContent.AiTaskManager).GetMethod("ShouldExecuteTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(orphan.GetBehavior<Vintagestory.GameContent.EntityBehaviorTaskAI>()!.TaskManager, new object[] { task }));
    }

    // Pretends a flock sheep stands in the hot sun, stressed: it must skip the trough and lie down
    // now and then. (The next condition check puts back the real weather.)
    void HeatTest()
    {
        var sheep = watched.FirstOrDefault(e => e.Alive && e != hutSheep && e.Code.Path.Contains("adult"));
        if (sheep == null) return;
        var shelter = sheep.GetBehavior<EntityBehaviorLivestockShelter>()!;
        var hygiene = sheep.GetBehavior<EntityBehaviorLivestockHygiene>()!;
        sheep.WatchedAttributes.SetInt("lwWeatherConditions", 8 | 64 | 256);
        sheep.WatchedAttributes.SetFloat("lwWeatherStress", 0.5f);
        var taskManager = sheep.GetBehavior<Vintagestory.GameContent.EntityBehaviorTaskAI>()!.TaskManager;
        var trough = taskManager.AllTasks.FirstOrDefault(t => t is Vintagestory.GameContent.AiTaskSeekFoodAndEat);
        object? troughAllowed = trough == null ? "no task" : typeof(Vintagestory.GameContent.AiTaskManager)
            .GetMethod("ShouldExecuteTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(taskManager, new object[] { trough });
        var listless = typeof(EntityBehaviorLivestockShelter).GetMethod("UpdateListless", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        int checks = 0;
        while (!hygiene.IsHeldStill && checks < 500)
        {
            listless.Invoke(shelter, new object[] { livestockwelfareModSystem.Config!.Weather });
            checks++;
        }
        sapi.Logger.Notification("[lwselftest] heat: status '{0}', trough allowed {1}, lay down after {2} checks (held {3})",
            shelter.StatusText("en"), troughAllowed, checks, hygiene.IsHeldStill);
    }

    // At night a kept sheep in the open has no safe place to sleep; the one in the hut does.
    void NightTest()
    {
        double hour = sapi.World.Calendar.HourOfDay;
        sapi.World.Calendar.Add((float)((24 - hour + 23) % 24));
        var check = typeof(EntityBehaviorLivestockShelter).GetMethod("CheckConditions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var sheep in new[] { watched.FirstOrDefault(e => e.Alive && e != hutSheep && e.Code.Path.Contains("adult")), hutSheep })
        {
            if (sheep == null) continue;
            var shelter = sheep.GetBehavior<EntityBehaviorLivestockShelter>()!;
            check.Invoke(shelter, new object[] { livestockwelfareModSystem.Config!.Weather });
            sapi.Logger.Notification("[lwselftest] night {0:0.0}h: {1}: '{2}'", sapi.World.Calendar.HourOfDay, sheep == hutSheep ? "hut sheep" : "flock sheep", shelter.StatusText("en"));
        }
    }

    // With Hydrate or Diedrate installed: its clean rainwater counts as water, its tainted water doesn't.
    void HydrateOrDiedrateTest()
    {
        var waterSlot = typeof(EntityBehaviorLivestockThirst).GetMethod("WaterSlot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var undrinkable = typeof(EntityBehaviorLivestockThirst).GetMethod("HoldsUndrinkable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var pos = spawn.Copy().Set(spawn.X - 30, sapi.World.BlockAccessor.GetTerrainMapheightAt(spawn.Copy().Set(spawn.X - 30, 0, spawn.Z)) + 1, spawn.Z);
        sapi.World.BlockAccessor.SetBlock(sapi.World.GetBlock(new AssetLocation("game:barrel")).Id, pos);
        if (sapi.World.BlockAccessor.GetBlockEntity(pos) is not Vintagestory.GameContent.BlockEntityBarrel barrel) return;
        foreach (var code in new[] { "hydrateordiedrate:waterportion-fresh-rain-clean", "hydrateordiedrate:waterportion-fresh-well-tainted", "game:waterportion" })
        {
            var item = sapi.World.GetItem(new AssetLocation(code));
            if (item == null) { sapi.Logger.Notification("[lwselftest] water {0}: not loaded", code); continue; }
            barrel.Inventory[1].Itemstack = new ItemStack(item, 1000);
            sapi.Logger.Notification("[lwselftest] water {0}: drinkable {1}, undrinkable {2}", code,
                waterSlot.Invoke(null, new object[] { barrel }) != null, undrinkable.Invoke(null, new object[] { barrel }));
        }
    }

    // Two ponds next to thirsty kept hens: one with its surface level with the bank, one a block
    // lower. A thirsty wild hen with no water near must not die of thirst.
    readonly List<(string Name, Entity Hen)> pondHens = new();

    void PondTest(BlockPos at)
    {
        var accessor = sapi.World.BlockAccessor;
        var water = sapi.World.GetBlock(new AssetLocation("game:water-still-7"));
        for (int pond = 0; pond < 2; pond++)
        {
            var corner = at.AddCopy(pond * 12, 0, 0);
            int y = accessor.GetTerrainMapheightAt(corner) + 1;
            for (int dx = 0; dx < 3; dx++)
            for (int dz = 0; dz < 3; dz++)
            for (int dy = -3; dy <= 2; dy++)
            {
                // pond 0: water at y-1 and y-2 (surface level with the bank); pond 1: at y-2 and y-3.
                bool wet = pond == 0 ? dy is -1 or -2 : dy is -2 or -3;
                bool air = dy >= 0 || (pond == 1 && dy == -1);
                var pos = corner.AddCopy(dx, 0, dz).Set(corner.X + dx, y + dy, corner.Z + dz);
                if (wet) accessor.SetBlock(water.Id, pos);
                else if (air) accessor.SetBlock(0, pos);
            }
            // Right on the bank, its hitbox touching the pond's edge.
            var hen = Spawn("game:chicken-hen", corner, 1, y, 3);
            hen.Pos.Z = corner.Z + 3 + hen.SelectionBox.Width / 2 + 0.01;
            hen.WatchedAttributes.SetFloat("lwThirst", 0.9f);
            pondHens.Add((pond == 0 ? "pond level hen" : "pond lower hen", hen));
        }
        var wild = Spawn("game:chicken-hen", at.AddCopy(-20, 0, -20), 0, accessor.GetTerrainMapheightAt(at.AddCopy(-20, 0, -20)) + 1, 0);
        wild.Attributes.SetString("origin", "wild");
        wild.WatchedAttributes.SetFloat("lwThirst", 1f);
        pondHens.Add(("wild hen", wild));

        // Broken muck drops its rot.
        var muckPos = at.AddCopy(-6, 0, -6);
        muckPos.Y = accessor.GetTerrainMapheightAt(muckPos) + 1;
        accessor.SetBlock(sapi.World.GetBlock(new AssetLocation(LivestockUtil.Domain, "muck-4")).Id, muckPos);
        accessor.BreakBlock(muckPos, null);

        int step = 0;
        sapi.Event.RegisterGameTickListener(_ =>
        {
            step++;
            if (step == 1)
            {
                var drops = sapi.World.GetEntitiesAround(muckPos.ToVec3d().Add(0.5, 0.5, 0.5), 3, 3, e => e is EntityItem);
                sapi.Logger.Notification("[lwselftest] broken muck-4 dropped: {0}", string.Join(", ", drops.Select(e => ((EntityItem)e).Itemstack?.StackSize + "x " + ((EntityItem)e).Itemstack?.Collectible?.Code)));
            }
            if (step > 4) return;
            LogPond(step);
            if (step == 1) CheckPondReach();
            // The wild hen has no water near: it must live through days of thirst.
            if (step == 2) sapi.World.Calendar.Add(72);
        }, 7000);
    }

    // Without a player near, the server doesn't move animals, so walking to the pond can't be tested
    // here. Instead: does a hen on the bank count as arrived at the water it would walk to?
    void CheckPondReach()
    {
        foreach (var (name, hen) in pondHens.Where(p => p.Name != "wild hen"))
        {
            var found = hen.GetBehavior<EntityBehaviorLivestockThirst>()?.FindDrinkTarget(16, _ => false);
            if (found is not { } target) { sapi.Logger.Notification("[lwselftest] {0}: no water found", name); continue; }
            var box = hen.SelectionBox.ToDouble().Translate(hen.Pos.X, hen.Pos.Y, hen.Pos.Z);
            double bottom = box.ShortestDistanceFrom(target.Pos.ToVec3d().Add(0.5, 0, 0.5));
            double surface = box.ShortestDistanceFrom(target.Pos.ToVec3d().Add(0.5, 0.9, 0.5));
            sapi.Logger.Notification("[lwselftest] {0} at {1:0.00} {2:0.00} {3:0.00}: water {4}, distance to its bottom {5:0.00}, to its surface {6:0.00} (arrived within 1.30)",
                name, hen.Pos.X, hen.Pos.Y, hen.Pos.Z, target.Pos, bottom, surface);
        }
    }

    void LogPond(int step)
    {
        foreach (var (name, hen) in pondHens)
        {
            sapi.Logger.Notification("[lwselftest] {0} #{1} step {2}: alive {3}, thirst {4:0.00}, health {5:0.0}, at {6}", name, hen.EntityId, step, hen.Alive,
                hen.WatchedAttributes.GetFloat("lwThirst"), (hen.WatchedAttributes["health"] as Vintagestory.API.Datastructures.ITreeAttribute)?.GetFloat("currenthealth"), hen.Pos.AsBlockPos);
        }
    }

    Entity? hutSheep;
    Entity? nursingEwe, newborn, doomedEwe, orphan, chick, hen;
    BlockPos? barrelPos;

    // Like vanilla EntityBehaviorMultiply.GiveBirth: the baby appears where its mother stands.
    Entity SpawnBorn(string code, Entity mother)
    {
        var e = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation(code)));
        e.Pos.SetFrom(mother.Pos);
        e.Attributes.SetString("origin", "reproduction");
        e.WatchedAttributes.SetInt("generation", 1);
        spawning = true;
        sapi.World.SpawnEntity(e);
        spawning = false;
        return e;
    }
    BlockPos? controlBarrel;

    // Big steps test long-term effects; small steps (LW_SELFTEST_STEPHOURS=2) let the AI walk in between.
    static readonly float StepHours = float.TryParse(Environment.GetEnvironmentVariable("LW_SELFTEST_STEPHOURS"), out float h) ? h : 24;
    static int Steps => (int)(16 * 24 / StepHours);
    const int BarrelDistance = 9;

    void PlaceWaterBarrel(BlockPos pos, int litres)
    {
        var accessor = sapi.World.BlockAccessor;
        accessor.SetBlock(sapi.World.GetBlock(new AssetLocation("game:barrel")).Id, pos);
        if (accessor.GetBlockEntity(pos) is not Vintagestory.GameContent.BlockEntityBarrel barrel)
        {
            sapi.Logger.Warning("[lwselftest] barrel block entity missing");
            return;
        }
        barrel.Inventory[1].Itemstack = new ItemStack(sapi.World.GetItem(new AssetLocation("game:waterportion")), litres * 100);
        barrel.MarkDirty(true);
        barrelPos = pos;
    }

    float BarrelLitres() =>
        barrelPos != null && sapi.World.BlockAccessor.GetBlockEntity(barrelPos) is Vintagestory.GameContent.BlockEntityBarrel b
            ? b.Inventory[1].StackSize / 100f : -1;

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
            sapi.Logger.Notification("[lwselftest]   weather {0}: {1}°C rain {2:0.00} -> {3}; thirst {4:0.00}, water: {5}",
                where, climate?.Temperature, climate?.Rainfall, shelter.StatusText("en"),
                e.GetBehavior<EntityBehaviorLivestockThirst>()?.Thirst, e.GetBehavior<EntityBehaviorLivestockThirst>()?.WaterSourceText("en"));
        }
        double nearest = barrelPos == null ? -1 : watched.Where(e => e.Alive).Select(e => e.Pos.DistanceTo(barrelPos.ToVec3d().Add(0.5, 0, 0.5))).DefaultIfEmpty(-1).Min();
        float maxThirst = watched.Where(e => e.Alive).Select(e => e.GetBehavior<EntityBehaviorLivestockThirst>()?.Thirst ?? 0).DefaultIfEmpty(0).Max();
        sapi.Logger.Notification("[lwselftest]   barrel water: {0:0.00} L, nearest sheep {1:0.0} blocks away, highest thirst {2:0.00}",
            BarrelLitres(), nearest, maxThirst);
        foreach (var (name, e) in new[] { ("ewe", nursingEwe), ("newborn", newborn), ("orphan", orphan), ("chick", chick), ("hen", hen) })
        {
            if (e == null) continue;
            var a = e.WatchedAttributes;
            sapi.Logger.Notification("[lwselftest]   {0} {1} #{2}: alive {3}, nursing: {4}, filth {5:0.00}, sick {6}, health {7:0.0}, grow start {8:0.0} h, thirst {9:0.00}",
                name, e.Code.Path, e.EntityId, e.Alive, e.GetBehavior<EntityBehaviorLivestockNursing>()?.StatusText("en") ?? "-",
                a.GetFloat("lwFilth"), a.GetBool("lwSick"), (a["health"] as Vintagestory.API.Datastructures.ITreeAttribute)?.GetFloat("currenthealth"),
                a.GetTreeAttribute("grow")?.GetDouble("timeSpawned") ?? -1, a.GetFloat("lwThirst"));
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
