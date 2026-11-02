"""Bounded BGI ARC20 / DSC / compiled-v1 dialogue reader.

Archive and DSC algorithms adapted from morkt/GARbro (MIT); compiled bytecode
schema and stack handling adapted from VNTranslationTools (MIT, arcusmaximus).
See licenses/ScriptExtraction. Does not execute bytecode or modify game files.
"""
import pathlib
import struct
from bgi_opcodes import BGI_V1_OPERANDS

MAX_BYTES = 64 * 1024 * 1024


def need(ok):
    if not ok:
        raise ValueError("BGI 剧本格式或边界无效，未开始翻译。")


def u32(b, at):
    need(0 <= at <= len(b) - 4)
    return struct.unpack_from('<I', b, at)[0]


def dsc_decode(b):
    if not b.startswith(b'DSC FORMAT 1.00\0'):
        return b
    size = u32(b, 20); tokens = u32(b, 24); seed = u32(b, 16)
    need(0 < size <= MAX_BYTES and tokens <= size and len(b) >= 544)
    magic = int.from_bytes(b[:2], 'little') << 16
    codes = []
    for symbol, value in enumerate(b[32:544]):
        lo = 20021 * (seed & 65535)
        hi = ((magic | (seed >> 16)) * 20021 + seed * 346 + (lo >> 16)) & 65535
        seed = ((hi << 16) + (lo & 65535) + 1) & 0xffffffff
        depth = (value - (hi & 255)) & 255
        need(depth <= 32)
        if depth:
            codes.append((depth, symbol))
    need(1 < len(codes) <= 512)
    # Canonical MSB Huffman codes, ordered by depth then symbol.
    table = {}; code = 0; previous = 0
    for depth, symbol in sorted(codes):
        code <<= depth - previous; need(code < (1 << depth))
        table[(depth, code)] = symbol; code += 1; previous = depth
    at = 544; value = 0; bits = 0; out = bytearray()
    def take(n):
        nonlocal at, value, bits
        while bits < n:
            need(at < len(b)); value = (value << 8) | b[at]; at += 1; bits += 8
        bits -= n; result = (value >> bits) & ((1 << n) - 1); value &= (1 << bits) - 1
        return result
    for _ in range(tokens):
        code = 0; symbol = None
        for depth in range(1, previous + 1):
            code = (code << 1) | take(1)
            symbol = table.get((depth, code))
            if symbol is not None: break
        need(symbol is not None)
        if symbol < 256:
            need(len(out) < size); out.append(symbol)
        else:
            distance = take(12) + 2; length = (symbol & 255) + 2
            need(distance <= len(out) and len(out) + length <= size)
            for _ in range(length): out.append(out[-distance])
    need(len(out) == size)
    return bytes(out)


def archive_entries(b):
    need(len(b) >= 16)
    if b.startswith(b'BURIKO ARC20'):
        stride = 128; name_bytes = 96
    elif b.startswith(b'PackFile    '):
        stride = 32; name_bytes = 16
    else:
        raise ValueError("此 BGI 封包版本尚未适配。")
    count = u32(b, 12); base = 16 + count * stride
    need(0 < count <= 10000 and base <= len(b))
    for i in range(count):
        at = 16 + i * stride
        name = b[at:at + name_bytes].split(b'\0')[0].decode('cp932')
        offset = base + u32(b, at + name_bytes); size = u32(b, at + name_bytes + 4)
        need(name and offset >= base and 0 < size <= MAX_BYTES and offset + size <= len(b))
        yield name, dsc_decode(b[offset:offset + size])


def script_rows(b, locations=False, decode=None):
    magic = b'BurikoCompiledScriptVer1.00\0'
    if not b.startswith(magic):
        return
    base = len(magic) + u32(b, len(magic)); at = base; max_code = 0; stack = []; rows = []
    string_start = len(b)
    need(len(magic) + 4 <= base < len(b))
    def text(address):
        nonlocal string_start
        offset = base + address; need(base <= offset < len(b))
        string_start = min(string_start, offset)
        end = b.index(0, offset); need(end - offset <= 16384)
        return decode(b[offset:end]) if decode else b[offset:end].decode('cp932')
    for _ in range(min(len(b) // 4, 2_000_000)):
        need(at + 4 <= string_start); position = at; op = u32(b, at); at += 4
        # Newer VM intrinsics (>=80) take operands from the VM stack. Only the
        # small core instruction set carries immediate words. See msg-tool's
        # V1_INSTS and disassemble(), with the supported intrinsic range bounded.
        if op not in BGI_V1_OPERANDS and not 0x80 <= op <= 0x4ff:
            raise ValueError(f"BGI 含未适配的指令：{op:04X}（{position-base:X}）。")
        if op == 3:
            address = u32(b, at); text(address); stack.append((at - base, address)); at += 4
        elif op in (0x140, 0x143):
            need(stack); index, address = stack.pop(); message = text(address)
            speaker = text(stack.pop()[1]) if stack else ''
            if message: rows.append((position - base, speaker, message, base + index) if locations else (position - base, speaker, message))
        elif op == 0x160:
            for index, address in stack:
                message = text(address)
                if message: rows.append((index, '', message, base + index) if locations else (index, '', message))
            stack.clear()
        elif op == 0x1c:
            if stack and text(stack.pop()[1]) == '_SelectEx':
                for index, address in stack:
                    message = text(address)
                    if message: rows.append((index, '', message, base + index) if locations else (index, '', message))
                stack.clear()
        else:
            for typ in BGI_V1_OPERANDS.get(op, ''):
                value = u32(b, at); at += 4
                if typ == 'c':
                    need(value < len(b) - base); max_code = max(max_code, value)
        need(at <= string_start and len(stack) <= 10000 and len(rows) <= 100000)
        if op in (0x7e, 0x7f, 0xfe, 0x1b5): stack.clear()
        if op in (0x1b, 0xf4) and max_code < at - base:
            return rows
    raise ValueError("BGI 剧本指令未正常结束。")


def sources(directory):
    # Verified original-game script group; avoid third-party translation archives,
    # multimedia archives and user save/backup directories.
    directory = pathlib.Path(directory) / 'Archive'
    if not directory.is_dir() or directory.is_symlink() or directory.is_junction(): return []
    return sorted(directory.glob('data01*.arc'))
