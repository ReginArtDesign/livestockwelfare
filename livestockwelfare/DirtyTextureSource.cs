using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace livestockwelfare;

/// <summary>Uses the dirty texture for codes we replaced and the entity's normal texture for the rest.</summary>
public class DirtyTextureSource : ITexPositionSource
{
    readonly Dictionary<string, int> subIds;
    readonly ITextureAtlasAPI atlas;
    readonly ITexPositionSource fallback;

    public DirtyTextureSource(Dictionary<string, int> subIds, ITextureAtlasAPI atlas, ITexPositionSource fallback)
    {
        this.subIds = subIds;
        this.atlas = atlas;
        this.fallback = fallback;
    }

    public TextureAtlasPosition? this[string textureCode] =>
        subIds.TryGetValue(textureCode, out int subId) ? atlas.Positions[subId] : fallback[textureCode];

    public Size2i? AtlasSize => atlas.Size;
}
