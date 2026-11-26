using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace livestockwelfare;

/// <summary>
/// Taming (see LivestockTaming): a wild animal fed by hand stays around the spot where it was first
/// fed, so the player can find it again the next day. Strayed too far (wandering, or after running
/// off), it walks back. Once tame it is the player's to lead away.
/// </summary>
public class AiTaskLivestockStayNearHome : AiTaskBase
{
    public const string Code = "livestockwelfare-staynearhome";

    readonly float moveSpeed;
    Vec3d? home;

    public AiTaskLivestockStayNearHome(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        moveSpeed = taskConfig["movespeed"].AsFloat(0.01f);
    }

    static int Radius => LivestockTreats.Settings?.TameHomeRadius ?? 16;

    public override bool ShouldExecute()
    {
        if (IsOnCooldown() || !PreconditionsSatisfied() || !LivestockTaming.IsTamable(entity)) return false;
        home = LivestockTaming.Home(entity);
        return home != null && entity.Pos.SquareDistanceTo(home) > Radius * Radius && LivestockUtil.ShouldSimulate(entity);
    }

    public override void StartExecute()
    {
        base.StartExecute();
        if (home == null) return;
        pathTraverser.NavigateTo(home, moveSpeed, Radius / 3f, () => { }, () => { }, null, giveUpWhenNoPath: true, 999, 1);
    }

    public override bool ContinueExecute(float dt) =>
        home != null && pathTraverser.Active && entity.Pos.SquareDistanceTo(home) > Radius * Radius / 9.0;

    public override void FinishExecute(bool cancelled)
    {
        base.FinishExecute(cancelled);
        pathTraverser.Stop();
    }
}
