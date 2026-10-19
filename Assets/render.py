"""
Tiny signed-distance-field raymarcher (numpy) that renders the Z ULT Connect
stage art: Sony ULT Wear-style headphones, alone and on a dog's head.
Outputs RGBA PNG frames for a seamless sway loop.

usage: python3 render.py <variant: hp|dog> <frames> <width> <height> <outdir> [single_frame_index]
"""
import sys, os, math
import numpy as np
from PIL import Image

F = np.float32

# ----------------------------------------------------------------- SDF helpers
def length(v):
    return np.sqrt(np.sum(v * v, axis=-1))

def sd_sphere(p, c, r):
    return length(p - c) - r

def sd_ellipsoid(p, c, r):
    q = (p - c) / r
    k0 = length(q)
    k1 = length(q / r)
    return k0 * (k0 - 1.0) / np.maximum(k1, 1e-6)

def sd_capsule(p, a, b, r):
    pa, ba = p - a, b - a
    h = np.clip(np.sum(pa * ba, -1) / np.dot(ba, ba), 0, 1)[..., None]
    return length(pa - ba * h) - r

def sd_round_cyl_x(p, c, radius, half_len, round_r, yscale=1.0):
    """Rounded cylinder with its axis along x (an ear cup)."""
    q = p - c
    q = q * np.array([1, 1 / yscale, 1], F)
    d_rad = np.sqrt(q[..., 1] ** 2 + q[..., 2] ** 2) - (radius - round_r)
    d_ax = np.abs(q[..., 0]) - (half_len - round_r)
    dx = np.maximum(d_rad, 0); dy = np.maximum(d_ax, 0)
    return (np.minimum(np.maximum(d_rad, d_ax), 0) + np.sqrt(dx * dx + dy * dy) - round_r) * min(1.0, yscale)

def sd_torus_x(p, c, R, r, yscale=1.0):
    """Torus lying in the YZ plane (ring facing +-x)."""
    q = p - c
    q = q * np.array([1, 1 / yscale, 1], F)
    t = np.sqrt(q[..., 1] ** 2 + q[..., 2] ** 2) - R
    return np.sqrt(t * t + q[..., 0] ** 2) - r

def sd_band(p, c, R, w, t, ymin, ys=1.0):
    """Headband: arc in the XY plane, rounded-rectangle cross-section (w along z, t radial), clipped below ymin."""
    q = p - c
    q = q * np.array([1, 1 / ys, 1], F)
    rad = np.sqrt(q[..., 0] ** 2 + q[..., 1] ** 2) - R
    z = q[..., 2]
    dx = np.abs(rad) - t; dz = np.abs(z) - w
    rr = min(t, w) * 0.9
    ox = np.maximum(dx + rr, 0); oz = np.maximum(dz + rr, 0)
    d = np.sqrt(ox * ox + oz * oz) + np.minimum(np.maximum(dx + rr, dz + rr), 0) - rr
    clip = (ymin - q[..., 1])
    return np.maximum(d, clip)

def sd_cone_ear(p, base, tip, r_base, r_tip):
    """Rounded cone (capsule with varying radius) for a dog ear."""
    pa, ba = p - base, tip - base
    h = np.clip(np.sum(pa * ba, -1) / np.dot(ba, ba), 0, 1)
    r = r_base + (r_tip - r_base) * h
    return length(pa - ba * h[..., None]) - r

def smin(a, b, k):
    h = np.clip(0.5 + 0.5 * (b - a) / k, 0, 1)
    return b * (1 - h) + a * h - k * h * (1 - h)

def rot_y(p, ang):
    c, s = math.cos(ang), math.sin(ang)
    x, z = p[..., 0], p[..., 2]
    return np.stack([c * x + s * z, p[..., 1], -s * x + c * z], -1)

def rot_x(p, ang):
    c, s = math.cos(ang), math.sin(ang)
    y, z = p[..., 1], p[..., 2]
    return np.stack([p[..., 0], c * y - s * z, s * y + c * z], -1)

# ----------------------------------------------------------------- materials
# id: (albedo, spec strength, spec power)
MAT = {
    0: ((0.055, 0.058, 0.066), 0.55, 70.0),   # headphone shell (matte-gloss black)
    1: ((0.030, 0.031, 0.035), 0.12, 10.0),   # cushions / pads (soft black leather)
    2: ((1.000, 0.420, 0.180), 0.60, 90.0),   # ULT orange accent
    3: ((0.20, 0.21, 0.23), 0.80, 120.0),     # metal sliders
    4: ((0.93, 0.53, 0.21), 0.05, 8.0),       # fur (shiba tan)
    5: ((0.98, 0.92, 0.82), 0.04, 8.0),       # cream muzzle / cheeks
    6: ((0.025, 0.020, 0.020), 0.70, 90.0),   # nose
    7: ((0.020, 0.015, 0.012), 1.00, 160.0),  # eyes
    8: ((0.96, 0.72, 0.62), 0.03, 6.0),       # inner ear
}

# ----------------------------------------------------------------- scenes
def headphones(p, scale, center, spread=1.0, band_ys=1.0):
    """Returns list of (distance, material) for the headphones."""
    q = (p - np.array(center, F)) / scale
    s = spread
    parts = []
    # headband shell + inner pad
    parts.append((sd_band(q, np.array([0, -0.05, 0], F), 1.02 * s, 0.17, 0.065, -0.05, band_ys) * min(1, band_ys), 0))
    parts.append((sd_band(q, np.array([0, -0.05, 0], F), 0.93 * s, 0.13, 0.04, 0.25 * band_ys, band_ys) * min(1, band_ys), 1))
    # sliders (metal) from band ends to yokes
    for sx in (-1, 1):
        a = np.array([sx * 1.02 * s, -0.02, 0], F); b = np.array([sx * 1.02 * s, -0.30, 0], F)
        parts.append((sd_capsule(q, a, b, 0.045), 3))
        # yoke ring around cup
        parts.append((sd_torus_x(q, np.array([sx * 1.06 * s, -0.62, 0], F), 0.46, 0.035, 1.18), 0))
        # ear cup
        parts.append((sd_round_cyl_x(q, np.array([sx * 1.08 * s, -0.66, 0], F), 0.42, 0.15, 0.09, 1.18), 0))
        # orange ULT accent ring on outer face
        parts.append((sd_torus_x(q, np.array([sx * 1.225 * s, -0.66, 0], F), 0.25, 0.032, 1.18), 2))
        # cushion on inner side
        parts.append((sd_torus_x(q, np.array([sx * 0.90 * s, -0.66, 0], F), 0.30, 0.10, 1.2), 1))
    return [(d * scale, m) for d, m in parts]

DOG_DY = 0.10

def dog(p):
    p = p - np.array([0, DOG_DY, 0], F)
    parts = []
    head_c = np.array([0, -0.18, 0], F)
    head = sd_ellipsoid(p, head_c, np.array([0.80, 0.74, 0.74], F))
    cheekL = sd_sphere(p, np.array([-0.42, -0.42, 0.30], F), 0.36)
    cheekR = sd_sphere(p, np.array([0.42, -0.42, 0.30], F), 0.36)
    muzzle = sd_ellipsoid(p, np.array([0, -0.46, 0.58], F), np.array([0.36, 0.25, 0.34], F))
    d_head = smin(smin(smin(head, cheekL, 0.2), cheekR, 0.2), muzzle, 0.18)
    # ears (pointy, slightly tilted out), behind the band
    earL = sd_cone_ear(p, np.array([-0.42, 0.30, -0.12], F), np.array([-0.62, 0.95, -0.18], F), 0.24, 0.04)
    earR = sd_cone_ear(p, np.array([0.42, 0.30, -0.12], F), np.array([0.62, 0.95, -0.18], F), 0.24, 0.04)
    d_head = smin(d_head, np.minimum(earL, earR), 0.10)
    # flatten ears front-to-back a bit by intersecting with a slab
    parts.append((d_head, 4))  # fur; cream decided by region below
    inner_earL = sd_cone_ear(p, np.array([-0.42, 0.36, -0.02], F), np.array([-0.60, 0.88, -0.08], F), 0.14, 0.02)
    inner_earR = sd_cone_ear(p, np.array([0.42, 0.36, -0.02], F), np.array([0.60, 0.88, -0.08], F), 0.14, 0.02)
    parts.append((np.minimum(inner_earL, inner_earR) + 0.005, 8))
    parts.append((sd_ellipsoid(p, np.array([0, -0.33, 0.92], F), np.array([0.13, 0.085, 0.08], F)), 6))
    for sx in (-1, 1):
        parts.append((sd_sphere(p, np.array([sx * 0.29, -0.08, 0.62], F), 0.095), 7))
    return parts

def scene(p, variant):
    if variant == "hp":
        parts = headphones(p, 1.0, (0, 0.18, 0))
    else:
        parts = dog(p) + headphones(p, 0.80, (0, 0.16, -0.02), spread=1.05, band_ys=0.82)
    d = np.stack([x[0] for x in parts], 0)
    idx = np.argmin(d, 0)
    mats = np.array([x[1] for x in parts])
    return np.min(d, 0), mats[idx]

def smoothstep(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)

def fur_color(pos, base):
    """Soft cream mask over muzzle, cheeks and chin, plus two eyebrow dots; tan elsewhere."""
    p = pos - np.array([0, DOG_DY, 0], F)
    mask_d = sd_ellipsoid(p, np.array([0, -0.50, 0.50], F), np.array([0.64, 0.30, 0.52], F))
    w = 1 - smoothstep(-0.02, 0.06, mask_d)
    for sx in (-1, 1):
        bd = sd_ellipsoid(p, np.array([sx * 0.26, 0.09, 0.62], F), np.array([0.07, 0.045, 0.2], F))
        w = np.maximum(w, 1 - smoothstep(-0.005, 0.015, bd))
    cream = np.array(MAT[5][0], F)
    return base * (1 - w[..., None]) + cream * w[..., None]

# ----------------------------------------------------------------- renderer
def render(variant, yaw, pitch, W, H, ss=2):
    w, h = W * ss, H * ss
    aspect = w / h
    fov = 0.34
    ys, xs = np.mgrid[0:h, 0:w].astype(F)
    u = (xs + 0.5) / w * 2 - 1
    v = 1 - (ys + 0.5) / h * 2
    ro = np.array([0, 0.05, 4.6], F)
    rd = np.stack([u * aspect * fov, v * fov, -np.ones_like(u)], -1)
    rd /= length(rd)[..., None]
    rd = rd.reshape(-1, 3)
    n = rd.shape[0]
    ro_all = np.broadcast_to(ro, (n, 3)).copy()

    # rotate the world by rotating the rays (inverse)
    def to_obj(pp):
        return rot_x(rot_y(pp, -yaw), -pitch)

    t = np.full(n, 2.0, F)
    hit = np.zeros(n, bool)
    alive = np.ones(n, bool)
    for _ in range(110):
        idx = np.nonzero(alive)[0]
        if idx.size == 0:
            break
        pos = to_obj(ro_all[idx] + rd[idx] * t[idx, None])
        d, _ = scene(pos, variant)
        t[idx] += d * 0.9
        h_ = d < 0.0015
        hit[idx[h_]] = True
        alive[idx[h_ | (t[idx] > 7.5)]] = False

    rgb = np.zeros((n, 3), F)
    alpha = hit.astype(F)
    hi = np.nonzero(hit)[0]
    if hi.size:
        P = to_obj(ro_all[hi] + rd[hi] * t[hi, None])
        D = rot_x(rot_y(rd[hi], -yaw), -pitch)
        _, mid = scene(P, variant)
        e = 0.0015
        ks = [np.array(k, F) for k in ((1, -1, -1), (-1, -1, 1), (-1, 1, -1), (1, 1, 1))]
        N = np.zeros_like(P)
        for k in ks:
            N += k * scene(P + k * e, variant)[0][..., None]
        N /= np.maximum(length(N)[..., None], 1e-6)

        alb = np.array([MAT[m][0] for m in range(9)], F)[mid]
        spec_k = np.array([MAT[m][1] for m in range(9)], F)[mid]
        spec_p = np.array([MAT[m][2] for m in range(9)], F)[mid]
        fur = mid == 4
        if variant == "dog" and fur.any():
            alb[fur] = fur_color(P[fur], alb[fur])

        # lights are fixed in camera space -> transform to object space
        def L(v):
            v = np.array(v, F); v /= np.linalg.norm(v)
            return rot_x(rot_y(v[None], -yaw), -pitch)[0]
        key, fill, rim = L((-0.55, 0.75, 0.65)), L((0.8, 0.15, 0.45)), L((0.2, 0.45, -1.0))
        up = L((0, 1, 0))
        V = -D

        # soft shadow toward key light
        sh = np.ones(len(P), F)
        ts = np.full(len(P), 0.02, F)
        for _ in range(28):
            dd, _ = scene(P + key * ts[:, None], variant)
            sh = np.minimum(sh, np.clip(10 * dd / ts, 0, 1))
            ts += np.clip(dd, 0.02, 0.2)
        sh = np.clip(sh, 0, 1) ** 1.5

        # ambient occlusion
        ao = np.zeros(len(P), F)
        for i in range(1, 6):
            hh = 0.035 * i
            dd, _ = scene(P + N * hh, variant)
            ao += (hh - dd) / (2 ** i)
        ao = np.clip(1 - 3.0 * ao, 0.25, 1)

        def lam(l):
            return np.clip(np.sum(N * l, -1), 0, 1)

        def spec(l):
            hv = l + V; hv /= length(hv)[..., None]
            return np.clip(np.sum(N * hv, -1), 0, 1) ** spec_p

        hemi = 0.5 + 0.5 * np.sum(N * up, -1)
        col = alb * (
            (lam(key) * sh * 1.25)[:, None] * np.array([1.0, 0.97, 0.92], F)
            + (lam(fill) * 0.35)[:, None] * np.array([0.75, 0.82, 1.0], F)
            + ((0.10 + 0.22 * hemi) * ao)[:, None]
        )
        col += (spec_k * spec(key) * sh * 1.4)[:, None]
        col += (spec_k * spec(fill) * 0.35)[:, None]
        fres = (1 - np.clip(np.sum(N * V, -1), 0, 1)) ** 3
        col += ((lam(rim) * 0.55 + fres * 0.18) * ao)[:, None] * np.array([0.85, 0.9, 1.0], F) * np.where(spec_k > 0.3, 1.0, 0.45)[:, None]
        rgb[hi] = np.clip(col, 0, 1) ** (1 / 2.2)

    img = np.concatenate([rgb * alpha[:, None], alpha[:, None]], -1).reshape(h, w, 4)
    # downsample (premultiplied) then un-premultiply
    img = img.reshape(H, ss, W, ss, 4).mean((1, 3))
    a = img[..., 3:4]
    rgbu = np.where(a > 1e-4, img[..., :3] / np.maximum(a, 1e-4), 0)
    out = np.concatenate([rgbu, a], -1)
    return Image.fromarray((np.clip(out, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")

if __name__ == "__main__":
    variant, frames, W, H, outdir = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), sys.argv[5]
    only = int(sys.argv[6]) if len(sys.argv) > 6 else None
    os.makedirs(outdir, exist_ok=True)
    for i in range(frames):
        if only is not None and i != only:
            continue
        ph = 2 * math.pi * i / frames
        yaw = math.radians(28) * math.sin(ph) + math.radians(-8)
        pitch = math.radians(9) + math.radians(3) * math.sin(2 * ph)
        im = render(variant, yaw, pitch, W, H)
        im.save(os.path.join(outdir, f"{variant}_{i:02d}.png"), optimize=True)
        print(variant, i, flush=True)
