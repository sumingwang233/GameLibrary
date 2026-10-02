"""Offline GitHub Pages checks; Python standard library only."""

import argparse
from html.parser import HTMLParser
from pathlib import Path
import struct
import tempfile
from urllib.parse import unquote, urlsplit
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1] / "website/out"
ORIGIN = "https://sumingwang233.github.io/GameLibrary/"
PREFIX = "/GameLibrary/"


class Page(HTMLParser):
    def __init__(self, text):
        super().__init__(convert_charrefs=True)
        self.ids = set()
        self.links = []
        self.images = []
        self.dialogs = []
        self.tooltips = 0
        self.surfaces = set()
        self.meta = {}
        self.alternates = {}
        self.lang = self.canonical = ""
        self.h1 = self.faq = 0
        self.errors = []
        self.title = ""
        self.in_title = False
        self.feed(text)

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        classes = set(a.get("class", "").split())
        if "surface" in classes:
            self.surfaces |= classes & {"sidebar", "screenshot-modal", "language-dropdown", "tooltip"}
        if "id" in a:
            if a["id"] in self.ids:
                self.errors.append(f"duplicate id: {a['id']}")
            self.ids.add(a["id"])
        if tag == "html":
            self.lang = a.get("lang", "")
        elif tag == "meta":
            self.meta[a.get("name", "")] = a.get("content", "")
        elif tag == "title":
            self.in_title = True
        elif tag == "h1":
            self.h1 += 1
        elif tag == "summary" and "aria-expanded" not in a:
            self.faq += 1
        elif tag == "img":
            self.images.append(a)
        elif tag == "dialog":
            self.dialogs.append(a)
        elif tag == "link":
            if a.get("rel") == "canonical":
                self.canonical = a.get("href", "")
            elif a.get("rel") == "alternate":
                self.alternates[a.get("hreflang", "")] = a.get("href", "")
        for attr in ("href", "src"):
            if a.get(attr):
                self.links.append(a[attr])
        for attr in ("srcset", "imagesrcset"):
            for candidate in a.get(attr, "").split(","):
                if candidate.strip():
                    self.links.append(candidate.strip().split()[0])
        if a.get("role") == "tooltip":
            self.tooltips += 1
        if tag == "iframe":
            self.errors.append("unexpected iframe")
        if tag == "script" and a.get("src") and not a["src"].startswith(PREFIX + "_next/"):
            self.errors.append("unexpected external script")

    def handle_endtag(self, tag):
        if tag == "title":
            self.in_title = False

    def handle_data(self, data):
        if self.in_title:
            self.title += data


def validate(root):
    root = root.resolve()
    locales = {"zh-CN": "", "zh-TW": "zh-TW/", "en": "en/", "ja": "ja/"}
    expected = {root / path / "index.html": locale for locale, path in locales.items()}
    pages = {p: Page(p.read_text(encoding="utf-8")) for p in expected if p.is_file()}
    errors = []
    styles = set()
    if pages.keys() != expected.keys():
        errors.append("missing language export; run the website build first")

    def resolve(file, path):
        if path.startswith(PREFIX):
            return (root / unquote(path[len(PREFIX):])).resolve()
        if path.startswith("/"):
            return None
        return (file.parent / unquote(path)).resolve() if path else file
    for file, page in pages.items():
        name = file.relative_to(root).as_posix()
        problems = list(page.errors)
        if page.lang != expected.get(file):
            problems.append("incorrect document language")
        if not page.title or not page.meta.get("description") or not page.meta.get("viewport"):
            problems.append("missing title, description, or viewport")
        if page.h1 != 1 or page.faq != 6:
            problems.append("expected one h1 and six FAQ items")
        if page.canonical != ORIGIN + locales.get(page.lang, ""):
            problems.append("incorrect canonical URL")
        if page.alternates != {**{locale: ORIGIN + path for locale, path in locales.items()}, "x-default": ORIGIN}:
            problems.append("incorrect language alternates")
        if not {"main", "experience", "collection", "launch", "safety", "download", "preview", "questions"} <= page.ids:
            problems.append("missing navigation target")
        if page.surfaces != {"sidebar", "screenshot-modal", "language-dropdown", "tooltip"}:
            problems.append("missing interaction surface")
        if len(page.dialogs) != 3 or page.tooltips != 1:
            problems.append("expected a sidebar, two screenshot dialogs, and one tooltip")
        for dialog in page.dialogs:
            if not dialog.get("aria-labelledby") or dialog["aria-labelledby"] not in page.ids:
                problems.append("dialog needs a valid accessible name")
        for link in page.links:
            url = urlsplit(link)
            if url.scheme or url.netloc:
                if url.scheme != "https":
                    problems.append(f"non-HTTPS link: {link}")
                continue
            target = resolve(file, url.path)
            if target is None:
                problems.append(f"path missing project prefix: {link}")
                continue
            if target.is_dir():
                target /= "index.html"
            if not target.is_relative_to(root.resolve()) or not target.is_file():
                problems.append(f"missing or out-of-site target: {link}")
            elif url.fragment and (target not in pages or url.fragment not in pages[target].ids):
                problems.append(f"missing anchor: {link}")
            elif target.suffix == ".css":
                styles.add(target.read_text(encoding="utf-8"))
        for image in page.images:
            if urlsplit(image.get("src", "")).netloc:
                problems.append("image must use a verified local asset")
            if "alt" not in image or not image.get("width") or not image.get("height"):
                problems.append(f"image needs alt and dimensions: {image.get('src')}")
            if not image.get("alt") and "icon.png" not in image.get("src", ""):
                problems.append("content screenshot needs descriptive alt text")
            target = resolve(file, urlsplit(image.get("src", "")).path)
            if target and target.is_file() and target.suffix == ".png" and "icon.png" not in str(target):
                size = struct.unpack(">II", target.read_bytes()[16:24])
                if tuple(str(n) for n in size) != (image.get("width"), image.get("height")):
                    problems.append(f"PNG dimensions disagree with HTML: {target.name}: {size}")
        errors.extend(f"{name}: {problem}" for problem in problems)
    if len(pages) == len(locales):
        cn = pages[root / "index.html"]
        downloads = lambda page: {l for l in page.links if "/releases/" in l}
        for page in pages.values():
            if {i for i in cn.ids if not i.startswith("_")} != {i for i in page.ids if not i.startswith("_")}:
                errors.append("translated pages have different section IDs")
            if downloads(cn) != downloads(page) or len(downloads(page)) != 6:
                errors.append("release links must match across languages (three packages, checksums, stable notes, prerelease)")
    if not any("prefers-reduced-motion" in style for style in styles):
        errors.append("compiled styles need a reduced-motion rule")
    try:
        sitemap = ET.parse(root / "sitemap.xml")
        urls = {n.text for n in sitemap.findall(".//{http://www.sitemaps.org/schemas/sitemap/0.9}loc")}
        if urls != {ORIGIN + path for path in locales.values()}:
            errors.append("incorrect sitemap URLs")
    except (OSError, ET.ParseError) as error:
        errors.append(f"invalid sitemap: {error}")
    if not (root / "robots.txt").is_file() or not (root / ".nojekyll").is_file():
        errors.append("missing robots.txt or .nojekyll")
    return errors


def self_test():
    # A broken navigation link and an inaccessible image must fail the gate.
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        (root / "index.html").write_text('<html lang="zh-CN"><a href="#absent">Go</a><img src="missing.png"></html>', encoding="utf-8")
        errors = validate(root)
        assert any("missing anchor" in error for error in errors), errors
        assert any("image needs alt and dimensions" in error for error in errors), errors
        assert any("missing or out-of-site target" in error for error in errors), errors
        (root / "index.html").write_text('<a href="/assets/missing.png">Go</a>', encoding="utf-8")
        assert any("path missing project prefix" in error for error in validate(root))
        (root / "index.html").write_text('<dialog class="surface sidebar"></dialog>', encoding="utf-8")
        errors = validate(root)
        assert any("dialog needs a valid accessible name" in error for error in errors), errors
        (root / "index.html").write_text('<img src="https://example.com/placeholder.svg" alt="Screenshot" width="1320" height="820">', encoding="utf-8")
        assert any("verified local asset" in error for error in validate(root))
        (root / "index.html").write_text('<link rel="stylesheet" href="test.css">', encoding="utf-8")
        (root / "test.css").write_text('body{color:#111d23}', encoding="utf-8")
        assert any("reduced-motion" in error for error in validate(root))
    print("Website gate regression check passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--self-test", action="store_true")
    parser.add_argument("--directory", type=Path, default=ROOT)
    args = parser.parse_args()
    if args.self_test:
        self_test()
    else:
        failures = validate(args.directory)
        if failures:
            raise SystemExit("\n".join(failures))
        print("Website checks passed: four languages, links, local images, metadata, and sitemap.")
