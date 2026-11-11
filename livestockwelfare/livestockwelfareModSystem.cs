using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace livestockwelfare;

public class livestockwelfareModSystem : ModSystem
{
    /// <summary>Only set on the server; the client reads everything it needs from WatchedAttributes.</summary>
    public static LivestockWelfareConfig? Config { get; private set; }

    public override void Start(ICoreAPI api)
    {
        api.RegisterEntityBehaviorClass(EntityBehaviorLivestockAge.Code, typeof(EntityBehaviorLivestockAge));
        api.RegisterEntityBehaviorClass(EntityBehaviorLivestockHygiene.Code, typeof(EntityBehaviorLivestockHygiene));
        api.RegisterEntityBehaviorClass(EntityBehaviorLivestockShelter.Code, typeof(EntityBehaviorLivestockShelter));
        api.RegisterEntityBehaviorClass(EntityBehaviorLivestockThirst.Code, typeof(EntityBehaviorLivestockThirst));
        api.RegisterEntityBehaviorClass(EntityBehaviorLivestockNursing.Code, typeof(EntityBehaviorLivestockNursing));
        api.RegisterEntityBehaviorClass(EntityBehaviorLivestockMouth.Code, typeof(EntityBehaviorLivestockMouth));
        api.RegisterEntityBehaviorClass(EntityBehaviorLivestockDog.Code, typeof(EntityBehaviorLivestockDog));
        api.RegisterCollectibleBehaviorClass(CollectibleBehaviorGroom.Code, typeof(CollectibleBehaviorGroom));
        api.RegisterCollectibleBehaviorClass(CollectibleBehaviorHandFeed.Code, typeof(CollectibleBehaviorHandFeed));
        api.RegisterBlockBehaviorClass(BlockBehaviorMuckNeedsGround.Code, typeof(BlockBehaviorMuckNeedsGround));
        api.RegisterItemClass(ItemChickenCoop.ClassName, typeof(ItemChickenCoop));
        api.RegisterBlockEntityClass(BlockEntityDogBowl.ClassName, typeof(BlockEntityDogBowl));
        // Added to livestock in code (see the thirst and shelter behaviors), not through JSON.
        AiTaskRegistry.Register<AiTaskLivestockSeekWater>(AiTaskLivestockSeekWater.Code);
        AiTaskRegistry.Register<AiTaskLivestockSeekShelter>(AiTaskLivestockSeekShelter.Code);
        AiTaskRegistry.Register<AiTaskLivestockDustBath>(AiTaskLivestockDustBath.Code);
        AiTaskRegistry.Register<AiTaskLivestockFollowFood>(AiTaskLivestockFollowFood.Code);
        AiTaskRegistry.Register<AiTaskLivestockDogGuard>(AiTaskLivestockDogGuard.Code);
        AiTaskRegistry.Register<AiTaskLivestockDogRest>(AiTaskLivestockDogRest.Code);
        AiTaskRegistry.Register<AiTaskLivestockDogPotty>(AiTaskLivestockDogPotty.Code);
        AiTaskRegistry.Register<AiTaskLivestockBedding>(AiTaskLivestockBedding.Code);
    }

    // Hand feeding works with any food or liquid container (milk is checked on use), so it is added in
    // code rather than patched into each item. Runs on both sides, so they agree on the behaviors.
    public override void AssetsFinalize(ICoreAPI api)
    {
        foreach (var collectible in api.World.Collectibles)
        {
            if (collectible?.Code == null || (collectible.NutritionProps == null && collectible is not BlockLiquidContainerBase)) continue;
            collectible.CollectibleBehaviors = collectible.CollectibleBehaviors.Append(new CollectibleBehaviorHandFeed(collectible));
        }
        if (api is ICoreServerAPI sapi)
        {
            MoveHygieneBeforeHealth(api);
            LivestockBreeding.Apply(api, LoadConfig(sapi).Breeding);
        }
    }

    // Hygiene must run before vanilla "health" so it can cancel shovel damage. The patches append it
    // at the end, because inserting it at the front would shift the behavior indices that other mods'
    // patches use ("/server/behaviors/10/aitasks/..."), so it is moved here, once all patches are in.
    static void MoveHygieneBeforeHealth(ICoreAPI api)
    {
        foreach (var type in api.World.EntityTypes)
        {
            if (type?.Server?.BehaviorsAsJsonObj is not { } behaviors) continue;
            int hygiene = Array.FindIndex(behaviors, b => b["code"].AsString() == EntityBehaviorLivestockHygiene.Code);
            int health = Array.FindIndex(behaviors, b => b["code"].AsString() == "health");
            if (hygiene < 0 || health < 0 || hygiene < health) continue;

            var moved = behaviors[hygiene];
            Array.Copy(behaviors, health, behaviors, health + 1, hygiene - health);
            behaviors[health] = moved;
        }
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        LoadConfig(api);
        api.Event.OnEntitySpawn += CarryOverFromBaby;
        DogHunting.Start(api);
        beddingPatches = LivestockBedding.Patch(Mod.Info.ModID + ".bedding");
    }

    // Needed by AssetsFinalize (breeding settings) and StartServerSide; loaded by whichever runs first.
    LivestockWelfareConfig LoadConfig(ICoreServerAPI api)
    {
        if (Config != null) return Config;
        LivestockWelfareConfig config;
        try
        {
            config = api.LoadModConfig<LivestockWelfareConfig>(LivestockWelfareConfig.FileName) ?? new LivestockWelfareConfig();
            config.Sanitize();
            // Writes the file on first launch, and adds settings introduced by newer versions.
            api.StoreModConfig(config, LivestockWelfareConfig.FileName);
        }
        catch (Exception e)
        {
            // Keep the broken file as it is, so the player's edits are not lost.
            Mod.Logger.Error("Could not read {0}, using defaults: {1}", LivestockWelfareConfig.FileName, e.Message);
            config = new LivestockWelfareConfig();
        }

        Config = config;
        return config;
    }

    // Kept when a baby grows up: its age, sickness and how stressed it is by the weather.
    static readonly string[] GrownUpKeepsAttrs = { "lwBirthTotalDays", "lwSick", "lwImmuneUntilDays", "lwWeatherStress" };

    // Vanilla EntityBehaviorGrow turns a baby into an adult by killing the baby (reason "expire") and
    // spawning a new adult entity at the same spot, marked with "wasFedToAdulthood". The baby is still
    // in the world at that moment, so the adult picks up what is worth keeping from it.
    static void CarryOverFromBaby(Entity adult)
    {
        if (!adult.Attributes.HasAttribute("wasFedToAdulthood") || !adult.HasBehavior<EntityBehaviorLivestockAge>()) return;

        var baby = adult.World.GetNearestEntity(adult.Pos.XYZ, 0.5f, 0.5f, e =>
            e != adult && !e.Alive && e.DespawnReason?.Reason == EnumDespawnReason.Expire
            && e.GetBehavior<EntityBehaviorLivestockAge>() is { IsBaby: true });
        if (baby == null) return;

        foreach (string attr in GrownUpKeepsAttrs)
        {
            if (baby.WatchedAttributes.HasAttribute(attr)) adult.WatchedAttributes[attr] = baby.WatchedAttributes[attr].Clone();
        }
        LivestockUtil.SetRegenBlocked(adult, "sick", adult.WatchedAttributes.GetBool("lwSick"));
        adult.World.Logger.Debug("[livestockwelfare] {0} #{1} grew up from #{2}, sick: {3}", adult.Code, adult.EntityId, baby.EntityId, adult.WatchedAttributes.GetBool("lwSick"));
    }

    HarmonyLib.Harmony? beddingPatches;

    public override void Dispose()
    {
        beddingPatches?.UnpatchAll(beddingPatches.Id);
        Config = null;
        DogHunting.Clear();
        // The client texture atlas is rebuilt for every world, so cached positions become invalid.
        DirtTextures.Clear();
    }
}
