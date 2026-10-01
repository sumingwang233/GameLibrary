"""Check shared UI translations, placeholders, WPF resources and NSIS language coverage."""
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "src/GameLibrary.Contracts/Localization/ui.json"


def main():
    pairs = json.loads(CATALOG.read_text(encoding="utf-8"), object_pairs_hook=list)
    keys = [key for key, _ in pairs]
    assert len(keys) == len(set(keys)), "Duplicate source string"
    catalog = dict(pairs)
    fields = lambda text: sorted(re.findall(r"\{\d+(?:[^}]*)\}", text))
    for source, translations in catalog.items():
        assert len(translations) == 3, source
        for text in translations:
            assert text.strip() and "\ufffd" not in text, source
            assert fields(source) == fields(text), source
    for path in (ROOT / "src/GameLibrary.Tauri/src").rglob("*.ts*"):
        if ".test." in path.name:
            continue
        for literal in re.findall(r'\bt\(\s*("(?:[^"\\]|\\.)*")', path.read_text(encoding="utf-8")):
            source = re.sub(r"\s+", " ", json.loads(literal)).strip()
            assert source in catalog, f"Missing translation: {path.name}: {source}"
    resources = {"L10n." + re.sub(r"\s+", "_", source) for source in keys}
    assert len(resources) == len(keys), "Colliding WPF resource names"
    xaml = (ROOT / "src/GameLibrary.Desktop/MainWindow.xaml").read_text(encoding="utf-8")
    for resource in re.findall(r"\{DynamicResource (L10n\.[^}]+)\}", xaml):
        assert resource in resources, resource
    nsis = (ROOT / "artifacts/installer/Languages.nsh").read_text(encoding="utf-8")
    sets = [set(re.findall(rf"LangString (\w+) \$\{{LANG_{language}\}}", nsis))
            for language in ("SIMPCHINESE", "TRADCHINESE", "ENGLISH", "JAPANESE")]
    assert sets[0] and all(items == sets[0] for items in sets), "Incomplete NSIS translations"
    print(f"Localization verified: {len(keys)} shared UI strings; {len(sets[0])} installer strings in four languages.")


if __name__ == "__main__":
    main()
