"""Minimal binary FBX (7.x) reader: returns a tree of (name, props, children). No external modules."""
import struct
import zlib

ARR = {b'f': ('f', 4), b'd': ('d', 8), b'l': ('q', 8), b'i': ('i', 4), b'b': ('?', 1)}


class Node:
    __slots__ = ("name", "props", "children")

    def __init__(self, name, props, children):
        self.name, self.props, self.children = name, props, children

    def find(self, name):
        for c in self.children:
            if c.name == name:
                return c
        return None

    def findall(self, name):
        return [c for c in self.children if c.name == name]

    def __repr__(self):
        return "Node(%s, %d props, %d children)" % (self.name, len(self.props), len(self.children))


def _read_prop(buf, pos):
    t = buf[pos:pos + 1]; pos += 1
    if t == b'Y': return struct.unpack_from('<h', buf, pos)[0], pos + 2
    if t == b'C': return buf[pos] != 0, pos + 1
    if t == b'I': return struct.unpack_from('<i', buf, pos)[0], pos + 4
    if t == b'F': return struct.unpack_from('<f', buf, pos)[0], pos + 4
    if t == b'D': return struct.unpack_from('<d', buf, pos)[0], pos + 8
    if t == b'L': return struct.unpack_from('<q', buf, pos)[0], pos + 8
    if t in (b'S', b'R'):
        n = struct.unpack_from('<I', buf, pos)[0]; pos += 4
        raw = bytes(buf[pos:pos + n]); pos += n
        return (raw if t == b'R' else raw.decode('utf-8', 'replace')), pos
    if t in ARR:
        n, enc, clen = struct.unpack_from('<III', buf, pos); pos += 12
        data = bytes(buf[pos:pos + clen]); pos += clen
        if enc == 1:
            data = zlib.decompress(data)
        fmt, size = ARR[t]
        if len(data) != n * size:
            raise ValueError("array size mismatch")
        import array
        a = array.array({'f': 'f', 'd': 'd', 'q': 'q', 'i': 'i', '?': 'b'}[fmt])
        a.frombytes(data)
        return a, pos
    raise ValueError("unknown property type %r at %d" % (t, pos - 1))


def _read_node(buf, pos, wide):
    if wide:
        end, nprops, plen = struct.unpack_from('<QQQ', buf, pos); pos += 24
    else:
        end, nprops, plen = struct.unpack_from('<III', buf, pos); pos += 12
    nlen = buf[pos]; pos += 1
    if end == 0:
        return None, pos
    name = bytes(buf[pos:pos + nlen]).decode('ascii', 'replace'); pos += nlen
    props = []
    for _ in range(nprops):
        v, pos = _read_prop(buf, pos)
        props.append(v)
    children = []
    while pos < end:
        child, pos = _read_node(buf, pos, wide)
        if child is None:
            break
        children.append(child)
    return Node(name, props, children), end


def load(path):
    with open(path, 'rb') as f:
        buf = memoryview(f.read())
    if bytes(buf[:20]) != b'Kaydara FBX Binary  ':
        raise ValueError("not a binary FBX")
    version = struct.unpack_from('<I', buf, 23)[0]
    wide = version >= 7500
    pos = 27
    top = []
    while pos < len(buf):
        n, pos = _read_node(buf, pos, wide)
        if n is None:
            break
        top.append(n)
    return version, Node("root", [], top)
