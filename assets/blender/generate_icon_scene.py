"""
Procedural 3D Icon & Scene Generator for Blender 5.x
Generates the Rclone Cloud + Folder + Sync Arrows 3D glossy neon icon,
saves the .blend project, and renders both the full dark-navy scene
and a tightly framed transparent master icon for multi-size icon export.
"""

import math
import os
import bpy
import numpy as np


# ==============================================================================
# 1. 2D SIGNED DISTANCE FIELD (SDF) & MARCHING SQUARES CONTOUR ENGINE
# ==============================================================================

def sd_circle(px, py, cx, cy, r):
    return np.hypot(px - cx, py - cy) - r


def sd_round_box(px, py, x0, y0, x1, y1, r):
    cx = 0.5 * (x0 + x1)
    cy = 0.5 * (y0 + y1)
    hx = 0.5 * (x1 - x0) - r
    hy = 0.5 * (y1 - y0) - r
    dx = np.abs(px - cx) - hx
    dy = np.abs(py - cy) - hy
    outside = np.hypot(np.maximum(dx, 0.0), np.maximum(dy, 0.0))
    inside = np.minimum(np.maximum(dx, dy), 0.0)
    return outside + inside - r


def sd_polygon(px, py, verts):
    """Exact vectorized 2D signed distance to a closed polygon (negative inside)."""
    v = np.asarray(verts, dtype=np.float32)
    n = len(v)
    min_d2 = np.full(px.shape, 1e12, dtype=np.float32)
    inside = np.zeros(px.shape, dtype=bool)

    for i in range(n):
        x1, y1 = v[i]
        x2, y2 = v[(i + 1) % n]
        ex, ey = x2 - x1, y2 - y1
        wx = px - x1
        wy = py - y1
        ee = ex * ex + ey * ey
        t = np.clip((wx * ex + wy * ey) / max(ee, 1e-8), 0.0, 1.0)
        bx = wx - ex * t
        by = wy - ey * t
        d2 = bx * bx + by * by
        min_d2 = np.minimum(min_d2, d2)

        cond = ((y1 > py) != (y2 > py)) & (
            px < (x2 - x1) * (py - y1) / np.where(np.abs(y2 - y1) > 1e-7, y2 - y1, 1e-7) + x1
        )
        inside ^= cond

    dist = np.sqrt(min_d2)
    return np.where(inside, -dist, dist)


def smin(a, b, k):
    """Smooth minimum (filleted union) with radius k."""
    if k <= 1e-5:
        return np.minimum(a, b)
    h = np.maximum(k - np.abs(a - b), 0.0) / k
    return np.minimum(a, b) - h * h * k * 0.25


def smax(a, b, k):
    """Smooth maximum (filleted intersection / subtraction) with radius k."""
    return -smin(-a, -b, k)


def sd_arc_ring(px, py, cx, cy, r_mid, half_width, ang_start_deg, ang_end_deg):
    """
    Signed distance to a circular arc band in image coordinates (px right, py down).
    Angles are measured in standard screen degrees: 0 = right (+x), -90 = top (-y), +90 = bottom (+y), 180 = left (-x).
    Arc sweeps clockwise (increasing screen angle) from ang_start_deg to ang_end_deg.
    """
    dx = px - cx
    dy = py - cy
    ang = np.degrees(np.arctan2(dy, dx))
    # Normalize relative to ang_start_deg in [0, 360)
    span = (ang_end_deg - ang_start_deg) % 360.0
    rel = (ang - ang_start_deg) % 360.0

    in_sector = rel <= span
    r = np.hypot(dx, dy)
    d_radial = np.abs(r - r_mid) - half_width

    # Distance to the two radial end caps
    a0 = math.radians(ang_start_deg)
    a1 = math.radians(ang_end_deg)
    ux0, uy0 = math.cos(a0), math.sin(a0)
    ux1, uy1 = math.cos(a1), math.sin(a1)

    t0 = np.clip(dx * ux0 + dy * uy0, r_mid - half_width, r_mid + half_width)
    d_cap0 = np.hypot(dx - t0 * ux0, dy - t0 * uy0)

    t1 = np.clip(dx * ux1 + dy * uy1, r_mid - half_width, r_mid + half_width)
    d_cap1 = np.hypot(dx - t1 * ux1, dy - t1 * uy1)

    d_caps = np.minimum(d_cap0, d_cap1)
    return np.where(in_sector, d_radial, d_caps)


def extract_contours(field, xs, ys, level=0.0):
    """
    Sub-pixel linear-interpolated Marching Squares contour extractor.
    Returns a list of closed Nx2 float64 contours in (px, py) coordinates.
    """
    f = field - level
    h, w = f.shape
    f00 = f[:-1, :-1]
    f10 = f[:-1, 1:]
    f11 = f[1:, 1:]
    f01 = f[1:, :-1]

    case = (
        ((f00 < 0).astype(np.uint8) << 0)
        | ((f10 < 0).astype(np.uint8) << 1)
        | ((f11 < 0).astype(np.uint8) << 2)
        | ((f01 < 0).astype(np.uint8) << 3)
    )

    active_ys, active_xs = np.where((case != 0) & (case != 15))
    if len(active_ys) == 0:
        return []

    dx = xs[1] - xs[0]
    dy = ys[1] - ys[0]
    x0_base = xs[0]
    y0_base = ys[0]

    # Marching squares segment table (edges: 0=top, 1=right, 2=bottom, 3=left)
    # Directed so interior (<0) is consistently to the right/left for clean chaining
    seg_table = {
        1: [(3, 0)],
        2: [(0, 1)],
        3: [(3, 1)],
        4: [(1, 2)],
        5: [(3, 2), (1, 0)],
        6: [(0, 2)],
        7: [(3, 2)],
        8: [(2, 3)],
        9: [(2, 0)],
        10: [(0, 3), (2, 1)],
        11: [(2, 1)],
        12: [(1, 3)],
        13: [(1, 0)],
        14: [(0, 3)],
    }

    adj = {}

    def edge_key_and_pt(ix, iy, edge):
        if edge == 0:  # top: (ix, iy) -> (ix+1, iy)
            v0, v1 = f[iy, ix], f[iy, ix + 1]
            t = float(v0 / (v0 - v1)) if abs(v0 - v1) > 1e-9 else 0.5
            key = (0, ix, iy)
            pt = (x0_base + (ix + t) * dx, y0_base + iy * dy)
        elif edge == 1:  # right: (ix+1, iy) -> (ix+1, iy+1)
            v0, v1 = f[iy, ix + 1], f[iy + 1, ix + 1]
            t = float(v0 / (v0 - v1)) if abs(v0 - v1) > 1e-9 else 0.5
            key = (1, ix + 1, iy)
            pt = (x0_base + (ix + 1) * dx, y0_base + (iy + t) * dy)
        elif edge == 2:  # bottom: (ix, iy+1) -> (ix+1, iy+1)
            v0, v1 = f[iy + 1, ix], f[iy + 1, ix + 1]
            t = float(v0 / (v0 - v1)) if abs(v0 - v1) > 1e-9 else 0.5
            key = (0, ix, iy + 1)
            pt = (x0_base + (ix + t) * dx, y0_base + (iy + 1) * dy)
        else:  # left: (ix, iy) -> (ix, iy+1)
            v0, v1 = f[iy, ix], f[iy + 1, ix]
            t = float(v0 / (v0 - v1)) if abs(v0 - v1) > 1e-9 else 0.5
            key = (1, ix, iy)
            pt = (x0_base + ix * dx, y0_base + (iy + t) * dy)
        return key, pt

    pts_map = {}
    for iy, ix in zip(active_ys.tolist(), active_xs.tolist()):
        c = int(case[iy, ix])
        for e_start, e_end in seg_table[c]:
            k0, p0 = edge_key_and_pt(ix, iy, e_start)
            k1, p1 = edge_key_and_pt(ix, iy, e_end)
            pts_map[k0] = p0
            pts_map[k1] = p1
            adj.setdefault(k0, []).append(k1)

    loops = []
    visited_edges = set()
    for start_k in list(adj.keys()):
        for next_k in adj[start_k]:
            if (start_k, next_k) in visited_edges:
                continue
            loop_pts = [pts_map[start_k]]
            visited_edges.add((start_k, next_k))
            curr = next_k
            guard = 0
            while curr != start_k and guard < 200000:
                guard += 1
                loop_pts.append(pts_map[curr])
                outs = adj.get(curr, [])
                chosen = None
                for cand in outs:
                    if (curr, cand) not in visited_edges:
                        chosen = cand
                        break
                if chosen is None:
                    break
                visited_edges.add((curr, chosen))
                curr = chosen
            if len(loop_pts) >= 12:
                arr = np.asarray(loop_pts, dtype=np.float64)
                # Smooth staircase micro-jitter while preserving sharp corners (2 passes Laplacian)
                for _ in range(3):
                    arr = 0.25 * np.roll(arr, 1, axis=0) + 0.50 * arr + 0.25 * np.roll(arr, -1, axis=0)
                # Subsample gently so spline vertices are ~1.2px apart (smooth & fast beveling)
                step = max(1, len(arr) // 700)
                arr = arr[::step]
                if len(arr) >= 8:
                    loops.append(arr)
    return loops


# ==============================================================================
# 2. ICON SHAPE DEFINITIONS (1024x1024 REFERENCE COORDINATES)
# ==============================================================================

def build_sdf_fields(grid_res=1200):
    xs = np.linspace(180.0, 844.0, grid_res, dtype=np.float32)
    ys = np.linspace(180.0, 844.0, grid_res, dtype=np.float32)
    px, py = np.meshgrid(xs, ys)

    # --- A. FOLDER ---
    # Main body + Top-left tab + C1-continuous bottom-left swoosh tail
    d_f_body = sd_round_box(px, py, 407.0, 535.0, 616.0, 676.0, 22.0)
    d_f_tab = sd_round_box(px, py, 407.0, 508.0, 492.0, 575.0, 16.0)
    d_folder_core = smin(d_f_body, d_f_tab, 14.0)

    # Disk centered at cx=429 (exact tangent point where d_f_body's flat bottom y=676 begins: 407+22=429!)
    # with cy=618, R=58 (618+58=676 -> exact C1 bottom tangency),
    # carved from upper-left by circle (cx=363, cy=600, R=44 -> 363+44=407 exact C1 left-wall tangency!).
    d_tail_disk = sd_circle(px, py, 429.0, 618.0, 58.0)
    d_tail_disk = np.maximum(d_tail_disk, 600.0 - py)
    d_tail_disk = np.maximum(d_tail_disk, px - 430.0)
    d_tail_carve = sd_circle(px, py, 363.0, 600.0, 44.0)
    d_tail = np.maximum(d_tail_disk, -d_tail_carve)
    d_folder = np.minimum(d_folder_core, d_tail)

    # --- B. CLOUD ---
    # 3 circular lobes + flat bottom base at y=676, minus 19.5px offset of Folder
    d_c_left = sd_circle(px, py, 429.0, 502.0, 112.0)
    d_c_top = sd_circle(px, py, 538.0, 471.0, 129.0)
    d_c_right = sd_circle(px, py, 681.0, 562.0, 114.0)
    d_c_base = sd_round_box(px, py, 435.0, 530.0, 685.0, 676.0, 12.0)

    d_cloud_raw = smin(smin(d_c_left, d_c_top, 8.0), d_c_right, 8.0)
    d_cloud_raw = np.minimum(d_cloud_raw, d_c_base)
    # Subtract Folder expanded by 19.5px with smooth 9px rounded corners
    d_cloud = smax(d_cloud_raw, -(d_folder - 19.5), 9.0)

    # --- C. BOTTOM SYNC ARROW ---
    # Arrowhead pointing up-left at (270, 526)
    bot_head_poly = [
        (270.0, 526.0),
        (343.0, 586.0),
        (251.0, 624.0),
    ]
    d_bot_head = sd_polygon(px, py, bot_head_poly) - 2.0

    # Arc from bottom-right (~36 deg) clockwise through +90 deg (bottom) to +156 deg (left-bottom neck)
    d_bot_arc = sd_arc_ring(px, py, 512.0, 512.0, 247.0, 23.0, 35.0, 157.0)
    # Trim the right tail horizontally along y = 696 (exact 20px gap below cloud bottom y=676!)
    # Only apply y >= 696 cut on the right half (px > 540)
    right_cut = np.where(px > 540.0, 696.0 - py, -1000.0)
    d_bot_arc = smax(d_bot_arc, right_cut, 3.0)
    d_bot_arrow = np.minimum(d_bot_arc, d_bot_head)

    # --- D. TOP SYNC ARROW ---
    # Arrowhead pointing down-right at (739, 423)
    top_head_poly = [
        (739.0, 423.0),
        (651.0, 382.0),
        (738.0, 327.0),
    ]
    d_top_head = sd_polygon(px, py, top_head_poly) - 2.0

    # Arc from left (~177 deg) clockwise through 270 deg (-90 deg top) to ~323.5 deg
    d_top_arc = sd_arc_ring(px, py, 512.0, 512.0, 247.0, 23.0, 177.0, 323.5)
    # Smoothly taper left tail above Bottom Arrowhead & inside Left Crescent (single unbroken tail!)
    d_left_taper = sd_circle(px, py, 212.0, 542.0, 66.0)
    d_top_arc = smax(d_top_arc, -d_left_taper, 4.0)
    d_top_arc = smax(d_top_arc, -(d_bot_head - 17.5), 4.0)
    d_top_arrow = np.minimum(d_top_arc, d_top_head)

    # --- E. LEFT ACCENT CRESCENT ---
    # Unbroken sleek outer-left wing: R in [261..282] around left transition, cleanly separated by 15px
    d_cres_ring = sd_arc_ring(px, py, 512.0, 512.0, 271.5, 10.5, 162.0, 192.0)
    d_cres_bound = sd_circle(px, py, 248.0, 537.0, 49.0)
    d_crescent = smax(d_cres_ring, d_cres_bound, 5.0)
    d_crescent = smax(d_crescent, -(d_top_arrow - 14.5), 3.0)
    d_crescent = smax(d_crescent, -(d_bot_arrow - 15.5), 3.0)

    # --- F. DUAL-TRACK OUTER RIDGE BANDS FOR ARROWS ---
    # Top arrow outer bright ridge (outer half of the band + outer rim of arrowhead)
    d_top_outer_track = np.maximum(d_top_arrow, -(sd_circle(px, py, 512.0, 512.0, 248.0)))
    d_top_rim_field = np.maximum(d_top_arrow, -(d_top_arrow + 7.5))
    d_top_ridge = np.minimum(d_top_outer_track, d_top_rim_field)

    # Bottom arrow inner/mid stepped ridge + rim
    d_bot_inner_track = np.maximum(d_bot_arrow, sd_circle(px, py, 512.0, 512.0, 257.0))
    d_bot_rim_field = np.maximum(d_bot_arrow, -(d_bot_arrow + 7.0))
    d_bot_ridge = d_bot_rim_field

    return {
        "xs": xs,
        "ys": ys,
        "px": px,
        "py": py,
        "cloud": d_cloud,
        "folder": d_folder,
        "top_arrow": d_top_arrow,
        "top_ridge": d_top_ridge,
        "bot_arrow": d_bot_arrow,
        "bot_ridge": d_bot_ridge,
        "bot_inner_track": d_bot_inner_track,
        "crescent": d_crescent,
    }


# ==============================================================================
# 3. BLENDER 3D CURVE / MESH & SHADER BUILDERS
# ==============================================================================

WORLD_SCALE = 10.0 / 1024.0  # Maps 1024x1024 pixels -> [-5.0, +5.0] Blender units


def px_to_world(pts_2d):
    wx = (pts_2d[:, 0] - 512.0) * WORLD_SCALE
    wy = (512.0 - pts_2d[:, 1]) * WORLD_SCALE
    return wx, wy


def create_2d_curve_object(
    name,
    loops_list,
    collection,
    z_pos=0.0,
    extrude=0.10,
    bevel_depth=0.02,
    bevel_res=6,
    material=None,
):
    curve_data = bpy.data.curves.new(name=f"{name}_Curve", type="CURVE")
    curve_data.dimensions = "2D"
    curve_data.fill_mode = "BOTH"
    curve_data.extrude = extrude
    curve_data.bevel_depth = bevel_depth
    curve_data.bevel_resolution = bevel_res
    curve_data.use_fill_caps = True

    for loop in loops_list:
        wx, wy = px_to_world(loop)
        spline = curve_data.splines.new(type="POLY")
        spline.points.add(len(wx) - 1)
        coords = np.ones((len(wx), 4), dtype=np.float32)
        coords[:, 0] = wx
        coords[:, 1] = wy
        coords[:, 2] = 0.0
        spline.points.foreach_set("co", coords.ravel())
        spline.use_cyclic_u = True
        spline.use_smooth = True

    obj = bpy.data.objects.new(name, curve_data)
    obj.location = (0.0, 0.0, z_pos)
    collection.objects.link(obj)
    if material is not None:
        obj.data.materials.append(material)
    return obj


def srgb_to_linear(c):
    out = []
    for ch in c[:3]:
        if ch <= 0.04045:
            out.append(ch / 12.92)
        else:
            out.append(((ch + 0.055) / 1.055) ** 2.4)
    out.append(c[3] if len(c) > 3 else 1.0)
    return tuple(out)


def hex_rgba(hex_str, alpha=1.0):
    h = hex_str.lstrip("#")
    r = int(h[0:2], 16) / 255.0
    g = int(h[2:4], 16) / 255.0
    b = int(h[4:6], 16) / 255.0
    return srgb_to_linear((r, g, b, alpha))


def new_material(name):
    mat = bpy.data.materials.new(name=name)
    if not mat.use_nodes:
        mat.use_nodes = True
    nt = mat.node_tree
    nt.nodes.clear()
    return mat, nt


def add_coord_nodes(nt, ref_empty):
    tex = nt.nodes.new("ShaderNodeTexCoord")
    tex.object = ref_empty
    sep = nt.nodes.new("ShaderNodeSeparateXYZ")
    nt.links.new(tex.outputs["Object"], sep.inputs["Vector"])
    return tex, sep


def add_arc_mask_node(nt, tex_node, cx_px, cy_px, radius_px, softness=0.015):
    """
    Creates a smooth mask = 1 inside circle (cx_px, cy_px, radius_px), 0 outside.
    Used for the signature sweeping diagonal glossy reflection across Cloud & Folder!
    """
    wx = (cx_px - 512.0) * WORLD_SCALE
    wy = (512.0 - cy_px) * WORLD_SCALE
    wr = radius_px * WORLD_SCALE

    dist = nt.nodes.new("ShaderNodeVectorMath")
    dist.operation = "DISTANCE"
    dist.inputs[1].default_value = (wx, wy, 0.0)
    nt.links.new(tex_node.outputs["Object"], dist.inputs[0])

    map_r = nt.nodes.new("ShaderNodeMapRange")
    map_r.clamp = True
    map_r.inputs["From Min"].default_value = wr - softness
    map_r.inputs["From Max"].default_value = wr + softness
    map_r.inputs["To Min"].default_value = 1.0
    map_r.inputs["To Max"].default_value = 0.0
    nt.links.new(dist.outputs["Value"], map_r.inputs["Value"])
    return map_r.outputs["Result"]


def build_glossy_body_material(
    name,
    ref_empty,
    upper_top_hex,
    upper_bot_hex,
    lower_top_hex,
    lower_bot_hex,
    arc_cx,
    arc_cy,
    arc_r,
    y_min_w,
    y_max_w,
    emission_strength=0.42,
    extra_top_glow_pos=None,
):
    mat, nt = new_material(name)
    tex, sep = add_coord_nodes(nt, ref_empty)

    # Vertical normalized factor t_y in [0 (bottom), 1 (top)]
    map_y = nt.nodes.new("ShaderNodeMapRange")
    map_y.clamp = True
    map_y.inputs["From Min"].default_value = y_min_w
    map_y.inputs["From Max"].default_value = y_max_w
    nt.links.new(sep.outputs["Y"], map_y.inputs["Value"])

    # Horizontal factor t_x in [0 (left), 1 (right)] to enrich cyan-to-blue gradient
    map_x = nt.nodes.new("ShaderNodeMapRange")
    map_x.clamp = True
    map_x.inputs["From Min"].default_value = -2.0
    map_x.inputs["From Max"].default_value = 2.8
    nt.links.new(sep.outputs["X"], map_x.inputs["Value"])

    # Upper glossy reflection gradient
    mix_upper = nt.nodes.new("ShaderNodeMix")
    mix_upper.data_type = "RGBA"
    mix_upper.inputs["A"].default_value = hex_rgba(upper_bot_hex)
    mix_upper.inputs["B"].default_value = hex_rgba(upper_top_hex)
    nt.links.new(map_y.outputs["Result"], mix_upper.inputs["Factor"])

    # Lower deep ocean glass gradient
    mix_lower = nt.nodes.new("ShaderNodeMix")
    mix_lower.data_type = "RGBA"
    mix_lower.inputs["A"].default_value = hex_rgba(lower_bot_hex)
    mix_lower.inputs["B"].default_value = hex_rgba(lower_top_hex)
    nt.links.new(map_y.outputs["Result"], mix_lower.inputs["Factor"])

    # Crisp curved reflection boundary
    arc_mask = add_arc_mask_node(nt, tex, arc_cx, arc_cy, arc_r, softness=0.018)

    mix_final = nt.nodes.new("ShaderNodeMix")
    mix_final.data_type = "RGBA"
    nt.links.new(arc_mask, mix_final.inputs["Factor"])
    nt.links.new(mix_lower.outputs["Result"], mix_final.inputs["A"])
    nt.links.new(mix_upper.outputs["Result"], mix_final.inputs["B"])

    color_out = mix_final.outputs["Result"]

    # Optional bright top-spot glow (e.g., top lobe of cloud!)
    if extra_top_glow_pos is not None:
        gx, gy, gr, g_hex = extra_top_glow_pos
        spot = nt.nodes.new("ShaderNodeVectorMath")
        spot.operation = "DISTANCE"
        spot.inputs[1].default_value = ((gx - 512.0) * WORLD_SCALE, (512.0 - gy) * WORLD_SCALE, 0.0)
        nt.links.new(tex.outputs["Object"], spot.inputs[0])

        spot_map = nt.nodes.new("ShaderNodeMapRange")
        spot_map.clamp = True
        spot_map.inputs["From Min"].default_value = 0.0
        spot_map.inputs["From Max"].default_value = gr * WORLD_SCALE
        spot_map.inputs["To Min"].default_value = 0.75
        spot_map.inputs["To Max"].default_value = 0.0
        nt.links.new(spot.outputs["Value"], spot_map.inputs["Value"])

        mix_glow = nt.nodes.new("ShaderNodeMix")
        mix_glow.data_type = "RGBA"
        mix_glow.inputs["B"].default_value = hex_rgba(g_hex)
        nt.links.new(spot_map.outputs["Result"], mix_glow.inputs["Factor"])
        nt.links.new(color_out, mix_glow.inputs["A"])
        color_out = mix_glow.outputs["Result"]

    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Metallic"].default_value = 0.15
    bsdf.inputs["Roughness"].default_value = 0.14
    bsdf.inputs["Specular IOR Level"].default_value = 0.85
    bsdf.inputs["Coat Weight"].default_value = 1.0
    bsdf.inputs["Coat Roughness"].default_value = 0.05
    bsdf.inputs["Emission Strength"].default_value = emission_strength
    nt.links.new(color_out, bsdf.inputs["Base Color"])
    nt.links.new(color_out, bsdf.inputs["Emission Color"])

    out = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return mat


def build_rim_material(
    name,
    ref_empty,
    bot_hex,
    mid_hex,
    top_hex,
    peak_hex,
    y_min_w,
    y_max_w,
    emission_base=0.75,
    emission_peak=1.95,
):
    mat, nt = new_material(name)
    tex, sep = add_coord_nodes(nt, ref_empty)

    map_y = nt.nodes.new("ShaderNodeMapRange")
    map_y.clamp = True
    map_y.inputs["From Min"].default_value = y_min_w
    map_y.inputs["From Max"].default_value = y_max_w
    nt.links.new(sep.outputs["Y"], map_y.inputs["Value"])

    ramp = nt.nodes.new("ShaderNodeValToRGB")
    elems = ramp.color_ramp.elements
    elems[0].position = 0.0
    elems[0].color = hex_rgba(bot_hex)
    elems[1].position = 1.0
    elems[1].color = hex_rgba(peak_hex)
    e1 = elems.new(0.48)
    e1.color = hex_rgba(mid_hex)
    e2 = elems.new(0.85)
    e2.color = hex_rgba(top_hex)
    nt.links.new(map_y.outputs["Result"], ramp.inputs["Fac"])

    em_map = nt.nodes.new("ShaderNodeMapRange")
    em_map.clamp = True
    em_map.inputs["From Min"].default_value = 0.0
    em_map.inputs["From Max"].default_value = 1.0
    em_map.inputs["To Min"].default_value = emission_base
    em_map.inputs["To Max"].default_value = emission_peak
    nt.links.new(map_y.outputs["Result"], em_map.inputs["Value"])

    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Metallic"].default_value = 0.35
    bsdf.inputs["Roughness"].default_value = 0.10
    bsdf.inputs["Coat Weight"].default_value = 1.0
    bsdf.inputs["Coat Roughness"].default_value = 0.03
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Emission Color"])
    nt.links.new(em_map.outputs["Result"], bsdf.inputs["Emission Strength"])

    out = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return mat


def build_backdrop_material(ref_empty):
    """Deep navy radial studio backdrop with subtle cyan top-glow & vignette matching the reference."""
    mat, nt = new_material("Backdrop_Navy_Mat")
    tex, _ = add_coord_nodes(nt, ref_empty)

    # Radial distance from icon upper-center (0.0, +0.4)
    dist_main = nt.nodes.new("ShaderNodeVectorMath")
    dist_main.operation = "DISTANCE"
    dist_main.inputs[1].default_value = (0.0, 0.45, 0.0)
    nt.links.new(tex.outputs["Object"], dist_main.inputs[0])

    map_bg = nt.nodes.new("ShaderNodeMapRange")
    map_bg.clamp = True
    map_bg.inputs["From Min"].default_value = 0.0
    map_bg.inputs["From Max"].default_value = 6.2
    map_bg.inputs["To Min"].default_value = 0.0
    map_bg.inputs["To Max"].default_value = 1.0
    nt.links.new(dist_main.outputs["Value"], map_bg.inputs["Value"])

    ramp = nt.nodes.new("ShaderNodeValToRGB")
    elems = ramp.color_ramp.elements
    elems[0].position = 0.0
    elems[0].color = hex_rgba("#082f60")
    elems[1].position = 1.0
    elems[1].color = hex_rgba("#010a1f")
    e1 = elems.new(0.40)
    e1.color = hex_rgba("#052048")
    e2 = elems.new(0.74)
    e2.color = hex_rgba("#021330")
    nt.links.new(map_bg.outputs["Result"], ramp.inputs["Fac"])

    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Roughness"].default_value = 0.85
    bsdf.inputs["Specular IOR Level"].default_value = 0.15
    bsdf.inputs["Emission Strength"].default_value = 0.55
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    nt.links.new(ramp.outputs["Color"], bsdf.inputs["Emission Color"])

    out = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(bsdf.outputs["BSDF"], out.inputs["Surface"])
    return mat


def create_glow_halo_plane(name, collection, wx, wy, wz, radius_w, color_hex, strength=1.2):
    """Soft additive/alpha-blended radial glow disc for lush neon bloom around bright peaks."""
    bpy.ops.mesh.primitive_plane_add(size=radius_w * 2.0, location=(wx, wy, wz))
    plane = bpy.context.active_object
    plane.name = name
    for c in list(plane.users_collection):
        c.objects.unlink(plane)
    collection.objects.link(plane)
    # Disable shadow casting on glow discs!
    plane.visible_shadow = False

    mat, nt = new_material(f"{name}_Mat")
    tex = nt.nodes.new("ShaderNodeTexCoord")
    dist = nt.nodes.new("ShaderNodeVectorMath")
    dist.operation = "DISTANCE"
    dist.inputs[1].default_value = (0.5, 0.5, 0.0)
    nt.links.new(tex.outputs["UV"], dist.inputs[0])

    map_r = nt.nodes.new("ShaderNodeMapRange")
    map_r.clamp = True
    map_r.inputs["From Min"].default_value = 0.0
    map_r.inputs["From Max"].default_value = 0.44
    map_r.inputs["To Min"].default_value = 1.0
    map_r.inputs["To Max"].default_value = 0.0
    nt.links.new(dist.outputs["Value"], map_r.inputs["Value"])

    # Cubic falloff for ultra-smooth soft halo
    pow_n = nt.nodes.new("ShaderNodeMath")
    pow_n.operation = "POWER"
    pow_n.inputs[1].default_value = 2.8
    nt.links.new(map_r.outputs["Result"], pow_n.inputs[0])

    mult_n = nt.nodes.new("ShaderNodeMath")
    mult_n.operation = "MULTIPLY"
    mult_n.inputs[1].default_value = strength
    nt.links.new(pow_n.outputs["Value"], mult_n.inputs[0])

    emis = nt.nodes.new("ShaderNodeEmission")
    emis.inputs["Color"].default_value = hex_rgba(color_hex)
    nt.links.new(mult_n.outputs["Value"], emis.inputs["Strength"])

    transp = nt.nodes.new("ShaderNodeBsdfTransparent")

    # Mix transparent and emission using falloff so alpha channel stays super clean!
    alpha_mult = nt.nodes.new("ShaderNodeMath")
    alpha_mult.operation = "MULTIPLY"
    alpha_mult.inputs[1].default_value = min(0.55, strength * 0.35)
    nt.links.new(pow_n.outputs["Value"], alpha_mult.inputs[0])

    mix_s = nt.nodes.new("ShaderNodeMixShader")
    nt.links.new(alpha_mult.outputs["Value"], mix_s.inputs["Fac"])
    nt.links.new(transp.outputs["BSDF"], mix_s.inputs[1])
    nt.links.new(emis.outputs["Emission"], mix_s.inputs[2])

    out = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(mix_s.outputs["Shader"], out.inputs["Surface"])
    plane.data.materials.append(mat)
    return plane


# ==============================================================================
# 4. MAIN SCENE ASSEMBLY & RENDER PIPELINE
# ==============================================================================

def main():
    import shutil
    out_dir_blend = r"C:\rclone\assets\blender"
    out_dir_renders = r"C:\rclone\assets\blender\renders"
    out_docs_assets = r"C:\rclone\docs\assets"
    os.makedirs(out_dir_blend, exist_ok=True)
    os.makedirs(out_dir_renders, exist_ok=True)
    os.makedirs(out_docs_assets, exist_ok=True)

    # Clear default startup objects cleanly while preserving OpenColorIO color management
    scene = bpy.context.scene
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for col in list(bpy.data.collections):
        bpy.data.collections.remove(col)
    for mesh in list(bpy.data.meshes):
        bpy.data.meshes.remove(mesh)
    for light in list(bpy.data.lights):
        bpy.data.lights.remove(light)
    for cam in list(bpy.data.cameras):
        bpy.data.cameras.remove(cam)

    # Organize collections
    col_icon = bpy.data.collections.new("Icon_3D")
    col_glow = bpy.data.collections.new("Neon_Glows")
    col_lights = bpy.data.collections.new("Lighting")
    col_cams = bpy.data.collections.new("Cameras")
    col_bg = bpy.data.collections.new("Backdrop")
    for c in (col_icon, col_glow, col_lights, col_cams, col_bg):
        scene.collection.children.link(c)

    # Reference origin Empty for procedural shaders
    ref_empty = bpy.data.objects.new("Shader_Coords_Origin", None)
    ref_empty.empty_display_size = 0.5
    col_icon.objects.link(ref_empty)

    print("Computing 2D SDF contours...")
    fields = build_sdf_fields(grid_res=1200)
    xs, ys = fields["xs"], fields["ys"]

    # Extract outer & rim contours
    cloud_outer = extract_contours(fields["cloud"], xs, ys, level=0.0)
    cloud_inner = extract_contours(fields["cloud"], xs, ys, level=-13.0)

    folder_outer = extract_contours(fields["folder"], xs, ys, level=0.0)
    folder_inner = extract_contours(fields["folder"], xs, ys, level=-8.5)

    top_arrow_outer = extract_contours(fields["top_arrow"], xs, ys, level=0.0)
    top_ridge_loops = extract_contours(fields["top_ridge"], xs, ys, level=0.0)

    bot_arrow_outer = extract_contours(fields["bot_arrow"], xs, ys, level=0.0)
    bot_ridge_loops = extract_contours(fields["bot_ridge"], xs, ys, level=0.0)
    bot_inner_loops = extract_contours(fields["bot_inner_track"], xs, ys, level=0.0)

    crescent_outer = extract_contours(fields["crescent"], xs, ys, level=0.0)

    # Build Materials
    mat_cloud_body = build_glossy_body_material(
        "Cloud_Body_Mat",
        ref_empty,
        upper_top_hex="#18f6ff",
        upper_bot_hex="#148df2",
        lower_top_hex="#088ba8",
        lower_bot_hex="#055388",
        arc_cx=340.0,
        arc_cy=180.0,
        arc_r=385.0,
        y_min_w=-1.60,
        y_max_w=1.66,
        emission_strength=0.52,
        extra_top_glow_pos=(536.0, 385.0, 120.0, "#66ffff"),
    )

    mat_cloud_rim = build_rim_material(
        "Cloud_Rim_Mat",
        ref_empty,
        bot_hex="#1478f2",
        mid_hex="#1cd4f0",
        top_hex="#4cf7f5",
        peak_hex="#7efaf6",
        y_min_w=-1.60,
        y_max_w=1.66,
        emission_base=0.62,
        emission_peak=1.12,
    )

    mat_folder_body = build_glossy_body_material(
        "Folder_Body_Mat",
        ref_empty,
        upper_top_hex="#24f2ec",
        upper_bot_hex="#0db5ea",
        lower_top_hex="#067696",
        lower_bot_hex="#03518c",
        arc_cx=315.0,
        arc_cy=290.0,
        arc_r=370.0,
        y_min_w=-1.60,
        y_max_w=0.05,
        emission_strength=0.46,
    )

    mat_folder_rim = build_rim_material(
        "Folder_Rim_Mat",
        ref_empty,
        bot_hex="#1480dc",
        mid_hex="#22c4e6",
        top_hex="#4cf2f4",
        peak_hex="#78f8f7",
        y_min_w=-1.60,
        y_max_w=0.05,
        emission_base=0.60,
        emission_peak=1.08,
    )

    mat_top_arrow_body = build_rim_material(
        "Top_Arrow_Body_Mat",
        ref_empty,
        bot_hex="#0c7de6",
        mid_hex="#08b8eb",
        top_hex="#10e0ef",
        peak_hex="#34f2f2",
        y_min_w=-0.20,
        y_max_w=2.64,
        emission_base=0.50,
        emission_peak=0.92,
    )

    mat_top_arrow_rim = build_rim_material(
        "Top_Arrow_Rim_Mat",
        ref_empty,
        bot_hex="#2298f6",
        mid_hex="#2fdff4",
        top_hex="#5cf7f4",
        peak_hex="#82fbf6",
        y_min_w=-0.20,
        y_max_w=2.64,
        emission_base=0.68,
        emission_peak=1.18,
    )

    mat_bot_arrow_body = build_rim_material(
        "Bottom_Arrow_Body_Mat",
        ref_empty,
        bot_hex="#024462",
        mid_hex="#065f77",
        top_hex="#12999e",
        peak_hex="#1ebbb8",
        y_min_w=-2.64,
        y_max_w=-0.14,
        emission_base=0.38,
        emission_peak=0.78,
    )

    mat_bot_arrow_inner = build_rim_material(
        "Bottom_Arrow_Inner_Mat",
        ref_empty,
        bot_hex="#045970",
        mid_hex="#0b7288",
        top_hex="#1ab0b2",
        peak_hex="#2bd2cc",
        y_min_w=-2.64,
        y_max_w=-0.14,
        emission_base=0.45,
        emission_peak=0.92,
    )

    mat_bot_arrow_rim = build_rim_material(
        "Bottom_Arrow_Rim_Mat",
        ref_empty,
        bot_hex="#126e8c",
        mid_hex="#1a96a8",
        top_hex="#2ec5c6",
        peak_hex="#5ae8e2",
        y_min_w=-2.64,
        y_max_w=-0.14,
        emission_base=0.55,
        emission_peak=1.15,
    )

    print("Creating 3D beveled meshes & curves...")
    # 1. Cloud 3D Body + Raised Rim
    create_2d_curve_object(
        "Cloud_Body",
        cloud_outer,
        col_icon,
        z_pos=0.0,
        extrude=0.14,
        bevel_depth=0.022,
        bevel_res=6,
        material=mat_cloud_body,
    )
    create_2d_curve_object(
        "Cloud_Rim",
        cloud_outer + cloud_inner,
        col_icon,
        z_pos=0.06,
        extrude=0.11,
        bevel_depth=0.024,
        bevel_res=8,
        material=mat_cloud_rim,
    )

    # 2. Folder 3D Body + Raised Rim (slightly proud in Z for crisp depth)
    create_2d_curve_object(
        "Folder_Body",
        folder_outer,
        col_icon,
        z_pos=0.03,
        extrude=0.15,
        bevel_depth=0.020,
        bevel_res=6,
        material=mat_folder_body,
    )
    create_2d_curve_object(
        "Folder_Rim",
        folder_outer + folder_inner,
        col_icon,
        z_pos=0.09,
        extrude=0.11,
        bevel_depth=0.020,
        bevel_res=8,
        material=mat_folder_rim,
    )

    # 3. Top Sync Arrow + Stepped Outer Ridge + Left Accent Crescent
    create_2d_curve_object(
        "Arrow_Top_Body",
        top_arrow_outer,
        col_icon,
        z_pos=0.02,
        extrude=0.13,
        bevel_depth=0.018,
        bevel_res=6,
        material=mat_top_arrow_body,
    )
    create_2d_curve_object(
        "Arrow_Top_Ridge",
        top_ridge_loops,
        col_icon,
        z_pos=0.08,
        extrude=0.10,
        bevel_depth=0.018,
        bevel_res=8,
        material=mat_top_arrow_rim,
    )
    if crescent_outer:
        create_2d_curve_object(
            "Arrow_Left_Crescent",
            crescent_outer,
            col_icon,
            z_pos=0.04,
            extrude=0.12,
            bevel_depth=0.016,
            bevel_res=6,
            material=mat_top_arrow_body,
        )

    # 4. Bottom Sync Arrow + Dual-Track Stepped Inner Band + Rim
    create_2d_curve_object(
        "Arrow_Bottom_Body",
        bot_arrow_outer,
        col_icon,
        z_pos=0.01,
        extrude=0.12,
        bevel_depth=0.018,
        bevel_res=6,
        material=mat_bot_arrow_body,
    )
    create_2d_curve_object(
        "Arrow_Bottom_InnerTrack",
        bot_inner_loops,
        col_icon,
        z_pos=0.05,
        extrude=0.10,
        bevel_depth=0.015,
        bevel_res=6,
        material=mat_bot_arrow_inner,
    )
    create_2d_curve_object(
        "Arrow_Bottom_Rim",
        bot_ridge_loops,
        col_icon,
        z_pos=0.08,
        extrude=0.10,
        bevel_depth=0.016,
        bevel_res=8,
        material=mat_bot_arrow_rim,
    )

    # 5. Soft Neon Cyan Halo Glow Planes (behind bright top peaks)
    create_glow_halo_plane("Glow_Top_Arrow", col_glow, 0.0, 2.35, -0.08, 2.2, "#35f6ff", strength=0.95)
    create_glow_halo_plane("Glow_Cloud_Top", col_glow, 0.25, 1.35, -0.06, 1.9, "#2cf7ff", strength=1.10)
    create_glow_halo_plane("Glow_Cloud_Right", col_glow, 2.20, 0.15, -0.06, 1.3, "#24e8ff", strength=0.75)

    # 6. Backdrop Studio Plane (at Z = -0.32 so real 3D lights cast crisp soft drop shadows!)
    bpy.ops.mesh.primitive_plane_add(size=14.0, location=(0.0, 0.0, -0.32))
    backdrop = bpy.context.active_object
    backdrop.name = "Backdrop_Studio_Navy"
    for c in list(backdrop.users_collection):
        c.objects.unlink(backdrop)
    col_bg.objects.link(backdrop)
    backdrop.data.materials.append(build_backdrop_material(ref_empty))

    # 7. Studio 3D Area Lighting (creates natural 3D specular highlights on bevels + drop shadow)
    def add_area_light(name, loc, rot_deg, energy, size, hex_col):
        ldata = bpy.data.lights.new(name=name, type="AREA")
        ldata.energy = energy
        ldata.size = size
        ldata.color = hex_rgba(hex_col)[:3]
        lobj = bpy.data.objects.new(name=name, object_data=ldata)
        lobj.location = loc
        lobj.rotation_euler = tuple(math.radians(a) for a in rot_deg)
        col_lights.objects.link(lobj)
        return lobj

    # Key light from upper-center-front (casts subtle downward drop shadow onto backdrop!)
    add_area_light("Key_Top_Cyan", (0.0, 3.2, 4.8), (-28.0, 0.0, 0.0), 160.0, 4.5, "#a6ffff")
    # Specular sheen light from top-left
    add_area_light("Fill_Left_Sky", (-3.5, 2.0, 5.2), (-18.0, -28.0, 0.0), 95.0, 5.0, "#5ad4ff")
    # Accent rim light on right cloud lobe
    add_area_light("Rim_Right_Aqua", (3.8, 0.8, 4.0), (-10.0, 35.0, 0.0), 85.0, 3.5, "#4ef7ff")

    # Dark blue world background so metallic/coat bevels reflect rich cyan/navy studio tones
    world = bpy.data.worlds.new("Icon_Studio_World")
    scene.world = world
    if not world.use_nodes:
        world.use_nodes = True
    w_bg = world.node_tree.nodes.get("Background")
    if w_bg:
        w_bg.inputs[0].default_value = hex_rgba("#041d42")
        w_bg.inputs[1].default_value = 0.8

    # 8. Cameras: Full Poster Camera (ortho_scale = 10.0) & Tight Icon Camera (ortho_scale = 6.05)
    cam_full_data = bpy.data.cameras.new("Camera_Full_Poster")
    cam_full_data.type = "ORTHO"
    cam_full_data.ortho_scale = 10.0
    cam_full = bpy.data.objects.new("Camera_Full_Poster", cam_full_data)
    cam_full.location = (0.0, 0.0, 10.0)
    col_cams.objects.link(cam_full)

    # Tight icon framing: icon spans px [230..795] (w=565) and py [242..781] (h=539), center=(512.5, 511.5)
    # Using ortho_scale = 6.00 (~614px equivalent) fills ~92% of the icon canvas with clean margin for glow!
    cam_icon_data = bpy.data.cameras.new("Camera_Icon_Master")
    cam_icon_data.type = "ORTHO"
    cam_icon_data.ortho_scale = 6.00
    cam_icon = bpy.data.objects.new("Camera_Icon_Master", cam_icon_data)
    cam_icon.location = (0.01, 0.005, 10.0)
    col_cams.objects.link(cam_icon)

    # 9. Render Settings (Cycles GPU CUDA/OptiX on RTX 3060 with fallback to CPU)
    scene.render.engine = "CYCLES"
    try:
        prefs = bpy.context.preferences.addons["cycles"].preferences
        prefs.compute_device_type = "CUDA"
        prefs.get_devices()
        gpu_found = False
        for d in prefs.devices:
            if d.type in ("CUDA", "OPTIX"):
                d.use = True
                gpu_found = True
            else:
                d.use = False
        scene.cycles.device = "GPU" if gpu_found else "CPU"
    except Exception:
        scene.cycles.device = "CPU"
    print(f"Cycles render device: {scene.cycles.device}")
    scene.cycles.samples = 128
    scene.cycles.use_adaptive_sampling = True
    scene.cycles.adaptive_threshold = 0.008
    scene.cycles.use_denoising = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.image_settings.color_depth = "8"

    # Standard view transform keeps saturated neon cyans & deep blues punchy and true to reference
    try:
        scene.display_settings.display_device = "sRGB"
    except Exception:
        pass
    for vt_candidate in ("Standard", "AgX", "Filmic"):
        try:
            scene.view_settings.view_transform = vt_candidate
            break
        except Exception:
            continue

    # Save .blend project with Full Poster camera active & 2048x2048 resolution configured
    scene.camera = cam_full
    scene.render.film_transparent = False
    col_bg.hide_render = False
    scene.render.resolution_x = 2048
    scene.render.resolution_y = 2048
    scene.render.resolution_percentage = 100

    blend_path = os.path.join(out_dir_blend, "rclone_icon.blend")
    bpy.ops.wm.save_as_mainfile(filepath=blend_path)
    print(f"Saved Blender project: {blend_path}")

    # Render 1: Full Poster 1024x1024 (direct comparison to reference & standard artwork)
    scene.render.resolution_x = 1024
    scene.render.resolution_y = 1024
    render_1024_path = os.path.join(out_dir_renders, "rclone_logo_render_1024.png")
    scene.render.filepath = render_1024_path
    print("Rendering Full Poster 1024x1024...")
    bpy.ops.render.render(write_still=True)

    # Render 2: Full Poster 2048x2048 (Ultra-HD artwork)
    scene.render.resolution_x = 2048
    scene.render.resolution_y = 2048
    render_2048_path = os.path.join(out_dir_renders, "rclone_logo_render_2048.png")
    scene.render.filepath = render_2048_path
    print("Rendering Full Poster 2048x2048...")
    bpy.ops.render.render(write_still=True)

    # Render 3: Transparent Tightly-Framed Master Icon 1024x1024 (for .ico and all PNG icon sizes)
    scene.camera = cam_icon
    scene.render.film_transparent = True
    col_bg.hide_render = True
    # Slightly dial down wide glow halos on transparent icon so alpha edges stay super clean
    col_glow.hide_render = True
    scene.render.resolution_x = 1024
    scene.render.resolution_y = 1024
    render_trans_path = os.path.join(out_dir_renders, "rclone_icon_transparent_1024.png")
    scene.render.filepath = render_trans_path
    print("Rendering Transparent Icon Master 1024x1024...")
    bpy.ops.render.render(write_still=True)

    # Restore Full Poster camera & re-save .blend cleanly so opening the .blend shows the full scene
    scene.camera = cam_full
    scene.render.film_transparent = False
    col_bg.hide_render = False
    col_glow.hide_render = False
    scene.render.resolution_x = 2048
    scene.render.resolution_y = 2048
    scene.render.filepath = render_2048_path
    bpy.ops.wm.save_as_mainfile(filepath=blend_path)
    shutil.copy2(render_1024_path, os.path.join(out_docs_assets, "logo.png"))
    shutil.copy2(render_trans_path, os.path.join(out_docs_assets, "logo-transparent.png"))
    print("All Blender renders and .blend save complete!")


if __name__ == "__main__":
    main()
