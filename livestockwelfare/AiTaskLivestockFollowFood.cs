using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Treats (see TreatSettings): the animal walks after a player holding food it eats, and stops a
/// little way off, waiting to be fed. Meanwhile it doesn't run from that player, but still runs
/// from predators and when hurt.
/// </summary>
public class AiTaskLivestockFollowFood : AiTaskBase
{
    public const string Code = "livestockwelfare-followfood";

    const long LureCheckIntervalMs = 1000;
    const long RepathIntervalMs = 1000;
    const string HurtEmotion = "fleeondamage";
    // Animals they still run from while lured.
    static readonly AssetLocation[] Predators =
        { new("game:wolf-*"), new("game:fox-*"), new("game:bear-*"), new("game:hyena-*") };

    readonly float moveSpeed;
    EntityPlayer? lure;
    long lureCheckedMs = -LureCheckIntervalMs;
    long repathedMs;
    Vec3d? pathTarget;

    public AiTaskLivestockFollowFood(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        moveSpeed = taskConfig["movespeed"].AsFloat(0.015f);
    }

    /// <summary>The player nearby holding food for this animal (null if none, or a predator is near).</summary>
    public EntityPlayer? Lure
    {
        get
        {
            long now = entity.World.ElapsedMilliseconds;
            if (now - lureCheckedMs >= LureCheckIntervalMs)
            {
                lureCheckedMs = now;
                lure = FindLure();
            }
            return lure;
        }
    }

    /// <summary>Lured, it doesn't run from players, unless it was hurt.</summary>
    public bool KeepsFromFleeing =>
        Lure != null && entity.GetBehavior<EntityBehaviorEmotionStates>()?.IsInEmotionState(HurtEmotion) != true;

    EntityPlayer? FindLure()
    {
        if (LivestockTreats.Settings is not { } settings || !LivestockTreats.TakesTreats(entity)) return null;

        double range = settings.FollowRadius;
        EntityPlayer? nearest = null;
        double nearestDistance = range * range;
        foreach (var player in entity.World.AllOnlinePlayers)
        {
            if (player.Entity is not { Alive: true } playerEntity || player.WorldData.CurrentGameMode == EnumGameMode.Spectator) continue;
            double distance = playerEntity.Pos.SquareDistanceTo(entity.Pos);
            if (distance > nearestDistance || !Holds(playerEntity)) continue;
            nearest = playerEntity;
            nearestDistance = distance;
        }
        if (nearest == null) return null;

        bool predatorNear = entity.World.GetEntitiesAround(entity.Pos.XYZ, 12, 4,
            e => e.Alive && Predators.Any(code => WildcardUtil.Match(code, e.Code))).Length > 0;
        return predatorNear ? null : nearest;
    }

    bool Holds(EntityPlayer player) =>
        LivestockTreats.Eats(entity, player.RightHandItemSlot?.Itemstack) || LivestockTreats.Eats(entity, player.LeftHandItemSlot?.Itemstack);

    double StopDistance => LivestockTreats.Settings?.FollowDistance ?? 1.5;

    bool FarFrom(EntityPlayer player, double extra) =>
        player.Pos.SquareDistanceTo(entity.Pos) > (StopDistance + extra) * (StopDistance + extra);

    public override bool ShouldExecute() =>
        !IsOnCooldown() && PreconditionsSatisfied() && Lure is { } player && FarFrom(player, 1) && LivestockUtil.ShouldSimulate(entity);

    public override void StartExecute()
    {
        base.StartExecute();
        Repath();
    }

    void Repath()
    {
        if (lure == null) return;
        repathedMs = entity.World.ElapsedMilliseconds;
        pathTarget = lure.Pos.XYZ;
        pathTraverser.NavigateTo(pathTarget, moveSpeed, (float)StopDistance, OnGoalReached, OnStuck, null, giveUpWhenNoPath: false, 500, 1);
    }

    public override bool ContinueExecute(float dt)
    {
        if (Lure is not { } player || !FarFrom(player, 0)) return false;
        // Keep up with the player: a new path once they have moved on.
        if (entity.World.ElapsedMilliseconds - repathedMs >= RepathIntervalMs
            && (pathTarget == null || player.Pos.SquareDistanceTo(pathTarget) > 1 || !pathTraverser.Active))
        {
            Repath();
        }
        return true;
    }

    public override void FinishExecute(bool cancelled)
    {
        base.FinishExecute(cancelled);
        pathTraverser.Stop();
    }

    void OnGoalReached() { }

    void OnStuck() { }
}
