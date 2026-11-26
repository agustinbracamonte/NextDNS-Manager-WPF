"""Check translation keys and format placeholders without touching user data."""
import json
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parent.parent / "NextDNSModernWPF"
languages = ("es", "en", "fr", "pt", "de")
catalogs = [json.loads(path.read_text(encoding="utf-8-sig")) for path in (root / "Resources" / "Localization").glob("*.json")]
translations = {lang: {} for lang in languages}
for catalog in catalogs:
    for lang in languages:
        for key, value in catalog.get(lang, {}).items():
            if key in translations[lang]:
                raise SystemExit(f"Duplicate key {key} for {lang}")
            translations[lang][key] = value

references = set()
for path in root.rglob("*.cs"):
    if any(part in ("bin", "obj") for part in path.parts):
        continue
    references.update(re.findall(r'"((?:Code|Main\.Ui)\.[^" ]+)"', path.read_text(encoding="utf-8-sig")))
references.update(re.findall(r'\[((?:Code|Main\.Ui)\.[^\]]+)\]', (root / "MainWindow.xaml").read_text(encoding="utf-8-sig")))

errors = []
for lang in languages:
    errors.extend(f"{lang}: missing {key}" for key in sorted(references - translations[lang].keys()))
for lang in languages[1:]:
    for key, source in translations["es"].items():
        if key not in translations[lang]:
            errors.append(f"{lang}: missing catalog key {key}")
            continue
        expected = set(re.findall(r"\{\d+[^}]*\}", source))
        actual = set(re.findall(r"\{\d+[^}]*\}", translations[lang][key]))
        if expected != actual:
            errors.append(f"{lang}: format parameters differ for {key}")

if errors:
    print("\n".join(errors))
    sys.exit(1)
print(f"Localization OK: {len(references)} referenced keys, {len(languages)} languages.")
