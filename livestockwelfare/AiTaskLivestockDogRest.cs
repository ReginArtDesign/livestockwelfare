using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace livestockwelfare;

/// <summary>
/// A dog tired from a trip walks to the nearest dog bed (DogSettings.BedCodes) and lies down there
/// until it is rested. Without a bed nearby it lies down where it is.
/// </summary>
public class AiTaskLivestockDogRest : AiTaskLivestockGoTo
{
    public const string Code = "livestockwelfare-dogrest";

    // Lying down is done in stretches (see HoldStill), so the dog still notices when it is rested.
    const float LieSeconds = 90;
    const string LieAnimation = "lie";

    EntityBehaviorLivestockDog? dog;

    public AiTaskLivestockDogRest(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    public override void AfterInitialize()
    {
        base.AfterInitialize();
        dog = entity.GetBehavior<EntityBehaviorLivestockDog>();
    }

    static DogSettings? Settings =>
        livestockwelfareModSystem.Config?.Dogs is { Enabled: true, TripAftermath: true } settings ? settings : null;

    protected override bool Wanted() =>
        dog?.IsTired == true && entity.GetBehavior<EntityBehaviorLivestockHygiene>() is { IsHeldStill: false };

    // The nearest bed, or else the spot the dog stands on.
    protected override BlockPos? FindTarget()
    {
        var here = entity.Pos.AsBlockPos;
        if (Settings is not { } settings || settings.BedRadius <= 0) return here;

        int r = settings.BedRadius;
        BlockPos? best = null;
        double bestDistance = double.MaxValue;
        entity.World.BlockAccessor.WalkBlocks(here.AddCopy(-r, -3, -r), here.AddCopy(r, 3, r), (block, x, y, z) =>
        {
            if (block.Code == null || !IsBed(block, settings)) return;
            var pos = new BlockPos(x, y, z, here.dimension);
            if (IsFailed(pos)) return;
            double distance = here.DistanceSqTo(x, y, z);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = pos;
            }
        });
        return best ?? here;
    }

    static bool IsBed(Block block, DogSettings settings)
    {
        foreach (string code in settings.BedCodes)
        {
            if (WildcardUtil.Match(new AssetLocation(code), block.Code)) return true;
        }
        return false;
    }

    // Lie on the bed, not next to it.
    protected override bool IsAtTarget(double hitboxDistance) => hitboxDistance <= 0.3;
    protected override float ArriveDistance => 0.6f;

    protected override bool ActAtTarget(float dt, float timeAtTarget)
    {
        entity.GetBehavior<EntityBehaviorLivestockHygiene>()?.HoldStill(LieSeconds, LieAnimation);
        return false;
    }
}
