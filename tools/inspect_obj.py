#!/usr/bin/env python3
"""OPEN ITEM 1 helper: list the segments in a 3D Slicer OBJ export, without Unity.

  python3 tools/inspect_obj.py Segmentation-1.obj [--units m|mm]

Prints, per 'o'/'g' group: faces, bounds size and centre (mm), the PCA thickness ratio
(thin plates such as the septum have a small ratio) and a non-binding shape hint.
Group names are what LabelMapping.nameContains matches against in Unity.
Shape hints are NOT anatomy confirmation - an expert must confirm each segment.
"""

import argparse
import math
import sys
from collections import OrderedDict


def parse(path):
    verts, groups, current = [], OrderedDict(), "default"
    with open(path, "r", errors="replace") as fh:
        for line in fh:
            if line.startswith("v "):
                x, y, z = map(float, line.split()[1:4])
                verts.append((x, y, z))
            elif line.startswith(("o ", "g ")):
                current = line[2:].strip() or "unnamed"
                groups.setdefault(current, [])
            elif line.startswith("f "):
                idx = []
                for tok in line.split()[1:]:
                    i = int(tok.split("/")[0])
                    idx.append(i - 1 if i > 0 else len(verts) + i)
                groups.setdefault(current, []).append(idx)
    return verts, groups


def pca_extents(points):
    n = len(points)
    mx = [sum(p[k] for p in points) / n for k in range(3)]
    c = [[0.0] * 3 for _ in range(3)]
    for p in points:
        d = [p[k] - mx[k] for k in range(3)]
        for i in range(3):
            for j in range(3):
                c[i][j] += d[i] * d[j] / n
    # Eigenvalues of symmetric 3x3 (closed form).
    p1 = c[0][1] ** 2 + c[0][2] ** 2 + c[1][2] ** 2
    q = (c[0][0] + c[1][1] + c[2][2]) / 3
    if p1 < 1e-30:
        ev = sorted([c[0][0], c[1][1], c[2][2]])
    else:
        p2 = (c[0][0] - q) ** 2 + (c[1][1] - q) ** 2 + (c[2][2] - q) ** 2 + 2 * p1
        p = math.sqrt(p2 / 6)
        b = [[(c[i][j] - (q if i == j else 0)) / p for j in range(3)] for i in range(3)]
        r = (b[0][0] * (b[1][1] * b[2][2] - b[1][2] * b[2][1])
             - b[0][1] * (b[1][0] * b[2][2] - b[1][2] * b[2][0])
             + b[0][2] * (b[1][0] * b[2][1] - b[1][1] * b[2][0])) / 2
        phi = math.acos(max(-1, min(1, r))) / 3
        e1 = q + 2 * p * math.cos(phi)
        e3 = q + 2 * p * math.cos(phi + 2 * math.pi / 3)
        ev = sorted([e1, 3 * q - e1 - e3, e3])
    return [math.sqrt(max(e, 0)) for e in ev]  # std-devs, ascending


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("obj")
    ap.add_argument("--units", choices=["m", "mm"], default="m", help="units of the OBJ coordinates")
    a = ap.parse_args()
    to_mm = 1000.0 if a.units == "m" else 1.0

    verts, groups = parse(a.obj)
    print(f"{a.obj}: {len(verts)} vertices, {sum(len(f) for f in groups.values())} faces, {len(groups)} group(s)\n")
    for name, faces in groups.items():
        if not faces:
            continue
        ids = sorted({i for f in faces for i in f})
        pts = [verts[i] for i in ids]
        lo = [min(p[k] for p in pts) for k in range(3)]
        hi = [max(p[k] for p in pts) for k in range(3)]
        size = [(hi[k] - lo[k]) * to_mm for k in range(3)]
        centre = [(hi[k] + lo[k]) / 2 * to_mm for k in range(3)]
        sd = pca_extents(pts)
        thin = sd[0] / sd[2] if sd[2] > 0 else 0
        elong = sd[1] / sd[2] if sd[2] > 0 else 0
        if elong < 0.35 and thin > 0.5 * elong:
            hint = "elongated, round cross-section (turbinate-like?)"
        elif thin < 0.15:
            hint = "thin plate (septum-like: cartilage / bone plate / flap?)"
        else:
            hint = "bulky / irregular (airway, bone block or whole-nose shell?)"
        print(f"- '{name}': {len(faces)} faces")
        print(f"    bounds size  (mm): {size[0]:.1f} x {size[1]:.1f} x {size[2]:.1f}")
        print(f"    bounds centre(mm): {centre[0]:.1f}, {centre[1]:.1f}, {centre[2]:.1f}")
        print(f"    PCA thickness ratio {thin:.2f}, elongation {elong:.2f} -> hint: {hint}")
    print("\nHints are geometric only. Confirm each segment with the person who segmented it.")


if __name__ == "__main__":
    sys.exit(main())
