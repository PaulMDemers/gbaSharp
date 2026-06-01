#!/usr/bin/env python3
import argparse
import csv
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build an external reference-capture checklist from route manifests.")
    parser.add_argument("--reference-manifest", default="docs/gba-reference-frames.csv")
    parser.add_argument("--route-manifest", default="docs/gba-save-assisted-deep-routes.csv")
    parser.add_argument("--rom-root", default="curated_official_gba")
    parser.add_argument("--csv-output", default="artifacts/reference-capture-checklist.csv")
    parser.add_argument("--markdown-output", default="artifacts/reference-capture-checklist.md")
    parser.add_argument("--root", default=".")
    return parser.parse_args()


def resolve(root: Path, value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else root / path


def read_csv(path: Path) -> list[dict[str, str]]:
    with path.open(newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def read_input_summary(root: Path, script_value: str) -> tuple[int, str, str]:
    path = resolve(root, script_value)
    if not path.exists():
        return 0, "", f"missing input script: {script_value}"

    commands: list[str] = []
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            commands.append(line)

    if not commands:
        return 0, "", "empty input script"

    preview = "; ".join(commands[:5])
    if len(commands) > 5:
        preview += f"; ... (+{len(commands) - 5} more)"
    return len(commands), preview, ""


def resolve_route_rom(root: Path, rom_root: Path, route: dict[str, str]) -> tuple[str, str]:
    rom_path = (route.get("romPath") or "").strip()
    if rom_path:
        return rom_path, "explicit"

    index_text = (route.get("index") or "").strip()
    if not index_text:
        return "", "missing"

    try:
        index = int(index_text)
    except ValueError:
        return "", f"invalid index: {index_text}"

    roms = sorted(rom_root.rglob("*.gba"))
    if index <= 0 or index > len(roms):
        return "", f"index {index} outside ROM root size {len(roms)}"

    return str(roms[index - 1].relative_to(root)), f"curated index {index}"


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


def write_markdown(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = [
        "# Reference Capture Checklist",
        "",
        "Capture each row in an independent emulator, save the PNG to `referenceImage`, then run `scripts/compare-reference-frames.py`.",
        "",
    ]
    for row in rows:
        lines.extend(
            [
                f"## {row['label']}",
                "",
                f"- ROM: `{row['romPath']}` ({row['romSource']})",
                f"- Save fixture: `{row['saveFile']}`",
                f"- Input script: `{row['inputScript']}`",
                f"- Target frame: `{row['stopFrame']}`",
                f"- Reference output: `{row['referenceImage']}`",
                f"- gbaSharp baseline: `{row['actualImage']}`",
                f"- Input preview: `{row['inputPreview']}`",
                f"- Expected scene: {row['expectedScene']}",
                "",
            ]
        )
        if row["warnings"]:
            lines.extend([f"- Warning: {row['warnings']}", ""])
    path.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")


def main() -> int:
    args = parse_args()
    root = Path(args.root).resolve()
    rom_root = resolve(root, args.rom_root)
    reference_rows = read_csv(resolve(root, args.reference_manifest))
    route_rows = {row["label"]: row for row in read_csv(resolve(root, args.route_manifest))}

    if not reference_rows:
        raise SystemExit("No reference rows found.")

    rows: list[dict[str, str]] = []
    for reference in reference_rows:
        label = reference["label"]
        route = route_rows.get(label)
        if route is None:
            raise SystemExit(f"No route row found for reference label: {label}")

        input_count, input_preview, input_warning = read_input_summary(root, route.get("inputScript", ""))
        rom_path, rom_source = resolve_route_rom(root, rom_root, route)
        warnings = [input_warning] if input_warning else []
        rows.append(
            {
                "label": label,
                "romPath": rom_path,
                "romSource": rom_source,
                "curatedIndex": route.get("index", ""),
                "saveFile": route.get("saveFile", ""),
                "inputScript": route.get("inputScript", ""),
                "inputCommandCount": str(input_count),
                "inputPreview": input_preview,
                "stopFrame": route.get("stopFrame", ""),
                "snapshotFrames": route.get("snapshotFrames", ""),
                "expectedScene": route.get("expectedScene", ""),
                "actualImage": reference.get("actualImage", ""),
                "referenceImage": reference.get("referenceImage", ""),
                "maxDifferentPixels": reference.get("maxDifferentPixels", ""),
                "maxChannelDelta": reference.get("maxChannelDelta", ""),
                "warnings": "; ".join(warnings),
            }
        )

    write_csv(resolve(root, args.csv_output), rows)
    write_markdown(resolve(root, args.markdown_output), rows)
    print(f"wrote {len(rows)} rows")
    print(resolve(root, args.csv_output))
    print(resolve(root, args.markdown_output))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
