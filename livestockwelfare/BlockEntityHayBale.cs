using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// A hay bale set down in the pen: grass eaters (sheep, goats) find it like a trough and eat from
/// it directly. Each portion eaten makes it one size smaller (haybale-4 to haybale-1), and the
/// last one is gone. One portion is one hay block, as in the large trough.
/// </summary>
public class BlockEntityHayBale : BlockEntity, IAnimalFoodSource
{
    public const string ClassName = "livestockwelfare.haybale";

    // What the animals eat, for their diet check: vanilla hay ("grass" food tag).
    static readonly AssetLocation HayCode = new("game", "hay-normal-ud");
    ItemStack? hay;

    public Vec3d Position => Pos.ToVec3d().Add(0.5, 0.5, 0.5);

    public string Type => "food";

    /// <summary>Portions left, from the block's "portions" variant.</summary>
    public int Portions => int.TryParse(Api?.World.BlockAccessor.GetBlock(Pos).Variant["portions"], out int portions) ? portions : 0;

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        if (api.Side != EnumAppSide.Server) return;
        if (api.World.GetBlock(HayCode) is { } hayBlock) hay = new ItemStack(hayBlock);
        api.ModLoader.GetModSystem<POIRegistry>().AddPOI(this);
    }

    public bool IsSuitableFor(Entity entity, CreatureDiet diet) =>
        hay != null && diet != null && Portions > 0 && diet.Matches(hay);

    public float ConsumeOnePortion(Entity entity)
    {
        int portions = Portions;
        if (portions <= 0) return 0;

        var accessor = Api.World.BlockAccessor;
        var smaller = portions > 1 ? Api.World.GetBlock(Block.CodeWithVariant("portions", (portions - 1).ToString())) : null;
        // Exchanging keeps this block entity; the last portion takes the bale away.
        if (smaller != null) accessor.ExchangeBlock(smaller.Id, Pos);
        else accessor.SetBlock(0, Pos);
        accessor.MarkBlockDirty(Pos);
        return 1;
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
}
