using System;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// A kept dog near kept livestock runs at wild predators (DogSettings.ChaseCodes) that come
/// close and bites them until they die, flee out of range, or the dog gives up. When no predator
/// is about, it helps its people hunt (game a nearby player just wounded), or else chases wild crop
/// eaters (GardenPestCodes) away from the crops, without biting.
/// </summary>
public class AiTaskLivestockDogGuard : AiTaskBase
{
    public const string Code = "livestockwelfare-dogguard";

    const long SearchIntervalMs = 2000;
    const long RepathIntervalMs = 1000;
    const long BiteIntervalMs = 1500;
    const long GiveUpAfterMs = 30000;
    // How close (blocks, between the hitboxes) the dog has to be to bite.
    const double BiteReach = 1.2;
    // The chase ends when the predator is this many times the guard radius away.
    const float ChaseRangeFactor = 1.5f;

    readonly float moveSpeed;
    EntityBehaviorLivestockDog? dog;
    Entity? target;
    long lastSearchMs = -SearchIntervalMs;
    long lastRepathMs;
    long lastBiteMs;
    long startedMs;
    bool stuck;
    // Chasing a crop eater off: no biting, done once it is far enough from the crops.
    bool chaseOff;
    BlockPos? crop;

    public AiTaskLivestockDogGuard(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig)
    {
        moveSpeed = taskConfig["movespeed"].AsFloat(0.03f);
    }

    public override void AfterInitialize()
    {
        base.AfterInitialize();
        dog = entity.GetBehavior<EntityBehaviorLivestockDog>();
    }

    static DogSettings? Settings =>
        livestockwelfareModSystem.Config?.Dogs is { Enabled: true, AnyGuarding: true } settings ? settings : null;

    public override bool ShouldExecute()
    {
        long now = entity.World.ElapsedMilliseconds;
        if (now - lastSearchMs < SearchIntervalMs) return false;
        lastSearchMs = now;
        if (Settings is not { } settings || dog?.CanGuard != true || settings.GuardRadius <= 0) return false;

        float radius = settings.GuardRadius;
        chaseOff = false;
        crop = null;
        target = null;
        if (settings.GuardHerd && entity.World.GetNearestEntity(entity.Pos.XYZ, radius, 4, IsGuardedAnimal) != null)
        {
            target = entity.World.GetNearestEntity(entity.Pos.XYZ, radius, 4, e => EntityBehaviorLivestockDog.IsChaseTarget(e, settings));
        }
        // Helping a hunt: game one of its people nearby just wounded.
        if (target == null && settings.HelpHunt)
        {
            target = entity.World.GetNearestEntity(entity.Pos.XYZ, radius, 4, IsWoundedGame);
        }
        if (target == null && settings.GuardGarden)
        {
            // It keeps an ear on the garden from further off, even napping in the house.
            float gardenRadius = Math.Max(radius, settings.GardenWatchRadius);
            target = entity.World.GetNearestEntity(entity.Pos.XYZ, gardenRadius, 4, e =>
            {
                if (!EntityBehaviorLivestockDog.IsGardenPest(e, settings)) return false;
                crop = NearestCrop(e.Pos.AsBlockPos, settings.GardenCropRadius);
                return crop != null;
            });
            chaseOff = target != null;
        }
        return target != null;
    }

    BlockPos? NearestCrop(BlockPos pos, int r)
    {
        BlockPos? found = null;
        double best = double.MaxValue;
        entity.World.BlockAccessor.WalkBlocks(pos.AddCopy(-r, -2, -r), pos.AddCopy(r, 2, r), (block, x, y, z) =>
        {
            if (block is not BlockCrop) return;
            double d = pos.DistanceSqTo(x, y, z);
            if (d < best)
            {
                best = d;
                found = new BlockPos(x, y, z, pos.dimension);
            }
        });
        return found;
    }

    bool IsWoundedGame(Entity e)
    {
        if (e == entity || !e.Alive || e is EntityPlayer || LivestockUtil.IsKept(e) || Settings is not { } settings) return false;
        if (DogHunting.WoundedBy(e) is not { } uid || entity.World.PlayerByUid(uid)?.Entity is not { } hunter) return false;
        double near = settings.CompanyRadius * 2;
        return hunter.Pos.SquareDistanceTo(entity.Pos) <= near * near;
    }

    // Livestock of the player's, not other dogs.
    bool IsGuardedAnimal(Entity e) =>
        e != entity && e.Alive && e.HasBehavior<EntityBehaviorLivestockAge>() && !e.HasBehavior<EntityBehaviorLivestockDog>() && LivestockUtil.IsKept(e);

    public override void StartExecute()
    {
        base.StartExecute();
        startedMs = lastRepathMs = entity.World.ElapsedMilliseconds;
        lastBiteMs = 0;
        stuck = false;
        Navigate();
        // Herd predators get a growl from the barking already; the garden chase starts with one.
        if (chaseOff)
        {
            var growl = new AssetLocation("game", "sounds/creature/wolf/growl" + (1 + entity.World.Rand.Next(4)));
            entity.World.PlaySoundAt(growl, entity, null, randomizePitch: true, range: 32);
        }
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1} {2} {3} #{4}", entity.Code.Path, entity.EntityId,
            chaseOff ? "chases off the crops" : "goes for", target?.Code, target?.EntityId);
    }

    void Navigate()
    {
        if (target == null) return;
        stuck = false;
        pathTraverser.NavigateTo_Async(target.Pos.XYZ, moveSpeed, (float)BiteReach, () => { }, () => stuck = true, null, 1000, 1);
    }

    public override bool CanContinueExecute() => pathTraverser.Ready;

    public override bool ContinueExecute(float dt)
    {
        if (target == null || !target.Alive || Settings is not { } settings || dog?.CanGuard != true) return false;

        long now = entity.World.ElapsedMilliseconds;
        if (now - startedMs > GiveUpAfterMs) return false;
        float maxRange = (chaseOff ? Math.Max(settings.GuardRadius, settings.GardenWatchRadius) : settings.GuardRadius) * ChaseRangeFactor;
        if (entity.Pos.SquareDistanceTo(target.Pos) > maxRange * maxRange) return false;
        // Chased far enough from the crops: done.
        if (chaseOff && crop != null && target.Pos.SquareDistanceTo(crop.ToVec3d()) > (double)settings.GardenChaseOffDistance * settings.GardenChaseOffDistance) return false;

        double reach = entity.SelectionBox.ToDouble().Translate(entity.Pos.X, entity.Pos.Y, entity.Pos.Z)
            .ShortestDistanceFrom(target.Pos.XYZ.Add(0, target.SelectionBox.Y2 / 2, 0));
        if (reach <= BiteReach + target.SelectionBox.XSize / 2)
        {
            pathTraverser.Stop();
            double dx = target.Pos.X - entity.Pos.X, dz = target.Pos.Z - entity.Pos.Z;
            entity.Pos.Yaw = (float)Math.Atan2(dx, dz);
            if (!chaseOff && now - lastBiteMs >= BiteIntervalMs) Bite(settings, now);
            return true;
        }

        if (now - lastRepathMs >= RepathIntervalMs || !pathTraverser.Active)
        {
            if (stuck && now - lastRepathMs < RepathIntervalMs * 3) return false;
            lastRepathMs = now;
            if (!pathTraverser.NavigateTo(target.Pos.XYZ, moveSpeed, (float)BiteReach, () => { }, () => stuck = true, null, giveUpWhenNoPath: false, 500, 1))
            {
                stuck = true;
            }
        }
        return true;
    }

    void Bite(DogSettings settings, long now)
    {
        lastBiteMs = now;
        // Wolves and Wolf taming's dogs have "attack-withwindup"; Feral Kinship's tame wolf only "attack".
        if (!StartAnimationIfExists("attack-withwindup")) StartAnimationIfExists("attack");
        target!.ReceiveDamage(new DamageSource
        {
            Source = EnumDamageSource.Entity,
            SourceEntity = entity,
            Type = EnumDamageType.SlashingAttack,
            DamageTier = 2,
            KnockbackStrength = 1
        }, (float)settings.GuardBiteDamage * GlobalConstants.CreatureDamageModifier);
        var sound = new AssetLocation("game", "sounds/creature/wolf/attack" + (1 + entity.World.Rand.Next(8)));
        entity.World.PlaySoundAt(sound, entity, null, randomizePitch: true, range: 16);
    }

    public override void FinishExecute(bool cancelled)
    {
        base.FinishExecute(cancelled);
        pathTraverser.Stop();
        entity.World.Logger.Debug("[livestockwelfare] {0} #{1} stops guarding (target {2}, alive {3})", entity.Code.Path, entity.EntityId, target?.Code, target?.Alive);
        target = null;
    }

    bool StartAnimationIfExists(string code)
    {
        var animations = entity.Properties.Client?.Animations;
        if (animations == null) return false;
        foreach (var anim in animations)
        {
            if (anim.Code == code)
            {
                entity.AnimManager.StartAnimation(anim);
                return true;
            }
        }
        return false;
    }
}
