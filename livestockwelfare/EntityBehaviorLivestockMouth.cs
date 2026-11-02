using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace livestockwelfare;

/// <summary>
/// Lets a baby hold food it was fed by hand in its mouth while it chews, like vanilla raccoons hold
/// honeycomb: the item sits in the entity's left hand slot, which the game draws at the shape's
/// "LeftHand" attachment point (patched onto the baby shapes' jaw). Synced through WatchedAttributes.
/// </summary>
public class EntityBehaviorLivestockMouth : EntityBehavior
{
    public const string Code = "livestockwelfare.mouth";

    const string MouthAttr = "lwMouth";

    readonly InventoryGeneric inventory;
    // Server side: when the food is eaten up.
    long emptyAtMs;

    public EntityBehaviorLivestockMouth(Entity entity) : base(entity)
    {
        inventory = new InventoryGeneric(1, "lwmouth-" + entity.EntityId, entity.Api, (_, inv) => new ItemSlotBabyMouth(inv));
        if (entity is EntityAgent agent) agent.LeftHandItemSlot = inventory[0];
    }

    public override string PropertyName() => Code;

    public override void Initialize(EntityProperties properties, JsonObject attributes)
    {
        base.Initialize(properties, attributes);
        Load();
        if (entity.World.Side == EnumAppSide.Client) entity.WatchedAttributes.RegisterModifiedListener(MouthAttr, Load);
    }

    void Load()
    {
        if (entity.WatchedAttributes.GetTreeAttribute(MouthAttr) is { } tree) inventory.FromTreeAttributes(tree);
        inventory.ResolveBlocksOrItems();
    }

    /// <summary>Puts one of the item in the mouth for this long (server side).</summary>
    public void Hold(ItemStack stack, float seconds)
    {
        var item = stack.Clone();
        item.StackSize = 1;
        inventory[0].Itemstack = item;
        emptyAtMs = entity.World.ElapsedMilliseconds + (long)(seconds * 1000);
        Save();
    }

    void Save()
    {
        var tree = new TreeAttribute();
        inventory.ToTreeAttributes(tree);
        entity.WatchedAttributes[MouthAttr] = tree;
        entity.WatchedAttributes.MarkPathDirty(MouthAttr);
    }

    public override void OnGameTick(float deltaTime)
    {
        if (entity.World.Side != EnumAppSide.Server || inventory[0].Empty) return;
        // Eaten up; also after a restart (ElapsedMilliseconds starts over, emptyAtMs is 0).
        if (entity.World.ElapsedMilliseconds >= emptyAtMs || !entity.Alive)
        {
            inventory[0].Itemstack = null;
            Save();
        }
    }
}

/// <summary>The baby's mouth: draws the food small and lying across the jaw.</summary>
public class ItemSlotBabyMouth : ItemSlot
{
    const float MouthScale = 0.35f;
    static readonly FastVec3f MouthOrigin = new(0.5f, 0.1f, 0.5f);

    // The renderer scales the translation too and adds the origin unscaled, so the item's middle
    // ends up at origin + scale * translation from the jaw: -origin / scale puts it on the jaw.
    // (A translation of 0 left the food floating about a block above the baby's head.)
    static readonly ModelTransform MouthTransform = new()
    {
        Translation = new FastVec3f(-MouthOrigin.X / MouthScale, -MouthOrigin.Y / MouthScale, -MouthOrigin.Z / MouthScale),
        Rotation = new FastVec3f(0, 0, 0),
        Origin = MouthOrigin,
        Scale = MouthScale
    };

    public ItemSlotBabyMouth(InventoryBase inventory) : base(inventory) { }

    public override void OnBeforeRender(ItemRenderInfo renderInfo)
    {
        renderInfo.Transform = MouthTransform;
    }
}
