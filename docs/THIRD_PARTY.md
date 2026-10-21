# Third-party components bundled in the app

| Component | Where | License |
|---|---|---|
| Vosk speech recognition (`libvosk.so` from `com.alphacephei:vosk-android:0.3.47`) | `Assets/Plugins/Android/libs/arm64-v8a/` | Apache-2.0 |
| Vosk small Turkish model 0.3 (`vosk-model-small-tr-0.3`, alphacephei.com/vosk/models) | `Assets/StreamingAssets/vosk-model-small-tr/` | Apache-2.0 |
| Turkish voice clips, synthesized offline with Piper TTS, voice `tr_TR-dfki-medium` (rhasspy/piper-voices; dataset: MaryTTS dfki-ot-data) | `Assets/SeptoMentor/Resources/Voice/` (made by `tools/make_voice.py` from `tools/voice_phrases.tsv`) | Voice/dataset CC BY-NC-SA 4.0: **non-commercial use only**; regenerate the clips with a commercially licensed voice before any commercial use |
