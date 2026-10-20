# SeptoMentor

Educational AR/VR mentor for ENT residents learning endoscopic septoplasty, built for Meta Quest 3 (Unity 6, C#).

> **Training aid only. Not a medical device. The attending surgeon decides.**
> Structure labels are **not validated anatomy**. The app shows them with a `?` and an
> "UNVALIDATED LABELS" banner until a validated model is plugged in.

The APK starts in a **menu**: **A = XR Navigation** (operating room, passthrough, patient CT),
**B = VR Training** (the endoscopy simulator below). In any mode, hold **both grips for 2 s** to return.

## XR Navigation (operating room)

> **Research prototype, not a certified navigation system.** Quest controller tracking plus
> landmark registration typically leaves several millimetres of error, and tracking can be
> re-centred by the headset. Use it on phantoms / mannequins first; any use on patients needs
> ethics and regulatory approval, and decisions are always made on direct anatomy.

Passthrough (Meta OpenXR `Camera (Passthrough)` feature) shows the real room; the patient's CT
is overlaid on the patient's face after registration.

| Step | What the surgeon does |
|---|---|
| 1. Case | Right stick picks a case, **A** loads it. `Phantom (synthetic)` is a generated head CT (septum deviated to the patient's right, turbinates, skull base) for rehearsal without patient data. |
| 2. Pointer calibration | Tip of the right controller (or a stylus fixed to it) in a divot, **hold trigger** and pivot ~5 s. Saved on the headset. |
| 3. Plan landmarks | Only if the CT has no landmarks: aim the controller ray at the floating CT skin and pull the trigger for each missing landmark. |
| 4. Registration | Touch nasion, nose tip, subnasale, both inner eye corners with the tip (trigger each). Shows the fiducial registration error. |
| 5. Surface refinement | Hold trigger and stroke dorsum / forehead / cheeks; **A** runs ICP onto the CT skin. |
| 6. Verify | Live tip-to-CT-skin distance while touching the face. **A** accept, **B** redo. |
| Navigation | Septoplasty phases (A next, B back) with checklists, CT slices (axial / coronal / sagittal) through the tip, overlay of septum / bone / L-strut band, warnings (inside the 10 mm L-strut during resection, skull-base clearance, tracking re-centred), step times and warnings per phase, AI feedback, debrief. |

Other controls: **X** overlay mode, **left trigger** CT window (bone / soft tissue), **Y** ask the
AI, **right stick click** move the panel in front of you, **hold left grip + sticks** nudge the
registration.

### Loading a patient CT
Copy one folder per case to the headset (SideQuest file manager or `adb push`):

```
adb push MyCase /sdcard/Android/data/org.septomentor.app/files/Cases/MyCase
```

A case folder contains either an **uncompressed DICOM CT series** (compressed series: re-save
uncompressed, e.g. `gdcmconv --raw`, or export NRRD) or a **`.nrrd`** volume saved from 3D Slicer.
Optional extras:
- `*.mrk.json` 3D Slicer point markups with labels `Nasion`, `Pronasale`, `Subnasale`,
  `Endocanthion R`, `Endocanthion L` (registration), `SeptumASA`, `SeptumDorsal`, `SeptumANS`
  (corners of the L-strut: anterior septal angle, dorsal end, caudal-inferior end) and `SkullBase`.
  Without them the app auto-detects the nose tip and asks for the rest on the CT model.
- `*.obj` segment meshes exported from Slicer in LPS millimetres, shown as overlays.

Large volumes are halved to at most 256 voxels per axis on the headset. Data stays on the headset;
the AI proxy only receives text (phase, distances, registration error), never images or identifiers.

| Layer | File |
|---|---|
| Volume, loaders, phantom | `Scripts/Nav/CtVolume.cs`, `DicomLoader.cs`, `NrrdLoader.cs`, `CtPhantom.cs`, `CaseLoader.cs` |
| Surfaces | `Scripts/Nav/SurfaceNets.cs` (skin at -300 HU, bone at +300 HU, label meshes) |
| Registration | `Scripts/Nav/Registration.cs` (Horn paired-point, ICP, pivot calibration) |
| Flow, rules, AI context | `Scripts/Nav/NavigationApp.cs`, `NavDashboard.cs`, `Shaders/CtSlice.shader` |

## VR Training simulator

| Layer | File | Notes |
|---|---|---|
| Classes | `Scripts/Core/Structure.cs` | 16 classes. The enum value is the model channel. `SinonasalBone` is a placeholder class. |
| Provider base | `Scripts/Segmentation/SegmentationProvider.cs` | Video texture, R8 label map, per-class stats, `IsSimulation`, `LabelsValidated` |
| Synthetic (placeholder) | `SyntheticSegmentationProvider.cs`, `VirtualPatient.cs`, `VirtualInstrument.cs` | Virtual scope flies through `Segmentation-1.obj` (or procedural placeholder anatomy). Renders the video and the exact labels from the same pose, then reads labels back with `AsyncGPUReadback`. |
| ONNX | `SegmentationRunner.cs` (`SEPTO_ONNX`) | Unity Inference Engine. Input `[1,3,256,256]`, output logits `[1,16,256,256]`. ArgMax runs on the GPU, then an async readback. |
| Overlay | `Scripts/Overlay/OverlayView.cs` + `MonitorComposite.shader` | Video plus class fill/outline, floating labels, SIMULATION and UNVALIDATED banners |
| Rules | `Scripts/Mentor/MentorEngine.cs` | Phases: Setup → Incision → FlapElevation → Resection → Closure. Rules: ~10 mm L-strut, PPE torque, mucosal tear, turbinate protection, subperichondrial plane, remove only the deviated part. |
| AI mentor | `Scripts/AI/AIMentorClient.cs`, `proxy/server.py` | Coaching on warnings and phase changes (Haiku). Resident Q&A (Sonnet). The API key and system prompt live only on the proxy. |
| Voice | `Scripts/Speech/SpeechIO.cs` | Android TTS via JNI, plus a Meta Voice SDK hook for speech-to-text |
| Hologram | `Scripts/Hologram/AnatomyHologram.cs` | ~30 cm ghost anatomy with a live scope marker and view ray |
| Debrief | `Scripts/Logging/SessionLogger.cs` | JSONL in `persistentDataPath/sessions/`. Text only, never frames. |
| Input | `Scripts/Input/ControllerInput.cs` | A: next phase, B: cycle instrument, X: recenter, Y: ask AI. Right stick: look (hold grip to twist the instrument). Left stick: advance. Right trigger: labels. Left trigger: simulated tear. |
| Scene/CI | `Editor/SceneBuilder.cs`, `Editor/BuildScript.cs`, `.github/workflows/build-quest.yml` | The scene is generated in code. The APK is built by game-ci. |

The virtual patient sits 500 m from the user, beyond the headset camera's 50 m far clip. Only the endoscope cameras see it, and they render manually at 30 Hz.

## Run in the editor
1. Open the folder with Unity **6000.0.x**. Let packages resolve.
2. Optional: copy `Segmentation-1.obj` to `Assets/SeptoMentor/Models/` (Git LFS).
3. Menu **SeptoMentor → Build Scene**, then press Play.
   Keys: `N` phase, `I` instrument, `Q` ask, `T` tear, `L` labels, arrows to look, `W/S` to advance, hold `G` to twist.

## Build the APK (CI)
Add these repository secrets: `UNITY_LICENSE`, or `UNITY_EMAIL` + `UNITY_PASSWORD` + `UNITY_SERIAL`. `MENTOR_ENDPOINT` and `MENTOR_TOKEN` are optional.
Each push runs the proxy tests and `BuildScript.Build`. That method sets Android, IL2CPP, ARM64, API 29+, Vulkan and debug signing, tries to configure OpenXR, writes the mentor config and generates the scene. The APK is uploaded as a workflow artifact.
Install it with `adb install -r SeptoMentor.apk`.

## AI proxy
```bash
cd proxy && pip install -r requirements.txt
export ANTHROPIC_API_KEY=...  MENTOR_TOKEN=$(openssl rand -hex 24)
uvicorn server:app --host 0.0.0.0 --port 8080      # put HTTPS in front
python -m pytest -q tests                           # offline tests, fake client
```
- `/v1/coach` uses `claude-haiku-5-5` at low effort and replies in at most 35 words. `/v1/ask` uses `claude-sonnet-5-5` at medium effort. It has server-side refusal fallback enabled (`fallbacks: "default"`).
- Frames are dropped unless the proxy has `ALLOW_FRAMES=1` **and** the build has `MENTOR_ALLOW_FRAMES=1` **and** the runtime `sendFrames` toggle is on. **Sending patient video off the device needs institutional approval.**
- `MENTOR_TOKEN` can be extracted from the APK. Treat it as a revocable, rate-limited proxy credential. It is never an Anthropic key.

## Honesty: what is NOT verified
- **No C# has been compiled in Unity.** It was only syntax-checked with Roslyn and reviewed by hand. Expect a few compile fixes on first open.
- **Not run on a Quest 3.** Not tested: TTS on Quest OS (Quest may have no TTS engine), XR input bindings, `AsyncGPUReadback` R8 on Vulkan, and performance.
- **Guessed versions:** Unity `6000.0.58f1`, `com.unity.xr.openxr 1.15.1`, `xr.management 4.5.1`, `inputsystem 1.14.2`, `ugui 2.0.0`, `ai.inference 2.2.1`. Align them with your editor.
- **XR settings:** `XRSetup.cs` uses reflection and is best-effort. The reliable route is open item 2.
- **Built-in render pipeline** (no URP). The shaders are plain CG.
- **`SegmentationRunner`** is written against the Inference Engine 2.2 API from memory. The tensor row order is a guess (`flipRows`).
- **Labels:** the OBJ segment→class mapping is a placeholder (`LabelMapping.cs`). Without the OBJ, the anatomy is procedural boxes and capsules. Rule thresholds are teaching defaults, not validated limits. The synthetic mm scale comes from known depth. A real scope needs calibration.
- The proxy **was** tested offline (7 pytest cases with a fake client). It was not tested against the live API.

## Data rules
- A trained model needs **expert-annotated, ethics-approved, de-identified** endoscopy video. YouTube videos are for reference only, never training data. See `docs/annotation_protocol.md`.
- Session logs contain clinical context. Keep them off git (see `.gitignore`).

## Open items
1. Confirm the two OBJ segments (`python3 tools/inspect_obj.py <obj>` or the menu **SeptoMentor → Inspect OBJ Segments**). Then write `Assets/SeptoMentor/Resources/label_mapping.json` (example in `docs/`) with `confirmed: true`.
2. One-time local Unity setup: resolve packages, enable OpenXR with Meta Quest Support (Android), import TMP Essentials, and commit `Packages/packages-lock.json`, `ProjectSettings/**` and `Assets/XR/**`.
3. Passthrough (MR) instead of the dark background.
4. Learned phase classifier, calibrated L-strut in mm. (Async readback is done.)
5. Annotation protocol and training pipeline for the real ONNX model (draft in `docs/`).
6. Controller-driven virtual instrument.
