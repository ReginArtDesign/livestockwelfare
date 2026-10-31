using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

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
        api.RegisterCollectibleBehaviorClass(CollectibleBehaviorGroom.Code, typeof(CollectibleBehaviorGroom));
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
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
    }

    public override void Dispose()
    {
        Config = null;
        // The client texture atlas is rebuilt for every world, so cached positions become invalid.
        DirtTextures.Clear();
    }
}
