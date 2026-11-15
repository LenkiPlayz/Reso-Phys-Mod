"""Rewrites the D3D11 programs in an AssetBundle built with Unity 2019.4.31f1 so that the game's renderer (Unity 2019.4.19f1)
can create them.

Why: 2019.4.31 writes each D3D11 program's code with a newer header: a first byte of 2 and 32 more bytes (all zero in our
shaders) before the DXBC container. 2019.4.19 only knows the first byte 1 (no extra bytes). It hands Direct3D the zeros instead
of the DXBC, and Direct3D refuses every program ("D3D shader create error ... [0x80070057]", then "ShaderProgram is
unsupported" on every draw). The DXBC itself is fine: each program creates on D3D11 once given the right bytes. Nothing
else in the bundle differs: the Shader, Material and Texture3D type-tree hashes match the game's own data files.

What it does: for every Shader object, for every D3D11 program (platform 4), turns "02 00 xx xx xx xx" + 32 zero bytes + DXBC
into "01 00 xx xx xx xx" + DXBC, shortens the code length and the sub-program by 32, rebuilds the blob table, recompresses
the blob (LZ4), and writes the bundle back (uncompressed directory, LZ4 data blocks of 128 KB). Anything it doesn't
recognise (another first byte, non-zero extra bytes, no DXBC where expected) stops it without writing. Programs that are
already version 1 stay as they are, so running it twice changes nothing.

  python fix_d3d11_programs.py BUNDLE [OUT]       # OUT defaults to BUNDLE (replaced only after the result checks out)
  python fix_d3d11_programs.py BUNDLE --check     # only reports what it would do
  python fix_d3d11_programs.py BUNDLE OUT --make-broken   # tests only: version 1 with the 32 bytes left in, what the game
                                                          # sees (a 2019.4.31 editor then fails the same way)
"""
import os
import struct
import sys

D3D11 = 4               # ShaderCompilerPlatform: kShaderCompPlatformD3D11
SHADER = 48             # class ID
EXTRA = 32              # the extra bytes of the version 2 header
BLOCK = 0x20000         # data block size (as Unity's chunk-based compression)


# ---------------------------------------------------------------------------------------------------------- LZ4 (block format)

def lz4_decode(src, usize):
    dst = bytearray()
    i, n = 0, len(src)
    while i < n:
        token = src[i]; i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]; i += 1; lit += b
                if b != 255: break
        dst += src[i:i + lit]; i += lit
        if i >= n: break
        off = src[i] | (src[i + 1] << 8); i += 2
        if off == 0 or off > len(dst): raise ValueError("bad LZ4 offset")
        ml = token & 15
        if ml == 15:
            while True:
                b = src[i]; i += 1; ml += b
                if b != 255: break
        ml += 4
        start = len(dst) - off
        if off >= ml:
            dst += dst[start:start + ml]
        else:
            for k in range(ml): dst.append(dst[start + k])
    if len(dst) != usize: raise ValueError(f"LZ4: {len(dst)} bytes, expected {usize}")
    return bytes(dst)


def _length(out, v):
    while v >= 255:
        out.append(255); v -= 255
    out.append(v)


def lz4_encode(src):
    """Greedy LZ4 block compression (one hash slot per 4-byte key). Keeps the format's end rules: no match starts in the last
    12 bytes and the last 5 bytes are literals."""
    n = len(src)
    out = bytearray()
    anchor, i = 0, 0
    limit, end_limit = n - 12, n - 5
    table = {}
    while i < limit:
        key = src[i:i + 4]
        cand = table.get(key)
        table[key] = i
        if cand is None or i - cand > 65535:
            i += 1
            continue
        ml = 4
        while i + ml < end_limit and src[cand + ml] == src[i + ml]:
            ml += 1
        lit = i - anchor
        out.append((min(lit, 15) << 4) | min(ml - 4, 15))
        if lit >= 15: _length(out, lit - 15)
        out += src[anchor:i]
        out += struct.pack("<H", i - cand)
        if ml - 4 >= 15: _length(out, ml - 4 - 15)
        for k in range(i + 1, min(i + ml, limit), 4):   # a few positions inside the match, for later matches
            table[src[k:k + 4]] = k
        i += ml
        anchor = i
    lit = n - anchor
    out.append(min(lit, 15) << 4)
    if lit >= 15: _length(out, lit - 15)
    out += src[anchor:]
    return bytes(out)


def decompress(data, kind, usize):
    if kind == 0: return bytes(data)
    if kind in (2, 3): return lz4_decode(data, usize)
    raise ValueError(f"compression type {kind} is not handled (LZMA bundles: rebuild with ChunkBasedCompression)")


# ----------------------------------------------------------------------------------------------------------------- UnityFS

def cstr(b, i):
    j = b.index(b"\0", i)
    return b[i:j], j + 1


def read_bundle(b):
    if b[:8] != b"UnityFS\0": raise ValueError("not a UnityFS bundle")
    i = 8
    fmt = struct.unpack(">I", b[i:i + 4])[0]; i += 4
    engine, i = cstr(b, i)
    version, i = cstr(b, i)
    size, csz, usz, flags = struct.unpack(">qIII", b[i:i + 20]); i += 20
    if size != len(b): raise ValueError("the bundle's size field does not match the file")
    if fmt >= 7: i = (i + 15) & ~15
    if flags & 0x80:
        info = b[len(b) - csz:]
    else:
        info = b[i:i + csz]; i += csz
    info = decompress(info, flags & 0x3F, usz)
    j = 16
    nb = struct.unpack(">i", info[j:j + 4])[0]; j += 4
    blocks = []
    for _ in range(nb):
        blocks.append(struct.unpack(">IIH", info[j:j + 10])); j += 10
    data = bytearray()
    for u, c, f in blocks:
        data += decompress(b[i:i + c], f & 0x3F, u); i += c
    nn = struct.unpack(">i", info[j:j + 4])[0]; j += 4
    nodes = []
    for _ in range(nn):
        off, sz, f = struct.unpack(">qqI", info[j:j + 20]); j += 20
        name, j = cstr(info, j)
        nodes.append([name, f, bytes(data[off:off + sz])])
    return {"fmt": fmt, "engine": engine, "version": version, "flags": flags, "nodes": nodes}


def write_bundle(bundle):
    data = b"".join(n[2] for n in bundle["nodes"])
    blocks, payload = [], bytearray()
    for k in range(0, len(data), BLOCK):
        chunk = data[k:k + BLOCK]
        z = lz4_encode(chunk)
        if lz4_decode(z, len(chunk)) != chunk: raise AssertionError("LZ4 round trip failed")
        if len(z) < len(chunk):
            blocks.append((len(chunk), len(z), 2)); payload += z
        else:
            blocks.append((len(chunk), len(chunk), 0)); payload += chunk
    info = bytearray(16) + struct.pack(">i", len(blocks))
    for u, c, f in blocks: info += struct.pack(">IIH", u, c, f)
    info += struct.pack(">i", len(bundle["nodes"]))
    off = 0
    for name, f, d in bundle["nodes"]:
        info += struct.pack(">qqI", off, len(d), f) + name + b"\0"
        off += len(d)
    head = b"UnityFS\0" + struct.pack(">I", bundle["fmt"]) + bundle["engine"] + b"\0" + bundle["version"] + b"\0"
    flags = 0x40   # blocks and directory together, directory uncompressed
    fixed = 20
    start = len(head) + fixed
    if bundle["fmt"] >= 7: start = (start + 15) & ~15
    total = start + len(info) + len(payload)
    head += struct.pack(">qIII", total, len(info), len(info), flags)
    head += bytes(start - len(head))
    out = head + bytes(info) + bytes(payload)
    assert len(out) == total
    return out


# ------------------------------------------------------------------------------------------------------- serialized files

class Node:
    __slots__ = ("level", "flags", "name", "size", "meta", "children")

    def __init__(self, level, flags, name, size, meta):
        self.level, self.flags, self.name, self.size, self.meta, self.children = level, flags, name, size, meta, []


def read_serialized(buf):
    meta_size, file_size, version, data_off = struct.unpack(">IIII", buf[:16])
    if version not in (17, 18, 19, 20, 21): raise ValueError(f"serialized file version {version} is not handled")
    if buf[16] != 0: raise ValueError("big-endian serialized file")
    if file_size != len(buf): raise ValueError("the serialized file's size field does not match")
    i = 20
    _, i = cstr(buf, i)
    i += 4
    tree = buf[i] != 0; i += 1
    if not tree: raise ValueError("the bundle has no type trees")
    count = struct.unpack("<i", buf[i:i + 4])[0]; i += 4
    types = []
    for _ in range(count):
        cid = struct.unpack("<i", buf[i:i + 4])[0]; i += 4
        i += 3
        if cid == 114 or cid < 0: i += 16
        i += 16
        n, sbuf = struct.unpack("<ii", buf[i:i + 8]); i += 8
        size = 32 if version >= 19 else 24
        raw = [struct.unpack("<HBBIIiii", buf[i + size * k:i + size * k + 24]) for k in range(n)]
        i += size * n
        strings = buf[i:i + sbuf]; i += sbuf
        root, stack = None, []
        for (_, level, flags, _, noff, nsize, _, meta) in raw:
            # a name from Unity's common string table keeps its offset; none of the fields used here is one
            name = f"#{noff & 0x7FFFFFFF}" if noff & 0x80000000 else strings[noff:strings.index(b"\0", noff)].decode("latin-1")
            node = Node(level, flags, name, nsize, meta)
            while stack and stack[-1].level >= level: stack.pop()
            if stack: stack[-1].children.append(node)
            else: root = node
            stack.append(node)
        if version >= 21:
            deps = struct.unpack("<i", buf[i:i + 4])[0]; i += 4 + 4 * deps
        types.append((cid, root))
    n = struct.unpack("<i", buf[i:i + 4])[0]; i += 4
    objs = []
    for _ in range(n):
        i = (i + 3) & ~3
        entry = i + 8   # byteStart, byteSize follow the path ID
        start, size, tidx = struct.unpack("<IIi", buf[entry:entry + 12])
        objs.append({"entry": entry, "start": start, "size": size, "class": types[tidx][0], "tree": types[tidx][1]})
        i = entry + 12
    return {"data_off": data_off, "objs": objs, "head": buf[:data_off], "buf": buf}


def write_serialized(sf, new_bytes):
    """new_bytes: object index -> its new bytes (the others keep theirs). Objects keep their order, 8-byte aligned."""
    head = bytearray(sf["head"])
    data = bytearray()
    for k, o in sorted(enumerate(sf["objs"]), key=lambda t: t[1]["start"]):
        body = new_bytes.get(k, sf["buf"][sf["data_off"] + o["start"]:sf["data_off"] + o["start"] + o["size"]])
        data += bytes((-len(data)) % 8)
        struct.pack_into("<II", head, o["entry"], len(data), len(body))
        data += body
    out = bytes(head) + bytes(data)
    return out[:4] + struct.pack(">I", len(out)) + out[8:]   # the file size


def read_value(node, buf, pos):
    if node.flags & 1:
        n = struct.unpack("<i", buf[pos:pos + 4])[0]; pos += 4
        el = node.children[1]
        if not el.children and el.size in (1, 2, 4, 8):
            val = buf[pos:pos + n * el.size]; pos += n * el.size
            if el.size == 4: val = list(struct.unpack(f"<{n}I", val))
        else:
            val = []
            for _ in range(n):
                v, pos = read_value(el, buf, pos); val.append(v)
    elif not node.children:
        if node.size < 0: raise ValueError(f"field {node.name!r} has no fixed size")
        val = buf[pos:pos + node.size]; pos += node.size
    elif len(node.children) == 1 and node.children[0].flags & 1:
        val, pos = read_value(node.children[0], buf, pos)
    else:
        val = {}
        for c in node.children:
            v, pos = read_value(c, buf, pos); val[c.name] = v
    if node.meta & 0x4000: pos = (pos + 3) & ~3
    return val, pos


def read_fields(root, buf, pos):
    """The object's top-level fields: name -> (value, start, end)."""
    out = {}
    for c in root.children:
        v, end = read_value(c, buf, pos)
        out[c.name] = (v, pos, end)
        pos = end
    return out, pos


# ------------------------------------------------------------------------------------------------------------ the programs

def _align4(i): return (i + 3) & ~3


def code_span(sp):
    """Where a D3D11 sub-program's code length and code are (2019.4: version, type, 4 stats, keywords, local keywords)."""
    if struct.unpack("<i", sp[:4])[0] != 201806140: raise ValueError("unknown sub-program version")
    i = 24
    for _ in range(2):
        n = struct.unpack("<i", sp[i:i + 4])[0]; i += 4
        for _ in range(n):
            ln = struct.unpack("<i", sp[i:i + 4])[0]; i = _align4(i + 4 + ln)
    length = struct.unpack("<i", sp[i:i + 4])[0]
    return i, i + 4, length


def convert_blob(blob, broken=False):
    n = struct.unpack("<i", blob[:4])[0]
    entries = [struct.unpack("<iii", blob[4 + 12 * k:16 + 12 * k]) for k in range(n)]
    sps, changed = [], 0
    for off, ln, seg in entries:
        if seg != 0: raise ValueError("a program in another blob segment: not handled")
        sp = blob[off:off + ln]
        at, code_at, length = code_span(sp)
        code = sp[code_at:code_at + length]
        if code[0] == 2:
            # bytes 1-5 are the same fields in both versions (the game's own 2019.4.19 shaders show the same values there)
            extra = code[6:6 + EXTRA]
            if any(extra) or code[6 + EXTRA:10 + EXTRA] != b"DXBC":
                raise ValueError("a version 2 program that is not 6 bytes + 32 zero bytes + DXBC: not touched")
            dxbc_size = struct.unpack("<I", code[6 + EXTRA + 24:6 + EXTRA + 28])[0]
            if 6 + EXTRA + dxbc_size != length: raise ValueError("a program's DXBC does not fill its code")
            if broken:
                new_code = bytes([1]) + code[1:]
            else:
                new_code = bytes([1]) + code[1:6] + code[6 + EXTRA:]
            sp = sp[:at] + struct.pack("<i", len(new_code)) + new_code + sp[code_at + length:]
            changed += 1
        elif code[0] == 1:
            if code[6:10] != b"DXBC": raise ValueError("a version 1 program without DXBC after its header")
        else:
            raise ValueError(f"a D3D11 program with header version {code[0]}: not handled")
        sps.append((sp, seg))
    table, body = bytearray(struct.pack("<i", n)), bytearray()
    pos = 4 + 12 * n
    for sp, seg in sps:
        table += struct.pack("<iii", pos + len(body), len(sp), seg)
        body += sp
    return bytes(table + body), changed


def dxbc_list(blob):
    """(header version, DXBC bytes, bytes after the code) of every sub-program, for checking."""
    n = struct.unpack("<i", blob[:4])[0]
    out = []
    for k in range(n):
        off, ln, seg = struct.unpack("<iii", blob[4 + 12 * k:16 + 12 * k])
        sp = blob[off:off + ln]
        at, code_at, length = code_span(sp)
        code = sp[code_at:code_at + length]
        d = code.find(b"DXBC")
        out.append((code[0], d, code[d:], sp[:at], sp[code_at + length:]))
    return out


def fix_shader(fields, broken=False):
    plats = fields["platforms"][0]
    offs, clens, dlens = fields["offsets"][0], fields["compressedLengths"][0], fields["decompressedLengths"][0]
    blob = fields["compressedBlob"][0]
    parts, new_offs, new_clens, new_dlens, changed, checks = [], [], [], [], 0, []
    pos = 0
    for k, p in enumerate(plats):
        if p == D3D11 and len(offs[k]) != 1: raise ValueError("D3D11 programs in more than one blob segment: not handled")
        po, pc, pd = [], [], []
        for o, c, d in zip(offs[k], clens[k], dlens[k]):
            raw = lz4_decode(blob[o:o + c], d)
            if p == D3D11:
                new, ch = convert_blob(raw, broken)
                changed += ch
                checks.append((raw, new))
            else:
                new = raw
            z = lz4_encode(new)
            if lz4_decode(z, len(new)) != new: raise AssertionError("LZ4 round trip failed")
            po.append(pos); pc.append(len(z)); pd.append(len(new))
            parts.append(z); pos += len(z)
        new_offs.append(po); new_clens.append(pc); new_dlens.append(pd)
    return b"".join(parts), new_offs, new_clens, new_dlens, changed, checks


def vec_vec(v):
    out = bytearray(struct.pack("<i", len(v)))
    for inner in v:
        out += struct.pack("<i", len(inner)) + struct.pack(f"<{len(inner)}I", *inner)
    return bytes(out)


def rewrite_shader(obj, root, broken=False):
    fields, end = read_fields(root, obj, 0)
    if end != len(obj): raise ValueError("the Shader object did not read to its end (another layout)")
    names = list(fields)
    want = ["platforms", "offsets", "compressedLengths", "decompressedLengths", "compressedBlob"]
    k = names.index("offsets")
    if names[k - 1:k + 4] != want: raise ValueError(f"unexpected Shader fields: {names}")
    blob, offs, clens, dlens, changed, checks = fix_shader(fields, broken)
    start = fields["offsets"][1]
    blob_end = fields["compressedBlob"][2]
    mid = vec_vec(offs) + vec_vec(clens) + vec_vec(dlens) + struct.pack("<i", len(blob)) + blob
    mid += bytes((-len(mid)) % 4)
    new = obj[:start] + mid + obj[blob_end:]
    return new, changed, checks


# --------------------------------------------------------------------------------------------------------------------- main

def convert(path, out=None, check=False, broken=False):
    raw = open(path, "rb").read()
    bundle = read_bundle(raw)
    total, report = 0, []
    for node in bundle["nodes"]:
        name, f, data = node
        if not (f & 4): continue
        sf = read_serialized(data)
        new_bytes = {}
        for k, o in enumerate(sf["objs"]):
            if o["class"] != SHADER: continue
            obj = data[sf["data_off"] + o["start"]:sf["data_off"] + o["start"] + o["size"]]
            new, changed, checks = rewrite_shader(obj, o["tree"], broken)
            # checks: the same programs, the same DXBC, the same bytes around the code; only the header changed
            for old_blob, new_blob in checks:
                a, b = dxbc_list(old_blob), dxbc_list(new_blob)
                if len(a) != len(b): raise AssertionError("program count changed")
                for (va, da, xa, ha, ta), (vb, db, xb, hb, tb) in zip(a, b):
                    if xa != xb or ha != hb or ta != tb: raise AssertionError("a program's DXBC or other data changed")
                    if not broken and (vb != 1 or db != 6): raise AssertionError("a program did not end up as version 1")
            if changed:
                new_bytes[k] = new
                total += changed
                report.append(f"{name.decode()}: Shader object {o['size']} -> {len(new)} bytes, {changed} D3D11 programs to version 1")
        if new_bytes:
            node[2] = write_serialized(sf, new_bytes)
    if not total:
        print(f"{path}: every D3D11 program is already version 1; nothing to do")
        return 0
    for line in report: print(line)
    if check:
        print("(--check: nothing written)")
        return total
    result = write_bundle(bundle)
    # read the result back: the same files, the objects other than the shaders byte for byte, every program version 1
    again = read_bundle(result)
    if [(n[0], n[1]) for n in again["nodes"]] != [(n[0], n[1]) for n in bundle["nodes"]]: raise AssertionError("files changed")
    for (n1, f1, d1), (n2, f2, d2) in zip(bundle["nodes"], again["nodes"]):
        if d1 != d2: raise AssertionError(f"{n1!r} did not read back the same")
    target = out or path
    tmp = target + ".tmp"
    with open(tmp, "wb") as fh: fh.write(result)
    os.replace(tmp, target)
    print(f"wrote {target}: {len(raw)} -> {len(result)} bytes, {total} programs converted" + (" (BROKEN on purpose)" if broken else ""))
    return total


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if not args: sys.exit(__doc__)
    if "--make-broken" in sys.argv and (len(args) < 2 or os.path.abspath(args[1]) == os.path.abspath(args[0])):
        sys.exit("--make-broken needs an OUT path other than the bundle")
    convert(args[0], args[1] if len(args) > 1 else None, check="--check" in sys.argv, broken="--make-broken" in sys.argv)
