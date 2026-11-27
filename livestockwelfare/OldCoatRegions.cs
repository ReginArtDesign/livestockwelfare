using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace livestockwelfare;

/// <summary>
/// Where each pixel of a coat texture lands on the animal's 3D model, so old age can grey the
/// muzzle, the spine and the legs (from the feet up) more than the rest. Every face of the shape
/// is traced through its element's rotations; the body parts come from the element names
/// (head, snout, leg...) and from the model's geometry (the back is the upward faces of the body
/// near its middle line), so it works for any four-legged animal without per-animal settings.
/// </summary>
public class OldCoatRegions
{
    public readonly int Width, Height;
    // Per pixel (y * Width + x): on the muzzle; how far from the spine line (0 = on it, 1 = at the
    // flank, NaN = not on the back); how high up a leg (0 = at the foot, 1 = at the body, NaN = not a leg).
    public readonly bool[] Muzzle;
    public readonly float[] Spine;
    public readonly float[] Leg;
    // The traced texture pixels with their world position (in 1/16 blocks), for previews.
    public readonly List<(int X, int Y, Vec3f Pos)> Samples = new();

    enum Part { Other, Muzzle, Head, Leg, Torso, Tail }

    static readonly string[] MuzzleWords = { "snout", "nose", "muzzle", "mouth", "jaw", "lip", "jowl", "chin", "beak" };
    static readonly string[] LegWords = { "leg", "arm", "paw", "foot", "feet", "hoof", "toe", "claw", "pastern", "knee", "shin", "ankle", "wrist", "hand" };
    // Leg tops only with a side in the name ("L back Thigh", "R Shoulder"): a bear's "shoulders" is its chest.
    static readonly string[] LegTopWords = { "thigh", "haunch", "shoulder", "hip" };
    static readonly string[] SideWords = { "l", "r", "left", "right", "front", "back", "hind", "fore" };
    static readonly string[] SkipWords = { "eye", "pupil", "teeth", "tooth", "fang", "horn", "antler", "ear", "tusk", "tongue", "whisker" };

    OldCoatRegions(int width, int height)
    {
        Width = width; Height = height;
        Muzzle = new bool[width * height];
        Spine = Enumerable.Repeat(float.NaN, width * height).ToArray();
        Leg = Enumerable.Repeat(float.NaN, width * height).ToArray();
    }

    /// <summary>
    /// The regions of the texture with the given code (as the shape's faces name it, e.g. "hide")
    /// at the texture's real size. Null when the shape has no usable faces for it or no body.
    /// </summary>
    public static OldCoatRegions? Build(Shape shape, string textureCode, int width, int height)
    {
        if (shape?.Elements == null || width <= 0 || height <= 0) return null;
        int[]? size = null;
        shape.TextureSizes?.TryGetValue(textureCode, out size);
        float sx = width / (float)(size?[0] ?? shape.TextureWidth), sy = height / (float)(size?[1] ?? shape.TextureHeight);

        var hits = new List<(int X, int Y, Part Part, Vec3f Pos, Vec3f Normal)>();
        var identity = Mat4f.Create();
        foreach (var element in shape.Elements) Walk(element, identity, Part.Other, false, textureCode, sx, sy, width, height, hits);

        var torso = hits.Where(h => h.Part == Part.Torso).ToList();
        if (torso.Count == 0) return null;

        var regions = new OldCoatRegions(width, height);
        foreach (var h in hits) regions.Samples.Add((h.X, h.Y, h.Pos));

        float ground = hits.Min(h => h.Pos.Y);
        float legTop = torso.Min(h => h.Pos.Y);
        // The body is long along X or Z; the spine runs along the long one, in the middle of the other.
        float minX = torso.Min(h => h.Pos.X), maxX = torso.Max(h => h.Pos.X);
        float minZ = torso.Min(h => h.Pos.Z), maxZ = torso.Max(h => h.Pos.Z);
        bool alongX = maxX - minX >= maxZ - minZ;
        float mid = alongX ? (minZ + maxZ) / 2 : (minX + maxX) / 2;
        float halfWidth = Math.Max(0.01f, alongX ? (maxZ - minZ) / 2 : (maxX - minX) / 2);

        // No element named like a muzzle: the front third of the head is the muzzle.
        bool namedMuzzle = hits.Any(h => h.Part == Part.Muzzle);
        System.Func<Vec3f, bool> inFrontOfHead = _ => false;
        var head = hits.Where(h => h.Part == Part.Head).ToList();
        if (!namedMuzzle && head.Count > 0)
        {
            float Along(Vec3f p) => alongX ? p.X : p.Z;
            float bodyCentre = torso.Average(h => Along(h.Pos)), headCentre = head.Average(h => Along(h.Pos));
            float sign = headCentre >= bodyCentre ? 1 : -1;
            float tip = head.Max(h => sign * Along(h.Pos)), back = head.Min(h => sign * Along(h.Pos));
            float from = tip - (tip - back) / 3;
            inFrontOfHead = p => sign * Along(p) >= from;
        }

        var uses = new int[width * height];
        var muzzleUses = new int[width * height];
        var legUses = new int[width * height];
        var spineUses = new int[width * height];
        foreach (var (x, y, part, pos, normal) in hits)
        {
            int i = y * width + x;
            // A pixel can be used on several faces (models reuse texture parts): it only counts as
            // muzzle, leg or spine when every place it shows is one, and as high up the leg or as far
            // from the spine as its highest or farthest place, so no grey shows up where it should not.
            uses[i]++;
            if (part == Part.Muzzle || (part == Part.Head && inFrontOfHead(pos))) muzzleUses[i]++;
            if (part == Part.Leg)
            {
                legUses[i]++;
                float h = (pos.Y - ground) / Math.Max(0.01f, legTop - ground);
                regions.Leg[i] = float.IsNaN(regions.Leg[i]) ? h : Math.Max(regions.Leg[i], h);
            }
            if (part == Part.Torso && normal.Y > 0.6f)
            {
                spineUses[i]++;
                float d = Math.Abs((alongX ? pos.Z : pos.X) - mid) / halfWidth;
                regions.Spine[i] = float.IsNaN(regions.Spine[i]) ? d : Math.Max(regions.Spine[i], d);
            }
        }
        for (int i = 0; i < uses.Length; i++)
        {
            regions.Muzzle[i] = uses[i] > 0 && muzzleUses[i] == uses[i];
            if (legUses[i] < uses[i]) regions.Leg[i] = float.NaN;
            if (spineUses[i] < uses[i]) regions.Spine[i] = float.NaN;
        }
        return regions;
    }

    static Part Classify(string name, Part parent)
    {
        if (parent == Part.Muzzle) return Part.Muzzle;
        if (parent is Part.Head) return Has(name, MuzzleWords) ? Part.Muzzle : Part.Head;
        if (parent is Part.Leg) return Part.Leg;
        if (parent is Part.Tail || Has(name, "tail")) return Part.Tail;
        if (Has(name, "head", "skull", "face")) return Part.Head;
        if (Has(name, MuzzleWords)) return Part.Muzzle;
        if (Has(name, LegWords) || (Has(name, LegTopWords) && Tokens(name).Any(SideWords.Contains))) return Part.Leg;
        return Part.Torso;
    }

    static string[] Tokens(string name) => System.Text.RegularExpressions.Regex
        .Replace(name, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant()
        .Split(new[] { ' ', '-', '_', '.', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9' }, StringSplitOptions.RemoveEmptyEntries);

    // Whether the element name contains one of the words. Names are like "L back Thigh", "RHornBase",
    // "eartipinner" or "rear": "ear" must start a word, so the rear of the body is not an ear.
    static bool Has(string name, params string[] words)
    {
        var tokens = Tokens(name);
        return words.Any(w => tokens.Any(t => w == "ear" ? t.StartsWith(w) : t.Contains(w)));
    }

    static void Walk(ShapeElement element, float[] parentFrame, Part parent, bool parentSkipped, string textureCode,
        float sx, float sy, int width, int height, List<(int, int, Part, Vec3f, Vec3f)> hits)
    {
        var frame = Mat4f.Mul(Mat4f.Create(), parentFrame, element.GetLocalTransformMatrix(0));
        // Eyes, ears, horns and their children are left alone, but the parts around them are not.
        bool skipped = parentSkipped || Has(element.Name ?? "", SkipWords);
        var part = skipped ? Part.Other : Classify(element.Name ?? "", parent);

        if (element.FacesResolved != null && element.From != null && element.To != null && !skipped)
        {
            var from = element.From; var to = element.To;
            float dx = (float)(to[0] - from[0]), dy = (float)(to[1] - from[1]), dz = (float)(to[2] - from[2]);
            for (int side = 0; side < 6; side++)
            {
                var face = element.FacesResolved[side];
                if (face == null || !face.Enabled || face.Uv == null || face.Texture != textureCode) continue;
                // Corner at (u1, v1), u and v directions and the normal, relative to From (BlockFacing order N E S W U D).
                var (corner, du, dv, normal) = side switch
                {
                    0 => (new Vec3f(dx, dy, 0), new Vec3f(-dx, 0, 0), new Vec3f(0, -dy, 0), new Vec3f(0, 0, -1)),
                    1 => (new Vec3f(dx, dy, dz), new Vec3f(0, 0, -dz), new Vec3f(0, -dy, 0), new Vec3f(1, 0, 0)),
                    2 => (new Vec3f(0, dy, dz), new Vec3f(dx, 0, 0), new Vec3f(0, -dy, 0), new Vec3f(0, 0, 1)),
                    3 => (new Vec3f(0, dy, 0), new Vec3f(0, 0, dz), new Vec3f(0, -dy, 0), new Vec3f(-1, 0, 0)),
                    4 => (new Vec3f(0, dy, 0), new Vec3f(dx, 0, 0), new Vec3f(0, 0, dz), new Vec3f(0, 1, 0)),
                    _ => (new Vec3f(0, 0, dz), new Vec3f(dx, 0, 0), new Vec3f(0, 0, -dz), new Vec3f(0, -1, 0)),
                };
                var n = Transform(frame, normal, 0);
                float u1 = face.Uv[0], v1 = face.Uv[1], u2 = face.Uv[2], v2 = face.Uv[3];
                int x0 = (int)Math.Round(Math.Min(u1, u2) * sx), x1 = (int)Math.Round(Math.Max(u1, u2) * sx);
                int y0 = (int)Math.Round(Math.Min(v1, v2) * sy), y1 = (int)Math.Round(Math.Max(v1, v2) * sy);
                int rotation = ((int)Math.Round(face.Rotation / 90) % 4 + 4) % 4;
                for (int px = Math.Max(0, x0); px < Math.Min(width, x1); px++)
                {
                    for (int py = Math.Max(0, y0); py < Math.Min(height, y1); py++)
                    {
                        float a = u2 != u1 ? ((px + 0.5f) / sx - u1) / (u2 - u1) : 0.5f;
                        float b = v2 != v1 ? ((py + 0.5f) / sy - v1) / (v2 - v1) : 0.5f;
                        (a, b) = rotation switch { 1 => (b, 1 - a), 2 => (1 - a, 1 - b), 3 => (1 - b, a), _ => (a, b) };
                        var p = new Vec3f(corner.X + a * du.X + b * dv.X, corner.Y + a * du.Y + b * dv.Y, corner.Z + a * du.Z + b * dv.Z);
                        hits.Add((px, py, part, Transform(frame, p * (1 / 16f), 1) * 16, n));
                    }
                }
            }
        }
        if (element.Children == null) return;
        foreach (var child in element.Children) Walk(child, frame, part, skipped || parentSkipped, textureCode, sx, sy, width, height, hits);
    }

    static Vec3f Transform(float[] m, Vec3f v, float w)
    {
        var r = Mat4f.MulWithVec4(m, new[] { v.X, v.Y, v.Z, w });
        return new Vec3f(r[0], r[1], r[2]);
    }
}
