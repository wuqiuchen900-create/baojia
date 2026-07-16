import re, sys
f = open('BaoJiaCAD/Commands.cs', 'rb').read().decode('utf-8-sig', errors='replace')
for p in ['allSkipped', r'sel\.Status', 'DetectCurtainBoxLengths', 'DetectWindowAreas']:
    m = re.search(p, f)
    if not m:
        sys.stdout.buffer.write(f'=== {p} NOT FOUND ===\n'.encode('gbk', errors='replace'))
        continue
    snippet = f[max(0, m.start()-80):m.end()+200]
    sys.stdout.buffer.write(f'=== {p} ===\n'.encode('gbk', errors='replace'))
    sys.stdout.buffer.write(repr(snippet).encode('gbk', errors='replace'))
    sys.stdout.buffer.write(b'\n\n')
