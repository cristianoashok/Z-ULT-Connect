"""
Companion renderer v2 for Z ULT Connect - cartoon style.

Golden retriever (big happy smile, tongue out), cat, wolf dog and an alien,
each wearing the headphones over their ears. Simple rig: mouth, tongue, nod, tilt.
Eyes are shaded as real eyeballs (white, coloured iris, pupil, glossy highlight).

usage:
  python3 companions2.py <species> <set> <outdir>     # sets: idle music happy call
  python3 companions2.py test <outfile> [species...]   # contact sheet of key poses
"""
import sys, os, math
import numpy as np
from PIL import Image
from render import F, length, sd_sphere, sd_ellipsoid, sd_capsule, sd_cone_ear, smin, rot_y, rot_x, headphones, smoothstep

W, H, SS = 420, 294, 2

def v(*a):
    return np.array(a, F)

def rot_z(p, ang):
    c, s = math.cos(ang), math.sin(ang)
    x, y = p[..., 0], p[..., 1]
    return np.stack([c * x - s * y, s * x + c * y, p[..., 2]], -1)

def norm(a):
    a = np.array(a, F)
    return a / np.linalg.norm(a)

# materials: id -> (albedo or None, spec strength, spec power, emission)
MATS = {
    0: ((0.055, 0.058, 0.066), 0.55, 70, 0),     # headphone shell
    1: ((0.030, 0.031, 0.035), 0.12, 10, 0),     # cushions
    2: ((1.0, 0.42, 0.18), 0.6, 90, 0),          # ULT orange
    3: ((0.20, 0.21, 0.23), 0.8, 120, 0),        # metal
    4: (None, 0.05, 8, 0),                       # main fur/skin (species colour function)
    6: ((0.025, 0.020, 0.020), 0.75, 90, 0),     # nose (glossy black)
    8: ((0.96, 0.66, 0.66), 0.05, 8, 0),         # pink (inner ear, cat nose)
    9: ((0.30, 0.05, 0.07), 0.25, 20, 0),        # mouth inside
    10: ((0.95, 0.40, 0.47), 0.45, 35, 0),       # tongue
    12: ((0.95, 0.95, 0.95), 0.2, 20, 0),        # whiskers
    13: (None, 0.05, 8, 0),                      # secondary fur (ears, brows)
    14: ((0.75, 1.0, 0.45), 0.3, 30, 1.0),       # glowing antenna tips
    15: (None, 1.0, 180, 0),                     # eyeball (shaded by eye function)
    16: ((0.03, 0.02, 0.06), 1.0, 160, 0),       # alien eyes (glossy black)
}

# ---------------------------------------------------------------- shared parts
def smile(q, center, width, depth, m, bend=1.1, tongue=0.0, tongue_len=0.24, tongue_w=0.15):
    """A carved smile with a dark mouth behind it and an optional tongue hanging out.
    Returns (gap_distance, extra parts)."""
    qb = q.copy()
    qb[..., 1] = qb[..., 1] - bend * (q[..., 0] - center[0]) ** 2   # corners curve up
    h = 0.025 + 0.16 * m
    gap = sd_ellipsoid(qb, center, v(width, h, depth))
    cav = sd_ellipsoid(qb, center + v(0, 0, -0.05), v(width * 0.88, h * 0.85, depth * 0.85))
    parts = [(cav, 9)]
    if tongue > 0.01:
        tc = center + v(0, -h * 0.6 - 0.07 * tongue, depth * 0.55 * tongue)
        qt = q - tc
        a = 0.85 * tongue                         # hangs down and forward
        ca, sa = math.cos(a), math.sin(a)
        qt = np.stack([qt[..., 0], ca * qt[..., 1] - sa * qt[..., 2], sa * qt[..., 1] + ca * qt[..., 2]], -1)
        tl = tongue_len * (0.45 + 0.65 * tongue)
        t_d = sd_ellipsoid(qt, v(0, 0, 0), v(tongue_w, 0.045, tl))
        groove = sd_capsule(qt, v(0, 0.045, -tl), v(0, 0.045, tl * 0.8), 0.012)
        parts.append((np.maximum(t_d, -groove), 10))
    return gap, parts


class Species:
    name = ""
    hp_center = (0, 0.24, -0.02)
    hp_scale = 0.80
    hp_spread = 1.05
    hp_band = 0.80
    call_is_howl = False
    eyes = []          # list of (center, forward, iris colour, iris cos, pupil cos, slit)
    poses = {}

    def parts(self, q, ex): raise NotImplementedError
    def fur(self, q): raise NotImplementedError
    def fur2(self, q): return self.fur(q) * 0.8

    def eye_color(self, q):
        """White, iris and pupil chosen by the direction from each eye's centre."""
        out = np.ones(q.shape, F) * v(0.97, 0.97, 0.96)
        best = np.full(q.shape[0], 9.0, F)
        for c, fwd, iris, ci, cp, slit in self.eyes:
            d = q - c
            dist = length(d)
            dn = d / np.maximum(dist[..., None], 1e-6)
            cosang = np.sum(dn * fwd, -1)
            sel = dist < best
            best = np.where(sel, dist, best)
            col = np.ones(q.shape, F) * v(0.97, 0.97, 0.96)
            irisw = smoothstep(ci - 0.015, ci + 0.015, cosang)
            ring = 0.75 + 0.25 * smoothstep(ci, 1.0, cosang)       # darker towards the iris edge
            col = col * (1 - irisw[..., None]) + (np.array(iris, F) * ring[..., None]) * irisw[..., None]
            if slit:
                side = np.abs(d[..., 0]) / np.maximum(dist, 1e-6)
                pup = ((side < 0.10) & (cosang > ci + 0.02)).astype(F)
            else:
                pup = smoothstep(cp - 0.01, cp + 0.01, cosang)
            col = col * (1 - pup[..., None]) + v(0.02, 0.02, 0.025) * pup[..., None]
            out = np.where(sel[..., None], col, out)
        return out


class Golden(Species):
    name = "golden"
    def __init__(self):
        f = norm((0.22, 0.10, 1))
        self.eyes = [(v(-0.28, 0.0, 0.62), norm((-0.22, 0.10, 1)), (0.50, 0.26, 0.08), 0.64, 0.87, False),
                     (v(0.28, 0.0, 0.62), f, (0.50, 0.26, 0.08), 0.64, 0.87, False)]
        self.poses = {"idle": (0.20, 0.0), "music": (0.26, 0.0), "happy": (0.50, 0.0), "call_tongue": 0.0}

    def parts(self, q, ex):
        head = sd_ellipsoid(q, v(0, -0.05, 0), v(0.80, 0.76, 0.74))
        cheeks = smin(sd_sphere(q, v(-0.42, -0.36, 0.28), 0.36), sd_sphere(q, v(0.42, -0.36, 0.28), 0.36), 0.2)
        muzzle = sd_ellipsoid(q, v(0, -0.36, 0.58), v(0.38, 0.26, 0.36))
        chin = sd_ellipsoid(q, v(0, -0.62, 0.44), v(0.30, 0.15, 0.27))
        tuft = sd_sphere(q, v(0, 0.66, 0.16), 0.10)
        d = smin(smin(smin(head, cheeks, 0.22), muzzle, 0.16), chin, 0.12)
        gap, mparts = smile(q, v(0, -0.50, 0.80), 0.33, 0.20, ex["mouth"], tongue=ex["tongue"], tongue_len=0.27, tongue_w=0.16)
        d = np.maximum(d, -gap)
        parts = [(d, 4)] + mparts
        for sx in (-1, 1):
            parts.append((sd_sphere(q, v(sx * 0.28, 0.0, 0.62), 0.155), 15))
            qb = q - v(sx * 0.29, 0.25, 0.64)
            qb = qb + v(0, 0, 0) * 0
            qb[..., 1] = qb[..., 1] + 2.2 * qb[..., 0] ** 2          # raised, arched (happy) brows
            brow = sd_ellipsoid(qb, v(0, 0, 0), v(0.12, 0.030, 0.06))
            parts.append((brow, 13))
            ear = sd_ellipsoid(rot_z(q - v(sx * 0.86, -0.44, -0.04), sx * math.radians(10)), v(0, 0, 0), v(0.17, 0.46, 0.30))
            parts.append((ear, 13))
        parts.append((sd_ellipsoid(q, v(0, -0.25, 0.94), v(0.19, 0.125, 0.11)), 6))
        return parts

    def fur(self, q):
        base, light = v(0.91, 0.45, 0.07), v(0.99, 0.72, 0.30)
        m = sd_ellipsoid(q, v(0, -0.48, 0.55), v(0.48, 0.30, 0.50))
        w = (1 - smoothstep(-0.03, 0.08, m))[..., None]
        return base * (1 - w) + light * w

    def fur2(self, q):
        return np.ones(q.shape, F) * v(0.74, 0.36, 0.06)


class Cat(Species):
    name = "cat"
    hp_center = (0, 0.22, -0.02)
    def __init__(self):
        self.eyes = [(v(-0.29, -0.04, 0.55), norm((-0.20, 0.06, 1)), (0.45, 0.80, 0.28), 0.74, 0.0, True),
                     (v(0.29, -0.04, 0.55), norm((0.20, 0.06, 1)), (0.45, 0.80, 0.28), 0.74, 0.0, True)]
        self.poses = {"idle": (0.08, 0.0), "music": (0.15, 0.0), "happy": (0.30, 0.0), "call_tongue": 0.0}

    def parts(self, q, ex):
        head = sd_ellipsoid(q, v(0, -0.08, 0), v(0.82, 0.70, 0.72))
        cheeks = smin(sd_sphere(q, v(-0.42, -0.36, 0.22), 0.36), sd_sphere(q, v(0.42, -0.36, 0.22), 0.36), 0.25)
        pads = smin(sd_sphere(q, v(-0.11, -0.38, 0.64), 0.11), sd_sphere(q, v(0.11, -0.38, 0.64), 0.11), 0.05)
        d = smin(smin(head, cheeks, 0.25), pads, 0.06)
        gap, mparts = smile(q, v(0, -0.48, 0.70), 0.14, 0.12, ex["mouth"], bend=2.0, tongue=ex["tongue"],
                            tongue_len=0.12, tongue_w=0.08)
        d = np.maximum(d, -gap)
        parts = [(d, 4)] + mparts
        for sx in (-1, 1):
            # ears come out from under the ear cups, pointing up and out
            parts.append((sd_cone_ear(q, v(sx * 0.62, 0.20, -0.06), v(sx * 0.98, 0.80, -0.12), 0.25, 0.03), 4))
            parts.append((sd_cone_ear(q, v(sx * 0.64, 0.27, 0.04), v(sx * 0.95, 0.74, -0.03), 0.14, 0.02) + 0.004, 8))
            parts.append((sd_sphere(q, v(sx * 0.29, -0.04, 0.55), 0.155), 15))
            for ang in (-0.12, 0.0, 0.12):
                parts.append((sd_capsule(q, v(sx * 0.18, -0.40 + ang * 0.4, 0.70), v(sx * 0.80, -0.38 + ang * 1.6, 0.52), 0.008), 12))
        parts.append((sd_ellipsoid(q, v(0, -0.27, 0.74), v(0.075, 0.05, 0.05)), 8))
        return parts

    def fur(self, q):
        base, dark, white = v(0.94, 0.58, 0.25), v(0.74, 0.38, 0.13), v(0.99, 0.96, 0.91)
        x, y, z = q[..., 0], q[..., 1], q[..., 2]
        stripes = (np.sin(y * 26 + np.abs(x) * 6) > 0.55) & (y > 0.05) & (np.abs(x) < 0.36) & (z > 0.2)
        col = np.where(stripes[..., None], dark, base)
        m = sd_ellipsoid(q, v(0, -0.46, 0.48), v(0.40, 0.24, 0.40))
        w = (1 - smoothstep(-0.03, 0.06, m))[..., None]
        return col * (1 - w) + white * w


class Wolf(Species):
    name = "wolf"
    call_is_howl = True
    def __init__(self):
        self.eyes = [(v(-0.28, -0.02, 0.58), norm((-0.20, 0.08, 1)), (0.35, 0.65, 0.98), 0.80, 0.93, False),
                     (v(0.28, -0.02, 0.58), norm((0.20, 0.08, 1)), (0.35, 0.65, 0.98), 0.80, 0.93, False)]
        self.poses = {"idle": (0.18, 0.0), "music": (0.24, 0.0), "happy": (0.48, 0.0), "call_tongue": 0.0}

    def parts(self, q, ex):
        head = sd_ellipsoid(q, v(0, -0.06, 0), v(0.78, 0.72, 0.74))
        cheeks = smin(sd_sphere(q, v(-0.40, -0.34, 0.22), 0.36), sd_sphere(q, v(0.40, -0.34, 0.22), 0.36), 0.22)
        muzzle = sd_ellipsoid(q, v(0, -0.34, 0.68), v(0.30, 0.22, 0.44))
        chin = sd_ellipsoid(q, v(0, -0.56, 0.52), v(0.24, 0.12, 0.30))
        d = smin(smin(smin(head, cheeks, 0.22), muzzle, 0.14), chin, 0.10)
        gap, mparts = smile(q, v(0, -0.48, 0.88), 0.25, 0.18, ex["mouth"], bend=1.4, tongue=ex["tongue"], tongue_len=0.22, tongue_w=0.13)
        d = np.maximum(d, -gap)
        parts = [(d, 4)] + mparts
        for sx in (-1, 1):
            parts.append((sd_cone_ear(q, v(sx * 0.58, 0.22, -0.08), v(sx * 0.92, 0.92, -0.16), 0.26, 0.04), 4))
            parts.append((sd_cone_ear(q, v(sx * 0.60, 0.29, 0.02), v(sx * 0.89, 0.85, -0.07), 0.15, 0.02) + 0.004, 8))
            parts.append((sd_sphere(q, v(sx * 0.28, -0.02, 0.58), 0.12), 15))
        parts.append((sd_ellipsoid(q, v(0, -0.20, 1.10), v(0.15, 0.10, 0.09)), 6))
        return parts

    def fur(self, q):
        grey, dark, white = v(0.40, 0.42, 0.46), v(0.20, 0.21, 0.24), v(0.96, 0.96, 0.97)
        x, y, z = q[..., 0], q[..., 1], q[..., 2]
        col = grey * (1 - smoothstep(0.1, 0.5, y))[..., None] + dark * smoothstep(0.1, 0.5, y)[..., None]
        m = sd_ellipsoid(q, v(0, -0.44, 0.46), v(0.60, 0.32, 0.62))
        w = 1 - smoothstep(-0.03, 0.08, m)
        for sx in (-1, 1):
            b = sd_ellipsoid(q, v(sx * 0.26, 0.15, 0.62), v(0.09, 0.055, 0.2))
            w = np.maximum(w, 1 - smoothstep(-0.005, 0.02, b))
        stripe = (np.abs(x) < 0.05 + 0.1 * np.clip(-y, 0, 1)) & (z > 0.45) & (y < 0.28)
        w = np.maximum(w, stripe.astype(F) * 0.9)[..., None]
        return col * (1 - w) + white * w


class Alien(Species):
    name = "alien"
    hp_center = (0, 0.18, -0.02)
    def __init__(self):
        self.eyes = []
        self.poses = {"idle": (0.12, 0.0), "music": (0.2, 0.0), "happy": (0.40, 0.0), "call_tongue": 0.0}

    def parts(self, q, ex):
        cranium = sd_ellipsoid(q, v(0, 0.06, 0), v(0.86, 0.80, 0.76))
        face = sd_ellipsoid(q, v(0, -0.42, 0.18), v(0.46, 0.38, 0.46))
        d = smin(cranium, face, 0.28)
        gap, mparts = smile(q, v(0, -0.56, 0.58), 0.16, 0.12, ex["mouth"], bend=2.2, tongue=ex["tongue"],
                            tongue_len=0.12, tongue_w=0.08)
        d = np.maximum(d, -gap)
        parts = [(d, 4)] + mparts
        for sx in (-1, 1):
            qe = rot_z(q - v(sx * 0.30, -0.14, 0.70), sx * math.radians(-28))
            parts.append((sd_ellipsoid(qe, v(0, 0, 0), v(0.22, 0.125, 0.10)), 16))
            parts.append((sd_capsule(q, v(sx * 0.22, 0.70, 0.0), v(sx * 0.42, 1.12, -0.04), 0.035), 4))
            parts.append((sd_sphere(q, v(sx * 0.43, 1.16, -0.04), 0.078), 14))
        return parts

    def fur(self, q):
        skin, light, blush = v(0.30, 0.74, 0.34), v(0.58, 0.90, 0.52), v(0.95, 0.55, 0.60)
        m = sd_ellipsoid(q, v(0, -0.48, 0.32), v(0.40, 0.30, 0.40))
        w = (1 - smoothstep(-0.05, 0.10, m))[..., None]
        col = skin * (1 - w) + light * w
        for sx in (-1, 1):
            b = sd_sphere(q, v(sx * 0.40, -0.40, 0.48), 0.10)
            bw = (1 - smoothstep(-0.02, 0.06, b))[..., None] * 0.55
            col = col * (1 - bw) + blush * bw
        return col


SPECIES = {"golden": Golden(), "cat": Cat(), "wolf": Wolf(), "alien": Alien()}

# ---------------------------------------------------------------- scene + render
PIVOT = v(0, -0.65, 0)

def head_local(p, ex):
    q = p - PIVOT
    q = rot_x(q, -ex["nod"])
    q = rot_z(q, -ex["roll"])
    return q + PIVOT + v(0, -0.06, 0)

def scene(p, sp, ex):
    q = head_local(p, ex)
    parts = sp.parts(q, ex) + headphones(q, sp.hp_scale, sp.hp_center, spread=sp.hp_spread, band_ys=sp.hp_band)
    d = np.stack([x[0] for x in parts], 0)
    idx = np.argmin(d, 0)
    mats = np.array([x[1] for x in parts])
    return np.min(d, 0), mats[idx]

def render(sp, ex, yaw, pitch):
    w, h = W * SS, H * SS
    ys, xs = np.mgrid[0:h, 0:w].astype(F)
    u = (xs + 0.5) / w * 2 - 1
    vv = 1 - (ys + 0.5) / h * 2
    ro = v(0, 0.05, 4.6)
    fov = 0.34
    rd = np.stack([u * (w / h) * fov, vv * fov, -np.ones_like(u)], -1)
    rd /= length(rd)[..., None]
    rd = rd.reshape(-1, 3)
    n = len(rd)
    ro_all = np.broadcast_to(ro, (n, 3)).copy()
    to_obj = lambda pp: rot_x(rot_y(pp, -yaw), -pitch)

    # bounding sphere: skip rays that can't hit anything (big speed-up)
    R = 1.9
    b = rd @ ro
    disc = b * b - (ro @ ro - R * R)
    hitbox = disc > 0
    sq = np.sqrt(np.maximum(disc, 0))
    t = np.maximum(-b - sq, 0).astype(F)
    tmax = (-b + sq).astype(F)
    hit = np.zeros(n, bool)
    alive = hitbox.copy()
    for _ in range(130):
        idx = np.nonzero(alive)[0]
        if idx.size == 0:
            break
        pos = to_obj(ro_all[idx] + rd[idx] * t[idx, None])
        d, _ = scene(pos, sp, ex)
        t[idx] += d * 0.8
        hh = d < 0.0015
        hit[idx[hh]] = True
        alive[idx[hh | (t[idx] > tmax[idx])]] = False

    rgb = np.zeros((n, 3), F)
    alpha = hit.astype(F)
    hi = np.nonzero(hit)[0]
    if hi.size:
        P = to_obj(ro_all[hi] + rd[hi] * t[hi, None])
        D = rot_x(rot_y(rd[hi], -yaw), -pitch)
        _, mid = scene(P, sp, ex)
        e = 0.0015
        N = np.zeros_like(P)
        for k in ((1, -1, -1), (-1, -1, 1), (-1, 1, -1), (1, 1, 1)):
            k = v(*k)
            N += k * scene(P + k * e, sp, ex)[0][..., None]
        N /= np.maximum(length(N)[..., None], 1e-6)

        Q = head_local(P, ex)
        alb = np.zeros_like(P)
        emis = np.zeros(len(P), F)
        sk = np.zeros(len(P), F)
        spw = np.ones(len(P), F)
        for m, (col, s_k, s_p, em) in MATS.items():
            sel = mid == m
            if not sel.any():
                continue
            if col is not None: alb[sel] = col
            sk[sel] = s_k; spw[sel] = s_p; emis[sel] = em
        for m, fn in ((4, sp.fur), (13, sp.fur2), (15, sp.eye_color)):
            sel = mid == m
            if sel.any(): alb[sel] = fn(Q[sel])

        def L(vec):
            vec = v(*vec); vec /= np.linalg.norm(vec)
            return rot_x(rot_y(vec[None], -yaw), -pitch)[0]
        key, fill, rim, up = L((-0.55, 0.75, 0.65)), L((0.8, 0.15, 0.45)), L((0.2, 0.45, -1.0)), L((0, 1, 0))
        V = -D
        sh = np.ones(len(P), F); ts = np.full(len(P), 0.02, F)
        for _ in range(24):
            dd, _ = scene(P + key * ts[:, None], sp, ex)
            sh = np.minimum(sh, np.clip(10 * dd / ts, 0, 1)); ts += np.clip(dd, 0.02, 0.2)
        sh = np.clip(sh, 0, 1) ** 1.5
        ao = np.zeros(len(P), F)
        for i in range(1, 6):
            hh = 0.035 * i
            dd, _ = scene(P + N * hh, sp, ex)
            ao += (hh - dd) / (2 ** i)
        ao = np.clip(1 - 3.0 * ao, 0.25, 1)
        lam = lambda l: np.clip(np.sum(N * l, -1), 0, 1)
        def spec(l):
            hv = l + V; hv /= length(hv)[..., None]
            return np.clip(np.sum(N * hv, -1), 0, 1) ** spw
        hemi = 0.5 + 0.5 * np.sum(N * up, -1)
        col = alb * ((lam(key) * sh * 1.25)[:, None] * v(1.0, 0.97, 0.92)
                     + (lam(fill) * 0.35)[:, None] * v(0.75, 0.82, 1.0)
                     + ((0.12 + 0.24 * hemi) * ao)[:, None])
        col += (sk * spec(key) * sh * 1.4)[:, None] + (sk * spec(fill) * 0.35)[:, None]
        fres = (1 - np.clip(np.sum(N * V, -1), 0, 1)) ** 3
        col += ((lam(rim) * 0.55 + fres * 0.18) * ao)[:, None] * v(0.85, 0.9, 1.0) * np.where(sk > 0.3, 1.0, 0.45)[:, None]
        col += alb * emis[:, None] * 0.9
        rgb[hi] = np.clip(col, 0, 1) ** (1 / 2.2)

    img = np.concatenate([rgb * alpha[:, None], alpha[:, None]], -1).reshape(h, w, 4)
    img = img.reshape(H, SS, W, SS, 4).mean((1, 3))
    a = img[..., 3:4]
    out = np.concatenate([np.where(a > 1e-4, img[..., :3] / np.maximum(a, 1e-4), 0), a], -1)
    return Image.fromarray((np.clip(out, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")

# ---------------------------------------------------------------- animation sets
SETS = {"idle": 20, "music": 20, "happy": 12, "call": 10}
D = math.radians

def pose(sp, set_name, i):
    n = SETS[set_name]
    ph = 2 * math.pi * i / n
    mouth, tongue = sp.poses.get(set_name if set_name != "call" else "idle", (0, 0))
    ex = {"mouth": mouth, "tongue": tongue, "nod": 0.0, "roll": 0.0}
    yaw, pitch = D(-6) + D(20) * math.sin(ph), D(8)
    if set_name == "music":
        yaw = D(-6) + D(10) * math.sin(ph)
        ex["nod"] = D(10) * abs(math.sin(2 * ph)) - D(3)
        ex["roll"] = D(7) * math.sin(ph)
    elif set_name == "happy":
        yaw = D(-6) + D(9) * math.sin(ph)
        ex["mouth"] = mouth + 0.10 * math.sin(4 * ph)
        ex["nod"] = D(2.5) * math.sin(4 * ph)
        ex["roll"] = D(4) * math.sin(ph)
    elif set_name == "call":
        yaw = D(-4)
        ex["tongue"] = sp.poses.get("call_tongue", 0)
        if sp.call_is_howl:
            curve = [0, .35, .7, 1, 1, 1, 1, .7, .35, 0][i]
            ex["nod"] = -D(24) * curve
            ex["mouth"] = 0.2 + 0.7 * curve
        else:
            m = [0, .6, 1, .55, .1, .6, 1, .55, .15, 0][i]
            ex["mouth"] = mouth + (1 - mouth) * m
            ex["nod"] = -D(7) * m
    return ex, yaw, pitch


if __name__ == "__main__":
    if sys.argv[1] == "test":
        names = sys.argv[3:] or list(SPECIES)
        tiles = []
        for name in names:
            sp = SPECIES[name]
            for set_name, i in (("idle", 3), ("happy", 2), ("call", 2), ("music", 3)):
                ex, yaw, pitch = pose(sp, set_name, i)
                tiles.append(render(sp, ex, yaw, pitch))
        sheet = Image.new("RGBA", (W * 4, H * len(names)), (12, 36, 30, 255))
        for k, im in enumerate(tiles):
            sheet.alpha_composite(im, ((k % 4) * W, (k // 4) * H))
        sheet.save(sys.argv[2])
        sys.exit()
    name, set_name, outdir = sys.argv[1], sys.argv[2], sys.argv[3]
    os.makedirs(outdir, exist_ok=True)
    sp = SPECIES[name]
    for i in range(SETS[set_name]):
        ex, yaw, pitch = pose(sp, set_name, i)
        render(sp, ex, yaw, pitch).save(os.path.join(outdir, f"{name}_{set_name}_{i:02d}.png"), optimize=True)
        print(name, set_name, i, flush=True)
