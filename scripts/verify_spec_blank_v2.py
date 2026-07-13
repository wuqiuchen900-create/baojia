"""Tile-spec-blanking verification \u2014 fixed-version sim replicates C# IdentifyTileSpecMatch.

Why fixed: earlier sim had `s['isDefault']` (KeyError on specs that omit isDefault field).
This version uses `s.get('isDefault', False)` matching C# Newtonsoft deserialization behavior.
"""
import json
from pathlib import Path


def load_specs(cfg):
    """Replicate C# OrderByDescending of TileSpecOption per room (by total match length)."""
    tso = cfg.get('TemplateSettings', {}).get('TileSpecOptions', {})
    out = {}
    for room, specs in tso.items():
        valid = [
            (s.get('value', ''), s.get('match', []) or [], s.get('isDefault', False))
            for s in specs
        ]
        out[room] = sorted(
            valid,
            key=lambda x: (sum(len(m or '') for m in x[1]), len(x[1])),
            reverse=True,
        )
    return out


def asc(s):
    """ASCII-only render non-printable chars as \\uXXXX escapes for terminal-safe output."""
    out = []
    for c in str(s):
        o = ord(c)
        if 32 <= o < 127:
            out.append(c)
        else:
            out.append('\\u{:04x}'.format(o))
    return ''.join(out)


def identify(item_name, room_type, sd):
    """Replicate C# IdentifyTileSpecMatch \u2014 ALL-match first spec wins after sort."""
    specs = sd.get(room_type, [])
    for val, mt, _ in specs:
        if not mt:
            continue
        if all((m in item_name) for m in mt):
            return val
    return None


def decide(item_name, room_type, selected, sd):
    """Replicate C# FillRoomData spec-blank decision, full pipeline."""
    if not selected:
        return '[FILL FloorArea, no spec chosen]'
    item_spec = identify(item_name, room_type, sd)
    if item_spec is None:
        return '[FILL FloorArea, FALLBACK no tile-spec match]'
    if item_spec == selected:
        return '[FILL KEEP, user selected this one]'
    return '[BLANK itemSpec={} != selected={}]'.format(item_spec, selected)


# === zhubaojiao row sets ===
KT = [
    ('R50', '\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08300-800MM\uff09'),
    ('R51', '\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08800*1600MM\uff09'),
    ('R52', '\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08750*1500MM\uff09'),
    ('R53', '\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08900*1800MM\uff09'),
    ('R8',  '\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08600*1200MM\uff09'),
    ('R54', '\u5ba2\u5385\u53ca\u9910\u5385\u83f1\u94fa\u5730\u7816\uff08300-800MM\uff09'),
]
MBR_ITEMS = [
    ('R145', '\u5730\u9762\u627e\u5e73'),
    ('R146', '\u81ea\u6d41\u5e73\u627e\u5e73\u5904\u7406'),
]
MBR_FUTURE = [
    ('F1', '\u4e3b\u5367\u6b63\u94fa\u5730\u7816\uff08300-800MM\uff09'),
    ('F2', '\u4e3b\u5367\u6b63\u94fa\u5730\u7816\uff08750*1500MM\uff09'),
]
KTCHEN = [('R121', '\u94fa\u5730\u7816\uff08\u6b63\u8d34\uff09')]
WCET = [
    ('R160', '\u94fa\u5730\u7816\uff08\u6b63\u8d34\uff09'),
    ('R130', '\u94fa\u5730\u7816\uff08\u6b63\u8d34\uff09'),
]


def dump(name, room, items, sel):
    print('\n=== {}  (room={}, selected={}) ==='.format(
        name, asc(room), sel if sel else '<NONE>'))
    print('  {:>5}  {:<35}  {:<19}  {}'.format(
        'row', 'item_name (first 35 chars)', 'item_spec', 'decision'))
    print('  {:>5}  {:<35}  {:<19}  {}'.format(
        '-' * 5, '-' * 35, '-' * 19, '-' * 7))
    for row, item in items:
        ispec = identify(item, room, sd)
        d = decide(item, room, sel, sd)
        print('  {:>5}  {:<35}  {:<19}  {}'.format(
            row, item[:35], ispec if ispec else '-', d))


# === run ===
cfg = json.loads(Path('BaoJiaCAD/config.json').read_text(encoding='utf-8'))
sd = load_specs(cfg)

print('SD DEBUG: keys =', [asc(k) for k in sd.keys()])
KT_KEY = '\u5ba2\u9910\u5385'
print('SD DEBUG: KT_KEY used:', asc(KT_KEY), ';', 'found in sd?', KT_KEY in sd)
print('SD DEBUG: KT specs loaded:', len(sd.get(KT_KEY, [])))
for v, m, _ in sd.get(KT_KEY, []):
    print('   value={}  match={}'.format(asc(v), [asc(x) for x in m]))

dump('S1 kenting+sp750-1500 (default)',     KT_KEY, KT, 'sp750-1500')
dump('S2 kenting+sp300-800-LR',             KT_KEY, KT, 'sp300-800-LR')
dump('S3 kenting+spDiamond-LR (ling-dai)',  KT_KEY, KT, 'spDiamond-LR')
dump('S4 mainbed+sp750-1500-MBR (findping only)', '\u4e3b\u5367', MBR_ITEMS, 'sp750-1500-MBR')
dump('S5 mainbed+sp750-1500-MBR (future tile rows)', '\u4e3b\u5367', MBR_FUTURE, 'sp750-1500-MBR')
dump('S6 kichen+sp300-800-K (zheng-tie single row)',  '\u53a8\u623f', KTCHEN, 'sp300-800-K')
dump('S7 bath+sp300-800-G (zheng-tie single row)',    '\u536b\u751f\u95f4', WCET, 'sp300-800-G')
dump('S8 bedrm+sp300-800-BR (future tile rows)',       '\u5367\u5ba4', MBR_FUTURE, 'sp300-800-BR')
dump('S9 kenting+NO selection',                       KT_KEY, KT, None)

print('\n----- Cross-file equivalence -----')
sd_bin = load_specs(json.loads(Path('BaoJiaCAD/bin/Debug/net48/config.json').read_text(encoding='utf-8')))
diff_keys = [k for k in sd if sd[k] != sd_bin.get(k)]
print('JSON src vs bin/Debug copy delta keys:', diff_keys if diff_keys else 'IDENTICAL')
