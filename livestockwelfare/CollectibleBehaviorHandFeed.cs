using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Common.Entities;

namespace livestockwelfare;

/// <summary>
/// Added in code to food and liquid containers: sneak + hold right click on a hungry lamb, kid or
/// piglet that cannot suckle feeds it by hand (see EntityBehaviorLivestockNursing.HandFeed). The baby
/// stands still and eats while the player holds the food out. Animals that take treats (chickens, see
/// TreatSettings) take a bit of their food the same way. Anywhere else the item works as usual
/// (eating, drinking, milking).
/// </summary>
public class CollectibleBehaviorHandFeed : CollectibleBehavior
{
    public const string Code = "livestockwelfare.handfeed";

    const float FeedSeconds = 1.5f;
    // The player holds the item out, like vanilla does for most "use on something" actions.
    const string PlayerAnimation = "interactstatic";
    const string BabyAnimation = "eat";

    public CollectibleBehaviorHandFeed(CollectibleObject collObj) : base(collObj) { }

    // The animal that would eat this from the hand now, or null.
    static Entity? Target(ItemSlot slot, EntityAgent byEntity, EntitySelection? entitySel)
    {
        if (!byEntity.Controls.ShiftKey || entitySel?.Entity is not { } entity) return null;
        if (entity.GetBehavior<EntityBehaviorLivestockNursing>() is { } nursing)
        {
            return nursing.WantsHandFeeding && nursing.CanHandFeedWith(slot.Itemstack) ? entity : null;
        }
        return LivestockTreats.TakesTreats(entity) && LivestockTreats.Eats(entity, slot.Itemstack) && LivestockTreats.Refusal(entity) == null ? entity : null;
    }

    // Fed by hand at all: an orphaned baby, or an animal that takes treats.
    static bool HandFed(EntitySelection? entitySel) =>
        entitySel?.Entity is { } entity && (entity.HasBehavior<EntityBehaviorLivestockNursing>() || LivestockTreats.TakesTreats(entity));

    // Food it would eat, but not now: the reason (a lang key).
    static string? Refusal(ItemSlot slot, EntitySelection? entitySel)
    {
        if (entitySel?.Entity is not { } entity) return null;
        if (entity.GetBehavior<EntityBehaviorLivestockNursing>() is { } baby)
        {
            return baby.CanHandFeedWith(slot.Itemstack) ? baby.HandFeedRefusal : null;
        }
        return LivestockTreats.TakesTreats(entity) && LivestockTreats.Eats(entity, slot.Itemstack) ? LivestockTreats.Refusal(entity) : null;
    }

    // The animal stands still and eats while being fed (server side).
    static void KeepEating(EntityAgent byEntity, EntitySelection entitySel)
    {
        if (byEntity.World.Side == EnumAppSide.Server)
        {
            entitySel.Entity.GetBehavior<EntityBehaviorLivestockHygiene>()?.HoldStill(1, BabyAnimation);
        }
    }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handHandling, ref EnumHandling handling)
    {
        if (!firstEvent) return;
        if (Target(slot, byEntity, entitySel) == null)
        {
            // Food it would eat, but not now: say why, instead of the player eating it.
            if (byEntity.Controls.ShiftKey && Refusal(slot, entitySel) is { } reason)
            {
                handHandling = EnumHandHandling.PreventDefaultAction;
                handling = EnumHandling.PreventSubsequent;
                (byEntity.World.Api as ICoreClientAPI)?.TriggerIngameError(this, "handfeed", Lang.Get(reason));
            }
            return;
        }

        // PreventDefaultAction (not PreventDefault): it is what stops a liquid container from being
        // drunk from instead.
        handHandling = EnumHandHandling.PreventDefaultAction;
        handling = EnumHandling.PreventSubsequent;
        KeepEating(byEntity, entitySel);
    }

    public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        if (!byEntity.Controls.ShiftKey || !HandFed(entitySel))
        {
            return true;
        }

        handling = EnumHandling.PreventSubsequent;
        if (Target(slot, byEntity, entitySel) == null) return false;
        KeepEating(byEntity, entitySel);
        return secondsUsed < FeedSeconds;
    }

    public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, ref EnumHandling handling)
    {
        if (!byEntity.Controls.ShiftKey || !HandFed(entitySel)) return;

        handling = EnumHandling.PreventSubsequent;
        if (byEntity.World.Side != EnumAppSide.Server) return;
        if (secondsUsed < FeedSeconds - 0.1f) return;
        if (Target(slot, byEntity, entitySel) is not { } entity) return;
        if (entity.GetBehavior<EntityBehaviorLivestockNursing>() is { } nursing) nursing.HandFeed(slot, byEntity);
        else LivestockTreats.HandFeed(entity, slot, byEntity);
    }

    // Food would otherwise show the player eating it.
    public override string GetHeldTpUseAnimation(ItemSlot activeHotbarSlot, Entity forEntity, ref EnumHandling bhHandling)
    {
        if (forEntity is EntityPlayer player && Target(activeHotbarSlot, player, player.Player?.CurrentEntitySelection) != null)
        {
            bhHandling = EnumHandling.PreventDefault;
            return PlayerAnimation;
        }
        return base.GetHeldTpUseAnimation(activeHotbarSlot, forEntity, ref bhHandling);
    }
}
