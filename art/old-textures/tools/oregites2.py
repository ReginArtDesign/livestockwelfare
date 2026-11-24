"""Old-age greying placed on the 3D model: strongest around the nose and on the nose bridge,
along the spine, and on the legs from the hooves upwards; elsewhere only scattered grey hairs.
Every texture pixel is traced back to its spot on the model (shape elements, their rotations
and face UVs), so the grey lands on the right body part.

python oregites2.py <coat.png> <out-folder> <name> <shape.json> [<shape.json> ...] [--debug <png>]
writes <out-folder>/<name>-old1.png .. -old3.png"""
import json, math, random, sys, os
from PIL import Image

# Per stage 1..3.
NOSE = [0.35, 0.6, 0.85]        # share of grey hairs on the muzzle / nose bridge
SPINE = [0.18, 0.3, 0.45]       # half width of the grey spine stripe (of the half body width)
LEGS = [0.25, 0.5, 0.85]        # how far up the legs the grey reaches (of the leg height)
SCATTER = [0.02, 0.05, 0.10]    # single grey hairs anywhere
STRENGTH = [0.55, 0.7, 0.85]    # how grey a grey hair is

NOSE_PARTS = ('nose bridge', 'lip', 'jaw', 'cube2', 'mouthup', 'nose', 'snout')
LEG_PARTS = ('thigh', 'arm', 'pastern', 'hoof', 'haunch', 'leg', 'shoulder')
TORSO_PARTS = ('body', 'midsection', 'rear', 'neck', 'chest', 'fleece')

args = sys.argv[1:]
debug = None
if '--debug' in args:
    i = args.index('--debug'); debug = args[i + 1]; del args[i:i + 2]
coat_path, out_dir, name, shapes = args[0], args[1], args[2], args[3:]
coat = Image.open(coat_path).convert('RGBA')
W, H = coat.size
cpx = coat.load()


def mat_mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(4)) for j in range(4)] for i in range(4)]


def translate(x, y, z):
    return [[1, 0, 0, x], [0, 1, 0, y], [0, 0, 1, z], [0, 0, 0, 1]]


def rot(axis, deg):
    c, s = math.cos(math.radians(deg)), math.sin(math.radians(deg))
    if axis == 'x': return [[1, 0, 0, 0], [0, c, -s, 0], [0, s, c, 0], [0, 0, 0, 1]]
    if axis == 'y': return [[c, 0, s, 0], [0, 1, 0, 0], [-s, 0, c, 0], [0, 0, 0, 1]]
    return [[c, -s, 0, 0], [s, c, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]


def apply(m, p, w=1):
    return tuple(m[i][0] * p[0] + m[i][1] * p[1] + m[i][2] * p[2] + m[i][3] * w for i in range(3))


# Face corner at (u1, v1), the u and v directions, and the normal, in the element's frame.
def face_frame(side, f, t):
    dx, dy, dz = t[0] - f[0], t[1] - f[1], t[2] - f[2]
    return {
        'north': ((t[0], t[1], f[2]), (-dx, 0, 0), (0, -dy, 0), (0, 0, -1)),
        'south': ((f[0], t[1], t[2]), (dx, 0, 0), (0, -dy, 0), (0, 0, 1)),
        'east': ((t[0], t[1], t[2]), (0, 0, -dz), (0, -dy, 0), (1, 0, 0)),
        'west': ((f[0], t[1], f[2]), (0, 0, dz), (0, -dy, 0), (-1, 0, 0)),
        'up': ((f[0], t[1], f[2]), (dx, 0, 0), (0, 0, dz), (0, 1, 0)),
        'down': ((f[0], f[1], t[2]), (dx, 0, 0), (0, 0, -dz), (0, -1, 0)),
    }[side]


# Texture pixel -> (part, world position, world normal), from every shape.
samples = {}
for sp in shapes:
    shape = json.load(open(sp, encoding='utf-8'))
    sx, sy = W / shape['textureWidth'], H / shape['textureHeight']

    def walk(elements, frame, path):
        for e in elements:
            f, t = e['from'], e['to']
            o = e.get('rotationOrigin', [0, 0, 0])
            local = translate(*o)
            for axis in 'xyz':
                a = e.get('rotation' + axis.upper(), 0)
                if a: local = mat_mul(local, rot(axis, a))
            local = mat_mul(local, translate(-o[0], -o[1], -o[2]))
            m = mat_mul(frame, local)
            names = path + [e['name'].lower()]
            part = ('nose' if any(p in n for n in names for p in NOSE_PARTS) else
                    'leg' if any(p in names[-1] for p in LEG_PARTS) or any(p in n for n in names for p in ('thigh', 'haunch', 'shoulder')) else
                    'torso' if any(p in names[-1] for p in TORSO_PARTS) else 'other')
            for side, fc in (e.get('faces') or {}).items():
                if fc.get('enabled', True) is False or 'uv' not in fc: continue
                u1, v1, u2, v2 = fc['uv']
                corner, du, dv, normal = face_frame(side, f, t)
                n = apply(m, normal, 0)
                x0, x1 = sorted((u1 * sx, u2 * sx)); y0, y1 = sorted((v1 * sy, v2 * sy))
                for px in range(int(round(x0)), int(round(x1))):
                    for py in range(int(round(y0)), int(round(y1))):
                        if not (0 <= px < W and 0 <= py < H): continue
                        a = ((px + 0.5) / sx - u1) / (u2 - u1) if u2 != u1 else 0.5
                        b = ((py + 0.5) / sy - v1) / (v2 - v1) if v2 != v1 else 0.5
                        p = tuple(corner[i] + a * du[i] + b * dv[i] for i in range(3))
                        samples.setdefault((px, py), []).append((part, apply(m, p), n))
            walk(e.get('children') or [], mat_mul(m, translate(*f)), names)

    walk(shape['elements'], [[1 if i == j else 0 for j in range(4)] for i in range(4)], [])

all_pts = [s[1] for v in samples.values() for s in v]
ground = min(p[1] for p in all_pts)
torso = [s[1] for v in samples.values() for s in v if s[0] == 'torso']
leg_top = min(p[1] for p in torso)
zc = (min(p[2] for p in torso) + max(p[2] for p in torso)) / 2
half_w = (max(p[2] for p in torso) - min(p[2] for p in torso)) / 2

lums = sorted(0.3 * p[0] + 0.59 * p[1] + 0.11 * p[2] for p in coat.get_flattened_data() if p[3] > 0)
limit = lums[len(lums) // 2] + 40
def is_fur(p): return p[3] > 0 and 0.3 * p[0] + 0.59 * p[1] + 0.11 * p[2] < limit

rnd = random.Random(7)
noise = {k: (rnd.random(), rnd.random(), rnd.random()) for k in samples}

if debug:
    colours = {'nose': (255, 60, 60), 'leg': (60, 90, 255), 'torso': (60, 200, 60), 'other': (230, 200, 40)}
    xs = [p[0] for p in all_pts]; ys = [p[1] for p in all_pts]; zs = [p[2] for p in all_pts]
    z = 12
    img = Image.new('RGB', (int((max(xs) - min(xs)) * z) * 2 + 40, int((max(ys) - min(ys)) * z) + 20), (30, 30, 30))
    off = int((max(xs) - min(xs)) * z) + 20
    for v in samples.values():
        for part, p, n in v:
            c = colours[part]
            img.putpixel((int((p[0] - min(xs)) * z) + 10, int((max(ys) - p[1]) * z) + 10), c)
            if n[1] > 0.6 and part == 'torso':
                img.putpixel((off + int((p[0] - min(xs)) * z), int((p[2] - min(zs)) * z) + 10), c)
    img.save(debug)

outs = []
for k in range(3):
    amount = {}
    for (x, y), hits in samples.items():
        if not is_fur(cpx[x, y]): continue
        r1, r2, r3 = noise[(x, y)]
        a = 0.0
        for part, pos, n in hits:
            if part == 'nose':
                if r1 < NOSE[k]: a = max(a, 0.6 + 0.4 * r2)
            elif part == 'leg':
                h = (pos[1] - ground) / max(1e-6, leg_top - ground)   # 0 at the hoof, 1 at the body
                reach = LEGS[k] * (0.7 + 0.3 * r3)
                if h < reach: a = max(a, min(1.0, (reach - h) / (reach * 0.5)) * (0.5 + 0.5 * r2))
            if part == 'torso' and n[1] > 0.6:                    # the back, seen from above
                d = abs(pos[2] - zc) / max(1e-6, half_w)
                width = SPINE[k] * (0.6 + 0.8 * r3)
                if d < width: a = max(a, (1 - d / width) * (0.6 + 0.4 * r2))
        # Scattered hairs are soft: a lighter shade of the coat rather than a white dot.
        if r1 > 1 - SCATTER[k]: a = max(a, 0.3 + 0.3 * r2)
        amount[(x, y)] = a
    # Soften: every pixel takes some of its neighbours' grey, so dots and edges fade into the coat.
    soft = {}
    for (x, y), a in amount.items():
        near = [amount[q] for q in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1),
                                    (x - 1, y - 1), (x + 1, y - 1), (x - 1, y + 1), (x + 1, y + 1)) if q in amount]
        soft[(x, y)] = 0.6 * a + 0.4 * (sum(near) / len(near) if near else a)
    out = coat.copy()
    outs.append(out)
    op = out.load()
    for (x, y), a in soft.items():
        if a <= 0.01: continue
        p = cpx[x, y]
        r1, r2, r3 = noise[(x, y)]
        a *= STRENGTH[k]
        g = 115 + 75 * r3
        grey = (g, g * 0.97, g * 0.93)
        op[x, y] = tuple(round(p[j] * (1 - a) + grey[j] * a) for j in range(3)) + (p[3],)
    out.save(os.path.join(out_dir, f'{name}-old{k + 1}.png'))
if debug:
    # Side and top view of the model painted with the original and the 3 stages, for checking.
    xs = [p[0] for p in all_pts]; ys = [p[1] for p in all_pts]; zs = [p[2] for p in all_pts]
    z = 10
    vw, vh, dh = int((max(xs) - min(xs)) * z) + 20, int((max(ys) - min(ys)) * z) + 20, int((max(zs) - min(zs)) * z) + 20
    img = Image.new('RGB', (vw * 4, vh + dh), (90, 90, 90))
    for col, tex in enumerate([coat] + outs):
        tp = tex.load()
        side, top = {}, {}
        for (x, y), hits in samples.items():
            c = tp[x, y]
            if c[3] == 0: continue
            for part, p, n in hits:
                k1 = (col * vw + int((p[0] - min(xs)) * z) + 10, int((max(ys) - p[1]) * z) + 10)
                if k1 not in side or side[k1][0] < p[2]: side[k1] = (p[2], c)
                k2 = (col * vw + int((p[0] - min(xs)) * z) + 10, vh + int((p[2] - min(zs)) * z) + 10)
                if k2 not in top or top[k2][0] < p[1]: top[k2] = (p[1], c)
        for d in (side, top):
            for (px, py), (_, c) in d.items():
                for ox in range(2):
                    for oy in range(2):
                        if px + ox < img.width and py + oy < img.height: img.putpixel((px + ox, py + oy), c[:3])
    img.save(debug.replace('.png', '-3d.png'))
print(name, len(samples), 'pixels on the model, ground', round(ground, 1), 'legs up to', round(leg_top, 1))
