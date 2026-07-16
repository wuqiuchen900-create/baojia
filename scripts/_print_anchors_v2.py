import re, sys
f = open('BaoJiaCAD/Commands.cs', 'rb').read().decode('utf-8-sig', errors='replace')
for p in ['allSkipped', r'sel\.Status', 'DetectCurtainBoxLengths', 'DetectWindowAreas']:
    m = re.search(p, f)
    if not m:
        sys.stderr.write(f'NOT_FOUND {p}\n')
        continue
    sys.stderr.write(f'=== {p} ===\n')
    # Print using \x-escape decoupling in repr to avoid console issues
    snippet = f[max(0, m.start()-80):m.end()+200]
    sys.stderr.write(repr(snippet).replace('\\u', '\\\\u'))
    sys.stderr.write('\n===END===\n\n')
