using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace livestockwelfare;

/// <summary>
/// A chicken coop kit: right-clicked on the ground, it builds a small raised coop out of ordinary
/// blocks: on four posts, with a plank floor, a pitched roof of plank stairs, a pop hole the hens
/// reach up a little ramp, two hen boxes inside and a gate at the back for collecting the eggs.
/// Being ordinary blocks, the roof, muck and laying all work as in a coop built by hand, and it can
/// be rebuilt or taken down. The recipe costs about the planks it is made of.
/// </summary>
public class ItemChickenCoop : Item
{
    public const string ClassName = "livestockwelfare.ItemChickenCoop";

    // Outside size of the coop itself: Width across the pop hole, Depth away from it. The ramp
    // leads up to it from the clicked spot, RampLength blocks in front; the same steps lead up to
    // the gate at the back.
    public const int Width = 5, Depth = 5, RampLength = 2;

    enum Part { Air, Planks, Stairs, Slab, Post, Gate, HenBox }

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        if (blockSel == null || !firstEvent)
        {
            base.OnHeldInteractStart(slot, byEntity, blockSel, entitySel, firstEvent, ref handling);
            return;
        }
        handling = EnumHandHandling.PreventDefault;
        if (api.Side != EnumAppSide.Server || (byEntity as EntityPlayer)?.Player is not IServerPlayer player) return;

        if (blockSel.Face != BlockFacing.UP)
        {
            Fail(player, "coop-onground");
            return;
        }

        // The foot of the ramp goes on the clicked spot, the coop stretches away from the player.
        var rampFoot = blockSel.Position.UpCopy();
        double dx = rampFoot.X + 0.5 - byEntity.Pos.X, dz = rampFoot.Z + 0.5 - byEntity.Pos.Z;
        var forward = Math.Abs(dx) > Math.Abs(dz) ? new Vec3i(Math.Sign(dx), 0, 0) : new Vec3i(0, 0, dz < 0 ? -1 : 1);

        string? error = TryBuild(api.World, rampFoot, forward, Variant["wood"]);
        if (error != null)
        {
            Fail(player, error);
            return;
        }

        api.World.PlaySoundAt(new AssetLocation("game", "sounds/block/planks"), rampFoot.X + 0.5, rampFoot.Y, rampFoot.Z + 0.5, null, randomizePitch: true, range: 16);
        if (player.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            slot.TakeOut(1);
            slot.MarkDirty();
        }
    }

    static void Fail(IServerPlayer player, string key) =>
        player.SendIngameError(key, LivestockUtil.Translate(player.LanguageCode, LivestockUtil.Domain + ":" + key));

    /// <summary>
    /// Builds the coop with the foot of its ramp at <paramref name="rampFoot"/>, stretching away along
    /// <paramref name="forward"/>. Returns null when built, or the lang key (without domain) of why not.
    /// </summary>
    public static string? TryBuild(IWorldAccessor world, BlockPos rampFoot, Vec3i forward, string wood)
    {
        var accessor = world.BlockAccessor;
        var right = new Vec3i(-forward.Z, 0, forward.X);
        var layout = Layout(rampFoot, forward, right);

        Block? BlockFor(Part part, Vec3i? towards) => part == Part.Air ? null : world.GetBlock(new AssetLocation("game", part switch
        {
            Part.Planks => $"planks-{wood}-ud",
            // A stair's high side is the side in its code: it climbs towards it.
            Part.Stairs => $"plankstairs-{wood}-up-{BlockFacing.FromNormal(towards!).Code}-free",
            Part.Slab => $"plankslab-{wood}-down-free",
            Part.Post => $"woodenfence-{wood}-empty-free",
            // "n" gates run east-west, "w" gates north-south.
            Part.Gate => $"woodenfencegate-{wood}-{(right.X != 0 ? "n" : "w")}-closed-left-free",
            _ => "henbox"
        }));

        var blocks = new List<Block?>(layout.Count);
        foreach (var (pos, part, towards) in layout)
        {
            var block = BlockFor(part, towards);
            if (part != Part.Air && block == null) return "coop-noroom";
            blocks.Add(block);
            if (!Clearable(accessor.GetBlock(pos)) || accessor.GetBlock(pos, BlockLayersAccess.Fluid).Id != 0) return "coop-noroom";
            // The posts and the ramp stand on the ground; under the coop it may be uneven.
            if (pos.Y == rampFoot.Y && part != Part.Air && !accessor.GetBlock(pos.DownCopy()).SideSolid[BlockFacing.indexUP]) return "coop-noground";
        }

        // Nobody may end up inside a block (a hen under the coop or in it is fine to build around).
        var (min, max) = Bounds(layout);
        foreach (var entity in world.GetEntitiesInsideCuboid(min, max.AddCopy(1, 1, 1), e => e.Alive && e.IsInteractable))
        {
            var at = entity.Pos.AsBlockPos;
            if (layout.Exists(p => p.Pos.Equals(at) && p.Part != Part.Air)) return "coop-blocked";
        }

        for (int i = 0; i < layout.Count; i++)
        {
            var pos = layout[i].Pos;
            // Flowers, ferns, loose stones and the like are picked up, as if broken by hand.
            if (accessor.GetBlock(pos).Id != 0) accessor.BreakBlock(pos, null);
            if (blocks[i] is { } block) accessor.SetBlock(block.Id, pos);
        }
        return null;
    }

    // Air, grass and snow, and small things without a hitbox (flowers, ferns, loose stones), but
    // nothing solid and nothing with contents, like a chest or a hen box.
    static bool Clearable(Block block) =>
        block.Replaceable >= 6000
        || (block.EntityClass == null && !block.IsLiquid() && (block.CollisionBoxes == null || block.CollisionBoxes.Length == 0));

    /// <summary>
    /// Every block of the coop, from the ground up: d is the distance from the front wall (the ramp
    /// is at -2 and -1), w the offset to the right of the middle, h the height above the ground.
    /// Towards is the direction a stair climbs.
    /// </summary>
    static List<(BlockPos Pos, Part Part, Vec3i? Towards)> Layout(BlockPos rampFoot, Vec3i forward, Vec3i right)
    {
        var layout = new List<(BlockPos, Part, Vec3i?)>();
        var left = new Vec3i(-right.X, 0, -right.Z);
        int half = Width / 2, last = Depth - 1;
        void Add(int d, int w, int h, Part part, Vec3i? towards = null) =>
            layout.Add((rampFoot.AddCopy(forward.X * (d + RampLength) + right.X * w, h, forward.Z * (d + RampLength) + right.Z * w), part, towards));

        // The ramp: two steps up to the pop hole, the upper one on a block.
        Add(-2, 0, 0, Part.Stairs, forward);
        Add(-2, 0, 1, Part.Air);
        Add(-1, 0, 0, Part.Planks);
        Add(-1, 0, 1, Part.Stairs, forward);
        Add(-1, 0, 2, Part.Air);

        // The same two steps up to the gate at the back, with head room, so the player can walk in
        // to collect the eggs.
        var back = new Vec3i(-forward.X, 0, -forward.Z);
        Add(Depth + 1, 0, 0, Part.Stairs, back);
        Add(Depth + 1, 0, 1, Part.Air);
        Add(Depth + 1, 0, 2, Part.Air);
        Add(Depth, 0, 0, Part.Planks);
        Add(Depth, 0, 1, Part.Stairs, back);
        Add(Depth, 0, 2, Part.Air);
        Add(Depth, 0, 3, Part.Air);

        for (int d = 0; d <= last; d++)
        for (int w = -half; w <= half; w++)
        {
            bool side = Math.Abs(w) == half, gableEnd = d == 0 || d == last;
            var upToRidge = w < 0 ? right : left;

            // Posts in the corners, open underneath, so hens can shelter there too.
            Add(d, w, 0, gableEnd && side ? Part.Post : Part.Air);
            Add(d, w, 1, Part.Planks);

            // The walls: the pop hole in the front, a gate in the back to reach the eggs, and the
            // hen boxes in the back corners, where hens like to lay.
            Add(d, w, 2, d == 0 && w == 0 ? Part.Air
                : d == last && w == 0 ? Part.Gate
                : side || gableEnd ? Part.Planks
                : d == last - 1 && Math.Abs(w) == half - 1 ? Part.HenBox
                : Part.Air);

            // The pitched roof: stairs climbing to the ridge from both sides, the gable ends closed
            // but for a doorway over the gate, tall enough for the player.
            Add(d, w, 3, side ? Part.Stairs : d == last && w == 0 ? Part.Air : gableEnd ? Part.Planks : Part.Air, upToRidge);
            Add(d, w, 4, Math.Abs(w) == half - 1 ? Part.Stairs : w == 0 && gableEnd ? Part.Planks : Part.Air, upToRidge);
            if (w == 0) Add(d, w, 5, Part.Slab);
        }
        return layout;
    }

    static (BlockPos Min, BlockPos Max) Bounds(List<(BlockPos Pos, Part Part, Vec3i? Towards)> layout)
    {
        var min = layout[0].Pos.Copy();
        var max = layout[0].Pos.Copy();
        foreach (var (pos, _, _) in layout)
        {
            min.Set(Math.Min(min.X, pos.X), Math.Min(min.Y, pos.Y), Math.Min(min.Z, pos.Z));
            max.Set(Math.Max(max.X, pos.X), Math.Max(max.Y, pos.Y), Math.Max(max.Z, pos.Z));
        }
        return (min, max);
    }

    public override WorldInteraction[] GetHeldInteractionHelp(ItemSlot inSlot) => new[]
    {
        new WorldInteraction { ActionLangCode = LivestockUtil.Domain + ":heldhelp-coop", MouseButton = EnumMouseButton.Right }
    };
}
