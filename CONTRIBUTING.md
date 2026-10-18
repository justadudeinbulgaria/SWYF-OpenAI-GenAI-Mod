# Contributing to SWYF-OpenAI-GenAI-Mod

Contributions, bug reports, and feature requests are welcome!

## Ground Rules

1. **Never Commit Copyrighted Game Binaries**:
   - Do **not** commit `Assembly-CSharp.dll`, `ScriptsAssDef.dll`, `MetaVosk.dll`, `com.whisper.unity.dll`, or any other binary from `Scam With Your Friends_Data\Managed`.
   - All game modifications must be performed via `Mono.Cecil` IL weaving inside `src/SwyfModTool/Program.cs` or runtime hooks inside `src/SurplusBridge/SurplusBridge.cs`.
2. **Never Commit API Keys or Secrets**:
   - Never hardcode any API key (`sk-...`, `AIza...`, etc.) in source files, commit messages, or screenshots.
   - Only commit placeholder keys in `ai_mod_config.example.json`.
3. **Target Framework Compatibility**:
   - Both `SurplusBridge.csproj` and `SwyfModTool.csproj` target `.NET Framework 4.8` (`net48`) to match the Unity Mono runtime used by *Scam With Your Friends*.
   - Avoid referencing types or methods that are stripped from the game's `Managed` folder (`System.Text.Json`, `System.Net.Http`, etc.); use `UnityWebRequest` or `HttpWebRequest` and lightweight JSON helpers as implemented in `SurplusBridge.cs`.

## Verification Checklist Before Submitting a PR

1. Build both projects in `Release` mode:
   ```powershell
   dotnet build src/SurplusBridge/SurplusBridge.csproj -c Release
   dotnet build src/SwyfModTool/SwyfModTool.csproj -c Release
   ```
2. Run `SwyfModTool.exe` against a clean Steam installation of *Scam With Your Friends* and ensure the built-in IL & member reference verifier passes with **0 errors**.
3. Launch the game and verify `http://127.0.0.1:47890/logs` reports **0 errors / exceptions**.
