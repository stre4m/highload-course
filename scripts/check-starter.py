#!/usr/bin/env python3
"""Проверка синтаксиса и структуры (Python 3.11+). Не запускает Codex или Podman."""
from pathlib import Path
import sys
import tomllib

root = Path(__file__).resolve().parents[1]
roles = {'architect', 'developer', 'test-engineer', 'reviewer', 'documentation'}
errors = []
for f in [root / '.codex/config.toml', *(root / '.codex/agents' / f'{r}.toml' for r in roles)]:
    if not f.is_file():
        errors.append(f'Отсутствует: {f.relative_to(root)}')
        continue
    try:
        data = tomllib.loads(f.read_text(encoding='utf-8'))
        if f.parent.name == 'agents':
            if data.get('name') != f.stem:
                errors.append(f'Несовпадение имени: {f.name}')
            for key in ('description', 'developer_instructions'):
                if not data.get(key): errors.append(f'Отсутствует {key}: {f.name}')
        elif not data.get('agents', {}).get('enabled'):
            errors.append('Мультиагентный режим не включён')
    except Exception as ex:
        errors.append(f'Некорректный TOML {f.name}: {ex}')
for name in ('AGENTS.md','README.md','infrastructure/compose.yaml','.env.example','.gitignore','docs/architecture.md','docs/development.md','docs/api.md','docs/database.md','docs/decisions/0001-no-orm.md','docs/decisions/0002-postgresql-preferred.md','prompts/01-verify-agents.md','prompts/02-first-feature.md'):
    if not (root / name).is_file(): errors.append(f'Отсутствует: {name}')
for e in errors: print('ОШИБКА:', e)
if errors: sys.exit(1)
print('OK: файлы шаблона и синтаксис TOML проверены.')
print('ПРИМЕЧАНИЕ: это не проверка реального запуска подагентов Codex или Podman.')
