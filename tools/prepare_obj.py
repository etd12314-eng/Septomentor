#!/usr/bin/env python3
"""Turn a raw 3D Slicer OBJ export into the mesh the app expects.

  pip install numpy fast-simplification
  python3 tools/prepare_obj.py raw_Segmentation-1.obj Assets/SeptoMentor/Models/Segmentation-1.obj

- Decimates to ~--target triangles in total (each group keeps at least --min-group).
- Converts mm -> metres and recentres on the combined bounding-box centre.
- Converts Slicer LPS to the app convention. Unity's OBJ importer negates x, so the
  file is written as (R, S, P) and lands in Unity as +X patient-left (the endoscope
  view's right), +Y superior, +Z posterior. This is a rotation, not a mirror.
- Drops normals and materials (ObjImportSettings recalculates normals).
- Renames groups in input order with --names so LabelMapping rules can match them.
"""

import argparse
import sys

import numpy as np
import fast_simplification


def parse(path):
    verts, groups, current = [], {}, "default"
    with open(path, "r", errors="replace") as fh:
        for line in fh:
            if line.startswith("v "):
                verts.append(line.split()[1:4])
            elif line.startswith(("o ", "g ")):
                current = line[2:].strip() or "unnamed"
                groups.setdefault(current, [])
            elif line.startswith("f "):
                idx = [int(t.split("/")[0]) for t in line.split()[1:]]
                idx = [i - 1 if i > 0 else len(verts) + i for i in idx]
                for k in range(1, len(idx) - 1):
                    groups.setdefault(current, []).append((idx[0], idx[k], idx[k + 1]))
    return np.asarray(verts, dtype=np.float64), {g: np.asarray(f, dtype=np.int64) for g, f in groups.items() if f}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("dst")
    ap.add_argument("--target", type=int, default=74000, help="total triangles after decimation")
    ap.add_argument("--min-group", type=int, default=8000, help="minimum triangles kept per group")
    ap.add_argument("--units", choices=["mm", "m"], default="mm", help="units of the source file")
    ap.add_argument("--names", default="", help="comma-separated new group names, in input order")
    args = ap.parse_args()

    verts, groups = parse(args.src)
    total = sum(len(f) for f in groups.values())
    names = [n.strip() for n in args.names.split(",")] if args.names else []
    print(f"{args.src}: {len(verts)} vertices, {total} triangles, {len(groups)} group(s)")

    # LPS (x=L, y=P, z=S) -> file (R, S, P); Unity then negates x -> (L, S, P).
    pts = np.column_stack([-verts[:, 0], verts[:, 2], verts[:, 1]])
    if args.units == "mm":
        pts = pts * 0.001

    out = []
    for gi, (name, faces) in enumerate(groups.items()):
        used, inv = np.unique(faces, return_inverse=True)
        gv, gf = pts[used].astype(np.float32), inv.reshape(-1, 3).astype(np.int32)
        keep = max(args.min_group, round(args.target * len(gf) / total))
        if keep < len(gf):
            gv, gf = fast_simplification.simplify(gv, gf, target_reduction=1.0 - keep / len(gf))
        new_name = names[gi] if gi < len(names) and names[gi] else name
        print(f"  '{name}' -> '{new_name}': {len(faces)} -> {len(gf)} triangles")
        out.append((new_name, np.asarray(gv, np.float64), np.asarray(gf)))

    allv = np.vstack([v for _, v, _ in out])
    centre = (allv.min(0) + allv.max(0)) / 2
    size = allv.max(0) - allv.min(0)
    print(f"  recentred by {centre} m; size {size * 1000} mm (X=L, Y=S, Z=P in Unity)")

    with open(args.dst, "w", newline="\n") as fh:
        fh.write(f"# SeptoMentor mesh from {args.src.split('/')[-1]}: metres, recentred, "
                 "file axes (R,S,P) -> Unity (+X patient-left, +Y superior, +Z posterior)\n")
        base = 1
        for name, v, f in out:
            fh.write(f"g {name}\n")
            fh.write("".join(f"v {x:.6f} {y:.6f} {z:.6f}\n" for x, y, z in v - centre))
            fh.write("".join(f"f {a + base} {b + base} {c + base}\n" for a, b, c in f))
            base += len(v)
    print(f"wrote {args.dst}: {sum(len(f) for _, _, f in out)} triangles")
    return 0


if __name__ == "__main__":
    sys.exit(main())
