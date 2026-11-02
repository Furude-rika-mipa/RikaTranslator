"""Local, bounded resource patch writers. GPL-3.0; see ScriptExtraction/NOTICE.md.

Only writes a new output directory. Original game resources and executables are
read-only. No licensing/authentication code is inspected or changed.
"""
import collections
import hashlib
import json
import pathlib
import re
import struct
import contextlib
import io
import multi_engine as m


class Encoding:
    # Valid SJIS lead bytes with otherwise unused low bytes. MIT algorithm from
    # VNTranslationTools' SjisTunnelEncoding; avoid VM control punctuation.
    lows = [x for x in range(1, 64) if x not in (9, 10, 13, 32, 34, 39, 44)]
    leads = list(range(0x81, 0xa0)) + list(range(0xe0, 0xfd))

    def __init__(self): self.mapping = {}

    def encode(self, text):
        result = bytearray()
        for ch in text:
            try: result.extend(ch.encode('cp932')); continue
            except UnicodeEncodeError: pass
            m.require(ord(ch) <= 0xffff and not 0xd800 <= ord(ch) <= 0xdfff,
                      '当前日文引擎不能表示这个字符，请先替换补充平面字符。')
            if ch not in self.mapping:
                n = len(self.mapping); m.require(n < len(self.leads) * len(self.lows), '补丁扩展字数超过当前引擎编码容量。')
                self.mapping[ch] = bytes([self.leads[n // len(self.lows)], self.lows[n % len(self.lows)]])
            result.extend(self.mapping[ch])
        return bytes(result)

    def decode(self, data):
        reverse = {v: k for k, v in self.mapping.items()}; out = []; i = 0
        while i < len(data):
            length = 2 if data[i] in self.leads else 1
            token = bytes(data[i:i + length]); out.append(reverse.get(token) or token.decode('cp932')); i += length
        return ''.join(out)


def write_arc20(items):
    out = bytearray(b'BURIKO ARC20' + struct.pack('<I', len(items)) + bytes(128 * len(items)))
    base = len(out); seen = set()
    for i, (name, data) in enumerate(items):
        nb = name.encode('cp932'); m.require(0 < len(nb) < 96 and name not in seen); seen.add(name)
        at = 16 + 128 * i; out[at:at + len(nb)] = nb
        struct.pack_into('<II', out, at + 96, len(out) - base, len(data)); out.extend(data)
    return bytes(out)


def will_items(b):
    end = 8 + m.u32(b, 4); at = 8; items = []
    for _ in range(m.u32(b, 0)):
        size, offset = struct.unpack_from('<II', b, at); at += 8
        name, at = m.cstring(b[:end], at, wide=True)
        items.append((name, m.bounded(b, end + offset, size)))
    m.require(at == end); return items


def write_will(items):
    index = bytearray(); data = bytearray()
    for name, blob in items:
        index.extend(struct.pack('<II', len(blob), len(data)) + name.encode('utf-16le') + b'\0\0'); data.extend(blob)
    return struct.pack('<II', len(items), len(index)) + index + data


def will_parse(b):
    from will_opcodes import TABLES
    last = None
    for table in reversed(TABLES):
        at = 0; addresses = []; messages = {}; boundaries = set()
        def operands(schema):
            nonlocal at
            values = []; i = 0
            while i < len(schema):
                typ = schema[i]; i += 1; count = 1
                if i < len(schema) and schema[i] == '*': count = b[at]; at += 1; i += 1
                for _ in range(count):
                    start = at
                    if typ == 's':
                        text, at = m.cstring(b, at, wide=True); values.append((start, at, text)); continue
                    size = {'b': 1, 'h': 2, 'i': 4, 'a': 4, 'f': 4}[typ]; at += size; m.require(at <= len(b))
                    value = int.from_bytes(b[start:at], 'little')
                    if typ == 'a': m.require(value < len(b)); addresses.append((start, value))
                    values.append(value)
            return values
        def instruction():
            nonlocal at
            start = at; boundaries.add(at); op = b[at]; at += 1
            m.require(op in table, f'此 WS2 指令尚未适配：{op:02X}。')
            args = operands(table[op])
            if op == 0x14: messages[start] = args[2]
            if op == 0xf:
                for _ in range(args[0]): operands('hsbh'); instruction()
        try:
            while at < len(b) - 8: instruction()
            m.require(all(target in boundaries or target >= len(b) - 8 for _, target in addresses), 'WS2 跳转位置未通过验证。')
            return messages, addresses
        except (ValueError, IndexError, UnicodeError) as error:
            if last is None: last = ValueError(f'{error} (position {at:X})')
    raise ValueError(str(last))


def patch_will(b, rows, enc):
    messages, addresses = will_parse(b); edits = []
    for row in rows:
        start, end, original = messages[row['index']]
        source = re.sub(r'(?:%\w+)+$', '', original).replace('\\n', '\n')
        m.require(source == row['source'])
        suffix = re.search(r'(?:%\w+)+$', original)
        text = row['translation'].replace('\n', '\\n') + (suffix[0] if suffix else '')
        edits.append((start, end, text.encode('utf-16le') + b'\0\0'))
    edits.sort(); out = bytearray(); at = 0
    def relocated(pos):
        delta = 0
        for start, end, data in edits:
            m.require(not start < pos < end, 'WS2 跳转到了文本内部，未生成补丁。')
            if end <= pos: delta += len(data) - (end - start)
        return pos + delta
    for start, end, data in edits: m.require(start >= at); out.extend(b[at:start]); out.extend(data); at = end
    out.extend(b[at:])
    for pos, target in addresses: struct.pack_into('<I', out, relocated(pos), relocated(target))
    will_parse(out)
    return bytes(out)


def patch_bgi(b, rows, enc):
    import bgi_reader as bg
    locations = {r[0]: r for r in bg.script_rows(b, locations=True)}
    out = bytearray(b); magic = b'BurikoCompiledScriptVer1.00\0'; base = len(magic) + bg.u32(b, len(magic))
    for row in rows:
        original = locations[row['index']]; m.require(original[1:3] == (row['speaker'], row['source']))
        struct.pack_into('<I', out, original[3], len(out) - base)
        out.extend(enc.encode(row['translation']) + b'\0')
    result = {r[0]: r[2] for r in bg.script_rows(out, decode=enc.decode)}
    m.require(all(result[r['index']] == r['translation'] for r in rows))
    return bytes(out)


def patch_esc(b, rows, enc):
    original = list(m.esc_rows(b)); texts = [r[2] for r in original]
    for row in rows:
        m.require(texts[row['index']] == row['source']); texts[row['index']] = row['translation']
    data = bytearray(); offsets = []
    for text in texts: offsets.append(len(data)); data.extend(enc.encode(text.replace('\n', '<r>')) + b'\0')
    return b'@mess:__' + struct.pack('<II', len(texts), len(data)) + struct.pack('<' + 'I' * len(offsets), *offsets) + bytes(x ^ 0x55 for x in data)


def write_esc(items, seed):
    def key():
        nonlocal seed
        seed ^= 0x65ac9365; seed ^= (((seed >> 1) ^ seed) >> 3) ^ (((seed << 1) ^ seed) << 3); seed &= 0xffffffff; return seed
    initial = seed; names = bytearray(); desc = []; offset = 20 + 12 * len(items)
    for name, data in items: desc.append((len(names), data)); names.extend(name.encode('cp932') + b'\0')
    offset += len(names); header = b'ESC-ARC2' + struct.pack('<III', initial, len(items) ^ key(), len(names) ^ key()); index = bytearray(); payload = bytearray()
    for no, data in desc:
        for v in [no, offset + len(payload), len(data)]: index.extend(struct.pack('<I', v ^ key()))
        payload.extend(data)
    return header + index + names + payload


def patch_rld(b, rows, enc, table):
    plain = m.rld_decode(b, table) if table else b; ops = m.rld_ops(plain); replacements = {r['index']: r for r in rows}
    out = bytearray(plain[:m.u32(plain, 8) + 4])
    # Opcode integer operands refer to instruction indices, not byte positions.
    at = m.u32(plain, 8) + 4
    for index, op, ints, strings in ops:
        header = plain[at:at + 4 + 4 * len(ints)]; at += len(header); raw = []
        for _ in strings:
            end = plain.index(0, at); raw.append(plain[at:end]); at = end + 1
        if index in replacements:
            row = replacements[index]; m.require(op == 28 and strings[1] == row['source']); raw[1] = enc.encode(row['translation'])
        out.extend(header)
        for text in raw: out.extend(text + b'\0')
    return m.rld_decode(out, table) if table else bytes(out)


def patch_yuris(b, rows, enc, commands, key):
    data = bytearray(b); sizes = [m.u32(b, i) for i in (12, 16, 20, 24)]; at = 32; kb = struct.pack('<I', key)
    for size in sizes:
        for i in range(size): data[at + i] ^= kb[i % 4]
        at += size
    ao = 32 + sizes[0]; vo = ao + sizes[1]; attributes = []; groups = {}; group = []; group_index = 0; ad = ao
    def read_value(attr):
        p, typ, length, offset = attr; raw = bytes(data[vo + offset:vo + offset + length])
        if typ == 3:
            if len(raw) < 5 or raw[0] != 0x4d or int.from_bytes(raw[1:3], 'little') + 3 != len(raw): return None
            raw = raw[3:]
            if raw[:1] != raw[-1:] or raw[0] not in (34,39,96): return None
            raw = raw[1:-1]
        elif typ != 0: return None
        for a,z in [(b'\xef\xf0',b'\n'),(b'\xef\xf2',b'\\p'),(b'\xef\xf3',b'\\c'),(b'\xef\xf5',b'\\u')]: raw=raw.replace(a,z)
        return raw.decode('cp932')
    for i in range(m.u32(b, 8)):
        op,n = data[32 + i*4:34 + i*4]; attrs=[]; command=commands[op]
        for _ in range(n):
            _,typ,length,offset=struct.unpack_from('<hhII',data,ad); attr=(ad,typ,length,offset); ad+=12;attrs.append(attr);attributes.append(attr)
        values=[read_value(a) if command in ('WORD','_','GOSUB') else None for a in attrs];text=values[0] if values else None
        if command=='WORD' or command=='_' and text is not None and '\\p' not in text:
            if text:
                if not group:group_index=i
                group.append(attrs[0])
            continue
        if group:groups[group_index]=group;group=[]
        if command=='GOSUB' and values and (values[0] or '').upper()=='ES.SEL.SET':
            for j,s in enumerate(values[1:]):
                if s and re.search(r'[\u3040-\u30ff\u4e00-\u9fff]',s):groups[i*256+j+1_000_000]=[attrs[j+1]]
    if group:groups[group_index]=group
    extra=bytearray()
    for row in rows:
        attrs=groups[row['index']];old=''.join(read_value(a) for a in attrs);match=re.match(r'^(.+?)([「『])(.+)([』」])$',old,re.S)
        m.require((match[3] if match else old)==row['source'])
        replacement=(match[1]+match[2]+row['translation']+match[4]) if match else row['translation']
        for i,attr in enumerate(attrs):
            p,typ,length,offset=attr;raw=enc.encode(replacement if i==0 else '')
            for a,z in [(b'\n',b'\xef\xf0'),(b'\\p',b'\xef\xf2'),(b'\\c',b'\xef\xf3'),(b'\\u',b'\xef\xf5')]:raw=raw.replace(a,z)
            if typ==3:
                quote=data[vo+offset+3:vo+offset+4];raw=b'\x4d'+struct.pack('<H',len(raw)+2)+quote+raw+quote
            struct.pack_into('<II',data,p+4,len(raw),sizes[2]+len(extra));extra.extend(raw)
    out=data[:vo+sizes[2]]+extra+data[vo+sizes[2]:];struct.pack_into('<I',out,20,sizes[2]+len(extra));sizes[2]+=len(extra);at=32
    for size in sizes:
        for i in range(size):out[at+i]^=kb[i%4]
        at+=size
    return bytes(out)


def murmur2(data):
    h=len(data);at=0
    while at+4<=len(data):
        k=int.from_bytes(data[at:at+4],'little');at+=4;k=k*0x5bd1e995&0xffffffff;k^=k>>24;k=k*0x5bd1e995&0xffffffff;h=((h*0x5bd1e995)^k)&0xffffffff
    tail=data[at:]
    if tail:h^=int.from_bytes(tail,'little');h=h*0x5bd1e995&0xffffffff
    h^=h>>13;h=h*0x5bd1e995&0xffffffff;return (h^(h>>15))&0xffffffff


def xxh32(data):
    p1,p2,p3,p4,p5=0x9e3779b1,0x85ebca77,0xc2b2ae3d,0x27d4eb2f,0x165667b1
    def rot(n,k):return ((n<<k)|(n>>(32-k)))&0xffffffff
    def round(v,n):return rot((v+n*p2)&0xffffffff,13)*p1&0xffffffff
    at=0
    if len(data)>=16:
        vs=[(p1+p2)&0xffffffff,p2,0,(-p1)&0xffffffff]
        while at+16<=len(data):
            for i in range(4):vs[i]=round(vs[i],int.from_bytes(data[at:at+4],'little'));at+=4
        h=sum(rot(v,k) for v,k in zip(vs,[1,7,12,18]))&0xffffffff
    else:h=p5
    h=(h+len(data))&0xffffffff
    while at+4<=len(data):h=rot((h+int.from_bytes(data[at:at+4],'little')*p3)&0xffffffff,17)*p4&0xffffffff;at+=4
    while at<len(data):h=rot((h+data[at]*p5)&0xffffffff,11)*p1&0xffffffff;at+=1
    h^=h>>15;h=h*p2&0xffffffff;h^=h>>13;h=h*p3&0xffffffff;return (h^(h>>16))&0xffffffff


def write_ypf(original, changed):
    version=m.u32(original,4);count=m.u32(original,8);at=32;entries=[];key=None
    m.require(version>=480,'此 YPF 写回版本尚未通过验证。')
    import zlib
    checksum=None
    swap=[9,11,13,19,21,27,32,35,38,41,44,47,46,50]
    for _ in range(count):
        start=at;length=original[at+4]^255
        if length in swap:length=swap[swap.index(length)^1]
        at+=5;name_raw=original[at:at+length];at+=length
        if key is None:key=name_raw[-4]^46
        name=bytes(x^key for x in name_raw).decode('cp932');_,packed,unpacked,size,offset=struct.unpack_from('<BBIII',original,at)
        end=at+(22 if version>=473 else 18);entry=bytearray(original[start:end]);at=end
        old=m.bounded(original,offset,size);expected=m.u32(original,at-4)
        if expected:
            if checksum is None:
                checksum=next((f for f in [lambda x:zlib.adler32(x)&0xffffffff,murmur2,xxh32] if f(old)==expected),None)
                m.require(checksum is not None,'此 YPF 资源校验算法尚未适配。')
            m.require(checksum(old)==expected,'原始 YPF 资源校验失败。')
        blob=changed.get(name)
        if blob is not None:
            # Repack only modified entries. Preserve original name hashes and
            # untouched payload/checksum bytes; changed Adler32 is updated.
            raw_size=len(blob);blob=zlib.compress(blob) if packed else blob;rel=5+length
            struct.pack_into('<III',entry,rel+2,raw_size,len(blob),0)
            struct.pack_into('<I',entry,rel+18,checksum(blob) if expected else 0)
        else:blob=old
        entries.append((entry,blob,5+length+10))
    out=bytearray(original[:at]);pos=32
    for entry,blob,off in entries:
        struct.pack_into('<I',entry,off,len(out));out[pos:pos+len(entry)]=entry;pos+=len(entry);out.extend(blob)
    return bytes(out)


def build(request, output):
    engine=request['engine'];exe=pathlib.Path(request['executable']);root=exe.parent;source=request['archive'];rows=request['rows']
    m.require(0<len(rows)<=m.MAX_LINES);output=pathlib.Path(output);m.require(not output.exists() and output.parent.is_dir())
    for p in [output.parent,*output.parent.parents]:m.require(not p.is_symlink() and not p.is_junction())
    extracted=m.extract(engine,str(exe),source);original={(r['file'],r['index']):r for r in extracted['segments']};groups=collections.defaultdict(list);seen=set()
    for row in rows:
        key=(row['file'],row['index']);r=original.get(key);m.require(r is not None and all(r[k]==row[k] for k in ('file','index','speaker','source')) and key not in seen,'译文与当前游戏剧本不一致。');seen.add(key)
        t=row['translation'];m.require(isinstance(t,str) and 0<len(t)<=16384 and not any(ord(c)<32 and c not in '\n\r\t' for c in t) and '\0' not in t,'译文含无效字符。');groups[row['file']].append(row)
    enc=Encoding();files=[]
    if engine=='BGI / Ethornell':
        import bgi_reader as bg
        for path in bg.sources(root):
            prefix=path.relative_to(root).as_posix()+'/';items=[];changed=0
            for name,b in bg.archive_entries(m.read(path)):
                rr=groups.get(prefix+name,[]);items.append((name,patch_bgi(b,rr,enc) if rr else b));changed+=len(rr)
            if changed:files.append((path.relative_to(root).as_posix(),write_arc20(items),changed))
    elif engine=='WillPlus':
        items=[]
        for name,b in will_items(m.read(root/source)):
            rr=groups.get(name,[])
            if rr:
                plain=bytes((x>>2)|((x&3)<<6) for x in b);plain=patch_will(plain,rr,enc);b=bytes(((x<<2)&255)|(x>>6) for x in plain)
            items.append((name,b))
        files.append((source,write_will(items),len(rows)))
    elif engine=='Escu:de':
        raw=m.read(root/source);items=[(n,patch_esc(b,groups[n],enc) if n in groups else b) for n,b in m.esc_archive(raw)]
        files.append((source,write_esc(items,m.u32(raw,8)),len(rows)))
    elif engine=='ExHIBIT':
        table=None;sample=root/'rld'/next(iter(groups));raw=m.read(sample)
        try:m.rld_ops(raw)
        except (ValueError,UnicodeError):
            for seed in dict.fromkeys(m.rld_seeds(exe)):
                candidate=m.rld_table(seed)
                try:m.rld_ops(m.rld_decode(raw,candidate));table=candidate;break
                except (ValueError,UnicodeError,struct.error):continue
            m.require(table is not None)
        for name,rr in groups.items():files.append(('rld/'+name,patch_rld(m.read(root/'rld'/name),rr,enc,table),len(rr)))
    elif engine=='YU-RIS':
        raw=m.read(root/source);resources=dict(m.ypf_archive(raw));commands=m.yuris_commands(next(b for n,b in resources.items() if pathlib.PureWindowsPath(n).name.lower()=='ysc.ybn'))
        key=collections.Counter(m.u32(b,32+m.u32(b,12)+8) for b in resources.values() if b.startswith(b'YSTB') and m.u32(b,16)>=12).most_common(1)[0][0]
        changed={name:patch_yuris(resources[name],rr,enc,commands,key) for name,rr in groups.items()};files.append((source,write_ypf(raw,changed),len(rows)))
    elif engine=='SiglusEngine':
        from siglus_ssu import pck,textmap,BS,const as C
        raw=m.read(root/source);hdr=pck.parse_i32_header(raw,C.PACK_HDR_FIELDS,C.PACK_HDR_SIZE);key=pck._resolve_pck_scene_exe_el(raw,str(root/source),hdr=hdr)
        out=bytearray(raw)
        for item in pck.iter_pck_scene_dat_items(raw,str(root/source),hdr=hdr,scene_exe_el=key,require_exe=True):
            name=item['scene_name']+'.dat';rr=groups.get(name)
            if not rr:continue
            strings,out_scn=textmap._parse_scn_dat(item['blob']);strings=list(strings)
            for row in rr:m.require(strings[row['index']]==row['source']);strings[row['index']]=row['translation']
            blob=BS.build_scn_dat({'str_list':strings},out_scn)
            # Literal LZSS groups are deterministic and linear-time. Repacking
            # large novels must not spend minutes searching compression trees.
            packed=bytearray(bytes(8))
            for at in range(0,len(blob),8):
                chunk=blob[at:at+8];packed.append((1<<len(chunk))-1);packed.extend(chunk)
            struct.pack_into('<II',packed,0,len(packed),len(blob))
            from siglus_ssu.native_ops import xor_cycle_inplace
            xor_cycle_inplace(packed,C.EASY_ANGOU_CODE,0)
            if hdr['scn_data_exe_angou_mod']:xor_cycle_inplace(packed,key,0)
            blob=bytes(packed)
            offset=len(out)-hdr['scn_data_list_ofs'];struct.pack_into('<II',out,hdr['scn_data_index_list_ofs']+8*item['scene_no'],offset,len(blob));out.extend(blob)
        files.append((source,bytes(out),len(rows)))
    else:raise ValueError('此引擎尚未适配补丁写回。')
    m.require(sum(f[2] for f in files)==len(rows) and len(files)<=256 and sum(len(f[1]) for f in files)<=512*1024*1024)
    output.mkdir();report=[]
    for i,(name,data,changed) in enumerate(files):
        m.require(len(data)<=128*1024*1024);payload=f'{i+1:04}.bin';(output/payload).write_bytes(data)
        report.append(dict(name=name,payload=payload,sourceHash=hashlib.sha256(m.read(root/name)).hexdigest(),payloadHash=hashlib.sha256(data).hexdigest(),changed=changed,bytes=len(data)))
    mapping=[dict(code=v.hex(),character=ord(k)) for k,v in enc.mapping.items()]
    (output/'result.json').write_text(json.dumps(dict(files=report,mapping=mapping),ensure_ascii=True),encoding='utf-8')
