using System;
using System.Collections.Generic;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace livestockwelfare;

/// <summary>
/// Client side: builds "dirty" and "old" copies of entity textures by greying the coat and painting
/// mud spots onto it, and inserts them into the entity texture atlas. Generated in code because vanilla texture overlays
/// must be exactly as large as the base texture, and livestock textures come in many sizes.
/// </summary>
public static class DirtTextures
{
    public const int Stages = 3;
    public const int AgeStages = 3;

    // How much of the coat is covered with mud at each stage (index = stage).
    static readonly float[] Coverage = { 0, 0.18f, 0.35f, 0.55f };
    // Mud colour, and how strongly it covers the coat.
    static readonly SKColor Mud = new(88, 62, 38);
    const float MudOpacity = 0.75f;
    // Spot size in texture pixels.
    const float SpotSize = 3.5f;

    // Old age (index = age stage): how much colour the coat loses, how much it fades toward
    // light grey, and the share of single grey hairs.
    static readonly float[] AgeDesaturate = { 0, 0.30f, 0.50f, 0.70f };
    static readonly float[] AgeFade = { 0, 0.15f, 0.28f, 0.40f };
    static readonly float[] AgeGreyHairs = { 0, 0.12f, 0.25f, 0.40f };
    static readonly SKColor Grey = new(205, 200, 192);
    // Many faint hairs rather than few strong ones: on a black coat strong ones look like dots.
    const float GreyHairOpacity = 0.2f;

    static readonly Dictionary<(AssetLocation, int, int), int> subIds = new();

    /// <summary>
    /// Atlas sub id of the given texture with mud of the given stage (0..Stages) on a coat greyed
    /// by the given age stage (0..AgeStages).
    /// </summary>
    public static int GetSubId(ICoreClientAPI capi, AssetLocation bakedName, int stage, int ageStage)
    {
        if (subIds.TryGetValue((bakedName, stage, ageStage), out int cached)) return cached;

        var key = new AssetLocation(LivestockUtil.Domain, "dirty/" + Sanitize(bakedName.ToString()) + "-" + stage + "-old" + ageStage);
        var atlas = capi.EntityTextureAtlas;
        atlas.GetOrInsertTexture(new AssetLocationAndSource(key), out int subId, out _,
            () => Paint(atlas.LoadCompositeBitmap(new AssetLocationAndSource(bakedName)), stage, ageStage));
        subIds[(bakedName, stage, ageStage)] = subId;
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

    // A texture that fails to load must never crash the client: then the coat simply stays clean.
    static IBitmap Paint(IBitmap? source, int stage, int ageStage)
    {
        if (source == null || source.Width <= 0 || source.Height <= 0) return source ?? new BitmapExternal(1, 1);
        try
        {
            return new BitmapExternal(PaintBitmap(source, stage, ageStage));
        }
        catch (Exception)
        {
            return source;
        }
    }

    /// <summary>The old and/or muddy copy of a texture; internal so the self-test can export previews.</summary>
    internal static SKBitmap PaintBitmap(IBitmap source, int stage, int ageStage = 0)
    {
        int width = source.Width, height = source.Height;
        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        float coverage = Coverage[Math.Clamp(stage, 0, Stages)];
        ageStage = Math.Clamp(ageStage, 0, AgeStages);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SKColor color = source.GetPixel(x, y);
                // Old age first, so the mud lies on top of the grey coat.
                if (ageStage > 0 && color.Alpha > 0) color = Age(color, x, y, ageStage);
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

    // Faded, greyer coat with single grey hairs. The hairs come from a per-pixel hash, so a later
    // stage keeps the hairs of the earlier ones and every animal with the same texture looks alike.
    static SKColor Age(SKColor color, int x, int y, int ageStage)
    {
        float luminance = 0.299f * color.Red + 0.587f * color.Green + 0.114f * color.Blue;
        luminance += (Grey.Red - luminance) * AgeFade[ageStage];
        // Slightly warm grey, like grey hair rather than ash.
        var grey = new SKColor(
            (byte)Math.Clamp(luminance * 1.02f, 0, 255),
            (byte)Math.Clamp(luminance, 0, 255),
            (byte)Math.Clamp(luminance * 0.97f, 0, 255));
        float desaturate = AgeDesaturate[ageStage];
        color = new SKColor(
            Lerp(color.Red, grey.Red, desaturate),
            Lerp(color.Green, grey.Green, desaturate),
            Lerp(color.Blue, grey.Blue, desaturate),
            color.Alpha);

        if (Hash(x + 101, y + 7) < AgeGreyHairs[ageStage])
        {
            color = new SKColor(
                Lerp(color.Red, Grey.Red, GreyHairOpacity),
                Lerp(color.Green, Grey.Green, GreyHairOpacity),
                Lerp(color.Blue, Grey.Blue, GreyHairOpacity),
                color.Alpha);
        }
        return color;
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
