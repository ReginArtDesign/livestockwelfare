using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace livestockwelfare;

/// <summary>
/// Muck lies on the ground: when the block under it is dug out (or is no longer solid on top), it
/// breaks and drops its rot instead of floating in the air. Vanilla BreakIfFloating doesn't fit: it
/// only works with falling blocks turned on and lets any solid neighbour, like a wall, hold it up.
/// </summary>
public class BlockBehaviorMuckNeedsGround : BlockBehavior
{
    public const string Code = "livestockwelfare.needsground";

    public BlockBehaviorMuckNeedsGround(Block block) : base(block) { }

    public override void OnNeighbourBlockChange(IWorldAccessor world, BlockPos pos, BlockPos neibpos, ref EnumHandling handling)
    {
        if (world.Side == EnumAppSide.Server && neibpos.Y == pos.Y - 1
            && !world.BlockAccessor.GetBlock(neibpos).SideSolid[BlockFacing.UP.Index])
        {
            world.BlockAccessor.BreakBlock(pos, null);
        }
        base.OnNeighbourBlockChange(world, pos, neibpos, ref handling);
    }
}
