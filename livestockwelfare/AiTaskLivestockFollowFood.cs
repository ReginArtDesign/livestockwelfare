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
/// from predators and when hurt. Kept predators (the player's own dogs) don't scare it off.
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

    /// <summary>
    /// What lures it, instead of the treats (TreatSettings): wolf pups follow meat and bones, which
    /// Wolf taming then lets the player feed them to tame them.
    /// </summary>
    public System.Func<ItemStack, bool>? Lures { get; set; }

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
        if (LivestockTreats.Settings is not { } settings || (Lures == null && !LivestockTreats.TakesTreats(entity))) return null;

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
            e => e != entity && e.Alive && Predators.Any(code => WildcardUtil.Match(code, e.Code)) && !LivestockUtil.IsKept(e) && !IsYoung(e)).Length > 0;
        return predatorNear ? null : nearest;
    }

    // A wolf pup's brothers and sisters are no danger to it.
    static bool IsYoung(Entity e) => e.Code.Path.Contains("-baby") || e.Code.Path.Contains("-pup");

    bool Holds(EntityPlayer player) => Tempts(player.RightHandItemSlot?.Itemstack) || Tempts(player.LeftHandItemSlot?.Itemstack);

    bool Tempts(ItemStack? stack) => Lures != null ? stack != null && Lures(stack) : LivestockTreats.Eats(entity, stack);

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
