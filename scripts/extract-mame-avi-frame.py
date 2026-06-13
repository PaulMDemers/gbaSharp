#!/usr/bin/env python3
import argparse
import struct
from pathlib import Path

from PIL import Image


def iter_chunks(data: bytes, start: int, end: int):
    offset = start
    while offset + 8 <= end:
        chunk_id = data[offset : offset + 4]
        size = struct.unpack_from("<I", data, offset + 4)[0]
        payload = offset + 8
        padded_size = size + (size & 1)
        yield chunk_id, payload, size
        offset = payload + padded_size


def find_chunk(data: bytes, target: bytes) -> tuple[int, int]:
    offset = data.find(target)
    while offset >= 0 and offset + 8 <= len(data):
        size = struct.unpack_from("<I", data, offset + 4)[0]
        payload = offset + 8
        if payload + size <= len(data):
            return payload, size
        offset = data.find(target, offset + 1)

    raise ValueError(f"Chunk {target!r} not found")


def find_bitmap_format(data: bytes) -> tuple[int, int, int]:
    payload, size = find_chunk(data, b"strf")
    if size < 16:
        raise ValueError("AVI strf chunk is too small for BITMAPINFOHEADER")

    header_size, width, signed_height, planes, bit_count = struct.unpack_from("<IiiHH", data, payload)
    if header_size < 40:
        raise ValueError(f"Unsupported BITMAPINFOHEADER size: {header_size}")
    if planes != 1:
        raise ValueError(f"Unsupported AVI plane count: {planes}")
    if bit_count not in (24, 32):
        raise ValueError(f"Unsupported AVI bit depth: {bit_count}")

    return width, signed_height, bit_count


def find_video_frames(data: bytes) -> list[bytes]:
    frames: list[bytes] = []
    offset = 12
    while offset + 8 <= len(data):
        chunk_id = data[offset : offset + 4]
        size = struct.unpack_from("<I", data, offset + 4)[0]
        payload = offset + 8
        if chunk_id == b"LIST" and data[payload : payload + 4] == b"movi":
            for child_id, child_payload, child_size in iter_chunks(data, payload + 4, payload + size):
                if child_id.endswith(b"db") or child_id.endswith(b"dc"):
                    frames.append(data[child_payload : child_payload + child_size])
            break
        offset = payload + size + (size & 1)

    if not frames:
        raise ValueError("No video frames found in AVI movi list")
    return frames


def frame_to_image(frame: bytes, width: int, signed_height: int, bit_count: int) -> Image.Image:
    height = abs(signed_height)
    bytes_per_pixel = bit_count // 8
    row_stride = ((width * bytes_per_pixel) + 3) & ~3
    expected = row_stride * height
    if len(frame) < expected:
        raise ValueError(f"Frame is too small: expected {expected} bytes, got {len(frame)}")

    rows = []
    for row in range(height):
        source_row = row if signed_height < 0 else height - 1 - row
        start = source_row * row_stride
        row_data = frame[start : start + width * bytes_per_pixel]
        pixels = bytearray()
        for column in range(width):
            pixel = row_data[column * bytes_per_pixel : (column + 1) * bytes_per_pixel]
            blue, green, red = pixel[:3]
            pixels.extend((red, green, blue))
        rows.append(bytes(pixels))

    return Image.frombytes("RGB", (width, height), b"".join(rows))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Extract a PNG frame from MAME's uncompressed AVI output.")
    parser.add_argument("avi", help="Input AVI from MAME -aviwrite")
    parser.add_argument("png", help="Output PNG path")
    parser.add_argument("--frame-index", type=int, default=-1, help="0-based frame index; negative values count from the end")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    avi_path = Path(args.avi)
    png_path = Path(args.png)
    data = avi_path.read_bytes()
    if data[:4] != b"RIFF" or data[8:12] != b"AVI ":
        raise SystemExit(f"{avi_path} is not an AVI RIFF file")

    width, signed_height, bit_count = find_bitmap_format(data)
    frames = find_video_frames(data)
    frame_index = args.frame_index if args.frame_index >= 0 else len(frames) + args.frame_index
    if frame_index < 0 or frame_index >= len(frames):
        raise SystemExit(f"Frame index {args.frame_index} is out of range for {len(frames)} frame(s)")

    image = frame_to_image(frames[frame_index], width, signed_height, bit_count)
    png_path.parent.mkdir(parents=True, exist_ok=True)
    image.save(png_path)
    print(f"Wrote frame {frame_index} of {len(frames)} to {png_path.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
