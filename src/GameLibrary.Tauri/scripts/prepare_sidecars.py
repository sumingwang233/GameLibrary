import os
import shutil
import subprocess


PROJECT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
WORKSPACE = os.path.abspath(os.path.join(PROJECT, "..", ".."))
BINARIES = os.path.join(PROJECT, "src-tauri", "binaries")
DOTNET = os.path.expanduser(r"~\.dotnet-sdk-10.0\dotnet.exe")
TARGET = "x86_64-pc-windows-msvc"


def publish(project: str, executable: str, properties: list[str] | None = None) -> None:
    output = os.path.join(BINARIES, project)
    shutil.rmtree(output, ignore_errors=True)
    subprocess.run(
        [
            DOTNET,
            "publish",
            os.path.join(WORKSPACE, "src", project, f"{project}.csproj"),
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:DebugType=None",
            *(properties or []),
            "-o",
            output,
        ],
        cwd=WORKSPACE,
        check=True,
    )
    source = os.path.join(output, executable)
    target = os.path.join(BINARIES, f"{os.path.splitext(executable)[0]}-{TARGET}.exe")
    shutil.copy2(source, target)
    shutil.rmtree(output)


os.makedirs(BINARIES, exist_ok=True)
publish("GameLibrary.TauriBridge", "GameLibrary.TauriBridge.exe")
publish("GameLibrary.Host", "GameLibrary.Host.exe", ["-p:GameLibraryBackgroundHost=true"])
