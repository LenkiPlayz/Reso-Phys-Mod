"""Pictures of the flesh shell from `tests -- flesh-export <file>`:
    python -I tools/flesh_preview.py <flesh_debug.bin> <out folder>
1. the painted region and its sim surface (wireframe) from the front and the side;
2. a cut through the middle of the body: skin, the shell's outer and inner surfaces, and the bones;
3. the frame of the biggest jiggle in the jump test, coloured by how far each vertex moved.
Orthographic, painter's algorithm, 2x supersampled. Needs numpy and Pillow."""
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFont

SS = 2


def load(path):
    b = open(path, "rb").read()
    assert b[:4] == b"SBF1"
    o = 4
    V, T, N, ST, L, B, frame = struct.unpack_from("<7i", b, o); o += 28
    peak, = struct.unpack_from("<f", b, o); o += 4
    def arr(dtype, count, shape=None):
        nonlocal o
        a = np.frombuffer(b, dtype=dtype, count=count, offset=o).copy(); o += a.nbytes
        return a.reshape(shape) if shape else a
    d = dict(frame=frame, peak=peak)
    d["pos"] = arr("<f4", V * 3, (V, 3)); d["tri"] = arr("<i4", T * 3, (T, 3)); d["soft"] = arr("<f4", V)
    d["outer"] = arr("<f4", N * 3, (N, 3)); d["inner"] = arr("<f4", N * 3, (N, 3)); d["pinned"] = arr("<i4", N).astype(bool)
    d["stri"] = arr("<i4", ST * 3, (ST, 3)); d["heads"] = arr("<f4", B * 3, (B, 3)); d["tails"] = arr("<f4", B * 3, (B, 3))
    d["posed"] = arr("<f4", V * 3, (V, 3)); d["mag"] = arr("<f4", V); d["simpeak"] = arr("<f4", N * 3, (N, 3))
    return d


class View:
    """axes: (screen x axis, screen y axis, toward-camera axis) as unit vectors."""
    def __init__(self, sx, sy, sz, lo, hi, height_px, pad=0.04):
        self.m = np.array([sx, sy, sz], dtype=np.float64)
        p = np.array([lo, hi]) @ self.m[:2].T
        self.lo = p.min(axis=0) - pad; self.hi = p.max(axis=0) + pad
        self.scale = height_px * SS / (self.hi[1] - self.lo[1])
        self.w = int((self.hi[0] - self.lo[0]) * self.scale) + 1; self.h = int(height_px * SS) + 1

    def project(self, pts):
        q = pts @ self.m.T
        x = (q[:, 0] - self.lo[0]) * self.scale
        y = (self.hi[1] - q[:, 1]) * self.scale
        return x, y, q[:, 2]


def shade_tris(view, pos, tri, colors):
    x, y, z = view.project(pos)
    a, b, c = pos[tri[:, 0]], pos[tri[:, 1]], pos[tri[:, 2]]
    n = np.cross(b - a, c - a)
    n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
    lam = np.abs(n @ view.m[2])
    k = (0.3 + 0.7 * lam)[:, None]
    col = np.clip(colors * k, 0, 255).astype(np.uint8)
    depth = z[tri].mean(axis=1)
    return [(depth[i], "tri", (tuple(zip(x[tri[i]], y[tri[i]])), tuple(col[i]))) for i in range(len(tri))]


def draw(view, prims, bg=(250, 250, 252)):
    img = Image.new("RGB", (view.w, view.h), bg)
    dr = ImageDraw.Draw(img)
    for _, kind, data in sorted(prims, key=lambda p: p[0]):
        if kind == "tri":
            dr.polygon(data[0], fill=data[1])
        elif kind == "line":
            dr.line(data[0], fill=data[1], width=data[2])
        elif kind == "dot":
            (px, py), r, col = data
            dr.ellipse((px - r, py - r, px + r, py + r), fill=col)
    return img.resize((view.w // SS, view.h // SS), Image.LANCZOS)


def wire(view, pos, stri, col, width, lift):
    x, y, z = view.project(pos)
    edges = set()
    for t in stri:
        for k in range(3):
            a, b = int(t[k]), int(t[(k + 1) % 3])
            edges.add((min(a, b), max(a, b)))
    return [((z[a] + z[b]) * 0.5 + lift, "line", (((x[a], y[a]), (x[b], y[b])), col, width)) for a, b in edges]


def body_colors(soft, tri, base=(178, 180, 190), tint=(240, 140, 60)):
    s = soft[tri].mean(axis=1)[:, None]
    return np.array(base) * (1 - s) + np.array(tint) * s


def heat(t):
    t = np.clip(t, 0, 1)[:, None]
    stops = np.array([[178, 180, 190], [90, 160, 230], [250, 210, 60], [230, 50, 40]], dtype=np.float64)
    seg = np.minimum((t * 3).astype(int), 2)
    f = t * 3 - seg
    return stops[seg[:, 0]] * (1 - f) + stops[seg[:, 0] + 1] * f


def label(img, text):
    dr = ImageDraw.Draw(img)
    try:
        font = ImageFont.truetype("arial.ttf", 18)
    except OSError:
        font = ImageFont.load_default()
    dr.text((12, 10), text, fill=(30, 30, 40), font=font)
    return img


def section(d, x0, height_px):
    """The plane x = x0 seen from +x: body skin, shell outer and inner surfaces, bones near the plane."""
    lo, hi = d["pos"].min(axis=0), d["pos"].max(axis=0)
    v = View((0, 0, 1), (0, 1, 0), (1, 0, 0), lo, hi, height_px)
    img = Image.new("RGB", (v.w, v.h), (250, 250, 252))
    dr = ImageDraw.Draw(img)

    def cut(pos, tri, col, width):
        sd = pos[:, 0] - x0
        for t in tri:
            s = sd[t]
            if (s > 0).all() or (s < 0).all():
                continue
            pts = []
            for k in range(3):
                a, b = t[k], t[(k + 1) % 3]
                if (sd[a] > 0) != (sd[b] > 0):
                    u = sd[a] / (sd[a] - sd[b])
                    pts.append(pos[a] + (pos[b] - pos[a]) * u)
            if len(pts) == 2:
                x, y, _ = v.project(np.array(pts))
                dr.line(((x[0], y[0]), (x[1], y[1])), fill=col, width=width)

    # the shell between its two surfaces, filled: each sim triangle and its inner partner make a prism; cut its sides
    cut(d["pos"], d["tri"], (120, 120, 130), 2 * SS)
    cut(d["outer"], d["stri"], (230, 120, 40), 3 * SS)
    cut(d["inner"], d["stri"], (50, 110, 210), 3 * SS)
    for hd, tl in zip(d["heads"], d["tails"]):
        if abs(hd[0] - x0) < 0.03 and abs(tl[0] - x0) < 0.03:
            x, y, _ = v.project(np.array([hd, tl]))
            dr.line(((x[0], y[0]), (x[1], y[1])), fill=(200, 40, 60), width=4 * SS)
            dr.ellipse((x[0] - 5 * SS, y[0] - 5 * SS, x[0] + 5 * SS, y[0] + 5 * SS), fill=(200, 40, 60))
    return img.resize((v.w // SS, v.h // SS), Image.LANCZOS)


def main(src, outdir):
    d = load(src)
    os.makedirs(outdir, exist_ok=True)
    pos, tri = d["pos"], d["tri"]
    lo, hi = pos.min(axis=0), pos.max(axis=0)
    H = 620
    cols = body_colors(d["soft"], tri)
    out = []
    for name, axes in (("front", ((1, 0, 0), (0, 1, 0), (0, 0, 1))), ("side", ((0, 0, 1), (0, 1, 0), (1, 0, 0)))):
        v = View(*axes, lo, hi, H)
        prims = shade_tris(v, pos, tri, cols)
        prims += wire(v, d["outer"], d["stri"], (40, 45, 95), 1 * SS, 0.002)
        x, y, z = v.project(d["outer"])
        prims += [(z[i] + 0.003, "dot", ((x[i], y[i]), 2.5 * SS, (40, 110, 230))) for i in np.nonzero(d["pinned"])[0]]
        out.append(label(draw(v, prims), "painted region (orange) and its sim surface, " + name + "; blue dots = pinned ring"))
    sec = label(section(d, 0.0, H), "cut at the middle: skin (grey), shell outside (orange) and inside (blue), bones (red)")
    out.append(sec)
    # the jiggle frame
    v = View((0, 0, 1), (0, 1, 0), (1, 0, 0), np.minimum(lo, d["posed"].min(axis=0)), np.maximum(hi, d["posed"].max(axis=0)), H)
    mag = d["mag"][tri].mean(axis=1)
    peak = max(float(d["mag"].max()), 1e-6)
    prims = shade_tris(v, d["posed"], tri, heat(mag / peak))
    out.append(label(draw(v, prims), "biggest jiggle of the 8 cm jump (frame %d): up to %.1f mm, red = most" % (d["frame"], peak * 1000)))
    names = ["flesh_front.png", "flesh_side.png", "flesh_section.png", "flesh_jiggle.png"]
    for img, nm in zip(out, names):
        img.save(os.path.join(outdir, nm))
    # one sheet with all four
    w = sum(i.width for i in out); h = max(i.height for i in out)
    sheet = Image.new("RGB", (w, h), (250, 250, 252)); x = 0
    for i in out:
        sheet.paste(i, (x, 0)); x += i.width
    sheet.save(os.path.join(outdir, "flesh_sheet.png"))
    print("wrote", ", ".join(names + ["flesh_sheet.png"]), "to", outdir)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
