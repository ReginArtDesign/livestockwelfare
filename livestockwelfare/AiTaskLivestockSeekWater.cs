using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// A thirsty animal walks to fresh water or a barrel/bucket of water and drinks there, the same way
/// vanilla animals walk to a trough to eat: head down ("eat" animation), splashes and a sound.
/// At a chicken waterer it takes a few sips instead, dipping its beak into the bowl and lifting it.
/// </summary>
public class AiTaskLivestockSeekWater : AiTaskLivestockGoTo
{
    public const string Code = "livestockwelfare-seekwater";

    const float DrinkSeconds = 2.5f;
    // A sip at the waterer: beak down in the bowl, then up to swallow.
    const float SipSeconds = 1.2f, SipDownSeconds = 0.6f, WatererDrinkSeconds = 3 * SipSeconds;
    static readonly AssetLocation DrinkSound = new("game", "sounds/effect/water-fill");

    EntityBehaviorLivestockThirst? thirst;
    BlockEntityLiquidContainer? container;
    bool waterer;
    bool drinkStarted;
    bool headDown;
    static SimpleParticleProperties? splash, drops;

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
        waterer = found != null && entity.World.BlockAccessor.GetBlock(found.Value.Pos).Code is { Domain: "livestockwelfare" } code
            && code.Path.StartsWith("waterer");
        return found?.Pos;
    }

    // Fresh water: its surface, not the bottom of the water block. Measured from the bottom, an
    // animal on a bank above the water (often a small one, like a chicken) never got close enough.
    protected override Vec3d TargetPoint(BlockPos block) => block.ToVec3d().Add(0.5, container != null ? 0 : 0.9, 0.5);

    // A barrel is a whole block: count from its edge, not from its middle.
    protected override float ArriveDistance => container != null ? 1.1f : 1.3f;

    public override void StartExecute()
    {
        base.StartExecute();
        drinkStarted = false;
        headDown = false;
    }

    protected override bool ActAtTarget(float dt, float timeAtTarget)
    {
        if (thirst == null || Target == null) return false;

        if (waterer) Sip(timeAtTarget);
        else
        {
            if (!drinkStarted)
            {
                drinkStarted = true;
                StartAnimationIfExists("eat");
                entity.World.PlaySoundAt(DrinkSound, Target.X, Target.Y, Target.Z, null, randomizePitch: true, range: 12);
            }
            if (entity.World.Rand.NextDouble() < 0.3) SpawnSplash();
        }

        if (timeAtTarget < (waterer ? WatererDrinkSeconds : DrinkSeconds)) return true;
        thirst.DrinkFrom(container);
        return false;
    }

    protected override void OnFinish(bool arrivedAtTarget)
    {
        entity.AnimManager.StopAnimation("eat");
    }

    void Sip(float timeAtTarget)
    {
        bool down = timeAtTarget % SipSeconds < SipDownSeconds;
        if (down != headDown)
        {
            headDown = down;
            if (down)
            {
                StartAnimationIfExists("eat");
                entity.World.PlaySoundAt(DrinkSound, Target!.X, Target.Y, Target.Z, null, randomizePitch: true, range: 8, volume: 0.4f);
            }
            else entity.AnimManager.StopAnimation("eat");
        }
        if (down && entity.World.Rand.NextDouble() < 0.4) SpawnDrops();
    }

    // A few drops flicked up from the bowl, on the side where the bird drinks.
    void SpawnDrops()
    {
        drops ??= new SimpleParticleProperties(
            1, 2, ColorUtil.ToRgba(180, 90, 140, 220),
            new Vec3d(), new Vec3d(),
            new Vec3f(-0.15f, 0.25f, -0.15f), new Vec3f(0.15f, 0.6f, 0.15f),
            0.35f, 1f, 0.04f, 0.08f, EnumParticleModel.Cube);

        double dx = entity.Pos.X - Target!.X, dz = entity.Pos.Z - Target.Z;
        double len = System.Math.Sqrt(dx * dx + dz * dz);
        if (len < 0.01) { dx = 1; len = 1; }
        // Halfway between the bell and the rim, just above the water.
        const double ring = 0.34;
        drops.MinPos.Set(Target.X + dx / len * ring - 0.05, Target.Y + 0.12, Target.Z + dz / len * ring - 0.05);
        drops.AddPos.Set(0.1, 0.03, 0.1);
        entity.World.SpawnParticles(drops);
    }

    void SpawnSplash()
    {
        splash ??= new SimpleParticleProperties(
            1, 3, ColorUtil.ToRgba(180, 90, 140, 220),
            new Vec3d(), new Vec3d(),
            new Vec3f(-0.3f, 0.3f, -0.3f), new Vec3f(0.3f, 0.8f, 0.3f),
            0.6f, 1f, 0.08f, 0.15f, EnumParticleModel.Cube);

        // Fresh water: at the surface; a barrel: at its rim.
        double y = container != null ? Target!.Y + 1 : Target!.Y;
        splash.MinPos.Set(Target.X - 0.3, y, Target.Z - 0.3);
        splash.AddPos.Set(0.6, 0.1, 0.6);
        entity.World.SpawnParticles(splash);
    }
}
