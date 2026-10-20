`Segmentation-1.obj` lives here (Git LFS, see .gitattributes). Without it, SceneBuilder uses procedural PLACEHOLDER anatomy.

The committed file is prepared from the raw 3D Slicer export (LPS, mm, ~675k triangles) with:

    python3 tools/prepare_obj.py <raw Slicer export>.obj Assets/SeptoMentor/Models/Segmentation-1.obj --names Segment_2,Segment_1

That decimates to ~80k triangles, converts to metres, recentres, and maps LPS to Unity (+X patient-left, +Y superior, +Z posterior).
Groups were renamed so the `LabelMapping` rules match: `Segment_1` = the thin, septum-like plate (Slicer `grp329945`),
`Segment_2` = the bulky shell (Slicer `grp1`). These are geometric guesses: confirm them with whoever segmented the scan (open item 1).
Run `python3 tools/inspect_obj.py Assets/SeptoMentor/Models/Segmentation-1.obj --units m` to list the segments.

## Endoscope path

`VirtualPatient` places the simulated scope automatically. When a segment maps to septal
cartilage (here `Segment_1`), the scope enters the patient's right nostril just in front
of it and runs back along its right face, ending about 9 mm behind it. On this mesh that
corridor keeps at least 2.4 mm from every vertex. Set `pathStart` / `pathEnd` / `scopeYaw`
on `VirtualPatient` to override it.
