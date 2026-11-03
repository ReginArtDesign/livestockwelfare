using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace livestockwelfare;

/// <summary>
/// Lets an item (dry grass, patched in JSON) be used as a grooming wisp: sneak + hold right click
/// on a livestock animal rubs off some of its dirt; it lies down meanwhile. Without sneaking the item works as usual
/// (e.g. feeding), so this does not get in the way.
/// </summary>
public class CollectibleBehaviorGroom : CollectibleBehavior
{
    public const string Code = "livestockwelfare.groom";

    // Vanilla butchering animation: a back-and-forth arm motion that loops while held.
    const string PlayerAnimation = "knifecut";
    static readonly AssetLocation Sound = new("game", "sounds/block/plant");
    // The animal lies down while groomed, and stays down this long after the last stroke so the
    // player can start the next one; it gets up as soon as it is clean.
    const float LieDownSeconds = 8;

    float groomSeconds = 2;
    SimpleParticleProperties? dust;

    public CollectibleBehaviorGroom(CollectibleObject collObj) : base(collObj) { }

    public override void Initialize(JsonObject properties)
    {
        base.Initialize(properties);
        // Read on both sides from the patch, so client and server agree when grooming is done.
        groomSeconds = properties["groomSeconds"].AsFloat(2);
    }

    // Babies and chickens keep clean without help, so dry grass works as usual on them (e.g. feeding).
    static EntityBehaviorLivestockHygiene? Target(EntitySelection? entitySel) =>
        entitySel?.Entity is { Alive: true } e && e.GetBehavior<EntityBehaviorLivestockHygiene>() is { CanBeGroomed: true } hygiene ? hygiene : null;

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (!firstEvent || !byEntity.Controls.ShiftKey || Target(entitySel) is not { } hygiene) return;

        // Take over the click, so the animal is not fed or petted instead.
        handHandling = EnumHandHandling.PreventDefault;
        handling = EnumHandling.PreventSubsequent;
        byEntity.World.PlaySoundAt(Sound, entitySel.Entity, (byEntity as EntityPlayer)?.Player, randomizePitch: true, range: 12);
        if (byEntity.World.Side == EnumAppSide.Server) hygiene.HoldStill(LieDownSeconds, EntityBehaviorLivestockHygiene.RestAnimation);
    }

    public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        if (Target(entitySel) is not { } hygiene || !byEntity.Controls.ShiftKey) return true;

        handling = EnumHandling.PreventSubsequent;
        if (byEntity.World.Side == EnumAppSide.Client) SpawnDust(byEntity.World, entitySel.Entity);
        else hygiene.HoldStill(LieDownSeconds, EntityBehaviorLivestockHygiene.RestAnimation);
        return secondsUsed < groomSeconds;
    }

    public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        var hygiene = Target(entitySel);
        if (hygiene == null) return;

        handling = EnumHandling.PreventSubsequent;

        if (byEntity.World.Side != EnumAppSide.Server || secondsUsed < groomSeconds - 0.1f) return;
        var settings = livestockwelfareModSystem.Config?.Hygiene;
        if (settings is not { Enabled: true }) return;

        bool groomed = hygiene.Groom(settings.GroomFilthReduction);
        if (hygiene.Filth <= 0) hygiene.StopHoldingStill();
        if (!groomed) return;

        var player = (byEntity as EntityPlayer)?.Player as IServerPlayer;
        bool creative = player?.WorldData.CurrentGameMode == EnumGameMode.Creative;
        if (!creative && byEntity.World.Rand.NextDouble() < settings.GroomItemUseChance)
        {
            slot.TakeOut(1);
            slot.MarkDirty();
        }
        byEntity.World.PlaySoundAt(Sound, entitySel.Entity, null, randomizePitch: true, range: 12);
    }

    // The game plays the item's "use" animation while the right mouse button is held; for dry grass
    // that is a short twitch, so swap in a scrubbing motion while grooming an animal.
    public override string GetHeldTpUseAnimation(ItemSlot activeHotbarSlot, Entity forEntity, ref EnumHandling bhHandling)
    {
        if (forEntity is EntityPlayer player && player.Controls.ShiftKey && Target(player.Player?.CurrentEntitySelection) != null)
        {
            bhHandling = EnumHandling.PreventDefault;
            return PlayerAnimation;
        }
        return base.GetHeldTpUseAnimation(activeHotbarSlot, forEntity, ref bhHandling);
    }

    // Bits of dirt and straw falling off the animal's back.
    void SpawnDust(IWorldAccessor world, Entity animal)
    {
        if (world.Rand.NextDouble() > 0.3) return;

        dust ??= new SimpleParticleProperties(
            2, 4, ColorUtil.ToRgba(200, 110, 85, 50),
            new Vec3d(), new Vec3d(),
            new Vec3f(-0.4f, 0.1f, -0.4f), new Vec3f(0.4f, 0.6f, 0.4f),
            1.2f, 1f, 0.15f, 0.35f, EnumParticleModel.Cube);

        var box = animal.SelectionBox;
        var pos = animal.Pos;
        dust.MinPos.Set(pos.X + box.X1, pos.Y + box.Y2 * 0.6, pos.Z + box.Z1);
        dust.AddPos.Set(box.Width, box.Height * 0.4, box.Length);
        world.SpawnParticles(dust);
    }

    public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot, ref EnumHandling handling)
    {
        return new[]
        {
            new WorldInteraction
            {
                ActionLangCode = "livestockwelfare:heldhelp-groom",
                MouseButton = EnumMouseButton.Right,
                HotKeyCode = "shift"
            }
        };
    }
}
