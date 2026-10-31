using System;
using System.Collections.Generic;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace livestockwelfare;

/// <summary>
/// Client side: builds "dirty" copies of entity textures by painting mud spots onto them, and
/// inserts them into the entity texture atlas. Generated in code because vanilla texture overlays
/// must be exactly as large as the base texture, and livestock textures come in many sizes.
/// </summary>
public static class DirtTextures
{
    public const int Stages = 3;

    // How much of the coat is covered with mud at each stage (index = stage).
    static readonly float[] Coverage = { 0, 0.18f, 0.35f, 0.55f };
    // Mud colour, and how strongly it covers the coat.
    static readonly SKColor Mud = new(88, 62, 38);
    const float MudOpacity = 0.75f;
    // Spot size in texture pixels.
    const float SpotSize = 3.5f;

    static readonly Dictionary<(AssetLocation, int), int> subIds = new();

    /// <summary>Atlas sub id of the given texture with mud of the given stage (1..Stages).</summary>
    public static int GetSubId(ICoreClientAPI capi, AssetLocation bakedName, int stage)
    {
        if (subIds.TryGetValue((bakedName, stage), out int cached)) return cached;

        var key = new AssetLocation(LivestockUtil.Domain, "dirty/" + Sanitize(bakedName.ToString()) + "-" + stage);
        var atlas = capi.EntityTextureAtlas;
        atlas.GetOrInsertTexture(new AssetLocationAndSource(key), out int subId, out _,
            () => Paint(atlas.LoadCompositeBitmap(new AssetLocationAndSource(bakedName)), stage));
        subIds[(bakedName, stage)] = subId;
        return subId;
    }

    /// <summary>Forget cached textures, e.g. when leaving a world (the atlas is rebuilt then).</summary>
    public static void Clear() => subIds.Clear();

    static string Sanitize(string name)
    {
        var chars = name.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '/' or '-' or '_')) chars[i] = '_';
        }
        return new string(chars);
    }

    static IBitmap Paint(IBitmap source, int stage) => new BitmapExternal(PaintBitmap(source, stage));

    /// <summary>The muddy copy of a texture; internal so the self-test can export previews.</summary>
    internal static SKBitmap PaintBitmap(IBitmap source, int stage)
    {
        int width = source.Width, height = source.Height;
        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        float coverage = Coverage[Math.Clamp(stage, 0, Stages)];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SKColor color = source.GetPixel(x, y);
                // Patchy mud: spots where the noise is low, with soft, uneven strength.
                float n = Noise(x / SpotSize, y / SpotSize);
                if (color.Alpha > 0 && n < coverage)
                {
                    float strength = MudOpacity * Math.Min(1, (coverage - n) * 6 + 0.3f);
                    color = new SKColor(
                        Lerp(color.Red, Mud.Red, strength),
                        Lerp(color.Green, Mud.Green, strength),
                        Lerp(color.Blue, Mud.Blue, strength),
                        color.Alpha);
                }
                result.SetPixel(x, y, color);
            }
        }
        return result;
    }

    static byte Lerp(byte from, byte to, float t) => (byte)Math.Clamp(from + (to - from) * t, 0, 255);

    // Smooth value noise in 0..1, two octaves. Deterministic, so every animal with the same
    // texture gets the same spots and stages only add mud (stage 3 includes the spots of stage 1).
    static float Noise(float x, float y) => 0.7f * ValueNoise(x, y) + 0.3f * ValueNoise(x * 2.3f + 17, y * 2.3f + 5);

    static float ValueNoise(float x, float y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        float top = GameMath.Lerp(Hash(x0, y0), Hash(x0 + 1, y0), fx);
        float bottom = GameMath.Lerp(Hash(x0, y0 + 1), Hash(x0 + 1, y0 + 1), fx);
        return GameMath.Lerp(top, bottom, fy);
    }

    static float Hash(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177;
        return (h ^ (h >> 16)) / (float)uint.MaxValue;
    }
}
