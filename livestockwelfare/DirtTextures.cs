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
    /// by the given age stage (0..AgeStages). With the entity's shape and the texture's code in it,
    /// old age greys the muzzle, the spine and the legs most (see OldCoatRegions).
    /// </summary>
    public static int GetSubId(ICoreClientAPI capi, AssetLocation bakedName, int stage, int ageStage, Shape? shape = null, string? textureCode = null)
    {
        if (subIds.TryGetValue((bakedName, stage, ageStage), out int cached)) return cached;

        var key = AtlasKey(bakedName, stage, ageStage);
        var atlas = capi.EntityTextureAtlas;
        atlas.GetOrInsertTexture(new AssetLocationAndSource(key), out int subId, out _,
            () => PaintedOld(capi, bakedName, ageStage) is { } painted
                ? Paint(painted, stage, 0)
                : Paint(atlas.LoadCompositeBitmap(new AssetLocationAndSource(bakedName)), stage, ageStage, shape, textureCode));
        subIds[(bakedName, stage, ageStage)] = subId;
        return subId;
    }

    /// <summary>
    /// Client side: the entity's textures swapped for muddy and/or grey old copies, or null to keep
    /// its normal look. Used by the hygiene behavior, and by the age behavior for animals that do
    /// not get dirty (small animals), so they still grey with age.
    /// </summary>
    public static ITexPositionSource? TextureSource(Vintagestory.API.Common.Entities.Entity entity, int stage, int ageStage, ref EnumHandling handling)
    {
        if ((stage == 0 && ageStage == 0) || entity.Api is not ICoreClientAPI capi) return null;

        var textures = entity.Properties.Client?.Textures;
        if (textures == null || textures.Count == 0) return null;

        int textureIndex = entity.WatchedAttributes.GetInt("textureIndex");
        var subIds = new Dictionary<string, int>();
        foreach (var (code, texture) in textures)
        {
            var baked = texture.Baked;
            if (baked?.BakedName == null) continue;
            // Same alternate selection as the vanilla tesselator.
            if (baked.BakedVariants is { Length: > 1 } variants) baked = variants[textureIndex % variants.Length];
            subIds[code] = GetSubId(capi, baked.BakedName, stage, ageStage, entity.Properties.Client?.LoadedShapeForEntity, code);
        }

        var fallback = capi.Tesselator.GetTextureSource(entity, null, textureIndex);
        // Stop here: a later behavior returning null would otherwise wipe out our result.
        handling = EnumHandling.PreventSubsequent;
        return new DirtyTextureSource(subIds, capi.EntityTextureAtlas, fallback);
    }

    /// <summary>
    /// Where a hand-painted old coat for a texture goes: the texture's own path under
    /// livestockwelfare:textures/old/&lt;its mod&gt;/, with "-old1".."-old3" before ".png". For
    /// tradlivestock:textures/entity/land/cow/black-adult.png, stage 2 is
    /// livestockwelfare:textures/old/tradlivestock/entity/land/cow/black-adult-old2.png.
    /// </summary>
    public static AssetLocation PaintedOldLocation(AssetLocation bakedName, int ageStage)
    {
        string path = bakedName.Path;
        if (path.StartsWith("textures/")) path = path["textures/".Length..];
        if (path.EndsWith(".png")) path = path[..^4];
        return new AssetLocation(LivestockUtil.Domain, $"textures/old/{bakedName.Domain}/{path}-old{ageStage}.png");
    }

    // A hand-painted old coat, if there is one for this texture and stage (same size as the
    // original); otherwise the coat is greyed in code. Mud is still painted on top.
    static IBitmap? PaintedOld(ICoreClientAPI capi, AssetLocation bakedName, int ageStage)
    {
        if (ageStage <= 0 || bakedName.Path.Contains("++")) return null;
        try
        {
            var asset = capi.Assets.TryGet(PaintedOldLocation(bakedName, ageStage));
            if (asset == null) return null;
            var painted = new BitmapExternal(asset.Data, asset.Data.Length, capi.Logger);
            var original = capi.EntityTextureAtlas.LoadCompositeBitmap(new AssetLocationAndSource(bakedName));
            if (original != null && (painted.Width != original.Width || painted.Height != original.Height))
            {
                capi.Logger.Warning("[livestockwelfare] Painted old texture {0} is {1}x{2}, but {3} is {4}x{5}; greying in code instead.",
                    asset.Location, painted.Width, painted.Height, bakedName, original.Width, original.Height);
                return null;
            }
            return painted;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the hand-painted old coats from disk again and drops the cached old coats, so loaded
    /// animals show the new paint at once (for painting without restarting the game).
    /// Returns how many painted files were found.
    /// </summary>
    public static int ReloadPainted(ICoreClientAPI capi)
    {
        int found = capi.Assets.Reload(new AssetLocation(LivestockUtil.Domain, "textures/old/"));
        foreach (var key in new List<(AssetLocation, int, int)>(subIds.Keys))
        {
            if (key.Item3 == 0) continue;
            var (bakedName, stage, ageStage) = key;
            capi.EntityTextureAtlas.FreeTextureSpace(new AssetLocationAndSource(AtlasKey(bakedName, stage, ageStage)));
            subIds.Remove(key);
        }
        foreach (var e in capi.World.LoadedEntities.Values)
        {
            if (e.HasBehavior<EntityBehaviorLivestockHygiene>() || e.HasBehavior<EntityBehaviorLivestockAge>()) e.MarkShapeModified();
        }
        return found;
    }

    /// <summary>Forget cached textures, e.g. when leaving a world (the atlas is rebuilt then).</summary>
    public static void Clear() => subIds.Clear();

    static AssetLocation AtlasKey(AssetLocation bakedName, int stage, int ageStage) =>
        new(LivestockUtil.Domain, "dirty/" + Sanitize(bakedName.ToString()) + "-" + stage + "-old" + ageStage);

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
    static IBitmap Paint(IBitmap? source, int stage, int ageStage, Shape? shape = null, string? textureCode = null)
    {
        if (source == null || source.Width <= 0 || source.Height <= 0) return source ?? new BitmapExternal(1, 1);
        try
        {
            OldCoatRegions? regions = null;
            if (ageStage > 0 && shape != null && textureCode != null)
            {
                try { regions = OldCoatRegions.Build(shape, textureCode, source.Width, source.Height); }
                catch (Exception) { regions = null; }
            }
            return new BitmapExternal(PaintBitmap(source, stage, ageStage, regions));
        }
        catch (Exception)
        {
            return source;
        }
    }

    /// <summary>The old and/or muddy copy of a texture; internal so the self-test can export previews.</summary>
    internal static SKBitmap PaintBitmap(IBitmap source, int stage, int ageStage = 0, OldCoatRegions? regions = null)
    {
        int width = source.Width, height = source.Height;
        var result = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        float coverage = Coverage[Math.Clamp(stage, 0, Stages)];
        ageStage = Math.Clamp(ageStage, 0, AgeStages);
        // Light fur shows grey much more: it gets a weaker version, dark fur the full one.
        float[]? ageAmount = ageStage > 0 ? AgeAmountPerPixel(source) : null;
        float[]? whitening = ageStage > 0 && regions != null && regions.Width == width && regions.Height == height
            ? Whitening(source, regions, ageStage) : null;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                SKColor color = source.GetPixel(x, y);
                // Old age first, so the mud lies on top of the grey coat.
                if (ageStage > 0 && color.Alpha > 0)
                {
                    float amount = ageAmount![y * width + x];
                    color = Age(color, x, y, ageStage, amount);
                    float white = (whitening?[y * width + x] ?? 0) * amount;
                    if (white > 0)
                    {
                        // Grey-white, but on dark fur at most a good step lighter than the coat there.
                        float lum = 0.299f * color.Red + 0.587f * color.Green + 0.114f * color.Blue;
                        byte g = (byte)Math.Min(180 + 55 * Hash(x + 53, y + 211), lum + 110 + 50 * Hash(x + 53, y + 211));
                        color = new SKColor(Lerp(color.Red, g, white), Lerp(color.Green, (byte)(g * 0.98f), white),
                            Lerp(color.Blue, (byte)(g * 0.95f), white), color.Alpha);
                    }
                }
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
    static SKColor Age(SKColor color, int x, int y, int ageStage, float amount = 1)
    {
        float luminance = 0.299f * color.Red + 0.587f * color.Green + 0.114f * color.Blue;
        luminance += (Grey.Red - luminance) * AgeFade[ageStage] * amount;
        // Slightly warm grey, like grey hair rather than ash.
        var grey = new SKColor(
            (byte)Math.Clamp(luminance * 1.02f, 0, 255),
            (byte)Math.Clamp(luminance, 0, 255),
            (byte)Math.Clamp(luminance * 0.97f, 0, 255));
        float desaturate = AgeDesaturate[ageStage] * amount;
        color = new SKColor(
            Lerp(color.Red, grey.Red, desaturate),
            Lerp(color.Green, grey.Green, desaturate),
            Lerp(color.Blue, grey.Blue, desaturate),
            color.Alpha);

        if (Hash(x + 101, y + 7) < AgeGreyHairs[ageStage] * amount)
        {
            color = new SKColor(
                Lerp(color.Red, Grey.Red, GreyHairOpacity),
                Lerp(color.Green, Grey.Green, GreyHairOpacity),
                Lerp(color.Blue, Grey.Blue, GreyHairOpacity),
                color.Alpha);
        }
        return color;
    }

    // How strongly old age shows on each pixel, from how dark the fur is around it: 1 for dark fur
    // (brightness up to 80, as before), less for lighter fur, down to 0.15 for light fur, which would
    // otherwise turn white. Per pixel rather than per texture: a German shepherd's dark saddle greys,
    // its light belly hardly does, and unused light areas of a texture do not count.
    static float[] AgeAmountPerPixel(IBitmap source)
    {
        int width = source.Width, height = source.Height;
        var lum = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = source.GetPixel(x, y);
                lum[y * width + x] = c.Alpha == 0 ? -1 : 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
            }
        }
        var amount = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Averaged over the 3x3 neighbourhood, so single light or dark hairs do not make it patchy.
                float sum = 0; int count = 0;
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = x + ox, ny = y + oy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height || lum[ny * width + nx] < 0) continue;
                        sum += lum[ny * width + nx]; count++;
                    }
                }
                float l = count > 0 ? sum / count : 0;
                amount[y * width + x] = Math.Clamp((140 - l) / 60f, 0.15f, 1);
            }
        }
        return amount;
    }

    // Old age, per age stage 1..3: share of grey hairs on the muzzle, half width of the grey spine
    // stripe (of half the body width), how far up the legs the grey reaches, and how white it gets.
    static readonly float[] MuzzleGrey = { 0, 0.35f, 0.6f, 0.85f };
    static readonly float[] SpineWidth = { 0, 0.18f, 0.3f, 0.45f };
    static readonly float[] LegReach = { 0, 0.25f, 0.5f, 0.85f };
    static readonly float[] WhiteStrength = { 0, 0.45f, 0.6f, 0.75f };

    // How much each pixel whitens on top of the general greying: the muzzle, the spine and the legs
    // from the feet up, as soft grey hairs. Only the coat: lighter parts (skin, horns, eyes) stay.
    static float[] Whitening(IBitmap source, OldCoatRegions regions, int ageStage)
    {
        int width = source.Width, height = source.Height;
        var lum = new float[width * height];
        var lums = new List<float>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = source.GetPixel(x, y);
                lum[y * width + x] = c.Alpha == 0 ? -1 : 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
                if (c.Alpha > 0) lums.Add(lum[y * width + x]);
            }
        }
        var soft = new float[width * height];
        if (lums.Count == 0) return soft;
        lums.Sort();
        float furLimit = lums[lums.Count / 2] + 40;
        bool IsFur(int i) => lum[i] >= 0 && lum[i] < furLimit;

        var amount = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (!IsFur(i)) continue;
                float r1 = Hash(x + 7, y + 13), r2 = Hash(x + 29, y + 3), r3 = Hash(x + 71, y + 41);
                float a = 0;
                if (regions.Muzzle[i] && r1 < MuzzleGrey[ageStage]) a = 0.6f + 0.4f * r2;
                float leg = regions.Leg[i];
                if (!float.IsNaN(leg))
                {
                    float reach = LegReach[ageStage] * (0.7f + 0.3f * r3);
                    if (leg < reach) a = Math.Max(a, Math.Min(1, (reach - leg) / (reach * 0.5f)) * (0.5f + 0.5f * r2));
                }
                float spine = regions.Spine[i];
                if (!float.IsNaN(spine))
                {
                    float w = SpineWidth[ageStage] * (0.6f + 0.8f * r3);
                    if (spine < w) a = Math.Max(a, (1 - spine / w) * (0.6f + 0.4f * r2));
                }
                amount[i] = a;
            }
        }
        // Soften: every pixel takes some of its neighbours' grey, so the edges fade into the coat.
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                if (!IsFur(i)) continue;
                float sum = 0; int count = 0;
                for (int oy = -1; oy <= 1; oy++)
                {
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = x + ox, ny = y + oy;
                        if ((ox == 0 && oy == 0) || nx < 0 || ny < 0 || nx >= width || ny >= height || lum[ny * width + nx] < 0) continue;
                        sum += amount[ny * width + nx]; count++;
                    }
                }
                soft[i] = (0.6f * amount[i] + 0.4f * (count > 0 ? sum / count : amount[i])) * WhiteStrength[ageStage];
            }
        }
        return soft;
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
