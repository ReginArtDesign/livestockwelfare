using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// A thirsty animal walks to fresh water or a barrel/bucket of water and drinks there, the same way
/// vanilla animals walk to a trough to eat: head down ("eat" animation), splashes and a sound.
/// </summary>
public class AiTaskLivestockSeekWater : AiTaskLivestockGoTo
{
    public const string Code = "livestockwelfare-seekwater";

    const float DrinkSeconds = 2.5f;
    static readonly AssetLocation DrinkSound = new("game", "sounds/effect/water-fill");

    EntityBehaviorLivestockThirst? thirst;
    BlockEntityLiquidContainer? container;
    bool drinkStarted;
    static SimpleParticleProperties? splash;

    public AiTaskLivestockSeekWater(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    public override void AfterInitialize()
    {
        base.AfterInitialize();
        thirst = entity.GetBehavior<EntityBehaviorLivestockThirst>();
    }

    static ThirstSettings? Settings =>
        livestockwelfareModSystem.Config?.Thirst is { Enabled: true, WalkToWater: true } settings ? settings : null;

    protected override bool Wanted() =>
        thirst != null && Settings is { } settings && thirst.Thirst >= settings.SeekWaterAboveThirst && LivestockUtil.ShouldSimulate(entity);

    protected override BlockPos? FindTarget()
    {
        if (thirst == null || Settings is not { } settings) return null;
        var found = thirst.FindDrinkTarget(settings.SeekWaterRadius, IsFailed);
        container = found?.Container;
        return found?.Pos;
    }

    // A barrel is a whole block: count from its edge, not from its middle.
    protected override float ArriveDistance => container != null ? 1.1f : 1.3f;

    public override void StartExecute()
    {
        base.StartExecute();
        drinkStarted = false;
    }

    protected override bool ActAtTarget(float dt, float timeAtTarget)
    {
        if (thirst == null || Target == null) return false;

        if (!drinkStarted)
        {
            drinkStarted = true;
            StartAnimationIfExists("eat");
            entity.World.PlaySoundAt(DrinkSound, Target.X, Target.Y, Target.Z, null, randomizePitch: true, range: 12);
        }

        if (entity.World.Rand.NextDouble() < 0.3) SpawnSplash();

        if (timeAtTarget < DrinkSeconds) return true;
        thirst.DrinkFrom(container);
        return false;
    }

    protected override void OnFinish(bool arrivedAtTarget)
    {
        entity.AnimManager.StopAnimation("eat");
    }

    void SpawnSplash()
    {
        splash ??= new SimpleParticleProperties(
            1, 3, ColorUtil.ToRgba(180, 90, 140, 220),
            new Vec3d(), new Vec3d(),
            new Vec3f(-0.3f, 0.3f, -0.3f), new Vec3f(0.3f, 0.8f, 0.3f),
            0.6f, 1f, 0.08f, 0.15f, EnumParticleModel.Cube);

        // Fresh water: at the surface; a barrel: at its rim.
        double y = Target!.Y + (container != null ? 1 : 0.9);
        splash.MinPos.Set(Target.X - 0.3, y, Target.Z - 0.3);
        splash.AddPos.Set(0.6, 0.1, 0.6);
        entity.World.SpawnParticles(splash);
    }
}
