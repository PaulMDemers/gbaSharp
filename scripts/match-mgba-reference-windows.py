#!/usr/bin/env python3
import argparse
import atexit
import csv
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

from image_match_metrics import classify_match, image_metrics


FRAME_RE = re.compile(r"-f-(\d+)-")
ACTIVE_PROCESSES: list[subprocess.Popen[str]] = []


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Capture gbaSharp frame windows and find the closest frame to each mGBA reference image."
    )
    parser.add_argument("--routes", default="docs/gba-longplay-strict-routes.csv")
    parser.add_argument("--references", default="docs/gba-longplay-reference-frames.csv")
    parser.add_argument("--comparison", default="", help="Optional comparison CSV; defaults to rows with status=diff.")
    parser.add_argument("--labels", default="", help="Comma-separated labels to match.")
    parser.add_argument("--rom-root", default="curated_official_gba")
    parser.add_argument("--output-dir", default="")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--window", type=int, default=1200, help="Frames before/after route stopFrame to probe.")
    parser.add_argument("--stride", type=int, default=60, help="Frame interval to sample inside the window.")
    parser.add_argument("--max-items", type=int, default=0)
    parser.add_argument("--skip-items", type=int, default=0)
    parser.add_argument("--max-seconds", type=int, default=0, help="Override per-route emulated wall-clock limit.")
    parser.add_argument("--route-max-seconds-cap", type=int, default=0)
    parser.add_argument("--process-timeout", type=int, default=0, help="Outer process timeout in seconds.")
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--no-align-rom-entry", action="store_true")
    parser.add_argument("--columns", type=int, default=3)
    parser.add_argument("--scale", type=int, default=2)
    return parser.parse_args()


def read_csv(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def resolve_path(root: Path, value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else root / path


def find_cli_dll(root: Path, configuration: str) -> Path:
    bin_root = root / "src" / "Gba.Cli" / "bin" / configuration
    candidates = sorted(
        bin_root.rglob("Gba.Cli.dll"),
        key=lambda item: item.stat().st_mtime,
        reverse=True,
    )
    if not candidates:
        raise SystemExit(f"Could not find built Gba.Cli.dll under {bin_root}. Build first or omit --no-build.")
    return candidates[0]


def resolve_rom(root: Path, rom_root: Path, row: dict[str, str], roms: list[Path]) -> Path:
    rom_path = row.get("romPath", "").strip()
    if rom_path:
        return resolve_path(root, rom_path)

    index = int(row.get("index", "0"))
    if index <= 0 or index > len(roms):
        raise SystemExit(f"Route {row.get('label', '')} has ROM index {index} outside 1..{len(roms)}")
    return roms[index - 1]


def safe_name(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9._-]+", "-", value).strip("-")


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


atexit.register(terminate_active_processes)


def popen_creationflags() -> int:
    return subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0


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


def build_diff(actual_path: Path, reference: Image.Image) -> Image.Image:
    actual = Image.open(actual_path).convert("RGB")
    diff = Image.new("RGB", actual.size, "black")
    actual_pixels = actual.load()
    ref_pixels = reference.load()
    diff_pixels = diff.load()
    width, height = actual.size
    for y in range(height):
        for x in range(width):
            ar, ag, ab = actual_pixels[x, y]
            rr, rg, rb = ref_pixels[x, y]
            delta = max(abs(ar - rr), abs(ag - rg), abs(ab - rb))
            if delta:
                diff_pixels[x, y] = (255, min(255, abs(ag - rg) * 4), min(255, abs(ab - rb) * 4))
    return diff


def selected_labels(args: argparse.Namespace, root: Path) -> set[str] | None:
    if args.labels.strip():
        return {item.strip() for item in args.labels.split(",") if item.strip()}

    if args.comparison.strip():
        rows = read_csv(resolve_path(root, args.comparison))
        labels = {row["label"] for row in rows if row.get("status") == "diff"}
        return labels

    return None


def capture_route(
    root: Path,
    cli_dll: Path,
    rom: Path,
    row: dict[str, str],
    frame_start: int,
    frame_end: int,
    route_dir: Path,
    args: argparse.Namespace,
) -> tuple[int, str, bool]:
    max_steps = row.get("maxSteps", "").strip() or str(frame_end * 500000)
    max_seconds = args.max_seconds or int(row.get("maxSeconds", "") or "900")
    if args.route_max_seconds_cap > 0:
        max_seconds = min(max_seconds, args.route_max_seconds_cap)

    command = [
        "dotnet",
        str(cli_dll),
        "capture-frames",
        str(rom),
        "--stop-frame",
        str(frame_end),
        "--max-steps",
        str(max_steps),
        "--max-seconds",
        str(max_seconds),
        "--output-dir",
        str(route_dir),
        "--sample-frames",
        str(args.stride),
        "--frame-range",
        f"{frame_start}:{frame_end}",
    ]

    if not args.no_align_rom_entry:
        command.append("--align-rom-entry")

    input_script = row.get("inputScript", "").strip()
    if input_script:
        command.extend(["--input-script", input_script])

    save_file = row.get("saveFile", "").strip()
    if save_file:
        command.extend(["--save-file", save_file])
        if row.get("saveReadOnly", "").strip().lower() == "true":
            command.append("--save-read-only")

    timeout = args.process_timeout or max_seconds + 120
    code, stdout, stderr, timed_out = run_command(command, root, timeout)
    return code, " ".join((stdout + " " + stderr).split()), timed_out


def candidate_frame(path: Path) -> int:
    match = FRAME_RE.search(path.name)
    return int(match.group(1)) if match else -1


def make_contact_sheet(rows: list[dict[str, str]], output: Path, scale: int, columns: int) -> None:
    tiles: list[tuple[str, Image.Image, Image.Image, Image.Image]] = []
    for row in rows:
        actual_path = Path(row["bestImage"])
        reference_path = Path(row["referenceImage"])
        if not actual_path.exists() or not reference_path.exists():
            continue
        reference = Image.open(reference_path).convert("RGB")
        actual = Image.open(actual_path).convert("RGB")
        diff = build_diff(actual_path, reference)
        label = f"{row['label']} f{row['bestFrame']} {row['classification']} {row['bestDifferentPixels']}"
        tiles.append((label, actual, reference, diff))

    if not tiles:
        return

    base_w, base_h = tiles[0][1].size
    group_w = base_w * scale * 3
    group_h = base_h * scale + 32
    columns = max(1, min(columns, len(tiles)))
    sheet_rows = (len(tiles) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * group_w, sheet_rows * group_h), "white")
    draw = ImageDraw.Draw(sheet)

    for index, (label, actual, reference, diff) in enumerate(tiles):
        col = index % columns
        row = index // columns
        x0 = col * group_w
        y0 = row * group_h
        for image_index, image in enumerate((actual, reference, diff)):
            x = x0 + image_index * base_w * scale
            sheet.paste(image.resize((base_w * scale, base_h * scale), Image.Resampling.NEAREST), (x, y0))
        draw.rectangle([x0, y0 + base_h * scale, x0 + group_w - 1, y0 + group_h - 1], fill=(245, 245, 245))
        draw.text((x0 + 6, y0 + base_h * scale + 9), label[:110], fill=(0, 0, 0))

    output.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(output)


def main() -> int:
    args = parse_args()
    if args.window < 0:
        raise SystemExit("--window must be non-negative")
    if args.stride <= 0:
        raise SystemExit("--stride must be greater than zero")

    root = Path.cwd().resolve()
    routes = read_csv(resolve_path(root, args.routes))
    references = {row["label"]: row for row in read_csv(resolve_path(root, args.references))}
    labels = selected_labels(args, root)
    if labels is not None:
        routes = [row for row in routes if row.get("label", "") in labels]
    if args.skip_items > 0:
        routes = routes[args.skip_items :]
    if args.max_items > 0:
        routes = routes[: args.max_items]
    if not routes:
        raise SystemExit("No routes selected.")

    output_dir = resolve_path(
        root,
        args.output_dir or f"artifacts/mgba-window-match-{args.window}-{args.stride}",
    )
    frame_root = output_dir / "frames"
    frame_root.mkdir(parents=True, exist_ok=True)

    if not args.no_build:
        code, stdout, stderr, timed_out = run_command(
            ["dotnet", "build", "src/Gba.Cli", "-c", args.configuration],
            root,
            args.process_timeout or None,
        )
        if code != 0 or timed_out:
            raise SystemExit((stdout + "\n" + stderr).strip())

    cli_dll = find_cli_dll(root, args.configuration)
    rom_root = resolve_path(root, args.rom_root)
    roms = sorted(rom_root.rglob("*.gba"), key=lambda item: str(item))
    report_rows: list[dict[str, str]] = []

    try:
        for row in routes:
            label = row["label"]
            if label not in references:
                print(f"Skipping {label}; no reference row", file=sys.stderr)
                continue

            stop_frame = int(row["stopFrame"])
            frame_start = max(1, stop_frame - args.window)
            frame_end = stop_frame + args.window
            route_dir = frame_root / safe_name(label)
            route_dir.mkdir(parents=True, exist_ok=True)
            for old in route_dir.glob("*.ppm"):
                old.unlink()

            reference_path = resolve_path(root, references[label]["referenceImage"])
            reference = Image.open(reference_path).convert("RGB")
            rom = resolve_rom(root, rom_root, row, roms)
            print(f"Matching {label}: frames {frame_start}..{frame_end}", flush=True)
            exit_code, message, timed_out = capture_route(root, cli_dll, rom, row, frame_start, frame_end, route_dir, args)

            candidates = sorted(route_dir.glob("*.ppm"), key=candidate_frame)
            best: dict[str, object] | None = None
            original: dict[str, object] | None = None
            for candidate in candidates:
                frame = candidate_frame(candidate)
                metrics = image_metrics(candidate, reference)
                item = {
                    "frame": frame,
                    "path": candidate,
                    "metrics": metrics,
                }
                if best is None or (
                    metrics.different_pixels,
                    -metrics.structural_similarity,
                    metrics.coarse_mean_delta,
                    metrics.max_channel_delta,
                    abs(frame - stop_frame),
                ) < (
                    best["metrics"].different_pixels,
                    -best["metrics"].structural_similarity,
                    best["metrics"].coarse_mean_delta,
                    best["metrics"].max_channel_delta,
                    abs(best["frame"] - stop_frame),
                ):
                    best = item
                if frame == stop_frame:
                    original = item

            if original is None and best is not None:
                original = best

            if best is None or original is None:
                report_rows.append(
                    {
                        "label": label,
                        "status": "capture-failed",
                        "captureExitCode": str(exit_code),
                        "timedOut": str(timed_out).lower(),
                        "stopFrame": str(stop_frame),
                        "bestFrame": "",
                        "frameOffset": "",
                        "bestDifferentPixels": "",
                        "bestDifferentPercent": "",
                        "bestMaxChannelDelta": "",
                        "bestStructuralSimilarity": "",
                        "bestCoarseMeanDelta": "",
                        "originalDifferentPixels": "",
                        "originalStructuralSimilarity": "",
                        "originalCoarseMeanDelta": "",
                        "candidateCount": str(len(candidates)),
                        "classification": "no-candidates",
                        "bestImage": "",
                        "referenceImage": str(reference_path),
                        "message": message,
                    }
                )
                continue

            best_metrics = best["metrics"]
            original_metrics = original["metrics"]
            classification = classify_match(best_metrics, original_metrics, int(best["frame"]) - stop_frame)
            report_rows.append(
                {
                    "label": label,
                    "status": "matched",
                    "captureExitCode": str(exit_code),
                    "timedOut": str(timed_out).lower(),
                    "stopFrame": str(stop_frame),
                    "bestFrame": str(best["frame"]),
                    "frameOffset": str(int(best["frame"]) - stop_frame),
                    "bestDifferentPixels": str(best_metrics.different_pixels),
                    "bestDifferentPercent": f"{best_metrics.different_percent:.4f}",
                    "bestMaxChannelDelta": str(best_metrics.max_channel_delta),
                    "bestStructuralSimilarity": f"{best_metrics.structural_similarity:.6f}",
                    "bestCoarseMeanDelta": f"{best_metrics.coarse_mean_delta:.4f}",
                    "originalDifferentPixels": str(original_metrics.different_pixels),
                    "originalStructuralSimilarity": f"{original_metrics.structural_similarity:.6f}",
                    "originalCoarseMeanDelta": f"{original_metrics.coarse_mean_delta:.4f}",
                    "candidateCount": str(len(candidates)),
                    "classification": classification,
                    "bestImage": str(best["path"]),
                    "referenceImage": str(reference_path),
                    "message": message,
                }
            )
    except KeyboardInterrupt:
        terminate_active_processes()
        print("Interrupted; terminated active capture process.", file=sys.stderr)
        return 130
    finally:
        terminate_active_processes()

    if not report_rows:
        return 1

    report = output_dir / "window-match.csv"
    with report.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(report_rows[0].keys()))
        writer.writeheader()
        writer.writerows(report_rows)

    contact_sheet = output_dir / "window-match.png"
    make_contact_sheet(report_rows, contact_sheet, args.scale, args.columns)
    for classification in sorted({row["classification"] for row in report_rows}):
        count = sum(1 for row in report_rows if row["classification"] == classification)
        print(f"{classification}: {count}")
    print(report)
    if contact_sheet.exists():
        print(contact_sheet)
    return 0 if all(row["status"] == "matched" for row in report_rows) else 1


if __name__ == "__main__":
    raise SystemExit(main())
