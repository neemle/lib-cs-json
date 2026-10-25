# Copyright (c) Neemle contributors. Licensed under the MIT License.
import struct

def pe_pdb_identity(pe, pdb):
    assert pe[:2] == b'MZ'
    head = struct.unpack_from('<I', pe, 60)[0]
    assert pe[head:head + 4] == b'PE\x00\x00'
    sections = struct.unpack_from('<H', pe, head + 6)[0]
    optlen = struct.unpack_from('<H', pe, head + 20)[0]
    opt = head + 24
    magic = struct.unpack_from('<H', pe, opt)[0]
    dirs = opt + (112 if magic == 523 else 96)
    rva, size = struct.unpack_from('<II', pe, dirs + 6 * 8)

    def offset(rva):
        for i in range(sections):
            vs, va, rs, rp = struct.unpack_from('<IIII', pe, opt + optlen + i * 40 + 8)
            if va <= rva < va + max(vs, rs):
                return rp + rva - va
        raise ValueError('Unmapped RVA')
    entries = []
    for pos in range(offset(rva), offset(rva) + size, 28):
        _, stamp, _, _, kind, length, _, raw = struct.unpack_from('<IIHHIIII', pe, pos)
        if kind == 2:
            codeview = pe[raw:raw + length]
            assert codeview[:4] == b'RSDS'
            entries.append((codeview[4:20], stamp))
    assert len(entries) == 1
    assert pdb[:4] == b'BSJB'
    version_length = struct.unpack_from('<I', pdb, 12)[0]
    header = 16 + version_length + 3 & ~3
    _, streams = struct.unpack_from('<HH', pdb, header)
    pos = header + 4
    pdbpos = None
    for i in range(streams):
        off, length = struct.unpack_from('<II', pdb, pos)
        start = pos + 8
        end = pdb.index(0, start)
        name = pdb[start:end]
        pos = end + 4 & ~3
        if name == b'#Pdb':
            pdbpos = off
    assert pdbpos is not None
    identifier = pdb[pdbpos:pdbpos + 20]
    assert identifier[:16] == entries[0][0] and struct.unpack_from('<I', identifier, 16)[0] == entries[0][1], 'Mismatched PE/PDB identity'
    return (pdbpos, identifier.hex())
