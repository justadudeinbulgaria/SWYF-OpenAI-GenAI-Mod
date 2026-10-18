# Changelog

All notable changes to **SWYF-OpenAI-GenAI-Mod** are documented in this file.

## [v1.2.0] - 2026-10-18

### Added
- **October 18, 2026 Steam Update Compatibility**:
  - Automatically detects fresh unpatched Steam updates by inspecting `ScriptsAssDef.dll` assembly references and refreshes `.bak` backups before patching.
  - Patches `WindowsSpeechCompatibility` (`UseMoonshine`, `get_PreferMoonshine`, `get_ForceMoonshine` $\rightarrow$ `false`), `DeepgramSpeechSettings` (`get_Selected` $\rightarrow$ `false`), and `DeepgramSpeechOffer` (`Show` $\rightarrow$ no-op) so 100% of voice recognition routes through `SurplusBridge.TranscribeAsync` without loading the `Moonshine` CPU model into RAM.
  - Overrides `PauseMenuToolkitController.BindSpeechRecognition()` format callbacks on `speech-recognition-dropdown` to display `"AI Mod Cloud STT (<voice_model>)"`.
- **Dual Model ID Controls (`CHAT AI MODEL ID` & `VOICE AI MODEL ID`)**:
  - Added two independent text fields in the in-game `AI MOD SETTINGS` panel (`Settings` $\rightarrow$ `GENERAL`):
    1. `CHAT AI MODEL ID (DIALOGUE)` (`model` in `ai_mod_config.json`)
    2. `VOICE AI MODEL ID (STT)` (`voice_model` in `ai_mod_config.json`)
  - Automatically adjusts `stt_mode` when switching between Whisper/transcribe models (`whisper_api`) and multimodal chat models (`gemini_native` / `chat_audio`).
- **Local Loopback Diagnostic Server (`127.0.0.1:47890`)**:
  - Built-in localhost diagnostic endpoints (`/status`, `/ui`, `/logs`, `/screenshot`, `/open-settings`, `/close-settings`, `/select-tab`) for automated UI & log verification.

## [v1.1.0] - 2026-10-17

### Added
- **In-Game `AI MOD SETTINGS` Panel & Unlocked `AI MOD & HOST` Tab**:
  - Injected a dedicated settings panel at the top of `Settings` $\rightarrow$ `GENERAL` (and unlocked the `AI MOD & HOST` tab in both the Main Menu and In-Game Pause Menu).
  - Live editable **API Token** input field saved immediately to `ai_mod_config.json`, `surplus_config.json`, and Unity `PlayerPrefs`.
  - **Whisper Offline Fallback Dropdown (`~400MB RAM Saver`)**: Skips loading the ~400MB local Whisper `ggml` model in `WhisperManager.Awake` and `WhisperVcPipeline.EnsureRecognizerReady` when `enable_whisper_fallback` is `false` (default).
  - Native UI Toolkit `.menu-dropdown-surface` and `.menu-dropdown-anchor` styling applied to custom dropdowns.

## [v1.0.0] - 2026-10-16

### Added
- Initial release of **`SwyfModTool.exe`** (`Mono.Cecil` patcher, backup manager, and live API verifier) and **`SurplusBridge.dll`**.
- Direct support for **Google Gemini (GenAI Standard API)**, **OpenAI Standard API**, **OpenRouter**, and custom OpenAI-compatible endpoints.
- **4-Stage Real-Time Phone & Webcam HUD** with live elapsed STT and AI timers.
- Background-threaded WAV encoding, `0.008f` RMS silence trimming, smart partial transcript reuse (`0.01s` finalization), single-word filter removal in `MetaVosk.dll`, uncapped `900k`-token conversation memory, and full telemetry neutralization.
