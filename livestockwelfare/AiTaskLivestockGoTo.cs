using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace livestockwelfare;

/// <summary>
/// Shared AI for "walk somewhere and do something there" (drink, shelter), modelled on vanilla
/// AiTaskSeekFoodAndEat: find a target, path to it, act once close enough. Targets the animal
/// could not reach are skipped for a minute, so it doesn't keep walking into a fence.
/// </summary>
public abstract class AiTaskLivestockGoTo : AiTaskBase
{
    const long SearchIntervalMs = 10000;
    const long FailedTargetMemoryMs = 60000;
    const long GiveUpAfterStuckMs = 3000;

    protected readonly float MoveSpeed;
    protected Vec3d? Target;
    protected BlockPos? TargetBlock;

    readonly Dictionary<BlockPos, long> failedTargets = new();
    long lastSearchMs = -SearchIntervalMs;
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

    /// <summary>Called every tick once at the target; return false when done.</summary>
    protected abstract bool ActAtTarget(float dt, float timeAtTarget);

    /// <summary>How close (blocks, from the animal's hitbox) counts as arrived.</summary>
    protected virtual float ArriveDistance => 1f;

    protected virtual void OnFinish(bool arrivedAtTarget) { }

    protected bool IsFailed(BlockPos pos) =>
        failedTargets.TryGetValue(pos, out long ms) && entity.World.ElapsedMilliseconds - ms < FailedTargetMemoryMs;

    public override bool ShouldExecute()
    {
        long now = entity.World.ElapsedMilliseconds;
        if (now - lastSearchMs < SearchIntervalMs) return false;
        if (IsOnCooldown() || !PreconditionsSatisfied() || !Wanted()) return false;
        lastSearchMs = now;

        TargetBlock = FindTarget();
        Target = TargetBlock?.ToVec3d().Add(0.5, 0, 0.5);
        return Target != null;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        arrived = false;
        stuck = false;
        timeAtTarget = 0;
        pathTraverser.NavigateTo_Async(Target!, MoveSpeed, ArriveDistance - 0.1f, OnGoalReached, OnStuck, OnStuck, 1000, 1);
    }

    public override bool CanContinueExecute() => pathTraverser.Ready;

    public override bool ContinueExecute(float dt)
    {
        if (Target == null) return false;
        if (!arrived && !Wanted()) return false;

        double distance = entity.SelectionBox.ToDouble().Translate(entity.Pos.X, entity.Pos.Y, entity.Pos.Z).ShortestDistanceFrom(Target);
        if (arrived || distance <= ArriveDistance)
        {
            if (!arrived)
            {
                arrived = true;
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
        // Interrupted (e.g. by fleeing): allow trying again soon.
        if (cancelled && !arrived) cooldownUntilTotalHours = 0;
    }

    void OnStuck()
    {
        stuck = true;
        stuckAtMs = entity.World.ElapsedMilliseconds;
        if (TargetBlock != null) failedTargets[TargetBlock.Copy()] = stuckAtMs;
    }

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
