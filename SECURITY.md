# Security Policy

## Local API Key Storage
- **SWYF-OpenAI-GenAI-Mod** stores your API key locally on your computer in `ai_mod_config.json` (in your *Scam With Your Friends* installation folder) and Unity `PlayerPrefs`.
- Your API key is sent **only** to the AI endpoint configured in your `ai_mod_config.json` (e.g., `generativelanguage.googleapis.com`, `api.openai.com`, or `openrouter.ai`).
- All in-game analytics and telemetry (`AnalyticsCollector` and `AnalyticsCollection`) are completely disabled by the patcher.
- The built-in diagnostic server (`http://127.0.0.1:47890/`) binds strictly to `127.0.0.1` (loopback only) and masks your API key in `/status` responses.

## Reporting a Vulnerability
If you discover a security issue, please open an issue on [GitHub Issues](https://github.com/justadudeinbulgaria/SWYF-OpenAI-GenAI-Mod/issues) without including any personal API keys or credentials.
