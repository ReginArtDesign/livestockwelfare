using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace livestockwelfare;

/// <summary>
/// The dog food bowl: a vanilla trough, except for meat that has rotted in it. Vanilla draws rot
/// with the big trough's rot shape (and its wood textures, missing on the bowl), so here it is
/// drawn with the bowl's own fill shapes and a rot texture.
/// </summary>
public class BlockEntityDogBowl : BlockEntityTrough
{
    public const string ClassName = "livestockwelfare.dogbowl";

    // In the game domain, where vanilla can load the meat fill from (see foodbowl.json).
    const string FillShape = "game:shapes/block/livestockwelfare/dogbowl/meatfill";
    const string RotTexture = "contents-rot";

    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        var stack = Inventory[0].Itemstack;
        if (stack?.Collectible.Code.Path != "rot") return base.OnTesselation(mesher, tesselator);

        // Two items per fill level, like the meat (foodbowl.json), at most four levels.
        int level = GameMath.Clamp((stack.StackSize + 1) / 2, 1, 4);
        var shape = Shape.TryGet(Api, FillShape + level + ".json");
        if (shape == null) return false;
        var blockTextures = tesselator.GetTextureSource(Block);
        var rotation = new Vec3f(Block.Shape.rotateX, Block.Shape.rotateY, Block.Shape.rotateZ);
        tesselator.TesselateShape("dogbowlrot", shape, out var mesh, new RotTextures(blockTextures), rotation);
        mesher.AddMeshData(mesh);
        return false;
    }

    // The fill shapes paint with "#contents"; for rot that is the bowl's "contents-rot" texture.
    class RotTextures : ITexPositionSource
    {
        readonly ITexPositionSource block;

        public RotTextures(ITexPositionSource block) => this.block = block;

        public TextureAtlasPosition this[string textureCode] => block[textureCode == "contents" ? RotTexture : textureCode];

        public Size2i AtlasSize => block.AtlasSize;
    }
}
