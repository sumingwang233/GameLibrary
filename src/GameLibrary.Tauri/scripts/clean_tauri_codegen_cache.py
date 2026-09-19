"""Remove only GameLibrary's generated Tauri asset cache before release builds.

Tauri codegen stores Brotli output under content-hash filenames. If a build is
interrupted while one of those files is being written, later builds see the
path and reuse the truncated file. Restrict cleanup to this project's Cargo
build directories; never touch the global Cargo cache or another project.
"""

from pathlib import Path
import shutil


TARGET = Path(__file__).resolve().parents[1] / "src-tauri" / "target"


def main() -> None:
    removed = 0
    if TARGET.is_dir():
        for cache in TARGET.glob("*/build/gamelibrary-desktop-*/out/tauri-codegen-assets"):
            resolved = cache.resolve()
            if TARGET.resolve() not in resolved.parents:
                raise RuntimeError(f"refusing to remove cache outside target: {resolved}")
            shutil.rmtree(resolved)
            removed += 1
    print(f"cleaned {removed} GameLibrary Tauri codegen cache director{'y' if removed == 1 else 'ies'}")


if __name__ == "__main__":
    main()
