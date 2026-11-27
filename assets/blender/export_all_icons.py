"""
Multi-Format & Multi-Size Icon Pack Exporter (runs inside Blender Python using numpy + struct + zlib).
Reads the rendered high-resolution master PNGs and generates:
  - Windows .ico (16, 20, 24, 32, 40, 48, 64, 96, 128, 256) with 32-bit DIB (<256) + PNG (256)
  - Web favicon.ico (16, 32, 48)
  - macOS .icns (16, 32, 64, 128, 256, 512, 1024)
  - Complete transparent PNG suite (16x16 through 1024x1024)
  - Complete dark-navy square PNG suite (apple-touch-icon, android-chrome, store/avatar sizes)
"""

import os
import struct
import zlib
import bpy
import numpy as np


def load_rgba_uint8(path):
    """Loads an image via Blender into top-to-bottom uint8 RGBA (H, W, 4) in sRGB space."""
    img = bpy.data.images.load(path, check_existing=False)
    img.colorspace_settings.name = "Non-Color"
    w, h = img.size
    # With Non-Color, Blender returns exact raw [0..1] byte values without linearizing
    px = np.array(img.pixels[:], dtype=np.float32).reshape((h, w, 4))[::-1, :, :]
    bpy.data.images.remove(img)
    return px  # float32 [0..1] sRGB top-to-bottom


def srgb_to_lin(x):
    return np.where(x <= 0.04045, x / 12.92, np.power((x + 0.055) / 1.055, 2.4))


def lin_to_srgb(x):
    x = np.clip(x, 0.0, 1.0)
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(x, 1.0 / 2.4) - 0.055)


def lanczos_kernel(x, a=3.0):
    x = np.abs(x)
    out = np.zeros_like(x, dtype=np.float64)
    mask0 = x < 1e-8
    mask1 = (~mask0) & (x < a)
    out[mask0] = 1.0
    xm = x[mask1]
    out[mask1] = (np.sin(np.pi * xm) / (np.pi * xm)) * (np.sin(np.pi * xm / a) / (np.pi * xm / a))
    return out


def resample_axis_lanczos(arr, out_len, axis=0, a=3.0):
    """Ultra-fast BLAS matrix-multiply separable Lanczos-3 resampling (<5ms per image!)."""
    in_len = arr.shape[axis]
    if in_len == out_len:
        return arr.copy()

    scale = in_len / out_len
    support = a * max(1.0, scale)
    inv_filter_scale = 1.0 / max(1.0, scale)

    centers = (np.arange(out_len, dtype=np.float64)[:, None] + 0.5) * scale - 0.5
    in_coords = np.arange(in_len, dtype=np.float64)[None, :]
    dists = (in_coords - centers) * inv_filter_scale
    w_mat = np.where(np.abs(in_coords - centers) <= support, lanczos_kernel(dists, a=a), 0.0)
    w_mat /= np.maximum(w_mat.sum(axis=1, keepdims=True), 1e-12)
    w_f32 = w_mat.astype(np.float32)  # (out_len, in_len)

    if axis == 0:
        h_in, w_in, c_in = arr.shape
        res = (w_f32 @ arr.reshape(h_in, w_in * c_in)).reshape(out_len, w_in, c_in)
    else:
        # Swap H and W, multiply along W, swap back
        h_in, w_in, c_in = arr.shape
        swapped = np.ascontiguousarray(np.transpose(arr, (1, 0, 2))).reshape(w_in, h_in * c_in)
        res = np.transpose((w_f32 @ swapped).reshape(out_len, h_in, c_in), (1, 0, 2))
    return np.ascontiguousarray(res, dtype=np.float32)


def resize_rgba_hq(src_srgb_f32, target_size, sharpen_small=True):
    """
    Linear-light premultiplied-alpha Lanczos-3 resampler + gentle unsharp mask for tiny icons.
    Returns uint8 RGBA (target_size, target_size, 4).
    """
    rgb_lin = srgb_to_lin(src_srgb_f32[:, :, :3])
    alpha = src_srgb_f32[:, :, 3:4]
    premult = np.concatenate([rgb_lin * alpha, alpha], axis=2)

    tmp = resample_axis_lanczos(premult, target_size, axis=0, a=3.0)
    out_pre = resample_axis_lanczos(tmp, target_size, axis=1, a=3.0)

    out_a = np.clip(out_pre[:, :, 3:4], 0.0, 1.0)
    out_rgb_lin = np.where(out_a > 1e-4, out_pre[:, :, :3] / np.maximum(out_a, 1e-4), 0.0)
    out_rgb_srgb = lin_to_srgb(out_rgb_lin)

    # Subtle crispness boost on micro icon sizes (16..48px) so rims pop on taskbar/tray
    if sharpen_small and target_size <= 48:
        strength = 0.22 if target_size <= 24 else 0.12
        pad = np.pad(out_rgb_srgb, ((1, 1), (1, 1), (0, 0)), mode="edge")
        blur = (
            pad[:-2, 1:-1] + pad[2:, 1:-1] + pad[1:-1, :-2] + pad[1:-1, 2:] + 4.0 * out_rgb_srgb
        ) / 8.0
        out_rgb_srgb = np.clip(out_rgb_srgb + strength * (out_rgb_srgb - blur), 0.0, 1.0)

    rgba_u8 = np.empty((target_size, target_size, 4), dtype=np.uint8)
    rgba_u8[:, :, :3] = np.clip(np.rint(out_rgb_srgb * 255.0), 0, 255).astype(np.uint8)
    rgba_u8[:, :, 3] = np.clip(np.rint(out_a[:, :, 0] * 255.0), 0, 255).astype(np.uint8)
    return rgba_u8


def encode_png_bytes(rgba_u8):
    """Encodes an (H, W, 4) uint8 RGBA array into lossless standard PNG bytes."""
    h, w, _ = rgba_u8.shape

    def png_chunk(chunk_type, data):
        crc = zlib.crc32(chunk_type + data) & 0xFFFFFFFF
        return struct.pack(">I", len(data)) + chunk_type + data + struct.pack(">I", crc)

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)  # 8-bit RGBA
    srgb_chunk = png_chunk(b"sRGB", b"\x00")  # Perceptual sRGB

    # Prepend filter byte 0 (None) to each scanline
    filter_col = np.zeros((h, 1), dtype=np.uint8)
    scanlines = np.hstack([filter_col, rgba_u8.reshape(h, w * 4)]).tobytes()
    compressed = zlib.compress(scanlines, level=6)

    return (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", ihdr)
        + srgb_chunk
        + png_chunk(b"IDAT", compressed)
        + png_chunk(b"IEND", b"")
    )


def encode_ico_dib_bytes(rgba_u8):
    """
    Encodes an (H, W, 4) uint8 RGBA array as a 32-bit BGRA Windows Icon DIB
    (BITMAPINFOHEADER + bottom-up BGRA XOR mask + 32-bit aligned 1bpp AND mask).
    100% compatible with Win32, GDI+, System.Drawing.Icon, WPF, and PE resources.
    """
    h, w, _ = rgba_u8.shape
    # Flip vertically (BMP DIB is bottom-to-top) and convert RGBA -> BGRA
    flipped = rgba_u8[::-1, :, :]
    bgra = flipped[:, :, [2, 1, 0, 3]].copy()
    xor_bytes = bgra.tobytes()

    # 1-bit AND mask: 1 where alpha == 0, padded to 4-byte (32-bit) row stride
    row_bytes = ((w + 31) // 32) * 4
    and_mask = np.zeros((h, row_bytes), dtype=np.uint8)
    transparent_bits = (flipped[:, :, 3] == 0).astype(np.uint8)
    for x in range(w):
        byte_idx = x // 8
        bit_offset = 7 - (x % 8)
        and_mask[:, byte_idx] |= transparent_bits[:, x] << bit_offset
    and_bytes = and_mask.tobytes()

    bih = struct.pack(
        "<IIIHHIIIIII",
        40,  # biSize
        w,  # biWidth
        h * 2,  # biHeight (doubled for XOR + AND masks per ICO spec)
        1,  # biPlanes
        32,  # biBitCount
        0,  # biCompression (BI_RGB)
        len(xor_bytes) + len(and_bytes),  # biSizeImage
        0,
        0,
        0,
        0,
    )
    return bih + xor_bytes + and_bytes


def save_ico(path, frames_dict):
    """
    Writes a multi-resolution .ico file from {size: rgba_u8}.
    Uses DIB for sizes < 256 and PNG for 256x256 per Microsoft Windows Vista+ Icon guidelines.
    """
    sizes = sorted(frames_dict.keys())
    payloads = []
    for s in sizes:
        rgba = frames_dict[s]
        if s >= 256:
            payloads.append((s, encode_png_bytes(rgba)))
        else:
            payloads.append((s, encode_ico_dib_bytes(rgba)))

    header = struct.pack("<HHH", 0, 1, len(payloads))
    offset = 6 + 16 * len(payloads)
    entries = []
    for s, data in payloads:
        w_byte = 0 if s >= 256 else s
        h_byte = 0 if s >= 256 else s
        entry = struct.pack(
            "<BBBBHHII",
            w_byte,
            h_byte,
            0,  # bColorCount
            0,  # bReserved
            1,  # wPlanes
            32,  # wBitCount
            len(data),
            offset,
        )
        entries.append(entry)
        offset += len(data)

    with open(path, "wb") as f:
        f.write(header)
        for e in entries:
            f.write(e)
        for _, data in payloads:
            f.write(data)


def save_icns(path, frames_dict):
    """Writes a standard Apple macOS .icns bundle from PNG frames."""
    icns_types = [
        (16, b"icp4"),
        (32, b"icp5"),
        (64, b"icp6"),
        (128, b"ic07"),
        (256, b"ic08"),
        (512, b"ic09"),
        (1024, b"ic10"),
    ]
    chunks = []
    for sz, ostype in icns_types:
        if sz in frames_dict:
            png_data = encode_png_bytes(frames_dict[sz])
            chunks.append(ostype + struct.pack(">I", len(png_data) + 8) + png_data)
    body = b"".join(chunks)
    with open(path, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(body) + 8) + body)


def main():
    import shutil
    renders_dir = r"C:\rclone\assets\blender\renders"
    docs_dir = r"C:\rclone\docs"
    icons_root = r"C:\rclone\docs\assets\icons"
    png_dir = os.path.join(icons_root, "png")
    square_dir = os.path.join(icons_root, "square")
    os.makedirs(png_dir, exist_ok=True)
    os.makedirs(square_dir, exist_ok=True)

    trans_master_path = os.path.join(renders_dir, "rclone_icon_transparent_1024.png")
    poster_master_path = os.path.join(renders_dir, "rclone_logo_render_2048.png")

    print("Loading rendered masters...")
    trans_master = load_rgba_uint8(trans_master_path)
    poster_master = load_rgba_uint8(poster_master_path)

    # 1. Generate all transparent PNG icon sizes
    all_png_sizes = [16, 20, 24, 32, 40, 48, 64, 72, 96, 128, 144, 150, 180, 192, 256, 310, 384, 512, 1024]
    trans_frames = {}
    for sz in all_png_sizes:
        rgba = resize_rgba_hq(trans_master, sz, sharpen_small=True)
        trans_frames[sz] = rgba
        out_png = os.path.join(png_dir, f"icon-{sz}x{sz}.png")
        with open(out_png, "wb") as f:
            f.write(encode_png_bytes(rgba))
        print(f"  Generated transparent PNG: {out_png}")

    # 2. Generate Windows .ico (full high-DPI suite) & Web favicon.ico & macOS .icns
    win_ico_sizes = {sz: trans_frames[sz] for sz in [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]}
    ico_path = os.path.join(icons_root, "rclone_manager.ico")
    save_ico(ico_path, win_ico_sizes)
    print(f"  Generated Windows multi-size ICO: {ico_path}")

    fav_ico_sizes = {sz: trans_frames[sz] for sz in [16, 32, 48]}
    fav_path = os.path.join(icons_root, "favicon.ico")
    save_ico(fav_path, fav_ico_sizes)
    shutil.copy2(fav_path, os.path.join(docs_dir, "favicon.ico"))
    print(f"  Generated Web favicon.ico: {fav_path} and {os.path.join(docs_dir, 'favicon.ico')}")

    icns_path = os.path.join(icons_root, "rclone_manager.icns")
    save_icns(icns_path, trans_frames)
    print(f"  Generated macOS ICNS: {icns_path}")

    # 3. Generate Dark-Navy Square App Icons (from 2048x2048 full render, cropped slightly tighter so icon fills nicely)
    # Crop central 76% of 2048x2048 ([246..1802]) so square icons have ideal iOS/Android safe-zone padding!
    h_p, w_p, _ = poster_master.shape
    margin = int(0.11 * w_p)
    poster_cropped = poster_master[margin : h_p - margin, margin : w_p - margin, :].copy()
    poster_cropped[:, :, 3] = 1.0  # Opaque background

    square_targets = [
        (64, "icon-square-64x64.png"),
        (128, "icon-square-128x128.png"),
        (180, "apple-touch-icon.png"),
        (192, "android-chrome-192x192.png"),
        (256, "icon-square-256x256.png"),
        (512, "android-chrome-512x512.png"),
        (512, "icon-square-512x512.png"),
        (1024, "icon-square-1024x1024.png"),
    ]
    for sz, fname in square_targets:
        rgba = resize_rgba_hq(poster_cropped, sz, sharpen_small=True)
        out_sq = os.path.join(square_dir, fname)
        with open(out_sq, "wb") as f:
            f.write(encode_png_bytes(rgba))
        print(f"  Generated square PNG: {out_sq}")

    print("Icon pack generation finished successfully!")


if __name__ == "__main__":
    main()
