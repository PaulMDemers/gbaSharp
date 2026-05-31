#!/usr/bin/env python3
import argparse
import csv
import hashlib
import math
import os
import re
import signal
import subprocess
import sys
from pathlib import Path

try:
    from PIL import Image, ImageDraw
except ImportError as exc:
    raise SystemExit("Pillow is required: python -m pip install pillow") from exc


FRAME_RE = re.compile(r"frame=([0-9,]+)")
ACTIVE_PROCESSES: list[subprocess.Popen[str]] = []


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Capture candidate final frames for a deep gameplay manifest row."
    )
    parser.add_argument("--manifest", default="docs/gba-deep-gameplay-routes.csv")
    parser.add_argument("--label", required=True, help="Manifest label to probe")
    parser.add_argument(
        "--frames",
        required=True,
        help="Comma-separated stop frames to capture, such as 6000,12000,18000",
    )
    parser.add_argument("--rom-root", default="curated_official_gba")
    parser.add_argument("--output-dir", default="")
    parser.add_argument("--baseline-dir", default="visual-baselines/deep-gameplay")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--bios", default="")
    parser.add_argument("--max-seconds", type=int, default=0)
    parser.add_argument("--max-steps", default="")
    parser.add_argument("--snapshot-frames", type=int, default=0)
    parser.add_argument("--columns", type=int, default=4)
    parser.add_argument("--scale", type=int, default=2)
    parser.add_argument("--no-align-rom-entry", action="store_true")
    return parser.parse_args()


def read_manifest(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def find_row(rows: list[dict[str, str]], label: str) -> dict[str, str]:
    for row in rows:
        if row.get("label") == label:
            return row
    raise SystemExit(f"Label not found in manifest: {label}")


def parse_frames(value: str) -> list[int]:
    frames: list[int] = []
    for token in value.split(","):
        token = token.strip()
        if not token:
            continue
        frame = int(token)
        if frame <= 0:
            raise SystemExit("--frames values must be greater than zero")
        frames.append(frame)
    if not frames:
        raise SystemExit("--frames must include at least one frame")
    return frames


def resolve_path(root: Path, value: str) -> Path:
    path = Path(value)
    if not path.is_absolute():
        path = root / path
    return path


def resolve_rom(root: Path, rom_root: Path, row: dict[str, str]) -> Path:
    rom_path = row.get("romPath", "").strip()
    if rom_path:
        return resolve_path(root, rom_path)

    index_text = row.get("index", "").strip()
    if not index_text:
        raise SystemExit("Manifest row needs either romPath or index")
    index = int(index_text)
    roms = sorted(rom_root.rglob("*.gba"), key=lambda item: str(item))
    if index <= 0 or index > len(roms):
        raise SystemExit(f"Index {index} is outside ROM collection size {len(roms)}")
    return roms[index - 1]


def find_cli_dll(root: Path, configuration: str) -> Path:
    bin_root = root / "src" / "Gba.Cli" / "bin" / configuration
    candidates = sorted(
        bin_root.rglob("Gba.Cli.dll"),
        key=lambda item: item.stat().st_mtime,
        reverse=True,
    )
    if not candidates:
        raise SystemExit(
            f"Could not find built Gba.Cli.dll under {bin_root}. Build Gba.Cli first."
        )
    return candidates[0]


def default_bios(root: Path) -> str:
    candidate = (
        root
        / "gba_collection"
        / "Massive GBA - EverDrive GBA 2022-08-08"
        / "5 Tools & Service Test Carts"
        / "BIOS"
        / "[BIOS] Game Boy Advance (World).bin"
    )
    return str(candidate) if candidate.exists() else ""


def get_int(row: dict[str, str], key: str, fallback: int) -> int:
    value = row.get(key, "").strip()
    return int(value) if value else fallback


def sha256_or_empty(path: Path) -> str:
    if not path.exists():
        return ""
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def terminate_process_tree(process: subprocess.Popen[str]) -> None:
    if process.poll() is not None:
        return

    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
    else:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            return
        except Exception:
            process.terminate()

    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        if os.name == "nt":
            process.kill()
        else:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            except Exception:
                process.kill()
        process.wait(timeout=5)


def terminate_active_processes() -> None:
    for process in list(ACTIVE_PROCESSES):
        terminate_process_tree(process)


def popen_creationflags() -> int:
    if os.name != "nt":
        return 0
    return subprocess.CREATE_NEW_PROCESS_GROUP


def popen_start_new_session() -> bool:
    return os.name != "nt"


def run_command(command: list[str], root: Path, timeout: int | None) -> tuple[int, str, str, bool]:
    process = subprocess.Popen(
        command,
        cwd=root,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        creationflags=popen_creationflags(),
        start_new_session=popen_start_new_session(),
    )
    ACTIVE_PROCESSES.append(process)
    try:
        try:
            stdout, stderr = process.communicate(timeout=timeout)
            return process.returncode or 0, stdout, stderr, False
        except subprocess.TimeoutExpired:
            terminate_process_tree(process)
            stdout, stderr = process.communicate()
            return 124, stdout, stderr, True
    finally:
        if process in ACTIVE_PROCESSES:
            ACTIVE_PROCESSES.remove(process)


def run_capture(
    root: Path,
    cli_dll: Path,
    rom: Path,
    row: dict[str, str],
    frame: int,
    output_ppm: Path,
    snapshot_csv: Path,
    args: argparse.Namespace,
) -> dict[str, str]:
    max_steps = args.max_steps or row.get("maxSteps", "").strip() or str(frame * 500000)
    max_seconds = args.max_seconds or get_int(row, "maxSeconds", 900)
    snapshot_frames = args.snapshot_frames or get_int(row, "snapshotFrames", max(1, frame // 10))
    command = [
        "dotnet",
        str(cli_dll),
        "dump-frame",
        str(rom),
        "--stop-frame",
        str(frame),
        "--max-steps",
        str(max_steps),
        "--max-seconds",
        str(max_seconds),
        "--output",
        str(output_ppm),
        "--snapshot-csv",
        str(snapshot_csv),
        "--snapshot-frames",
        str(snapshot_frames),
    ]

    if not args.no_align_rom_entry:
        command.append("--align-rom-entry")

    bios = args.bios or default_bios(root)
    if bios:
        command.extend(["--bios", bios])

    input_script = row.get("inputScript", "").strip()
    if input_script:
        command.extend(["--input-script", input_script])

    save_file = row.get("saveFile", "").strip()
    if save_file:
        command.extend(["--save-file", save_file])
        if row.get("saveReadOnly", "").strip().lower() == "true":
            command.append("--save-read-only")

    timeout = max_seconds + 90 if max_seconds > 0 else None
    returncode, stdout, stderr, timed_out = run_command(command, root, timeout)
    message = " ".join((stdout + " " + stderr).split())
    match = FRAME_RE.search(message)
    observed_frame = int(match.group(1).replace(",", "")) if match else 0
    status = "pass" if returncode == 0 and observed_frame >= frame else "fail"
    if returncode == 5:
        status = "wall-timeout"
    elif returncode == 124 or timed_out:
        status = "process-timeout"

    return {
        "label": row.get("label", ""),
        "requestedFrame": str(frame),
        "status": status,
        "exitCode": str(returncode),
        "observedFrame": str(observed_frame),
        "finalPpm": str(output_ppm.relative_to(root)),
        "snapshotCsv": str(snapshot_csv.relative_to(root)),
        "actualHash": sha256_or_empty(output_ppm),
        "message": message,
    }


def ppm_to_image(path: Path) -> Image.Image:
    return Image.open(path).convert("RGB")


def draw_contact_sheet(
    root: Path,
    rows: list[dict[str, str]],
    output: Path,
    columns: int,
    scale: int,
) -> None:
    frames: list[tuple[str, Image.Image]] = []
    for row in rows:
        ppm = resolve_path(root, row["finalPpm"])
        if ppm.exists():
            label = f"{row['requestedFrame']} {row['status']}"
            frames.append((label, ppm_to_image(ppm)))

    if not frames:
        return

    base_width, base_height = frames[0][1].size
    tile_width = base_width * scale
    tile_height = base_height * scale
    label_height = 28
    columns = max(1, min(columns, len(frames)))
    rows_count = math.ceil(len(frames) / columns)
    sheet = Image.new("RGB", (columns * tile_width, rows_count * (tile_height + label_height)), "white")
    draw = ImageDraw.Draw(sheet)

    for index, (label, image) in enumerate(frames):
        col = index % columns
        row = index // columns
        x = col * tile_width
        y = row * (tile_height + label_height)
        sheet.paste(image.resize((tile_width, tile_height), Image.Resampling.NEAREST), (x, y))
        draw.rectangle([x, y + tile_height, x + tile_width - 1, y + tile_height + label_height - 1], fill=(245, 245, 245))
        draw.text((x + 6, y + tile_height + 8), label, fill=(0, 0, 0))

    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)


def main() -> int:
    args = parse_args()
    root = Path.cwd().resolve()
    manifest = resolve_path(root, args.manifest)
    rom_root = resolve_path(root, args.rom_root)
    rows = read_manifest(manifest)
    row = find_row(rows, args.label)
    frames = parse_frames(args.frames)
    output_dir = resolve_path(
        root,
        args.output_dir or f"deep-gameplay-candidates-{args.label}",
    )
    frame_dir = output_dir / "frames"
    snapshot_dir = output_dir / "snapshots"
    frame_dir.mkdir(parents=True, exist_ok=True)
    snapshot_dir.mkdir(parents=True, exist_ok=True)

    cli_dll = find_cli_dll(root, args.configuration)
    rom = resolve_rom(root, rom_root, row)
    report_rows: list[dict[str, str]] = []
    try:
        for frame in frames:
            safe_label = re.sub(r"[^A-Za-z0-9._-]+", "-", args.label).strip("-")
            output_ppm = frame_dir / f"{safe_label}-{frame}.ppm"
            snapshot_csv = snapshot_dir / f"{safe_label}-{frame}.csv"
            print(f"Capturing {args.label} at frame {frame}", flush=True)
            report_rows.append(run_capture(root, cli_dll, rom, row, frame, output_ppm, snapshot_csv, args))
    except KeyboardInterrupt:
        terminate_active_processes()
        print("Interrupted; terminated active capture process.", file=sys.stderr)
        if not report_rows:
            return 130
    finally:
        terminate_active_processes()

    if not report_rows:
        return 1

    report = output_dir / "frame-candidates.csv"
    with report.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(report_rows[0].keys()))
        writer.writeheader()
        writer.writerows(report_rows)

    contact_sheet = output_dir / "contact-sheet.png"
    draw_contact_sheet(root, report_rows, contact_sheet, args.columns, args.scale)
    print(report)
    if contact_sheet.exists():
        print(contact_sheet)
    return 0 if all(row["status"] == "pass" for row in report_rows) else 1


if __name__ == "__main__":
    raise SystemExit(main())
