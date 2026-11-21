using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// A handful of grain scattered on the ground (scatteredgrain-{type}-{portions}). Placed like any
/// block; another handful of the same grain on top tops it up, to eight portions at most.
/// </summary>
public class BlockScatteredGrain : Block
{
    public const string ClassName = "livestockwelfare.scatteredgrain";
    public const int MaxPortions = 8;

    public int Portions => int.TryParse(Variant["portions"], out int portions) ? portions : 0;

    // It lies on the ground, so it needs a solid top below it.
    public override bool CanPlaceBlock(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel, ref string failureCode)
    {
        if (!world.BlockAccessor.GetBlock(blockSel.Position.DownCopy()).SideSolid[BlockFacing.UP.Index])
        {
            failureCode = "requiresolidground";
            return false;
        }
        return base.CanPlaceBlock(world, byPlayer, blockSel, ref failureCode);
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        var slot = byPlayer?.InventoryManager?.ActiveHotbarSlot;
        if (slot?.Itemstack?.Block is not BlockScatteredGrain held || held.Variant["type"] != Variant["type"] || Portions >= MaxPortions)
        {
            return base.OnBlockInteractStart(world, byPlayer, blockSel);
        }
        if (world.Side == EnumAppSide.Server)
        {
            int portions = Math.Min(MaxPortions, Portions + held.Portions);
            if (world.GetBlock(CodeWithVariant("portions", portions.ToString())) is { } fuller)
            {
                world.BlockAccessor.ExchangeBlock(fuller.Id, blockSel.Position);
                world.BlockAccessor.MarkBlockDirty(blockSel.Position);
                if (byPlayer?.WorldData?.CurrentGameMode != EnumGameMode.Creative)
                {
                    slot.TakeOut(1);
                    slot.MarkDirty();
                }
                world.PlaySoundAt(Sounds?.Place.Location, blockSel.Position, 0, byPlayer);
            }
        }
        return true;
    }
}

/// <summary>
/// The scattered grain as food: chickens and other grain-eating birds find it like a trough and peck
/// it up, one portion at a time (one grain, as in the small trough). It thins out in four steps and
/// is gone when eaten up. Left out in the rain it gets soaked and spoils.
/// </summary>
public class BlockEntityScatteredGrain : BlockEntity, IAnimalFoodSource
{
    public const string ClassName = "livestockwelfare.scatteredgrain";

    ItemStack? grain;
    // Hours of rain it has been out in, and when that was last checked.
    double wetHours;
    double lastCheckHours;

    public Vec3d Position => Pos.ToVec3d().Add(0.5, 0.1, 0.5);

    public string Type => "food";

    public int Portions => (Api?.World.BlockAccessor.GetBlock(Pos) as BlockScatteredGrain)?.Portions ?? 0;

    public bool Soaked => wetHours > 0;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api.Side != EnumAppSide.Server) return;
        if (api.World.GetItem(new AssetLocation("game", "grain-" + Block.Variant["type"])) is { } grainItem) grain = new ItemStack(grainItem);
        api.ModLoader.GetModSystem<POIRegistry>().AddPOI(this);
        if (lastCheckHours <= 0) lastCheckHours = api.World.Calendar.TotalHours;
        RegisterGameTickListener(CheckRain, 10000, api.World.Rand.Next(5000));
    }

    /// <summary>Animals that may eat it (by default birds only, not pigs), if their diet takes the grain.</summary>
    public static bool Eats(Entity entity)
    {
        var codes = livestockwelfareModSystem.Config?.ScatteredGrain?.AnimalCodes;
        return entity?.Code != null && codes != null && codes.Any(code => WildcardUtil.Match(new AssetLocation(code), entity.Code));
    }

    public bool IsSuitableFor(Entity entity, CreatureDiet diet) =>
        grain != null && diet != null && Portions > 0 && Eats(entity) && diet.Matches(grain);

    public float ConsumeOnePortion(Entity entity)
    {
        int portions = Portions;
        if (portions <= 0) return 0;
        SetPortions(portions - 1);
        return 1;
    }

    void SetPortions(int portions)
    {
        var accessor = Api.World.BlockAccessor;
        var block = portions > 0 ? Api.World.GetBlock(Block.CodeWithVariant("portions", portions.ToString())) : null;
        // Exchanging keeps this block entity (and its rain count); the last portion takes it away.
        if (block != null) accessor.ExchangeBlock(block.Id, Pos);
        else accessor.SetBlock(0, Pos);
        accessor.MarkBlockDirty(Pos);
    }

    void CheckRain(float dt)
    {
        var settings = livestockwelfareModSystem.Config?.ScatteredGrain;
        double now = Api.World.Calendar.TotalHours;
        // After a long unload the weather back then is unknown, so at most half an hour is counted.
        double hours = Math.Clamp(now - lastCheckHours, 0, 0.5);
        lastCheckHours = now;
        if (settings == null || settings.SpoilAfterRainHours <= 0) return;

        var accessor = Api.World.BlockAccessor;
        bool roofed = accessor.GetRainMapHeightAt(Pos) > Pos.Y;
        if (roofed || !(accessor.GetClimateAt(Pos, EnumGetClimateMode.NowValues)?.Rainfall > 0.05f)) return;

        bool wasSoaked = Soaked;
        wetHours += hours;
        if (wetHours >= settings.SpoilAfterRainHours)
        {
            // Soaked grain goes mouldy: a little rot is left where it lay.
            if (Api.World.GetItem(new AssetLocation("game", "rot")) is { } rot)
            {
                Api.World.SpawnItemEntity(new ItemStack(rot), Pos.ToVec3d().Add(0.5, 0.1, 0.5));
            }
            accessor.SetBlock(0, Pos);
            return;
        }
        if (!wasSoaked) MarkDirty(true);
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetDouble("wetHours", wetHours);
        tree.SetDouble("lastCheckHours", lastCheckHours);
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessForResolve)
    {
        base.FromTreeAttributes(tree, worldAccessForResolve);
        wetHours = tree.GetDouble("wetHours");
        lastCheckHours = tree.GetDouble("lastCheckHours");
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder dsc)
    {
        base.GetBlockInfo(forPlayer, dsc);
        dsc.AppendLine(Lang.Get("livestockwelfare:scatteredgrain-portions", Portions));
        if (Soaked) dsc.AppendLine(Lang.Get("livestockwelfare:scatteredgrain-soaked"));
    }

    public override void OnBlockRemoved()
    {
        base.OnBlockRemoved();
        if (Api?.Side == EnumAppSide.Server) Api.ModLoader.GetModSystem<POIRegistry>().RemovePOI(this);
    }

    public override void OnBlockUnloaded()
    {
        base.OnBlockUnloaded();
        if (Api?.Side == EnumAppSide.Server) Api.ModLoader.GetModSystem<POIRegistry>().RemovePOI(this);
    }

    /// <summary>
    /// Vanilla gives chickens no eating animation, so they stand still at the trough. This adds their
    /// own "eat" (pecking) animation to their eating task, for the trough and the scattered grain alike.
    /// </summary>
    public static int AddPeckAnimation(ICoreAPI api)
    {
        int count = 0;
        foreach (var type in api.World.EntityTypes)
        {
            if (type?.Code == null || type.Code.Domain != "game" || !type.Code.Path.StartsWith("chicken-")) continue;
            var taskai = type.Server?.BehaviorsAsJsonObj?.FirstOrDefault(b => b["code"].AsString() == "taskai");
            if (taskai?["aitasks"].Token is not Newtonsoft.Json.Linq.JArray tasks) continue;
            foreach (var task in tasks.OfType<Newtonsoft.Json.Linq.JObject>())
            {
                if ((string?)task["code"] != "seekfoodandeat" || task["eatAnimation"] != null) continue;
                task["eatAnimation"] = "eat";
                count++;
            }
        }
        return count;
    }
}
