# SeptoMentor

Educational AR/VR mentor for ENT residents learning endoscopic septoplasty, built for Meta Quest 3 (Unity 6, C#).

> **Training aid only. Not a medical device. The attending surgeon decides.**
> Structure labels are **not validated anatomy**. The app shows them with a `?` and an
> "UNVALIDATED LABELS" banner until a validated model is plugged in.

## What it does

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
