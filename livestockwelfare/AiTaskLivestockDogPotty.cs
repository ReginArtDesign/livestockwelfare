using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// A dog that needs to go (EntityBehaviorLivestockDog.NeedsToGo): outdoors it sits down and leaves
/// dog poop. Indoors (under a roof) it is house-trained: it sits at the nearest door, whining, until
/// someone opens it, then walks out and goes there. Held in too long (DogSettings.AccidentAfterHours)
/// it goes where it is. A dog that went out the door comes back in through it afterwards, sitting
/// outside it if it was shut meanwhile (DogSettings.ComeBackHours).
/// </summary>
public class AiTaskLivestockDogPotty : AiTaskLivestockGoTo
{
    public const string Code = "livestockwelfare-dogpotty";

    const float SquatSeconds = 3;
    const long WhineIntervalMs = 8000;
    // How far past the door it walks to be out in the open.
    const int StepsOutside = 3;
    const string SitAnimation = "sit";

    // How far past the door it walks back in.
    const int StepsInside = 2;
    // Where it went out, kept until it is back in (or gave up): the door's block and the time.
    const string ComeBackAttr = "lwPottyComeBack";

    enum Plan { Squat, WaitAtDoor, GoOut, WaitToComeIn, ComeIn }

    EntityBehaviorLivestockDog? dog;
    Plan plan;
    BEBehaviorDoor? door;
    bool squatting;
    long lastWhineMs;

    public AiTaskLivestockDogPotty(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    public override void AfterInitialize()
    {
        base.AfterInitialize();
        dog = entity.GetBehavior<EntityBehaviorLivestockDog>();
    }

    static DogSettings? Settings =>
        livestockwelfareModSystem.Config?.Dogs is { Enabled: true } settings && settings.PoopChancePerDay > 0 ? settings : null;

    protected override bool Wanted() =>
        dog is { KeepsStillOnCommand: false } && (dog.NeedsToGo || WantsBackIn) && Settings != null
        && entity.GetBehavior<EntityBehaviorLivestockHygiene>() is not { IsCollapsed: true };

    bool WantsBackIn => entity.Attributes.HasAttribute(ComeBackAttr);

    void ForgetComeBack() => entity.Attributes.RemoveAttribute(ComeBackAttr);

    bool Roofed(BlockPos pos) => entity.World.BlockAccessor.GetRainMapHeightAt(pos) > pos.Y;

    // Through the door means the dog itself is through, not just the front of its selection box
    // (still 1.2 wide, see EntityBehaviorLivestockDog.Slim), which reaches past the doorway first.
    protected override bool IsAtTarget(double hitboxDistance) => plan switch
    {
        Plan.GoOut => !Roofed(entity.Pos.AsBlockPos) && CloseTo(TargetBlock, 1.5),
        Plan.ComeIn => Roofed(entity.Pos.AsBlockPos) && CloseTo(TargetBlock, 1.5),
        _ => base.IsAtTarget(hitboxDistance),
    };

    bool CloseTo(BlockPos? block, double distance) =>
        block != null && entity.Pos.HorDistanceTo(block.X + 0.5, block.Z + 0.5) <= distance;

    protected override BlockPos? FindTarget()
    {
        if (Settings is not { } settings || dog == null) return null;
        var here = entity.Pos.AsBlockPos;
        if (!dog.NeedsToGo) return FindWayBackIn(settings, here);
        bool accident = settings.AccidentAfterHours > 0 && dog.UrgeHours >= settings.AccidentAfterHours;
        if (!Roofed(here) || accident)
        {
            plan = Plan.Squat;
            return here;
        }

        door = NearestDoor(here, settings.DoorSearchRadius, out var doorPos);
        if (door == null || doorPos == null) return null;

        // The door's two sides: out is the one under the open sky, in is the other.
        var facing = door.facingWhenClosed;
        var sideA = doorPos.AddCopy(facing);
        var sideB = doorPos.AddCopy(facing.Opposite);
        bool aOutside = !Roofed(sideA);
        var outward = aOutside ? facing : facing.Opposite;
        if (door.Opened)
        {
            plan = Plan.GoOut;
            if (settings.ComeBackHours > 0)
            {
                var back = entity.Attributes.GetOrAddTreeAttribute(ComeBackAttr);
                back.SetInt("x", doorPos.X);
                back.SetInt("y", doorPos.Y);
                back.SetInt("z", doorPos.Z);
                back.SetDouble("sinceHours", entity.World.Calendar.TotalHours);
            }
            return doorPos.AddCopy(outward.Normali.X * StepsOutside, 0, outward.Normali.Z * StepsOutside);
        }
        plan = Plan.WaitAtDoor;
        return aOutside ? sideB : sideA;
    }

    // Done outside: back to the door it came out of. Open, it walks in; shut, it sits outside it.
    BlockPos? FindWayBackIn(DogSettings settings, BlockPos here)
    {
        var back = entity.Attributes.GetTreeAttribute(ComeBackAttr);
        var doorPos = back == null ? null : new BlockPos(back.GetInt("x"), back.GetInt("y"), back.GetInt("z"), here.dimension);
        door = doorPos == null ? null : entity.World.BlockAccessor.GetBlockEntity(doorPos)?.GetBehavior<BEBehaviorDoor>();
        bool tooLong = back != null && entity.World.Calendar.TotalHours - back.GetDouble("sinceHours") > settings.ComeBackHours;
        if (door == null || doorPos == null || tooLong || Roofed(here))
        {
            ForgetComeBack();
            return null;
        }

        var facing = door.facingWhenClosed;
        var outward = Roofed(doorPos.AddCopy(facing)) ? facing.Opposite : facing;
        if (door.Opened)
        {
            plan = Plan.ComeIn;
            return doorPos.AddCopy(-outward.Normali.X * StepsInside, 0, -outward.Normali.Z * StepsInside);
        }
        plan = Plan.WaitToComeIn;
        return doorPos.AddCopy(outward);
    }

    BEBehaviorDoor? NearestDoor(BlockPos here, int r, out BlockPos? found)
    {
        found = null;
        if (r <= 0) return null;
        var accessor = entity.World.BlockAccessor;
        BEBehaviorDoor? best = null;
        BlockPos? bestPos = null;
        double bestDistance = double.MaxValue;
        accessor.WalkBlocks(here.AddCopy(-r, -2, -r), here.AddCopy(r, 2, r), (block, x, y, z) =>
        {
            if (!block.HasBehavior<BlockBehaviorDoor>()) return;
            var pos = new BlockPos(x, y, z, here.dimension);
            if (IsFailed(pos) || accessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorDoor>() is not { } candidate) return;
            double distance = here.DistanceSqTo(x, y, z);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
                bestPos = pos;
            }
        });
        found = bestPos;
        return best;
    }

    public override void StartExecute()
    {
        // Narrow before the path is planned, so the pathfinder lets it through the door.
        if (plan is Plan.GoOut or Plan.ComeIn) dog?.SqueezeThroughDoor();
        base.StartExecute();
        squatting = false;
    }

    protected override bool ActAtTarget(float dt, float timeAtTarget)
    {
        if (dog == null) return false;
        switch (plan)
        {
            case Plan.WaitAtDoor:
                // Sits facing the door and whines now and then until it opens (or it can't wait).
                SitAndWhine();
                bool accident = Settings is { AccidentAfterHours: > 0 } settings && dog.UrgeHours >= settings.AccidentAfterHours;
                return door is { Opened: false } && !accident;

            case Plan.WaitToComeIn:
                // The same outside, until it opens or the dog gives up (FindWayBackIn).
                SitAndWhine();
                return door is { Opened: false } && WantsBackIn
                    && Settings is { } waitSettings && entity.World.Calendar.TotalHours - entity.Attributes.GetTreeAttribute(ComeBackAttr).GetDouble("sinceHours") <= waitSettings.ComeBackHours;

            case Plan.ComeIn:
                ForgetComeBack();
                return false;

            case Plan.GoOut:
                // Out the door; squats next time round, now under the open sky.
                return false;

            default:
                if (!squatting)
                {
                    squatting = true;
                    StartAnimationIfExists(SitAnimation);
                }
                if (timeAtTarget < SquatSeconds) return true;
                dog.Relieve();
                return false;
        }
    }

    void SitAndWhine()
    {
        if (!squatting)
        {
            squatting = true;
            StartAnimationIfExists(SitAnimation);
        }
        long now = entity.World.ElapsedMilliseconds;
        if (now - lastWhineMs <= WhineIntervalMs) return;
        lastWhineMs = now;
        var whine = new AssetLocation("game", "sounds/creature/wolf/pup-howl" + (1 + entity.World.Rand.Next(2)));
        entity.World.PlaySoundAt(whine, entity, null, 0.9f + (float)entity.World.Rand.NextDouble() * 0.2f, 16);
    }

    protected override void OnFinish(bool arrivedAtTarget)
    {
        if (squatting) entity.AnimManager.StopAnimation(SitAnimation);
        squatting = false;
        dog?.StopSqueezing();
    }
}
