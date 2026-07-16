import os
f = open('BaoJiaCAD/Commands.cs', 'rb').read().decode('utf-8-sig', errors='replace').replace('\r\n', '\n')
lines = f.split('\n')
out = open('scripts/_v175_dup_dump.txt', 'w', encoding='utf-8')
out.write('--- L80-115 ---\n')
for ln in range(80, 116):
    if ln < len(lines):
        out.write(f'L{ln+1}: {lines[ln]}\n')
out.write('\n--- all `bool exporting` / `globalSelectionScope` occurrences ---\n')
for ln, line in enumerate(lines, 1):
    if 'bool exporting' in line or 'globalSelectionScope' in line or 'HonorSelectionScope' in line:
        out.write(f'L{ln}: {line.rstrip()}\n')
out.close()
print(f'done, total lines: {len(lines)}')
