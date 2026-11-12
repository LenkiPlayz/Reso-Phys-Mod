"""Converts one skinned mesh of a binary FBX into the small .sbm test file the offline tests load (tests/FleshModel.cs).

    python -I tools/fbx_to_sbm.py <model.fbx> <mesh name> <out.sbm>

Only the standard library. What it writes, all in metres and in the FBX's world space at bind time ("mesh space"):
  render vertices (split like an importer does: one per distinct control point / UV / normal), their control point,
  normals, triangles (polygons fanned), 4 bone weights per vertex, the skinned bones (global bind matrix, parent
  among the skinned bones, a tail point for bone-distance tests) and frame-0 blendshape deltas.
Row-vector matrices (System.Numerics convention): the FBX's 16 numbers are read row by row, translation in 12..14.
"""
import math
import os
import struct
import sys
from collections import defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fbxbin

CM = 0.01  # FBX UnitScaleFactor 1 = centimetres


def name_of(o):
    return o.props[1].split("\x00")[0]


def mat(a):
    return [[float(a[r * 4 + c]) for c in range(4)] for r in range(4)]


def mul(a, b):
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def xform(p, m):
    return [p[0] * m[0][c] + p[1] * m[1][c] + p[2] * m[2][c] + m[3][c] for c in range(3)]


def xdir(d, m):
    return [d[0] * m[0][c] + d[1] * m[1][c] + d[2] * m[2][c] for c in range(3)]


def scale_m(s):
    return [[s, 0, 0, 0], [0, s, 0, 0], [0, 0, s, 0], [0, 0, 0, 1]]


def euler_xyz(deg):
    """FBX eEulerXYZ as a row-vector matrix: X first, then Y, then Z."""
    x, y, z = [math.radians(v) for v in deg]
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    rx = [[1, 0, 0, 0], [0, cx, sx, 0], [0, -sx, cx, 0], [0, 0, 0, 1]]
    ry = [[cy, 0, -sy, 0], [0, 1, 0, 0], [sy, 0, cy, 0], [0, 0, 0, 1]]
    rz = [[cz, sz, 0, 0], [-sz, cz, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]]
    return mul(mul(rx, ry), rz)


def local_matrix(model):
    p = {c.props[0]: c.props[4:] for c in model.find("Properties70").children}
    t = p.get("Lcl Translation", [0, 0, 0]); r = p.get("Lcl Rotation", [0, 0, 0]); s = p.get("Lcl Scaling", [1, 1, 1])
    pre = p.get("PreRotation", [0, 0, 0]); post = p.get("PostRotation", [0, 0, 0])
    if any(abs(v) > 1e-9 for v in list(pre) + list(post)):
        raise ValueError("pre/post rotations are not supported (" + name_of(model) + ")")
    m = mul([[s[0], 0, 0, 0], [0, s[1], 0, 0], [0, 0, s[2], 0], [0, 0, 0, 1]], euler_xyz(r))
    m[3][0], m[3][1], m[3][2] = t[0], t[1], t[2]
    return m


def main(src, mesh_name, out):
    ver, root = fbxbin.load(src)
    objs = {c.props[0]: c for c in root.find("Objects").children}
    up = defaultdict(list); down = defaultdict(list)
    for c in root.find("Connections").children:
        up[c.props[1]].append(c.props[2]); down[c.props[2]].append(c.props[1])

    models = {k: o for k, o in objs.items() if o.name == "Model"}
    def parent_model(k):
        for p in up[k]:
            if p in models:
                return p
        return None
    glob = {}
    def global_of(k):
        if k not in glob:
            m = local_matrix(models[k]); p = parent_model(k)
            glob[k] = mul(m, global_of(p)) if p else m
        return glob[k]

    geo = [o for o in objs.values() if o.name == "Geometry" and o.props[2] == "Mesh" and name_of(o) == mesh_name][0]
    gid = geo.props[0]
    mesh_model = [p for p in up[gid] if p in models][0]
    mesh_global = mul(global_of(mesh_model), scale_m(CM))

    cps = geo.find("Vertices").props[0]
    ncp = len(cps) // 3
    cp_world = [xform((cps[i * 3], cps[i * 3 + 1], cps[i * 3 + 2]), mesh_global) for i in range(ncp)]
    pvi = list(geo.find("PolygonVertexIndex").props[0])

    def layer(name, data, index):
        le = geo.find(name)
        if le is None:
            return None, None
        if le.find("MappingInformationType").props[0] != "ByPolygonVertex":
            raise ValueError(name + " is not ByPolygonVertex")
        vals = le.find(data).props[0]
        ref = le.find("ReferenceInformationType").props[0]
        idx = list(le.find(index).props[0]) if ref == "IndexToDirect" else list(range(len(pvi)))
        return vals, idx
    nvals, nidx = layer("LayerElementNormal", "Normals", "NormalsIndex")
    if nidx is None or len(nidx) != len(pvi):
        nvals, nidx = layer("LayerElementNormal", "Normals", "NormalIndex")
    uvals, uidx = layer("LayerElementUV", "UV", "UVIndex")

    # render vertices: one per (control point, uv, normal), like an importer splits them
    key_to_v = {}; v_cp = []; v_nrm = []
    tris = []
    poly = []
    lin = [r[:3] for r in mesh_global[:3]]
    for pv, raw in enumerate(pvi):
        cp = raw if raw >= 0 else ~raw
        # by value, as importers join identical vertices (Blender writes one normal index per polygon corner)
        nk = tuple(round(nvals[nidx[pv] * 3 + i], 4) for i in range(3)) if nidx else ()
        uk = tuple(round(uvals[uidx[pv] * 2 + i], 5) for i in range(2)) if uidx else ()
        key = (cp, uk, nk)
        v = key_to_v.get(key)
        if v is None:
            v = len(v_cp); key_to_v[key] = v; v_cp.append(cp)
            if nidx:
                n = nidx[pv]
                d = xdir((nvals[n * 3], nvals[n * 3 + 1], nvals[n * 3 + 2]), mesh_global)
                l = math.sqrt(sum(c * c for c in d)) or 1.0
                v_nrm.append([c / l for c in d])
            else:
                v_nrm.append([0.0, 1.0, 0.0])
        poly.append(v)
        if raw < 0:
            for k in range(1, len(poly) - 1):
                tris += [poly[0], poly[k], poly[k + 1]]
            poly = []
    nv = len(v_cp)

    # skin: clusters of the Skin deformer on this geometry
    skin = [objs[c] for c in down[gid] if c in objs and objs[c].name == "Deformer" and objs[c].props[2] == "Skin"][0]
    clusters = [objs[c] for c in down[skin.props[0]] if c in objs and objs[c].props[2] == "Cluster"]
    bone_keys = []; bone_index = {}
    cp_inf = defaultdict(list)
    worst_bind = 0.0
    for cl in clusters:
        bk = [c for c in down[cl.props[0]] if c in models][0]
        b = len(bone_keys); bone_keys.append(bk); bone_index[bk] = b
        idx = cl.find("Indexes"); w = cl.find("Weights")
        if idx is not None:
            for i, wt in zip(idx.props[0], w.props[0]):
                if wt > 0:
                    cp_inf[i].append((wt, b))
        # sanity: FK of the Lcl values must reproduce the cluster's bind matrix
        link = mat(cl.find("TransformLink").props[0]); fk = global_of(bk)
        worst_bind = max(worst_bind, max(abs(link[r][c] - fk[r][c]) for r in range(4) for c in range(4)))
    print("bones %d, FK vs TransformLink worst difference %.2e" % (len(bone_keys), worst_bind))

    # bone parents among the skinned bones; tails from any child model's head
    def skinned_parent(k):
        p = parent_model(k)
        while p is not None and p not in bone_index:
            p = parent_model(p)
        return bone_index.get(p, -1) if p is not None else -1
    links = {bone_keys[i]: mat(clusters[i].find("TransformLink").props[0]) for i in range(len(bone_keys))}
    gm = {k: mul(links[k], scale_m(CM)) for k in bone_keys}
    heads = {k: gm[k][3][:3] for k in bone_keys}
    tails = {}
    for k in bone_keys:
        kids = [c for c in down[k] if c in models]
        if kids:
            hs = [mul(global_of(c), scale_m(CM))[3][:3] for c in kids]
            tails[k] = [sum(h[i] for h in hs) / len(hs) for i in range(3)]
        else:
            p = parent_model(k)
            plen = math.dist(heads[k], mul(global_of(p), scale_m(CM))[3][:3]) if p else 0.02
            ydir = gm[k][1][:3]; l = math.sqrt(sum(c * c for c in ydir)) or 1.0
            tails[k] = [heads[k][i] + ydir[i] / l * plen * 0.5 for i in range(3)]

    # 4 strongest weights per control point
    cp_b = [[0, 0, 0, 0] for _ in range(ncp)]; cp_w = [[0.0] * 4 for _ in range(ncp)]
    for i in range(ncp):
        inf = sorted(cp_inf.get(i, []), reverse=True)[:4]
        s = sum(w for w, _ in inf)
        for q, (w, b) in enumerate(inf):
            cp_b[i][q] = b; cp_w[i][q] = w / s if s > 0 else 0.0

    # blendshapes: channels of the BlendShape deformer, first shape of each
    shapes = []
    bsd = [objs[c] for c in down[gid] if c in objs and objs[c].name == "Deformer" and objs[c].props[2] == "BlendShape"]
    cp_to_vs = defaultdict(list)
    for v, cp in enumerate(v_cp):
        cp_to_vs[cp].append(v)
    for bs in bsd:
        for ch in [objs[c] for c in down[bs.props[0]] if c in objs and objs[c].props[2] == "BlendShapeChannel"]:
            sh = [objs[c] for c in down[ch.props[0]] if c in objs and objs[c].name == "Geometry" and objs[c].props[2] == "Shape"]
            if not sh:
                continue
            s = sh[0]
            idx = s.find("Indexes").props[0]; dv = s.find("Vertices").props[0]
            entries = []
            for k, cp in enumerate(idx):
                d = xdir((dv[k * 3], dv[k * 3 + 1], dv[k * 3 + 2]), mesh_global)
                if d[0] == 0 and d[1] == 0 and d[2] == 0:
                    continue
                for v in cp_to_vs.get(cp, []):
                    entries.append((v, d))
            shapes.append((name_of(ch), entries))

    with open(out, "wb") as f:
        def s32(*a): f.write(struct.pack("<%di" % len(a), *a))
        def f32(*a): f.write(struct.pack("<%df" % len(a), *a))
        def name(s):
            b = s.encode("utf-8"); s32(len(b)); f.write(b)
        f.write(b"SBM1")
        s32(nv, len(tris) // 3, len(bone_keys), len(shapes), ncp)
        for v in range(nv):
            f32(*cp_world[v_cp[v]])
        for v in range(nv):
            f32(*v_nrm[v])
        s32(*v_cp)
        s32(*tris)
        for v in range(nv):
            s32(*cp_b[v_cp[v]])
        for v in range(nv):
            f32(*cp_w[v_cp[v]])
        for k in bone_keys:
            name(name_of(models[k])); s32(skinned_parent(k))
            f32(*[c for row in gm[k] for c in row]); f32(*tails[k])
        for nm, entries in shapes:
            name(nm); s32(len(entries))
            for v, d in entries:
                s32(v); f32(*d)
    lo = [min(p[i] for p in cp_world) for i in range(3)]; hi = [max(p[i] for p in cp_world) for i in range(3)]
    print("%s: %d control points -> %d render vertices, %d triangles, %d bones, %d blendshapes" %
          (mesh_name, ncp, nv, len(tris) // 3, len(bone_keys), len(shapes)))
    print("bounds (m) x %.3f..%.3f y %.3f..%.3f z %.3f..%.3f" % (lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]))
    def say(t): print(t.encode("ascii", "replace").decode("ascii"))
    say("bones: " + ", ".join(name_of(models[k]) for k in bone_keys))
    big = sorted(shapes, key=lambda s: -len(s[1]))[:20]
    say("largest blendshapes: " + ", ".join("%s (%d)" % (n, len(e)) for n, e in big))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
