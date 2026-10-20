# Annotation protocol and training pipeline (DRAFT, open item 5)

Status: a draft for discussion with the ethics board and the annotating surgeons. Nothing here has been approved.

## 1. Governance (before any data)
- Get ethics/IRB approval for retrospective or prospective septoplasty video, with consent per your institution.
- De-identify on site. Strip metadata and audio. Crop or blur on-screen overlays such as name, MRN, date and the OR clock. Do a manual spot check.
- Store the data on institutional storage only. Never put patient video in git or on the AI proxy.
- Public or YouTube surgical videos are **reference only**. Do not use them to train or validate.

## 2. Sampling
- Use 0° rigid endoscope recordings at ≥720p. Record the device and the light source.
- Split by **patient**, never by frame: 70 / 15 / 15 train/val/test, stratified by surgeon and by deviation type.
- Annotate frames at 1 fps, plus extra frames around phase transitions and adverse events such as tears and bleeding.

## 3. Classes (must match `Structure.cs`)
| id | class | rule |
|---|---|---|
| 0 | Background | Everything else, including blood and fog that obscures anatomy |
| 1 | Septal cartilage | Exposed cartilage (bluish-white) |
| 2 | Perpendicular plate of ethmoid | Exposed bone posterior-superior to the cartilage |
| 3 | Vomer | Exposed bone posterior-inferior |
| 4 | Maxillary crest | Inferior bony crest / premaxilla |
| 5 | Mucoperichondrial flap | Elevated flap, inner surface or edge |
| 6 | Mucosal tear | Full-thickness defect in a flap |
| 7 / 8 | Inferior / middle turbinate | Visible mucosal surface |
| 9 | Caudal septum | Caudal border region of the cartilage |
| 10 | Nasal floor | |
| 11–14 | Scalpel, Freer, scissors/forceps, suction | The whole visible instrument, including the shaft |
| 15 | Sinonasal bone (placeholder) | Use only for bone you cannot identify. Revisit before release. |

Also record per frame: surgical phase (5 classes), image quality (ok, blur, blood, fog), and an "uncertain" flag.

## 4. Process
- Tool: CVAT or Label Studio with polygon masks. Export PNG masks whose pixel value is the class id.
- Two annotators per frame, at least one of them an ENT surgeon. A senior rhinologist adjudicates disagreements.
- Track per-class Dice/IoU between annotators. Classes with IoU below 0.6 need guideline revision.

## 5. Training (suggested)
- Model: a lightweight encoder–decoder (e.g. MobileNetV3/EfficientNet-Lite + U-Net/DeepLabV3+). Input 256×256 RGB, ImageNet normalisation (it must match `SegmentationRunner.mean/std`).
- Loss: cross-entropy + Dice, class-weighted. Rare classes such as tears need oversampling.
- Augmentation: colour/white balance, blur, specular highlights, a circular field-stop mask, rotation ±30°.
- Export: ONNX opset 17. Output `[1,16,256,256]` logits, static shape, no argmax (the app adds it).
- On-device check: the Quest 3 GPU backend must reach ≥10 fps.

## 6. Validation before `modelValidated = true`
- Report per-class IoU on the held-out **patient** test set, with confidence intervals.
- Run a failure review with surgeons covering blood, fog and unusual anatomy.
- Write a model card covering data, limits and intended use (training only).
