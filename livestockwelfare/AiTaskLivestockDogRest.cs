using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;

namespace livestockwelfare;

/// <summary>
/// A dog tired from a trip walks to the nearest dog bed (DogSettings.BedCodes) and lies down there
/// until it is rested. Without a bed nearby it lies down where it is. At night a kept dog sleeps in
/// a free bed nearby too (DogSettings.SleepInBedAtNight), if there is one.
/// </summary>
public class AiTaskLivestockDogRest : AiTaskLivestockGoTo
{
    public const string Code = "livestockwelfare-dogrest";

    // Vanilla wolves and Wolf taming's dogs both have it.
    const string SleepAnimation = "sleep";

    EntityBehaviorLivestockDog? dog;

    public AiTaskLivestockDogRest(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    public override void AfterInitialize()
    {
        base.AfterInitialize();
        dog = entity.GetBehavior<EntityBehaviorLivestockDog>();
    }

    static DogSettings? Settings =>
        livestockwelfareModSystem.Config?.Dogs is { Enabled: true } settings ? settings : null;

    bool Tired => dog?.IsTired == true && Settings is { TripAftermath: true };

    bool Bedtime => Settings is { SleepInBedAtNight: true } settings && dog is { IsKept: true } && settings.IsNight(entity.World.Calendar);

    bool Sleepy => Tired || Bedtime;

    protected override bool Wanted() =>
        Sleepy && entity.GetBehavior<EntityBehaviorLivestockHygiene>() is { IsHeldStill: false };

    // The nearest free bed; tired from a trip, else the spot the dog stands on.
    protected override BlockPos? FindTarget()
    {
        var here = entity.Pos.AsBlockPos;
        var orHere = Tired ? here : null;
        if (Settings is not { } settings || settings.BedRadius <= 0) return orHere;

        int r = settings.BedRadius;
        BlockPos? best = null;
        double bestDistance = double.MaxValue;
        entity.World.BlockAccessor.WalkBlocks(here.AddCopy(-r, -3, -r), here.AddCopy(r, 3, r), (block, x, y, z) =>
        {
            if (block.Code == null || !IsBed(block, settings)) return;
            var pos = new BlockPos(x, y, z, here.dimension);
            if (IsFailed(pos) || Taken(pos)) return;
            double distance = here.DistanceSqTo(x, y, z);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = pos;
            }
        });
        return best ?? orHere;
    }

    // Another dog already lies on it (a two-block cushion counts as one bed).
    bool Taken(BlockPos pos) =>
        entity.World.GetEntitiesAround(pos.ToVec3d().Add(0.5, 0, 0.5), 1.5f, 1,
            e => e != entity && e.Alive && e.HasBehavior<EntityBehaviorLivestockDog>()).Length > 0;

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

    bool sleeping;

    // It stays (the task keeps running) until rested or morning, so wandering and Wolf taming's own
    // naps can't take it off the bed. Anything more important still can: going out, guarding, commands.
    protected override bool ActAtTarget(float dt, float timeAtTarget)
    {
        if (!sleeping)
        {
            sleeping = true;
            StartAnimationIfExists(SleepAnimation);
        }
        return Sleepy;
    }

    protected override void OnFinish(bool arrivedAtTarget)
    {
        if (sleeping) entity.AnimManager.StopAnimation(SleepAnimation);
        sleeping = false;
    }
}
