using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace livestockwelfare;

/// <summary>
/// In rain, snow, cold or strong sun, an animal standing in the open walks to the nearest spot under
/// a roof and stays there while the bad weather lasts.
/// </summary>
public class AiTaskLivestockSeekShelter : AiTaskLivestockGoTo
{
    public const string Code = "livestockwelfare-seekshelter";

    // Safety limit; normally it stays until morning or the weather clears. Eating and drinking
    // have a higher priority, so the animal can still go to the trough in between.
    const float MaxStaySeconds = 600;

    EntityBehaviorLivestockShelter? shelter;
    bool sleeping;

    public AiTaskLivestockSeekShelter(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    public override void AfterInitialize()
    {
        base.AfterInitialize();
        shelter = entity.GetBehavior<EntityBehaviorLivestockShelter>();
    }

    static WeatherSettings? Settings =>
        livestockwelfareModSystem.Config?.Weather is { Enabled: true, SeekShelter: true } settings ? settings : null;

    // At night it also runs for animals already under a roof: the nearest roofed spot is then right
    // where they stand, and they lie down to sleep there.
    protected override bool Wanted() =>
        shelter != null && Settings != null && ShouldBeUnderRoof() && (!shelter.Roofed || SleepTime)
        && LivestockUtil.ShouldSimulate(entity);

    bool SleepTime => shelter != null && shelter.Night && Settings?.SleepUnderRoof == true;

    // Bad weather, or night: animals go to sleep under the roof.
    bool ShouldBeUnderRoof() =>
        shelter != null && (shelter.BadWeather || (shelter.Night && Settings?.SleepUnderRoof == true));

    protected override float ArriveDistance => 0.6f;

    protected override BlockPos? FindTarget()
    {
        if (Settings is not { } settings) return null;

        int r = settings.SeekShelterRadius;
        var accessor = entity.World.BlockAccessor;
        var feet = entity.Pos.AsBlockPos;
        var pos = feet.Copy();
        BlockPos? best = null;
        int bestDistance = int.MaxValue;

        for (int dx = -r; dx <= r; dx++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                int distance = dx * dx + dz * dz;
                if (distance >= bestDistance || distance > r * r) continue;

                for (int dy = -2; dy <= 2; dy++)
                {
                    pos.Set(feet.X + dx, feet.Y + dy, feet.Z + dz);
                    if (!CanStandAt(accessor, pos) || accessor.GetRainMapHeightAt(pos) <= pos.Y || IsFailed(pos)) continue;
                    best = pos.Copy();
                    bestDistance = distance;
                    break;
                }
            }
        }
        return best;
    }

    // Two free blocks (most livestock is up to two blocks tall) on top of a solid block.
    static bool CanStandAt(IBlockAccessor accessor, BlockPos pos)
    {
        return IsFree(accessor.GetBlock(pos)) && IsFree(accessor.GetBlock(pos.UpCopy()))
            && accessor.GetBlock(pos.DownCopy()).SideSolid[BlockFacing.UP.Index];
    }

    static bool IsFree(Block block) =>
        !block.IsLiquid() && (block.CollisionBoxes == null || block.CollisionBoxes.Length == 0);

    public override void StartExecute()
    {
        base.StartExecute();
        sleeping = false;
    }

    protected override bool ActAtTarget(float dt, float timeAtTarget)
    {
        if (shelter == null) return false;

        // At night, lie down and sleep until morning.
        if (shelter.Night && Settings?.SleepUnderRoof == true && !sleeping)
        {
            sleeping = true;
            StartAnimationIfExists("sleep");
        }

        // Stay put under the roof while it lasts.
        return ShouldBeUnderRoof() && timeAtTarget < MaxStaySeconds;
    }

    protected override void OnFinish(bool arrivedAtTarget)
    {
        if (sleeping) entity.AnimManager.StopAnimation("sleep");
        sleeping = false;
    }
}
