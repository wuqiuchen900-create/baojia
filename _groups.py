import re, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8')
text = open(r'E:\xiangmu\baojia\_muban_dump.txt', 'r', encoding='utf-8').read()

for fname in ['dizhuan.xlsx','fushi.xlsx','mudiban.xlsx','zhubaojiao.xlsx']:
    m = re.search(r'======== ' + re.escape(fname) + r' ========(.*?)(?:======= |\Z)', text, re.S)
    if not m:
        print('!! not found:', fname); continue
    blk = m.group(1)
    print('========', fname, '========')
    groups, items = [], []
    for ln in blk.splitlines():
        mm = re.match(r'R(\d+):\s*([^|]*)\|([^|]*)\|', ln)
        if not mm: continue
        c1, c2 = mm.group(2).strip(), mm.group(3).strip()
        if re.match(r'^[一二三四五六七八九十][、]?$', c1) or re.match(r'^[A-Z]$', c1):
            groups.append((mm.group(1), c1, c2))
        elif re.match(r'^\d+$', c1):
            items.append((mm.group(1), c1, c2))
    print('-- Groups (count={})'.format(len(groups)))
    for r,c,c2 in groups:
        print('  R{}: [{}] {}'.format(r, c, c2))
    print('-- Items (count={})'.format(len(items)))
    for r,c,c2 in items:
        print('  R{}: [{}] {}'.format(r, c, c2))
    print()
