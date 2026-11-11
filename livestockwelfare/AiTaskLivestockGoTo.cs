using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Shared AI for "walk somewhere and do something there" (drink, shelter), modelled on vanilla
/// AiTaskSeekFoodAndEat: find a target, path to it, act once close enough. Targets the animal
/// could not reach are skipped for a minute, so it doesn't keep walking into a fence.
/// </summary>
public abstract class AiTaskLivestockGoTo : AiTaskBase
{
    /// <summary>How often it looks for a target while not running.</summary>
    protected virtual long SearchIntervalMs => 10000;
    const long FailedTargetMemoryMs = 60000;
    const long GiveUpAfterStuckMs = 3000;

    protected readonly float MoveSpeed;
    protected Vec3d? Target;
    protected BlockPos? TargetBlock;

    readonly Dictionary<BlockPos, long> failedTargets = new();
    long lastSearchMs = long.MinValue / 2;
    bool arrived;
    bool stuck;
    long stuckAtMs;
    float timeAtTarget;

    protected AiTaskLivestockGoTo(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        MoveSpeed = taskConfig["movespeed"].AsFloat(0.006f);
    }

    /// <summary>Whether the animal currently wants to go (checked before and while walking).</summary>
    protected abstract bool Wanted();

    /// <summary>Finds a target block, skipping those for which IsFailed is true; null if there is none.</summary>
    protected abstract BlockPos? FindTarget();

    /// <summary>The point in the target block to walk to and measure from: by default the middle of its bottom.</summary>
    protected virtual Vec3d TargetPoint(BlockPos block) => block.ToVec3d().Add(0.5, 0, 0.5);

    /// <summary>Called every tick once at the target; return false when done.</summary>
    protected abstract bool ActAtTarget(float dt, float timeAtTarget);

    /// <summary>How close (blocks, from the animal's hitbox) counts as arrived.</summary>
    protected virtual float ArriveDistance => 1f;

    /// <summary>
    /// Whether the animal is there. By default its hitbox is within ArriveDistance of the target
    /// (right for things it stands next to, like water); override to require more.
    /// </summary>
    protected virtual bool IsAtTarget(double hitboxDistance) => hitboxDistance <= ArriveDistance;

    protected virtual void OnFinish(bool arrivedAtTarget) { }

    /// <summary>Whether the animal has reached the target in the current run.</summary>
    protected bool Arrived => arrived;

    // Blocks next to an unreachable target are usually just as unreachable (the same closed barn,
    // the same pond behind a fence), so trying them one by one only repeats the same failed walk.
    const int FailedTargetRadius = 3;

    protected bool IsFailed(BlockPos pos)
    {
        long now = entity.World.ElapsedMilliseconds;
        foreach (var (failed, ms) in failedTargets)
        {
            if (now - ms < FailedTargetMemoryMs
                && Math.Abs(failed.X - pos.X) <= FailedTargetRadius && Math.Abs(failed.Z - pos.Z) <= FailedTargetRadius
                && Math.Abs(failed.Y - pos.Y) <= 2)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether this task is the one running now.</summary>
    protected bool IsActive => entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager?.ActiveTasksBySlot.Contains(this) == true;

    public override bool ShouldExecute()
    {
        // Vanilla also asks a running task, and a task that lets lower ones interrupt it (a lower
        // PriorityForCancel) would cancel and restart itself every search: the animal got up from
        // its bedding every 10 seconds, and her young left her each time.
        if (IsActive) return false;
        long now = entity.World.ElapsedMilliseconds;
        if (now - lastSearchMs < SearchIntervalMs) return false;
        if (IsOnCooldown() || !PreconditionsSatisfied() || !Wanted()) return false;
        lastSearchMs = now;

        TargetBlock = FindTarget();
        Target = TargetBlock == null ? null : TargetPoint(TargetBlock);
        return Target != null;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        arrived = false;
        stuck = false;
        timeAtTarget = 0;
        // Like vanilla AiTaskSeekFoodAndEat, no path found is not a failure: ContinueExecute then
        // heads straight for the target, and only really getting stuck gives up. (Giving up at once
        // left animals next to a barrel the pathfinder couldn't plan to, dying of thirst.)
        pathTraverser.NavigateTo_Async(Target!, MoveSpeed, ArriveDistance - 0.1f, OnGoalReached, OnStuck, null, 1000, 1);
        Log("start, target {0}", TargetBlock);
    }

    public override bool CanContinueExecute() => pathTraverser.Ready;

    public override bool ContinueExecute(float dt)
    {
        if (Target == null) return false;
        if (!arrived && !Wanted()) return false;

        double distance = entity.SelectionBox.ToDouble().Translate(entity.Pos.X, entity.Pos.Y, entity.Pos.Z).ShortestDistanceFrom(Target);
        if (arrived || IsAtTarget(distance))
        {
            if (!arrived)
            {
                arrived = true;
                Log("arrived at {0}", entity.Pos.AsBlockPos);
                pathTraverser.Stop();
                if (animMeta != null) entity.AnimManager.StopAnimation(animMeta.Code);
            }
            timeAtTarget += dt;
            return ActAtTarget(dt, timeAtTarget);
        }

        if (!pathTraverser.Active && !stuck)
        {
            if (!pathTraverser.NavigateTo(Target, MoveSpeed, ArriveDistance - 0.15f, OnGoalReached, OnStuck, null, giveUpWhenNoPath: false, 500, 1))
            {
                OnStuck();
            }
        }

        return !(stuck && entity.World.ElapsedMilliseconds > stuckAtMs + GiveUpAfterStuckMs);
    }

    public override void FinishExecute(bool cancelled)
    {
        base.FinishExecute(cancelled);
        pathTraverser.Stop();
        OnFinish(arrived);
        Log("finished (cancelled {0}, arrived {1}, at {2})", cancelled, arrived, entity.Pos.AsBlockPos);
        // Interrupted (e.g. by fleeing): allow trying again soon.
        if (cancelled && !arrived) cooldownUntilTotalHours = 0;
    }

    void OnStuck()
    {
        stuck = true;
        Log("stuck on the way to {0}", TargetBlock);
        stuckAtMs = entity.World.ElapsedMilliseconds;
        foreach (var old in failedTargets.Where(f => stuckAtMs - f.Value >= FailedTargetMemoryMs).Select(f => f.Key).ToList())
        {
            failedTargets.Remove(old);
        }
        if (TargetBlock != null) failedTargets[TargetBlock.Copy()] = stuckAtMs;
    }

    // Goes to server-debug.log, to see in a test world what the animals are doing.
    protected void Log(string message, params object?[] args) =>
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1} {2}: {3}", entity.Code.Path, entity.EntityId, GetType().Name, string.Format(message, args));

    void OnGoalReached()
    {
        pathTraverser.Active = true;
    }

    /// <summary>Plays one of the entity's own animations if it has it (codes are lower case).</summary>
    protected void StartAnimationIfExists(string code)
    {
        var animations = entity.Properties.Client?.Animations;
        if (animations == null) return;
        foreach (var anim in animations)
        {
            if (anim.Code == code)
            {
                entity.AnimManager.StartAnimation(anim);
                return;
            }
        }
    }
}
