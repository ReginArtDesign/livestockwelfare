using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// Straw bedding for breeding (see LivestockBedding). A kept female that is ready to mate walks to
/// clean bedding under a roof and lies down; a male nearby follows and lies down next to her, and
/// after a while together she can get pregnant. A pregnant female spends her time on the bedding,
/// going out only to eat and drink (both may interrupt her rest), and sleeps there at night.
/// </summary>
public class AiTaskLivestockBedding : AiTaskLivestockGoTo
{
    public const string Code = "livestockwelfare-bedding";

    // On the female: the male she waits for, and her bedding, so he knows where to go.
    const string MateIdAttr = "lwMateId";
    const string MatingBedAttr = "lwMatingBed";

    // A pair that never gets together (he can't reach her, she gets no pregnancy) gives up after this.
    const float MaxMatingSeconds = 240;
    // Re-checks how long she has lain on her own before he arrives.
    const float MaxWaitForMaleSeconds = 90;

    // Newborn: a mother staying on her bedding with her young after the birth; Baby: one of them.
    enum Role { None, Mating, Pregnant, Male, Newborn, Baby }

    Role role;
    EntityBehaviorMultiply? multiply;
    EntityBehaviorLivestockShelter? shelter;
    bool isBaby;
    // The mate, or for a baby its mother.
    Entity? partner;
    float togetherSeconds;
    string? lyingAnimation;

    public AiTaskLivestockBedding(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    public override void AfterInitialize()
    {
        base.AfterInitialize();
        multiply = entity.GetBehavior<EntityBehaviorMultiply>();
        shelter = entity.GetBehavior<EntityBehaviorLivestockShelter>();
        isBaby = entity.GetBehavior<EntityBehaviorLivestockNursing>() != null;
        // For the test world log: which task got her up from the bedding.
        var taskManager = entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
        if (taskManager != null) taskManager.OnTaskStarted += task =>
        {
            if (interruptedAtMs == entity.World.ElapsedMilliseconds && task != this)
                Log("interrupted by {0}", AiTaskRegistry.TaskCodes.TryGetValue(task.GetType(), out var code) ? code : task.GetType().Name);
        };
    }

    long interruptedAtMs = -1;

    static BreedingSettings? Settings => LivestockBedding.Settings;

    bool Pregnant => multiply?.IsPregnant == true;

    // Which part this animal plays right now, or None.
    Role WantedRole()
    {
        if (Settings == null || !LivestockBedding.Applies(entity) || !LivestockUtil.ShouldSimulate(entity)) return Role.None;
        if (multiply != null)
        {
            if (Pregnant) return Role.Pregnant;
            if (MyNewbornBed() != null) return Role.Newborn;
            // A male nearby is needed to set off; once on the way she keeps her choice.
            if (LivestockBedding.ReadyToMate(multiply) && (role == Role.Mating || LivestockBedding.FindMale(multiply) != null)) return Role.Mating;
            return Role.None;
        }
        if (isBaby) return NewbornMother() != null ? Role.Baby : Role.None;
        return FemaleWaitingForMe() != null ? Role.Male : Role.None;
    }

    // After the birth: her bedding (now soiled), while it is still there and roofed.
    BlockPos? MyNewbornBed()
    {
        var bed = LivestockBedding.NewbornBed(entity);
        var accessor = entity.World.BlockAccessor;
        return bed != null && LivestockBedding.IsAnyBedding(accessor.GetBlock(bed)) && accessor.GetRainMapHeightAt(bed) > bed.Y ? bed : null;
    }

    // A baby's mother in the first days after the birth (Breeding.NewbornDays): it stays with her.
    Entity? NewbornMother()
    {
        var mother = EntityBehaviorLivestockNursing.MotherOf(entity);
        if (mother == null || !mother.Alive || Settings is not { } settings) return null;
        if (mother.Pos.SquareDistanceTo(entity.Pos) > settings.BeddingRadius * settings.BeddingRadius) return null;
        return LivestockBedding.NewbornBed(mother) != null ? mother : null;
    }

    // Lying on her bedding with her newborns.
    static bool Nursing(Entity mother) =>
        mother.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager?.GetTask<AiTaskLivestockBedding>()?.NursingNewborns == true;

    // A newborn walking after its mother (to the trough and back) instead of lying next to her.
    bool following;
    // How far a newborn lets its mother get before it goes after her.
    const double FollowDistance = 2;

    // A female on her bedding who has picked this male.
    Entity? FemaleWaitingForMe()
    {
        var settings = Settings;
        if (settings == null) return null;
        return entity.World.GetNearestEntity(entity.Pos.XYZ, settings.BeddingRadius, 4, e =>
            e != entity && e.Alive && e.Attributes.GetLong(MateIdAttr) == entity.EntityId && e.Attributes.HasAttribute(MatingBedAttr + "X"));
    }

    protected override bool Wanted()
    {
        // On her way to food or water she is let go, and comes back after. (Otherwise the higher
        // priority of this task called her straight back, every time, and she never drank.)
        if (GoingForFoodOrWater()) return false;
        var wanted = WantedRole();
        // Ready but no bedding: noted for the info text, so the player knows what is missing.
        if (wanted == Role.None && role == Role.None) SetNeedsBedding(false);
        return wanted != Role.None && (role == Role.None || wanted == role);
    }

    /// <summary>Lying on her bedding, or a newborn next to its mother (not the male, who only visits).</summary>
    public bool Resting => Arrived && role is Role.Mating or Role.Pregnant or Role.Newborn or Role.Baby && !following && IsActive;

    /// <summary>A mother lying on her bedding with her newborns: they come and lie next to her.</summary>
    public bool NursingNewborns => role == Role.Newborn && Resting;

    bool GoingForFoodOrWater() =>
        entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager?.ActiveTasksBySlot
            .Any(task => task != null && task != this && AiTaskLivestockSeekShelter.IsFoodOrWater(task)) == true;

    void SetNeedsBedding(bool needs)
    {
        if (entity.WatchedAttributes.GetBool(LivestockBedding.NeedsBeddingAttr) == needs) return;
        entity.WatchedAttributes.SetBool(LivestockBedding.NeedsBeddingAttr, needs);
    }

    bool Night => shelter?.Night == true;

    // Above the shelter task (1.385), so a pregnant female sleeps on her bedding (which is roofed)
    // and not just anywhere under the roof. Fleeing (1.39 and up) still wins.
    const float GoPriority = 1.386f;
    // Resting on the bedding while pregnant in daytime: eating and drinking (1.25 and up) may interrupt.
    // (Vanilla idle animations are held back meanwhile, see EntityBehaviorLivestockShelter.)
    const float StayPriorityForCancel = 1.24f;

    public override float Priority
    {
        get => WantedRole() != Role.None ? GoPriority : base.Priority;
        set => base.Priority = value;
    }

    public override float PriorityForCancel =>
        Arrived && role is Role.Pregnant or Role.Newborn && !Night ? StayPriorityForCancel : GoPriority;

    bool BesideHer => role is Role.Male or Role.Baby;

    // A newborn looks again soon, so it doesn't fall behind its mother.
    protected override long SearchIntervalMs => isBaby ? 1000 : 10000;

    protected override float ArriveDistance => BesideHer ? 1.2f : 0.3f;

    // The female lies on the bedding itself; the male or her young next to her is enough.
    protected override bool IsAtTarget(double hitboxDistance) =>
        BesideHer
            ? hitboxDistance <= 1.2
            : Target != null && entity.Pos.HorDistanceTo(Target) <= 0.45 && Math.Abs(entity.Pos.Y - Target.Y) < 1.5;

    protected override BlockPos? FindTarget()
    {
        role = WantedRole();
        partner = null;
        if (role == Role.Male)
        {
            partner = FemaleWaitingForMe();
            return partner == null ? null : BedOf(partner);
        }
        following = false;
        if (role == Role.Baby)
        {
            partner = NewbornMother();
            if (partner == null) return null;
            if (Nursing(partner)) return LivestockBedding.NewbornBed(partner);
            // She is up: stay close to her, and only walk when she gets too far.
            if (partner.Pos.HorDistanceTo(entity.Pos) <= FollowDistance) return null;
            following = true;
            return partner.Pos.AsBlockPos;
        }
        if (role == Role.Newborn) return MyNewbornBed();

        // Her own bedding from before, if it is still clean: with several next to each other she
        // otherwise took the nearest one each time she got up, and kept shifting about.
        var bed = role == Role.None ? null : OwnBed() ?? FindBedding();
        if (bed != null) RememberBed(bed);
        SetNeedsBedding(role != Role.None && bed == null);
        if (role == Role.Mating && bed != null && multiply != null) CallMale(LivestockBedding.FindMale(multiply), bed);
        return bed;
    }

    // Tells the male where she is going, so he follows her there.
    void CallMale(Entity? male, BlockPos bed)
    {
        partner = male;
        if (male == null) return;
        entity.Attributes.SetLong(MateIdAttr, male.EntityId);
        entity.Attributes.SetInt(MatingBedAttr + "X", bed.X);
        entity.Attributes.SetInt(MatingBedAttr + "Y", bed.Y);
        entity.Attributes.SetInt(MatingBedAttr + "Z", bed.Z);
    }

    static BlockPos BedOf(Entity female) => new(
        female.Attributes.GetInt(MatingBedAttr + "X"), female.Attributes.GetInt(MatingBedAttr + "Y"), female.Attributes.GetInt(MatingBedAttr + "Z"),
        female.Pos.Dimension);

    const string OwnBedAttr = "lwOwnBed";

    BlockPos? OwnBed()
    {
        var attrs = entity.Attributes;
        if (!attrs.HasAttribute(OwnBedAttr + "X")) return null;
        var bed = new BlockPos(attrs.GetInt(OwnBedAttr + "X"), attrs.GetInt(OwnBedAttr + "Y"), attrs.GetInt(OwnBedAttr + "Z"), entity.Pos.Dimension);
        if (LivestockBedding.IsUsableBedding(entity.World.BlockAccessor, bed) && !Taken(bed)
            && bed.DistanceSqTo(entity.Pos.X, entity.Pos.Y, entity.Pos.Z) <= Settings!.BeddingRadius * Settings.BeddingRadius) return bed;
        attrs.RemoveAttribute(OwnBedAttr + "X");
        return null;
    }

    void RememberBed(BlockPos bed)
    {
        entity.Attributes.SetInt(OwnBedAttr + "X", bed.X);
        entity.Attributes.SetInt(OwnBedAttr + "Y", bed.Y);
        entity.Attributes.SetInt(OwnBedAttr + "Z", bed.Z);
    }

    // The nearest clean, roofed bedding that no other female lies on.
    BlockPos? FindBedding()
    {
        if (Settings is not { } settings) return null;
        int r = settings.BeddingRadius;
        var here = entity.Pos.AsBlockPos;
        var accessor = entity.World.BlockAccessor;
        BlockPos? best = null;
        double bestDistance = double.MaxValue;
        accessor.WalkBlocks(here.AddCopy(-r, -3, -r), here.AddCopy(r, 3, r), (block, x, y, z) =>
        {
            if (!LivestockBedding.IsCleanBedding(block)) return;
            var pos = new BlockPos(x, y, z, here.dimension);
            if (accessor.GetRainMapHeightAt(pos) <= y || IsFailed(pos) || Taken(pos)) return;
            double distance = here.DistanceSqTo(x, y, z);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = pos;
            }
        });
        return best;
    }

    bool Taken(BlockPos pos) =>
        entity.World.GetEntitiesAround(pos.ToVec3d().Add(0.5, 0, 0.5), 0.8f, 1,
            e => e != entity && e.Alive && e.GetBehavior<EntityBehaviorMultiply>() != null).Length > 0;

    public override void StartExecute()
    {
        base.StartExecute();
        togetherSeconds = 0;
        lyingAnimation = null;
    }

    protected override bool ActAtTarget(float dt, float timeAtTarget)
    {
        // Its bedding was soiled or taken away in the meantime (after the birth soiled is fine).
        var accessor = entity.World.BlockAccessor;
        if (role is Role.Mating or Role.Pregnant && TargetBlock != null && !LivestockBedding.IsUsableBedding(accessor, TargetBlock))
        {
            Log("left: bedding {0} no longer usable ({1})", TargetBlock, accessor.GetBlock(TargetBlock).Code);
            return false;
        }
        if (role is Role.Newborn or Role.Baby && !following && TargetBlock != null && !LivestockBedding.IsAnyBedding(accessor.GetBlock(TargetBlock)))
        {
            Log("left: bedding {0} gone ({1})", TargetBlock, accessor.GetBlock(TargetBlock).Code);
            return false;
        }

        // Caught up with her: done, and it looks again in a second.
        if (following) return false;

        LieDown(Night && role != Role.Mating ? "sleep" : EntityBehaviorLivestockHygiene.RestAnimation);
        bool stay = role switch
        {
            Role.Mating => ActMating(dt, timeAtTarget),
            Role.Pregnant => Pregnant,
            Role.Male => ActMale(timeAtTarget),
            Role.Newborn => MyNewbornBed() != null,
            // While she lies there; when she gets up it follows her (above), and comes back with her.
            Role.Baby => partner != null && partner.Alive && Nursing(partner),
            _ => false
        };
        if (!stay) Log("left: {0} done", role);
        return stay;
    }

    void LieDown(string animation)
    {
        long now = entity.World.ElapsedMilliseconds;
        if (lyingAnimation == animation)
        {
            // Something else may have stopped it; and a player who came along just as she lay
            // down (e.g. joining the game) could miss it and see her standing, so send it again now and then.
            if (!entity.AnimManager.IsAnimationActive(animation)) StartAnimationIfExists(animation);
            else if (now - lyingSentMs > ResendAnimationMs) entity.AnimManager.AnimationsDirty = true;
            else return;
            lyingSentMs = now;
            return;
        }
        if (lyingAnimation != null) entity.AnimManager.StopAnimation(lyingAnimation);
        lyingAnimation = animation;
        lyingSentMs = now;
        (entity as EntityAgent)?.Controls.StopAllMovement();
        StartAnimationIfExists(animation);
    }

    const long ResendAnimationMs = 10000;
    long lyingSentMs;

    bool ActMating(float dt, float timeAtTarget)
    {
        if (multiply == null || TargetBlock == null || Settings is not { } settings) return false;
        // Pregnant now: the task goes on as a pregnancy on the same bedding.
        if (Pregnant)
        {
            role = Role.Pregnant;
            ClearMatingCall();
            return true;
        }
        if (timeAtTarget > MaxMatingSeconds) return false;

        if (partner == null || !partner.Alive)
        {
            CallMale(LivestockBedding.FindMale(multiply), TargetBlock);
            if (partner == null) return false;
        }

        if (partner.Pos.DistanceTo(entity.Pos) <= 2.2)
        {
            togetherSeconds += dt;
            if (togetherSeconds >= settings.MatingSeconds && !LivestockBedding.RecentlyMated(entity))
            {
                LivestockBedding.MarkMated(entity);
                entity.World.Logger.Debug("[livestockwelfare] {0} #{1} mated with #{2} on the bedding at {3}", entity.Code.Path, entity.EntityId, partner.EntityId, TargetBlock);
            }
        }
        else if (togetherSeconds <= 0 && timeAtTarget > MaxWaitForMaleSeconds)
        {
            return false;
        }
        return true;
    }

    // He stays with her until she is pregnant or gives up.
    bool ActMale(float timeAtTarget)
    {
        if (partner == null || !partner.Alive || timeAtTarget > MaxMatingSeconds) return false;
        return partner.Attributes.GetLong(MateIdAttr) == entity.EntityId && partner.GetBehavior<EntityBehaviorMultiply>()?.IsPregnant == false;
    }

    void ClearMatingCall()
    {
        entity.Attributes.RemoveAttribute(MateIdAttr);
        entity.Attributes.RemoveAttribute(MatingBedAttr + "X");
        entity.Attributes.RemoveAttribute(MatingBedAttr + "Y");
        entity.Attributes.RemoveAttribute(MatingBedAttr + "Z");
    }

    public override void FinishExecute(bool cancelled)
    {
        if (cancelled) interruptedAtMs = entity.World.ElapsedMilliseconds;
        base.FinishExecute(cancelled);
    }

    protected override void OnFinish(bool arrivedAtTarget)
    {
        if (lyingAnimation != null) entity.AnimManager.StopAnimation(lyingAnimation);
        lyingAnimation = null;
        if (role != Role.Male) ClearMatingCall();
        role = Role.None;
        partner = null;
        following = false;
    }
}
