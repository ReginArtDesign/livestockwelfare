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

    // Dogs: the patches reached the wolves (and Wolf taming's dogs when it is installed), the config
    // knows the "dog" species, and raised, placed and Pet AI-owned dogs count as kept, wild ones not.
    void CheckDogs()
    {
        foreach (string code in new[] { "game:wolf-eurasian-adult-male", "game:wolf-eurasian-baby-female", "wolftaming:dog-shepherd-female", "feralkinship:tamewolf-eurasian-male" })
        {
            var type = sapi.World.GetEntityType(new AssetLocation(code));
            if (type == null)
            {
                sapi.Logger.Notification("[lwselftest] dog check {0}: not installed", code);
                continue;
            }
            bool patched = type.Server?.BehaviorsAsJsonObj?.Any(b => b["code"].AsString() == EntityBehaviorLivestockDog.Code) == true;
            bool diet = type.Attributes?["creatureDiet"].Exists == true;
            sapi.Logger.Notification("[lwselftest] dog check {0}: dog behavior {1}, diet {2}", code, patched ? "found" : "MISSING", diet ? "found" : "MISSING");
        }

        foreach (string code in new[] { "livestockwelfare:waterbowl-fired", "livestockwelfare:foodbowl-fired-ns", "livestockwelfare:dogpoop" })
        {
            var block = sapi.World.GetBlock(new AssetLocation(code));
            sapi.Logger.Notification("[lwselftest] dog check block {0}: {1} {2}", code, block == null ? "MISSING" : block.GetType().Name, block?.EntityClass);
        }

        var config = livestockwelfareModSystem.Config;
        sapi.Logger.Notification("[lwselftest] dog check config: aging {0}, weather {1}, thirst {2}, dogs section {3}",
            config?.Species.ContainsKey("dog"), config?.Weather.Species.ContainsKey("dog"), config?.Thirst.LitresPerFullDrink.ContainsKey("dog"), config?.Dogs != null);

        var wolfType = sapi.World.GetEntityType(new AssetLocation("game:wolf-eurasian-adult-male"));
        if (wolfType == null) return;
        bool Kept(Action<Entity> setup)
        {
            var wolf = sapi.World.ClassRegistry.CreateEntity(wolfType);
            setup(wolf);
            return LivestockUtil.IsKept(wolf);
        }
        bool wild = Kept(_ => { });
        bool bred = Kept(e => e.WatchedAttributes.SetInt("generation", 2));
        bool owned = Kept(e => e.WatchedAttributes.GetOrAddTreeAttribute("domesticationstatus").SetString("owner", "testplayeruid"));
        sapi.Logger.Notification("[lwselftest] dog check kept: wild {0} (expect False), bred {1}, Pet AI owner {2} (expect True) — {3}",
            wild, bred, owned, !wild && bred && owned ? "OK" : "FAILED");
    }

    // Feral Kinship: its tame sheep, goats, pigs and chickens have our behaviors and an "eat"
    // animation, and taming (old animal removed, tame one spawned in its place) keeps the dirt.
    void CheckFeralKinship(BlockPos spawn)
    {
        string[] behaviors = { EntityBehaviorLivestockAge.Code, EntityBehaviorLivestockHygiene.Code, "livestockwelfare.shelter", "livestockwelfare.thirst" };
        foreach (string code in new[] { "feralkinship:tamesheep-bighorn-female", "feralkinship:tamegoat-nubian-female", "feralkinship:tamepig-eurasian-female", "feralkinship:tamechicken-hen" })
        {
            var type = sapi.World.GetEntityType(new AssetLocation(code));
            if (type == null)
            {
                sapi.Logger.Notification("[lwselftest] feral kinship {0}: not installed", code);
                return;
            }
            var missing = behaviors.Where(b => type.Server?.BehaviorsAsJsonObj?.Any(j => j["code"].AsString() == b) != true).ToArray();
            bool eat = type.Client?.Animations?.Any(a => a.Code == "eat") == true;
            sapi.Logger.Notification("[lwselftest] feral kinship {0}: missing behaviors [{1}], eat animation {2} — {3}",
                code, string.Join(", ", missing), eat, missing.Length == 0 && eat ? "OK" : "FAILED");
        }

        var pos = spawn.AddCopy(-24, 40, 0);
        Entity Spawn(string code)
        {
            var e = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation(code)));
            e.Pos.SetPos(pos.X + 0.5, pos.Y, pos.Z + 0.5);
            spawning = true;
            sapi.World.SpawnEntity(e);
            spawning = false;
            return e;
        }
        var wild = Spawn("game:sheep-bighorn-adult-female");
        wild.WatchedAttributes.SetFloat("lwFilth", 0.77f);
        double birth = wild.WatchedAttributes.GetDouble("lwBirthTotalDays");
        wild.Die(EnumDespawnReason.Expire);
        var tame = Spawn("feralkinship:tamesheep-bighorn-female");
        float filth = tame.WatchedAttributes.GetFloat("lwFilth");
        bool sameBirth = tame.WatchedAttributes.GetDouble("lwBirthTotalDays") == birth;
        sapi.Logger.Notification("[lwselftest] feral kinship taming carry-over: filth {0} (expect 0.77), same birth day {1} — {2}",
            filth, sameBirth, filth == 0.77f && sameBirth ? "OK" : "FAILED");
        tame.Die(EnumDespawnReason.Removed);
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
        CheckDogs();

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
                CheckWatererAndBrush(spawn);
                CheckDustBath(spawn);
                CheckCoop(spawn);
                CheckTreats(spawn);
                CheckHayBale(spawn);
                CheckFeralKinship(spawn);
                if (Environment.GetEnvironmentVariable("LW_SELFTEST_POND") == "1") PondTest(spawn);
                else SpawnAndWatch(spawn);
            }, "lwselftest") });
    }

    // The brush grooms and wears out; the raw waterer fires into the fired one, which is a liquid
    // container a thirsty chicken finds and drinks from. Done high in the air and cleaned up after.
    void CheckWatererAndBrush(BlockPos spawn)
    {
        var brush = sapi.World.GetItem(new AssetLocation(LivestockUtil.Domain, "groomingbrush"));
        sapi.Logger.Notification("[lwselftest] brush: {0}, durability {1}",
            brush?.CollectibleBehaviors.Any(b => b is CollectibleBehaviorGroom) == true ? "found" : "MISSING", brush?.GetMaxDurability(new ItemStack(brush)));

        var raw = sapi.World.GetBlock(new AssetLocation(LivestockUtil.Domain, "waterer-raw"));
        var fired = sapi.World.GetBlock(new AssetLocation(LivestockUtil.Domain, "waterer-fired"));
        var smelted = raw?.CombustibleProps?.SmeltedStack?.ResolvedItemstack?.Collectible;
        sapi.Logger.Notification("[lwselftest] waterer: raw {0}, fired {1}, raw fires into fired {2}",
            raw != null, fired?.GetType().Name, smelted != null && smelted == fired);
        if (fired == null) return;

        // Too high to step onto (hens step 1.125), for the waterer and the troughs.
        var tall = new[] { "livestockwelfare:waterer-fired", "game:trough-genericwood-small-ns", "game:trough-genericwood-large-head-north" }
            .Select(code => (code, height: sapi.World.GetBlock(new AssetLocation(code))?.CollisionBoxes?.Max(b => b.Y2))).ToList();
        sapi.Logger.Notification("[lwselftest] no climbing: {0} — {1}", string.Join(", ", tall.Select(t => $"{t.code} {t.height}")),
            tall.All(t => t.height >= 1.5f) ? "OK" : "FAILED");

        var pos = spawn.AddCopy(0, 30, 0);
        var accessor = sapi.World.BlockAccessor;
        accessor.SetBlock(fired.Id, pos);
        if (accessor.GetBlockEntity(pos) is not Vintagestory.GameContent.BlockEntityLiquidContainer container)
        {
            sapi.Logger.Warning("[lwselftest] waterer: no liquid container block entity — FAILED");
            accessor.SetBlock(0, pos);
            return;
        }
        container.Inventory[0].Itemstack = new ItemStack(sapi.World.GetItem(new AssetLocation("game:waterportion")), 300);
        container.MarkDirty(true);

        var chicken = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game:chicken-hen")));
        chicken.Pos.SetPos(pos.X + 1.5, pos.Y, pos.Z + 0.5);
        chicken.Attributes.SetString("origin", "playerplaced");
        spawning = true;
        sapi.World.SpawnEntity(chicken);
        spawning = false;
        var thirst = chicken.GetBehavior<EntityBehaviorLivestockThirst>();
        chicken.WatchedAttributes.SetFloat("lwThirst", 1);
        var target = thirst?.FindDrinkTarget(4, _ => false);
        bool foundIt = target?.Pos.Equals(pos) == true && target.Value.Container == container;
        thirst?.DrinkFrom(target?.Container);
        float litres = container.Inventory[0].StackSize / 100f;
        sapi.Logger.Notification("[lwselftest] waterer: chicken finds it {0}, thirst after drinking {1:0.00}, litres left {2:0.00} — {3}",
            foundIt, thirst?.Thirst, litres, foundIt && thirst?.Thirst < 0.01 && litres < 3 ? "OK" : "FAILED");

        // A puddle a few blocks further than the waterer: the bird walks to the puddle, which never runs dry.
        var puddle = pos.AddCopy(3, 0, 0);
        var floor = puddle.DownCopy();
        accessor.SetBlock(sapi.World.GetBlock(new AssetLocation("game:rock-granite")).Id, floor);
        accessor.SetBlock(sapi.World.GetBlock(new AssetLocation("game:water-still-7")).Id, puddle);
        var choice = thirst?.FindDrinkTarget(6, _ => false);
        bool prefersPuddle = choice?.Pos.Equals(puddle) == true && choice.Value.Container == null;
        sapi.Logger.Notification("[lwselftest] waterer: chicken prefers the puddle 2 blocks further {0} — {1}",
            prefersPuddle, prefersPuddle ? "OK" : "FAILED");
        accessor.SetBlock(0, puddle);
        accessor.SetBlock(0, floor);

        chicken.Die(EnumDespawnReason.Removed);
        accessor.SetBlock(0, pos);
    }

    // Hay bale: a sheep finds it as food and a hen doesn't; each portion eaten makes it a size
    // smaller, and the last one takes it away. Done high in the air and cleaned up after.
    void CheckHayBale(BlockPos spawn)
    {
        var bale = sapi.World.GetBlock(new AssetLocation("livestockwelfare:haybale-4"));
        var accessor = sapi.World.BlockAccessor;
        var pos = spawn.AddCopy(-20, 40, 6);
        if (bale == null)
        {
            sapi.Logger.Warning("[lwselftest] hay bale: block missing — FAILED");
            return;
        }
        accessor.SetBlock(bale.Id, pos);
        if (accessor.GetBlockEntity(pos) == null) accessor.SpawnBlockEntity(bale.EntityClass, pos);
        var be = accessor.GetBlockEntity(pos) as BlockEntityHayBale;
        Entity Spawn(string code)
        {
            var e = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation(code)));
            e.Pos.SetPos(pos.X + 2.5, pos.Y, pos.Z + 0.5);
            spawning = true;
            sapi.World.SpawnEntity(e);
            spawning = false;
            return e;
        }
        var sheep = Spawn("game:sheep-bighorn-adult-female");
        var hen = Spawn("game:chicken-hen");
        var poi = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.POIRegistry>()
            .GetNearestPoi(sheep.Pos.XYZ, 8, p => p is BlockEntityHayBale);
        bool sheepEats = be != null && be.IsSuitableFor(sheep, LivestockTreats.Diet(sheep)!);
        bool henEats = be != null && LivestockTreats.Diet(hen) is { } henDiet && be.IsSuitableFor(hen, henDiet);
        var sizes = new List<string>();
        for (int i = 0; i < 4 && be != null; i++)
        {
            be.ConsumeOnePortion(sheep);
            sizes.Add(accessor.GetBlock(pos).Code?.Path ?? "?");
        }
        bool gone = accessor.GetBlock(pos).Id == 0;
        bool ok = be != null && poi == be && sheepEats && !henEats && string.Join(",", sizes) == "haybale-3,haybale-2,haybale-1,air" && gone;
        sapi.Logger.Notification("[lwselftest] hay bale: found as food {0}, sheep eats {1}, hen eats {2} (expect False), sizes {3} — {4}",
            poi == be, sheepEats, henEats, string.Join(", ", sizes), ok ? "OK" : "FAILED");
        sheep.Die(EnumDespawnReason.Removed);
        hen.Die(EnumDespawnReason.Removed);
        accessor.SetBlock(0, pos);
    }

    // Treats: a hen follows grain and takes one from the hand (a meal, then full for a while); a
    // sheep doesn't. Done high in the air and cleaned up after.
    void CheckTreats(BlockPos spawn)
    {
        var grain = sapi.World.GetItem(new AssetLocation("game:grain-spelt"));
        var pos = spawn.AddCopy(-20, 40, 0);
        Entity Spawn(string code)
        {
            var e = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation(code)));
            e.Pos.SetPos(pos.X + 0.5, pos.Y, pos.Z + 0.5);
            e.Attributes.SetString("origin", "playerplaced");
            spawning = true;
            sapi.World.SpawnEntity(e);
            spawning = false;
            return e;
        }
        var hen = Spawn("game:chicken-hen");
        var pup = Spawn("game:wolf-eurasian-baby-female");
        bool pupFollows = pup.GetBehavior<Vintagestory.GameContent.EntityBehaviorTaskAI>()?.TaskManager.AllTasks
            .OfType<AiTaskLivestockFollowFood>().Any(t => t.Lures?.Invoke(new ItemStack(sapi.World.GetItem(new AssetLocation("game:redmeat-raw")))) == true) == true;
        sapi.Logger.Notification("[lwselftest] pup lure: follows raw meat {0} — {1}", pupFollows, pupFollows ? "OK" : "FAILED");
        pup.Die(EnumDespawnReason.Removed);
        var dogAdult = Spawn("game:wolf-eurasian-adult-male");
        var dogBh = dogAdult.GetBehavior<EntityBehaviorLivestockDog>();
        float width = dogAdult.CollisionBox.XSize;
        dogBh?.SqueezeThroughDoor();
        float squeezed = dogAdult.CollisionBox.XSize;
        dogBh?.StopSqueezing();
        float restored = dogAdult.CollisionBox.XSize;
        sapi.Logger.Notification("[lwselftest] dog door squeeze: width {0} -> {1}, then back to {2} - {3}",
            width, squeezed, restored, width < 1 && squeezed < width && restored == width ? "OK" : "FAILED");
        dogAdult.Die(EnumDespawnReason.Removed);
        var sheep = Spawn("game:sheep-bighorn-adult-female");
        bool follows = hen.GetBehavior<Vintagestory.GameContent.EntityBehaviorTaskAI>()?.TaskManager.AllTasks.Any(t => t is AiTaskLivestockFollowFood) == true;
        var slot = new DummySlot(new ItemStack(grain, 3));
        double saturation = hen.WatchedAttributes.GetTreeAttribute("hunger")?.GetFloat("saturation") ?? 0;
        LivestockTreats.HandFeed(hen, slot, hen as EntityAgent ?? throw new InvalidOperationException());
        double after = hen.WatchedAttributes.GetTreeAttribute("hunger")?.GetFloat("saturation") ?? 0;
        string? refusal = LivestockTreats.Refusal(hen);
        LivestockTreats.HandFeed(hen, slot, (EntityAgent)hen);
        bool ok = grain != null && follows && LivestockTreats.TakesTreats(hen) && LivestockTreats.Eats(hen, slot.Itemstack)
            && !LivestockTreats.TakesTreats(sheep) && after > saturation && refusal != null && slot.StackSize == 2;
        sapi.Logger.Notification("[lwselftest] treats: hen follows {0}, takes treats {1}, sheep {2}, saturation {3} -> {4}, then {5}, grain left {6} — {7}",
            follows, LivestockTreats.TakesTreats(hen), LivestockTreats.TakesTreats(sheep), saturation, after, refusal, slot.StackSize, ok ? "OK" : "FAILED");
        hen.Die(EnumDespawnReason.Removed);
        sheep.Die(EnumDespawnReason.Removed);
    }

    // The coop kit builds the raised coop (floor, walls, pitched roof, ramp and back steps, hen boxes,
    // gate with an open doorway above) on a stone floor, picking a flower that is in the way, and
    // refuses a spot that is already taken. The open pop hole keeps it from being a closed room
    // (logged, not required). Done high in the air and cleaned up after.
    void CheckCoop(BlockPos spawn)
    {
        var accessor = sapi.World.BlockAccessor;
        var rampFoot = spawn.AddCopy(20, 40, 0);
        var stone = sapi.World.GetBlock(new AssetLocation("game:rock-granite"));
        for (int x = -3; x <= 3; x++)
        for (int z = -1; z <= 10; z++)
        {
            accessor.SetBlock(stone.Id, rampFoot.AddCopy(x, -1, z));
        }

        // A flower where a corner post goes: picked, not in the way.
        accessor.SetBlock(sapi.World.GetBlock(new AssetLocation("game:flower-catmint-free")).Id, rampFoot.AddCopy(2, 0, 2));
        string? error = ItemChickenCoop.TryBuild(sapi.World, rampFoot, new Vec3i(0, 0, 1), "oak");
        var counts = new Dictionary<string, int>();
        for (int x = -2; x <= 2; x++)
        for (int y = 0; y <= 5; y++)
        for (int z = 0; z <= 8; z++)
        {
            string? code = accessor.GetBlock(rampFoot.AddCopy(x, y, z)).Code?.Path;
            if (code == null || code == "air") continue;
            string kind = code.Split('-')[0];
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }
        var inside = rampFoot.AddCopy(0, 2, 4);
        bool roofed = accessor.GetRainMapHeightAt(inside) > inside.Y;
        var room = sapi.ModLoader.GetModSystem<Vintagestory.GameContent.RoomRegistry>()?.GetRoomForPosition(inside);
        bool enclosed = room != null && (room.ExitCount == 0 || room.SkylightCount < room.NonSkylightCount);
        string? again = ItemChickenCoop.TryBuild(sapi.World, rampFoot, new Vec3i(0, 0, 1), "oak");
        bool ok = error == null && accessor.GetBlock(rampFoot.AddCopy(0, 3, 6)).Id == 0 && counts.GetValueOrDefault("planks") == 48 && counts.GetValueOrDefault("plankstairs") == 24
            && counts.GetValueOrDefault("plankslab") == 5 && counts.GetValueOrDefault("woodenfence") == 4
            && counts.GetValueOrDefault("woodenfencegate") == 1 && counts.GetValueOrDefault("henbox") == 2
            && roofed && again == "coop-noroom";
        sapi.Logger.Notification("[lwselftest] coop: built {0}, blocks {1}, roofed {2}, enclosed {3} (exits {4}), second one refused {5} — {6}",
            error ?? "yes", string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")), roofed, enclosed, room?.ExitCount, again, ok ? "OK" : "FAILED");

        for (int x = -3; x <= 3; x++)
        for (int y = -1; y <= 5; y++)
        for (int z = -1; z <= 10; z++)
        {
            accessor.SetBlock(0, rampFoot.AddCopy(x, y, z));
        }
    }

    // A dirty hen finds dry sand under a roof, and not the sand under the open sky; a bath gets her
    // clean. Done high in the air and cleaned up after.
    void CheckDustBath(BlockPos spawn)
    {
        var settings = livestockwelfareModSystem.Config?.Hygiene;
        var sand = sapi.World.GetBlock(new AssetLocation("game:sand-granite"));
        var planks = sapi.World.GetBlock(new AssetLocation("game:planks-aged-ud"));
        if (settings == null || sand == null || planks == null)
        {
            sapi.Logger.Warning("[lwselftest] dust bath: setup missing — FAILED");
            return;
        }

        var accessor = sapi.World.BlockAccessor;
        var floor = spawn.AddCopy(4, 40, 0);
        var roofed = floor.AddCopy(2, 0, 0);
        var changed = new List<BlockPos> { floor, roofed, roofed.AddCopy(0, 3, 0) };
        accessor.SetBlock(planks.Id, floor);
        accessor.SetBlock(sand.Id, roofed);
        accessor.SetBlock(planks.Id, roofed.AddCopy(0, 3, 0));

        var hen = sapi.World.ClassRegistry.CreateEntity(sapi.World.GetEntityType(new AssetLocation("game:chicken-hen")));
        hen.Pos.SetPos(floor.X + 0.5, floor.Y + 1, floor.Z + 0.5);
        hen.Attributes.SetString("origin", "playerplaced");
        spawning = true;
        sapi.World.SpawnEntity(hen);
        spawning = false;
        var hygiene = hen.GetBehavior<EntityBehaviorLivestockHygiene>();
        hen.WatchedAttributes.SetFloat("lwFilth", 0.4f);

        var found = hygiene?.FindDustBath(settings, _ => false);
        // Wavy sand counts too (a player's andesite sand from the creative inventory was wavy).
        var wavy = sapi.World.GetBlock(new AssetLocation("game:sandwavy-andesite"));
        if (wavy != null) accessor.SetBlock(wavy.Id, roofed);
        bool foundWavy = wavy != null && hygiene?.FindDustBath(settings, _ => false)?.Equals(roofed) == true;
        hygiene?.DustBathed();
        bool clean = hygiene?.Filth == 0;
        sapi.Logger.Notification("[lwselftest] dust bath: finds the sand {0}, wavy sand {1}, clean after bathing {2} — {3}",
            found?.Equals(roofed) == true, foundWavy, clean, found?.Equals(roofed) == true && foundWavy && clean ? "OK" : "FAILED");

        hen.Die(EnumDespawnReason.Removed);
        foreach (var pos in changed) accessor.SetBlock(0, pos);
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
