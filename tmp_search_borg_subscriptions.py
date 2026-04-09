from pathlib import Path
root = Path('f:/Floofdev/HardLight')
for p in root.rglob('*.cs'):
    try:
        text = p.read_text(encoding='utf-8', errors='ignore')
    except Exception as e:
        print('SKIP', p, e)
        continue
    if 'BorgChassisComponent' not in text and 'ComponentStartup' not in text:
        continue
    for i, line in enumerate(text.splitlines(), 1):
        if 'SubscribeLocalEvent' in line and 'BorgChassisComponent' in line:
            print(p, i, line)
        elif 'BorgChassisComponent' in line and 'ComponentStartup' in line:
            print(p, i, line)
