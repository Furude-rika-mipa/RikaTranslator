"""Offline script extraction. GPL-3.0; third-party notices: licenses/ScriptExtraction.

Archive algorithms adapted from morkt/GARbro (MIT). YSTB parsing follows
arcusmaximus/VNTranslationTools (MIT). Siglus parsing uses vendored SSSU (0BSD).
No game is launched, no network API is used, and game files are opened read-only.
"""
import collections
import configparser
import contextlib
import io
import json
import pathlib
import re
import struct
import sys
import zlib

MAX_FILE = 64 * 1024 * 1024
MAX_LINES = 100_000


def require(ok, message="剧本格式或边界无效，未开始翻译。"):
    if not ok:
        raise ValueError(message)


def u32(b, at):
    require(0 <= at <= len(b) - 4)
    return struct.unpack_from("<I", b, at)[0]


def bounded(b, at, size):
    require(0 <= at <= len(b) and 0 <= size <= MAX_FILE and at + size <= len(b))
    return b[at:at + size]


def read(path, limit=MAX_FILE):
    path = pathlib.Path(path)
    for parent in [path, *path.parents]:
        require(not parent.is_symlink() and not parent.is_junction(), "输入路径含链接，请选择普通游戏目录。")
    require(path.is_file() and path.stat().st_size <= limit, "资源文件不存在或超出读取范围。")
    return path.read_bytes()


def cstring(b, at, encoding="cp932", wide=False):
    require(0 <= at < len(b))
    if wide:
        end = at
        while end + 2 <= len(b) and b[end:end + 2] != b"\0\0":
            end += 2
        require(end + 2 <= len(b) and end - at <= 16384)
        return b[at:end].decode("utf-16le"), end + 2
    end = b.index(0, at)
    require(end - at <= 16384)
    return b[at:end].decode(encoding), end + 1


def unpack_acp(b):
    if not b.startswith(b"acp\0"):
        return b
    size = int.from_bytes(bounded(b, 4, 4), "big")
    require(size <= MAX_FILE)
    # Bounded MSB LZW decoder; dictionary stores output positions (GARbro).
    out = bytearray(); dictionary = []; width = 9; at = 8; bits = 0; value = 0
    while len(out) < size:
        while bits < width:
            require(at < len(b)); value = (value << 8) | b[at]; at += 1; bits += 8
        bits -= width; token = (value >> bits) & ((1 << width) - 1); value &= (1 << bits) - 1
        if token == 256:
            break
        if token == 257:
            width += 1; require(width <= 24); continue
        if token == 258:
            width = 9; dictionary = []; continue
        require(len(dictionary) < 0x8900)
        dictionary.append(len(out))
        if token < 256:
            out.append(token)
        else:
            token -= 259; require(0 <= token < len(dictionary) - 1)
            start = dictionary[token]; count = min(size - len(out), dictionary[token + 1] - start + 1)
            require(0 < count <= MAX_FILE and start < len(out))
            for i in range(count):
                out.append(out[start + i])
    require(len(out) == size)
    return bytes(out)


def esc_archive(b):
    require(b.startswith(b"ESC-ARC2")); seed = u32(b, 8)
    def key():
        nonlocal seed
        seed ^= 0x65AC9365
        seed ^= (((seed >> 1) ^ seed) >> 3) ^ (((seed << 1) ^ seed) << 3)
        seed &= 0xffffffff
        return seed
    count = u32(b, 12) ^ key(); names_size = u32(b, 16) ^ key()
    require(0 < count <= 10000 and names_size <= 2 * 1024 * 1024)
    index = bytearray(bounded(b, 20, count * 12)); names = bounded(b, 20 + len(index), names_size)
    for at in range(0, len(index), 4):
        struct.pack_into("<I", index, at, u32(index, at) ^ key())
    total = 0
    for name_at, offset, size in struct.iter_unpack("<III", index):
        name, _ = cstring(names, name_at)
        data = unpack_acp(bounded(b, offset, size)); total += len(data)
        require(total <= 512 * 1024 * 1024)
        yield name, data


def will_archive(b):
    count = u32(b, 0); end = 8 + u32(b, 4); at = 8
    require(0 < count <= 10000 and end <= len(b))
    total = 0
    for _ in range(count):
        size = u32(b, at); offset = u32(b, at + 4); at += 8
        name, at = cstring(b[:end], at, wide=True)
        if name.lower().endswith(".ws2"):
            total += size; require(total <= 512 * 1024 * 1024)
            data = bounded(b, end + offset, size)
            yield name, bytes((x >> 2) | ((x & 3) << 6) for x in data)
    require(at == end)


def ypf_archive(b):
    require(b.startswith(b"YPF\0")); version = u32(b, 4); count = u32(b, 8); end = 32 + u32(b, 12); at = 32
    require(0 < count <= 10000 and end <= len(b) and 290 <= version <= 500)
    swap = [9, 11, 13, 19, 21, 27, 32, 35, 38, 41, 44, 47, 46, 50]
    if version < 290:
        raise ValueError("此 YPF 版本尚未适配。")
    key = None; total = 0
    for _ in range(count):
        require(at + 5 <= end); length = b[at + 4] ^ 255
        if length in swap:
            length = swap[swap.index(length) ^ 1]
        at += 5; raw_name = bounded(b, at, length); at += length
        require(length >= 4 and at + 18 <= end)
        if key is None:
            key = raw_name[-4] ^ 46
        name = bytes(x ^ key for x in raw_name).decode("cp932")
        _, packed, unpacked, size, offset = struct.unpack_from("<BBIII", b, at)
        at += 22 if version >= 473 else 18
        require(at <= end)
        if not name.lower().endswith(".ybn"):
            continue
        data = bounded(b, offset, size)
        require(unpacked <= MAX_FILE)
        total += unpacked; require(total <= 512 * 1024 * 1024)
        if packed:
            decoder = zlib.decompressobj(); data = decoder.decompress(data, unpacked + 1)
            require(decoder.eof and not decoder.unused_data and not decoder.unconsumed_tail)
        require(len(data) == unpacked)
        yield name, data
    # YPF titles use both absolute index-end and index-byte-count conventions.
    require(at in (end, end - 32))


def adv_rows(b):
    # AdvHD 2.x stores typed message records as: 14, uint32 ID, UTF16 "char", UTF16 message.
    # Recognize that record rather than guessing all strings or interpreting graphics opcodes.
    marker = "char\0".encode("utf-16le")
    names = []
    for m in re.finditer(re.escape("%L".encode("utf-16le")), b):
        at = m.start()
        if at > 0 and b[at - 1] == 0x15:
            name, _ = cstring(b, at, wide=True)
            if name.startswith(("%LC", "%LF", "%LR")):
                names.append((at, name[3:]))
    last_name = ""; ni = 0
    for m in re.finditer(re.escape(marker), b):
        at = m.start()
        if at < 5 or b[at - 5] != 0x14:
            continue
        while ni < len(names) and names[ni][0] < at:
            last_name = names[ni][1]; ni += 1
        text, _ = cstring(b, m.end(), wide=True)
        text = re.sub(r"(?:%\w+)+$", "", text).replace("\\n", "\n")
        yield at - 5, last_name, text


def esc_rows(b):
    if not b.startswith(b"@mess:__"):
        return
    count, size = u32(b, 8), u32(b, 12); start = 16 + count * 4
    require(count <= MAX_LINES and start + size == len(b))
    offsets = [u32(b, 16 + i * 4) for i in range(count)]
    require(offsets == sorted(set(offsets)) and (not offsets or offsets[0] == 0))
    text = bytes(x ^ 0x55 for x in bounded(b, start, size))
    for i, at in enumerate(offsets):
        s, end = cstring(text, at)
        require(end == (offsets[i + 1] if i + 1 < count else size))
        yield i, "", s.replace("<r>", "\n")


def yuris_commands(b):
    require(b.startswith(b"YSCM")); count = u32(b, 8); require(count <= 255); at = 16; names = []
    for _ in range(count):
        name, at = cstring(b, at); names.append(name); require(at < len(b)); attrs = b[at]; at += 1
        for _ in range(attrs):
            _, at = cstring(b, at); at += 2; require(at <= len(b))
    return names


def yuris_rows(original, commands, key):
    if not original.startswith(b"YSTB"):
        return
    b = bytearray(original); count = u32(b, 8); sizes = [u32(b, i) for i in [12, 16, 20, 24]]
    require(sum(sizes) + 32 == len(b) and sizes[0] == count * 4 and sizes[1] % 12 == 0)
    ao = 32 + sizes[0]; vo = ao + sizes[1]
    at = 32
    for size in sizes:
        kb = struct.pack("<I", key)
        for i in range(size):
            b[at + i] ^= kb[i % 4]
        at += size
    attr = ao; group = []; group_at = 0
    def value(typ, data):
        if typ == 3:
            if len(data) < 5 or data[0] != 0x4d or int.from_bytes(data[1:3], "little") + 3 != len(data):
                return None
            data = data[3:]
            if data[:1] != data[-1:] or data[0] not in (34, 39, 96):
                return None
            data = data[1:-1]
        elif typ != 0:
            return None
        for raw, target in [(b"\xef\xf0", b"\n"), (b"\xef\xf2", b"\\p"), (b"\xef\xf3", b"\\c"), (b"\xef\xf5", b"\\u")]:
            data = data.replace(raw, target)
        return data.decode("cp932")
    def emit(index, text):
        match = re.match(r"^(.+?)[「『](.+)[』」]$", text, re.S)
        return index, match[1] if match else "", match[2] if match else text
    for i in range(count):
        op, n = b[32 + i * 4:34 + i * 4]; require(op < len(commands)); attrs = []; command = commands[op]
        for _ in range(n):
            require(attr + 12 <= vo); _, typ, length, off = struct.unpack_from("<hhII", b, attr); attr += 12
            eligible = typ in (0, 3) and command in ("WORD", "_", "GOSUB")
            if eligible:
                require(off + length <= sizes[2])
            attrs.append(value(typ, bytes(bounded(b, vo + off, length))) if eligible else None)
        text = attrs[0] if attrs else None
        if command == "WORD" or command == "_" and text is not None and "\\p" not in text:
            if text:
                if not group: group_at = i
                group.append(text)
            continue
        if group:
            yield emit(group_at, "".join(group)); group = []
        if command == "GOSUB" and attrs and (attrs[0] or "").upper() == "ES.SEL.SET":
            for j, s in enumerate(attrs[1:]):
                if s and re.search(r"[\u3040-\u30ff\u4e00-\u9fff]", s):
                    yield i * 256 + j + 1_000_000, "", s
    require(attr == vo)
    if group:
        yield emit(group_at, "".join(group))


def rld_table(seed):
    # Engine uses the original MT19937 69069 initialization, not Python's seed algorithm.
    state = []; x = seed
    for _ in range(624):
        a = (69069 * x + 1) & 0xffffffff
        state.append((x & 0xffff0000) | (a >> 16)); x = (69069 * a + 1) & 0xffffffff
    for i in range(624):
        y = (state[i] & 0x80000000) | (state[(i + 1) % 624] & 0x7fffffff)
        state[i] = state[(i + 397) % 624] ^ (y >> 1) ^ ((y & 1) * 0x9908b0df)
    out = []
    for y in state[:256]:
        y ^= y >> 11; y ^= (y << 7) & 0x9d2c5680; y ^= (y << 15) & 0xefc60000; y ^= y >> 18
        out.append(y ^ seed)
    return struct.pack("<256I", *out)


def rld_seeds(executable):
    import pefile
    pe = pefile.PE(data=read(executable))
    try:
        ini = configparser.ConfigParser(interpolation=None)
        ini.read_string(read(pathlib.Path(executable).parent / "ExHIBIT.ini", 1024 * 1024).decode("cp932"))
        keys = "CLASS TITLE W_VIEW H_VIEW N_REG N_STRREG N_SYSREG N_LOCREG N_USAVE N_ASAVE N_QSAVE N_CG N_MESSAGE N_SCENE N_SOUND".split()
        checks = []
        for title in [False, True]:
            for extra in [False, True]:
                selected = [k for k in keys if title or k != "TITLE"] + (["FLAGS", "GUID", "SVDATA"] if extra else [])
                text = "".join(ini.get("setting", k, fallback="").strip().strip('"') for k in selected).encode("cp932")
                checks.append(sum((sum(text[i::4]) & 255) << (i * 8) for i in range(4)))
        for typ in pe.DIRECTORY_ENTRY_RESOURCE.entries:
            if typ.id != 2: continue
            for res in typ.directory.entries:
                if res.id != 152: continue
                for lang in res.directory.entries:
                    d = lang.data.struct; b = pe.get_data(d.OffsetToData, d.Size)
                    if len(b) < 40: continue
                    w, h, _, bits = struct.unpack_from("<iiHH", b, 4)
                    if not (32 <= w <= 4096 and 32 <= abs(h) <= 4096 and bits in [24, 32] and u32(b, 16) == 0): continue
                    stride = ((w * bits + 31) // 32) * 4
                    require(len(b) >= 40 + stride * abs(h))
                    for reverse in [False, True]:
                        for channel in range(3):
                            seed = 0
                            for i in range(32):
                                row = abs(h) - 32 + (31 - i if reverse else i)
                                seed = (seed << 1) | (b[40 + row * stride + 31 * (bits // 8) + channel] & 1)
                            for check in checks:
                                yield seed ^ check
    finally:
        pe.close()


def rld_decode(original, table):
    b = bytearray(original)
    for i in range(min(len(b) - 16, 0xffc0) // 4 * 4):
        b[16 + i] ^= table[i % 1024]
    return bytes(b)


def rld_ops(b):
    require(b.startswith(b"\0DLR") and u32(b, 4) == 3)
    count = u32(b, 12); at = u32(b, 8) + 4
    require(count <= 100_000 and 16 <= at <= len(b))
    ops = []
    for index in range(count):
        require(at + 4 <= len(b)); op, ni, ns = struct.unpack_from("<HBB", b, at); at += 4
        require(op <= 4096); ints = [u32(b, at + i * 4) for i in range(ni)]; at += ni * 4; strings = []
        for _ in range(ns & 15):
            s, at = cstring(b, at); strings.append(s)
        ops.append((index, op, ints, strings))
    require(at == len(b))
    return ops


def extract(engine, executable, source):
    root = pathlib.Path(executable).parent; files = []; rows = []; warnings = []
    if engine == "BGI / Ethornell":
        from bgi_reader import sources, archive_entries, script_rows
        paths = sources(root)
        require(0 < len(paths) <= 100)
        if source != "Archive":
            paths = [p for p in paths if p.relative_to(root).as_posix() == source]
            require(paths, "请选择原始 BGI 剧本资源。")
        total = 0
        for path in paths:
            for name, b in archive_entries(read(path)):
                total += len(b); require(total <= 512 * 1024 * 1024)
                found = script_rows(b)
                if found is None: continue
                label = path.relative_to(root).as_posix() + "/" + name
                files.append(label); rows.extend((label, *r) for r in found)
                require(len(rows) <= MAX_LINES)
    elif engine == "WillPlus":
        for name, b in will_archive(read(root / source)):
            files.append(name); rows.extend((name, *r) for r in adv_rows(b))
    elif engine == "Escu:de":
        for name, b in esc_archive(read(root / source)):
            if b.startswith(b"@mess:__"):
                files.append(name); rows.extend((name, *r) for r in esc_rows(b))
    elif engine == "YU-RIS":
        resources = dict(ypf_archive(read(root / source)))
        command_file = next((b for n, b in resources.items() if pathlib.PureWindowsPath(n).name.lower() == "ysc.ybn"), None)
        require(command_file is not None, "没有找到 YU-RIS 指令表 ysc.ybn。")
        commands = yuris_commands(command_file)
        keys = collections.Counter(u32(b, 32 + u32(b, 12) + 8) for b in resources.values() if b.startswith(b"YSTB") and u32(b, 16) >= 12)
        require(keys, "没有找到 YSTB 剧本。")
        key, votes = keys.most_common(1)[0]
        require(votes * 2 >= sum(keys.values()), "此 YSTB 的编码方式尚未适配。")
        for name, b in resources.items():
            if b.startswith(b"YSTB"):
                files.append(name); rows.extend((name, *r) for r in yuris_rows(b, commands, key))
    elif engine == "ExHIBIT":
        paths = sorted((root / "rld").glob("*.rld")); require(0 < len(paths) <= 10000)
        sample = next((p for p in paths if re.match(r"\d", p.name)), paths[0]); raw = read(sample); table = None
        try:
            rld_ops(raw)
        except (ValueError, UnicodeError):
            for seed in dict.fromkeys(rld_seeds(executable)):
                candidate = rld_table(seed)
                try: rld_ops(rld_decode(raw, candidate))
                except (ValueError, UnicodeError, struct.error): continue
                table = candidate; break
            require(table is not None, "此 RLD 的解码方式尚未适配，未开始翻译。")
        names = {}
        chara = next((p for p in paths if p.name.lower() == "defchara.rld"), None)
        if chara:
            b = read(chara); ops = rld_ops(rld_decode(b, table) if table else b)
            for _, op, _, ss in ops:
                if op == 48 and ss:
                    values = ss[0].split(",")
                    if len(values) > 3 and values[0].isdigit(): names[int(values[0])] = values[3]
        for path in paths:
            if path.name.lower() == "def.rld": continue
            b = read(path); ops = rld_ops(rld_decode(b, table) if table else b); files.append(path.name)
            for index, op, ints, ss in ops:
                if op == 28 and len(ss) >= 2:
                    speaker = names.get(ints[0], "") if ss[0] == "*" and ints else "" if ss[0] == "$noname$" else ss[0]
                    rows.append((path.name, index, speaker, ss[1]))
    elif engine == "SiglusEngine":
        from siglus_ssu import pck, dat, textmap
        path = root / source; raw = read(path)
        require(u32(raw, 0) == 92 and u32(raw, 80) <= 10000)
        original_unpack = pck.lzss_unpack; decoded_bytes = 0
        def limited_unpack(blob):
            nonlocal decoded_bytes
            size = u32(blob, 4); require(size <= MAX_FILE)
            decoded_bytes += size; require(decoded_bytes <= 512 * 1024 * 1024)
            return original_unpack(blob)
        pck.lzss_unpack = limited_unpack
        # SSSU resolves the extra key from this game's EXE; no title-key database is bundled.
        for item in pck.iter_pck_scene_dat_items(raw, str(path), require_exe=True):
            b = item["blob"]; require(len(b) <= MAX_FILE)
            parsed = textmap._parse_scn_dat(b); require(parsed is not None)
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                bundle = dat.dat_disassembly_bundle(b, "scene.dat", pack_context=item["pack_context"], scene_name=item["scene_name"])
            require(bundle and bundle.get("complete"), "此 Siglus 场景含未适配的指令，未开始翻译。")
            strings = parsed[0]; speaker = ""; name = item["scene_name"] + ".dat"; files.append(name)
            for event in bundle.get("trace", []):
                op = event.get("op"); index = event.get("str_id", -1)
                if op == "CD_NAME" and 0 <= index < len(strings): speaker = strings[index]
                if op == "CD_TEXT" and 0 <= index < len(strings): rows.append((name, index, speaker, strings[index]))
            require(len(rows) <= MAX_LINES)
    else:
        raise ValueError("此引擎尚未适配剧本提取。")
    require(len(files) <= 10000 and len(rows) <= MAX_LINES)
    segments = []; seen = set()
    for file, index, speaker, text in rows:
        if not text.strip(): continue
        require(len(text) <= 4096 and len(speaker) <= 512 and len(file) <= 512)
        identity = (file, index)
        if identity in seen: continue
        seen.add(identity)
        segments.append(dict(file=file, index=index, speaker=speaker, source=text))
    require(segments, "资源可读取，但没有找到受支持的对白记录。")
    return dict(engine=engine, format="multi-engine-v1", resources=len(files), scriptFiles=len({r["file"] for r in segments}),
                scenes=len({r["file"] for r in segments}), segments=segments, warnings=warnings)


def main():
    if len(sys.argv) == 4 and sys.argv[1] == '--patch':
        from multi_patch import build
        request = json.loads(read(sys.argv[2], 32 * 1024 * 1024).decode('utf-8-sig'))
        build(request, sys.argv[3])
        return
    require(len(sys.argv) == 5)
    engine, executable, source, output = sys.argv[1:]
    result = extract(engine, executable, source)
    path = pathlib.Path(output)
    require(not path.exists() and path.parent.is_dir())
    path.write_text(json.dumps(result, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        # Never echo game text, process environment or source paths to the host log.
        print(json.dumps({"error": str(error) if isinstance(error, ValueError) else "剧本读取失败，格式可能尚未适配。",
                          "code": type(error).__name__, "module": error.name if isinstance(error, ModuleNotFoundError) else None}, ensure_ascii=True), file=sys.stderr)
        sys.exit(1)
