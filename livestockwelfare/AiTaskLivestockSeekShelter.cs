using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// In rain, snow, cold or strong sun, an animal standing in the open walks to the nearest spot under
/// a roof and stays there while the bad weather lasts.
/// </summary>
public class AiTaskLivestockSeekShelter : AiTaskLivestockGoTo
{
    public const string Code = "livestockwelfare-seekshelter";

    // Safety limit; normally it stays until morning or the weather clears. Eating and drinking
    // have a higher priority, so the animal can still go to the trough in between once it is
    // hungry or thirsty enough (see HoldsBackFoodAndWater).
    const float MaxStaySeconds = 600;

    EntityBehaviorLivestockShelter? shelter;
    EntityBehaviorLivestockThirst? thirst;
    AiTaskManager? taskManager;
    bool sleeping;

    public AiTaskLivestockSeekShelter(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    public override void AfterInitialize()
    {
        base.AfterInitialize();
        shelter = entity.GetBehavior<EntityBehaviorLivestockShelter>();
        thirst = entity.GetBehavior<EntityBehaviorLivestockThirst>();
        taskManager = entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
    }

    static WeatherSettings? Settings =>
        livestockwelfareModSystem.Config?.Weather is { Enabled: true, SeekShelter: true } settings ? settings : null;

    // At night it also runs for animals already under a roof: the nearest roofed spot is then right
    // where they stand, and they lie down to sleep there.
    // A hungry or thirsty animal on its way to food or water is let go, and comes back after.
    protected override bool Wanted() =>
        shelter != null && Settings != null && ShouldBeUnderRoof() && (!shelter.Roofed || SleepTime)
        && !(NeedsFoodOrWater && GoingForFoodOrWater()) && LivestockUtil.ShouldSimulate(entity);

    /// <summary>The vanilla trough task and ours for water, which bad weather can hold back.</summary>
    public static bool IsFoodOrWater(IAiTask task) => task is AiTaskSeekFoodAndEat or AiTaskLivestockSeekWater;

    /// <summary>
    /// Sitting out bad weather under the roof, the animal waits with food and water until it is
    /// hungry or thirsty enough (WeatherSettings.ShelterYields...). Otherwise it would step out to
    /// the trough and be called straight back by this task, over and over.
    /// </summary>
    public bool HoldsBackFoodAndWater =>
        shelter != null && Arrived && !SleepTime && shelter.BadWeather && !NeedsFoodOrWater
        && taskManager?.ActiveTasksBySlot.Contains(this) == true;

    // Hungry or thirsty enough to go out in bad weather; never at night, when it sleeps.
    bool NeedsFoodOrWater
    {
        get
        {
            if (Settings is not { } settings || SleepTime) return false;
            if (thirst != null && thirst.Thirst >= settings.ShelterYieldsAboveThirst) return true;
            // Vanilla notes each meal from a trough; an animal that never ate counts as hungry.
            double lastMeal = entity.WatchedAttributes.GetDouble("lastMealEatenTotalHours");
            return entity.World.Calendar.TotalHours - lastMeal >= settings.ShelterYieldsHoursWithoutFood;
        }
    }

    bool GoingForFoodOrWater() =>
        taskManager?.ActiveTasksBySlot.Any(task => task != null && task != this && IsFoodOrWater(task)) == true;

    bool SleepTime => shelter != null && shelter.Night && Settings?.SleepUnderRoof == true;

    // Vanilla livestock has an "idle: sleep" task that makes them lie down wherever they are, and
    // only a higher priority can cancel it: sheep 1.35 (22:00-05:00), pigs 1.35 (04:00-15:00, so
    // also in daytime rain), goats 1.38 (from 20:00), chickens 1.25 but 1.38 to cancel (from 20:00).
    // Ours must win to walk them under the roof first, while fleeing (1.39 and up) still wins over ours.
    const float GoPriority = 1.385f;
    // Once under the roof in daytime bad weather, eating from the trough (1.25 and up) may interrupt.
    const float StayPriorityForCancel = 1.24f;

    public override float Priority
    {
        get => ShouldBeUnderRoof() ? GoPriority : base.Priority;
        set => base.Priority = value;
    }

    // While walking nothing but fleeing stops it; at night it also keeps the animal asleep in place.
    public override float PriorityForCancel => Arrived && !SleepTime ? StayPriorityForCancel : Priority;

    // Bad weather, or night: animals go to sleep under the roof.
    bool ShouldBeUnderRoof() =>
        shelter != null && (shelter.BadWeather || (shelter.Night && Settings?.SleepUnderRoof == true));

    // Walk right onto the spot: being "next to" a roof is not being under it.
    protected override float ArriveDistance => 0.3f;

    // The body's centre must stand on the target block; the hitbox alone reaches it too early.
    protected override bool IsAtTarget(double hitboxDistance) =>
        Target != null && entity.Pos.HorDistanceTo(Target) <= 0.45 && Math.Abs(entity.Pos.Y - Target.Y) < 1.5;

    protected override BlockPos? FindTarget()
    {
        if (Settings is not { } settings) return null;

        int r = settings.SeekShelterRadius;
        var accessor = entity.World.BlockAccessor;
        var feet = entity.Pos.AsBlockPos;
        var pos = feet.Copy();
        var occupied = OccupiedSpots(r + 2);

        // Best = deep inside the shelter (much roof around it), not taken by another animal, and
        // not too far. Depth counts more than distance, so animals walk past the edge to the inside,
        // but only up to MaxUsefulDepth: a small shed close by beats a big barn behind the fence.
        BlockPos? best = null;
        double bestScore = double.MinValue;
        // Fallback for narrow shelters (e.g. a one block wide lean-to): roof above the spot only.
        BlockPos? partly = null;
        int partlyDistance = int.MaxValue;

        for (int dx = -r; dx <= r; dx++)
        {
            for (int dz = -r; dz <= r; dz++)
            {
                int distance = dx * dx + dz * dz;
                if (distance > r * r) continue;

                for (int dy = -2; dy <= 2; dy++)
                {
                    pos.Set(feet.X + dx, feet.Y + dy, feet.Z + dz);
                    if (!CanStandAt(accessor, pos) || accessor.GetRainMapHeightAt(pos) <= pos.Y || IsFailed(pos)) continue;
                    bool taken = occupied.Contains(pos);

                    if (!taken && FullyRoofed(accessor, pos))
                    {
                        double score = Math.Min(RoofDepth(accessor, pos), MaxUsefulDepth) - Math.Sqrt(distance) * 0.5;
                        if (score > bestScore)
                        {
                            best = pos.Copy();
                            bestScore = score;
                        }
                    }
                    else if (distance < partlyDistance)
                    {
                        partly = pos.Copy();
                        partlyDistance = distance;
                    }
                    break;
                }
            }
        }
        return best ?? partly;
    }

    // Roof all around in a 3x3 area (and a little more) is enough for the whole animal to stay dry;
    // a deeper spot is not worth a long walk, which may lead to a building outside the pen.
    const int MaxUsefulDepth = 12;

    // How deep under the roof a spot is: roofed columns in the 5x5 area around it (max 25).
    static int RoofDepth(IBlockAccessor accessor, BlockPos pos)
    {
        int roofed = 0;
        var tmp = pos.Copy();
        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dz = -2; dz <= 2; dz++)
            {
                tmp.Set(pos.X + dx, pos.Y, pos.Z + dz);
                if (accessor.GetRainMapHeightAt(tmp) > pos.Y) roofed++;
            }
        }
        return roofed;
    }

    // Blocks where other livestock stands, so animals spread out instead of piling onto one spot.
    HashSet<BlockPos> OccupiedSpots(int range)
    {
        var spots = new HashSet<BlockPos>();
        foreach (var other in entity.World.GetEntitiesAround(entity.Pos.XYZ, range, 3,
            e => e != entity && e.Alive && e.HasBehavior<EntityBehaviorLivestockShelter>()))
        {
            spots.Add(other.Pos.AsBlockPos);
        }
        return spots;
    }

    // Roof above this block and the four next to it, so the whole body (sheep are over a block
    // long) is under it, not just the head.
    static bool FullyRoofed(IBlockAccessor accessor, BlockPos pos)
    {
        if (accessor.GetRainMapHeightAt(pos) <= pos.Y) return false;
        foreach (var face in BlockFacing.HORIZONTALS)
        {
            if (accessor.GetRainMapHeightAt(pos.AddCopy(face)) <= pos.Y) return false;
        }
        return true;
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
