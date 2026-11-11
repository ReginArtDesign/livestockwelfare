using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// A chicken that is due for a dust bath walks to dry sand nearby, sits down in it and fluffs
/// about in a cloud of sand for a while, and gets up clean.
/// </summary>
public class AiTaskLivestockDustBath : AiTaskLivestockGoTo
{
    public const string Code = "livestockwelfare-dustbath";

    const string SitAnimation = "sit";
    const float SoundIntervalSeconds = 2.5f;
    static readonly AssetLocation[] Sounds =
    {
        new("game", "sounds/walk/sand1"), new("game", "sounds/walk/sand2"), new("game", "sounds/walk/sand3")
    };

    EntityBehaviorLivestockHygiene? hygiene;
    bool bathStarted;
    float nextSound;
    static SimpleParticleProperties? sand;

    public AiTaskLivestockDustBath(EntityAgent entity, JsonObject taskConfig, JsonObject aiConfig)
        : base(entity, taskConfig, aiConfig) { }

    public override void AfterInitialize()
    {
        base.AfterInitialize();
        hygiene = entity.GetBehavior<EntityBehaviorLivestockHygiene>();
        // Vanilla hens run back to the rooster once more than 5 blocks away, and that task (1.3)
        // outranks the bath: with the sand further from him she turned back halfway, again and
        // again, and never bathed. While she goes for her bath, he can wait.
        var taskManager = entity.GetBehavior<EntityBehaviorTaskAI>()?.TaskManager;
        if (taskManager != null) taskManager.OnShouldExecuteTask += task => !(running && task is AiTaskStayCloseToEntity);
    }

    bool running;

    static HygieneSettings? Settings =>
        livestockwelfareModSystem.Config?.Hygiene is { Enabled: true, DustBathing: true } settings ? settings : null;

    protected override bool Wanted() =>
        hygiene != null && Settings is { } settings && hygiene.WantsDustBath(settings) && LivestockUtil.ShouldSimulate(entity);

    protected override BlockPos? FindTarget() =>
        hygiene != null && Settings is { } settings ? hygiene.FindDustBath(settings, pos => IsFailed(pos) || Taken(pos)) : null;

    // Another bird already on that sand: take the next one, so they spread out over the patch.
    bool Taken(BlockPos sand) =>
        entity.World.GetEntitiesAround(new Vec3d(sand.X + 0.5, sand.Y + 1, sand.Z + 0.5), 0.6f, 1f, e => e != entity && e.Alive).Length > 0;

    // On top of the sand, not next to it.
    protected override Vec3d TargetPoint(BlockPos block) => block.ToVec3d().Add(0.5, 1, 0.5);

    protected override float ArriveDistance => 0.35f;

    // Its feet on the sand, near the middle: measured from the hitbox, a bird at the edge (or on a
    // waterer next to it) already counted as there.
    protected override bool IsAtTarget(double hitboxDistance)
    {
        if (TargetBlock == null || Target == null) return false;
        var pos = entity.Pos;
        double dx = pos.X - Target.X, dz = pos.Z - Target.Z;
        return dx * dx + dz * dz < 0.3 * 0.3 && (int)System.Math.Floor(pos.Y - 0.05) == TargetBlock.Y && entity.OnGround;
    }

    public override void StartExecute()
    {
        base.StartExecute();
        bathStarted = false;
        running = true;
    }

    protected override bool ActAtTarget(float dt, float timeAtTarget)
    {
        if (hygiene == null || Target == null || Settings is not { } settings) return false;

        if (!bathStarted)
        {
            bathStarted = true;
            nextSound = 0;
            // Vanilla chickens list no settings for "sit", so start it from the shape directly.
            if (!entity.AnimManager.StartAnimation(SitAnimation))
            {
                entity.AnimManager.StartAnimation(new AnimationMetaData { Code = SitAnimation, Animation = SitAnimation }.Init());
            }
        }

        if (timeAtTarget >= nextSound)
        {
            nextSound = timeAtTarget + SoundIntervalSeconds;
            var sound = Sounds[entity.World.Rand.Next(Sounds.Length)];
            entity.World.PlaySoundAt(sound, entity, null, randomizePitch: true, range: 10);
        }
        if (entity.World.Rand.NextDouble() < 0.5) SpawnSand();

        if (timeAtTarget < settings.DustBathSeconds) return true;
        hygiene.DustBathed();
        return false;
    }

    protected override void OnFinish(bool arrivedAtTarget)
    {
        running = false;
        entity.AnimManager.StopAnimation(SitAnimation);
    }

    // Sand flung up around the bird.
    void SpawnSand()
    {
        sand ??= new SimpleParticleProperties(
            2, 5, ColorUtil.ToRgba(220, 205, 180, 130),
            new Vec3d(), new Vec3d(),
            new Vec3f(-0.6f, 0.4f, -0.6f), new Vec3f(0.6f, 1.2f, 0.6f),
            0.8f, 1f, 0.1f, 0.25f, EnumParticleModel.Cube);

        var pos = entity.Pos;
        sand.MinPos.Set(pos.X - 0.3, pos.Y + 0.05, pos.Z - 0.3);
        sand.AddPos.Set(0.6, 0.2, 0.6);
        entity.World.SpawnParticles(sand);
    }
}
