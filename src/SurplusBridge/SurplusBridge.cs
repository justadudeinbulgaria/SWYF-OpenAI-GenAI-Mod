using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using Whisper;

public static class SurplusBridge
{
    private const string DefaultApiKey = "";
    private const string DefaultFallbackApiKey = "";
    private const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";
    private const string DefaultModelsEndpoint = "https://api.openai.com/v1/models";
    private const string DefaultSttEndpoint = "https://api.openai.com/v1/audio/transcriptions";
    private const string DefaultModel = "gpt-4o-mini";

    private static readonly object ConfigLock = new object();
    private static bool _configLoaded = false;
    private static string _cachedProvider = "auto";
    private static string _cachedApiKey = DefaultApiKey;
    private static string _cachedFallbackApiKey = DefaultFallbackApiKey;
    private static string _cachedEndpoint = DefaultEndpoint;
    private static string _cachedModelsEndpoint = DefaultModelsEndpoint;
    private static string _cachedSttEndpoint = DefaultSttEndpoint;
    private static string _cachedSttMode = "auto"; // "chat_audio", "whisper_api", "gemini_native", "local"
    private static string _cachedModel = DefaultModel;
    private static string _cachedVoiceModel = DefaultModel;
    private static bool _cachedEnableWhisperFallback = false;
    private static bool _cachedMultimodalVoiceInChat = false;
    private static bool _usingFallbackKey = false;
    private static UnityWebRequest _activePartialRequest;
    private static TaskCompletionSource<bool> _activePartialTcs;
    private static Task<string> _activePartialTask;
    private static int _partialCancelVersion = 0;
    private static int _lastPartialSpeechEndSample = -1;
    private static string _lastCompletedPartialTranscript = null;
    private static DateTime _lastCompletedPartialUtc = DateTime.MinValue;
    private static bool _reuseActivePartialForFinal = false;

    private static readonly object StatusLock = new object();
    private static volatile bool _isUserSpeaking = false;
    private static DateTime _speechStartUtc = DateTime.MinValue;
    private static int _activeFinalTranscribeCount = 0;
    private static int _activePartialTranscribeCount = 0;
    private static DateTime _finalTranscribeStartUtc = DateTime.MinValue;
    private static DateTime _partialTranscribeStartUtc = DateTime.MinValue;
    private static double _lastTranscribeDurationSeconds = 0.0;
    private static DateTime _callerAiStartUtc = DateTime.MinValue;
    private static double _lastCallerAiDurationSeconds = 0.0;
    private static DateTime _waitingStateStartUtc = DateTime.MinValue;
    private static int _lastSeenAvailabilityState = -1;
    private static DateTime _lastUiTickUtc = DateTime.MinValue;
    private static volatile bool _uiDirty = false;

    public static string LatestWavBase64;
    public static string LatestWavTranscript;
    public static DateTime LatestWavTimestampUtc = DateTime.MinValue;

    public static void OnSpeechStarted()
    {
        lock (StatusLock)
        {
            _isUserSpeaking = true;
            _speechStartUtc = DateTime.UtcNow;
            _lastCompletedPartialTranscript = null;
            _lastPartialSpeechEndSample = -1;
            _reuseActivePartialForFinal = false;
            _uiDirty = true;
        }
    }

    public static int GetTrimmedSpeechEndIndex(float[] samples)
    {
        if (samples == null || samples.Length == 0)
            return 0;
        int end = samples.Length - 1;
        while (end > 0 && Math.Abs(samples[end]) < 0.008f)
            end--;
        return end;
    }

    public static void OnSpeechFinalizing(float[] finalSamples)
    {
        int finalSpeechEnd = GetTrimmedSpeechEndIndex(finalSamples);
        bool canReusePartial = false;

        lock (StatusLock)
        {
            _isUserSpeaking = false;
            _uiDirty = true;

            if (finalSamples != null && finalSamples.Length <= 72000 && _lastPartialSpeechEndSample >= 0)
            {
                // If no new speech occurred after the partial was captured (only trailing silence before FinalizeUtterance)
                if (finalSpeechEnd <= _lastPartialSpeechEndSample + 2000)
                {
                    canReusePartial = true;
                }
            }
            _reuseActivePartialForFinal = canReusePartial;
        }

        if (!canReusePartial)
        {
            CancelActivePartial();
        }
    }

    public static void NotifyCallerAiCompleted()
    {
        lock (StatusLock)
        {
            if (_callerAiStartUtc != DateTime.MinValue)
            {
                _lastCallerAiDurationSeconds = Math.Max(0.01, (DateTime.UtcNow - _callerAiStartUtc).TotalSeconds);
            }
            _uiDirty = true;
        }
    }

    public static string GetApiKeyForAttempt(int attempt)
    {
        EnsureConfigLoaded();
        if (attempt > 1 && !string.IsNullOrEmpty(_cachedFallbackApiKey))
        {
            _usingFallbackKey = true;
            return _cachedFallbackApiKey;
        }
        return GetApiKey();
    }

    public static bool ShouldForcePhoneUiRefresh(int availabilityState)
    {
        DateTime now = DateTime.UtcNow;
        if (availabilityState != _lastSeenAvailabilityState)
        {
            if (availabilityState == 6)
            {
                _waitingStateStartUtc = now;
            }
            _lastSeenAvailabilityState = availabilityState;
            _uiDirty = false;
            _lastUiTickUtc = now;
            return true;
        }

        if (_uiDirty)
        {
            _uiDirty = false;
            _lastUiTickUtc = now;
            return true;
        }

        bool hasActiveTimer = _isUserSpeaking ||
                              _activeFinalTranscribeCount > 0 ||
                              _activePartialTranscribeCount > 0 ||
                              availabilityState == 6;
        if (hasActiveTimer && (now - _lastUiTickUtc).TotalMilliseconds >= 80.0)
        {
            _lastUiTickUtc = now;
            return true;
        }

        return false;
    }

    public static int ResolvePhoneStatusDisplayStyle(int currentDisplayStyle, int availabilityState)
    {
        // 0 = DisplayStyle.Flex (visible), 1 = DisplayStyle.None (hidden)
        if (_isUserSpeaking || _activeFinalTranscribeCount > 0 || _activePartialTranscribeCount > 0)
        {
            return 0;
        }
        if (availabilityState == 6)
        {
            return 0;
        }
        return currentDisplayStyle;
    }

    public static string FormatPhoneStatusText(string currentText, int availabilityState)
    {
        DateTime now = DateTime.UtcNow;

        if (_activeFinalTranscribeCount > 0)
        {
            double elapsed = Math.Max(0.0, (now - _finalTranscribeStartUtc).TotalSeconds);
            return "TRANSCRIBING VOICE TO CALLER... (" + elapsed.ToString("0.0") + "s)";
        }

        if (_activePartialTranscribeCount > 0)
        {
            double elapsed = Math.Max(0.0, (now - _partialTranscribeStartUtc).TotalSeconds);
            return "LIVE TRANSCRIBING SPEECH... (" + elapsed.ToString("0.0") + "s)";
        }

        if (_isUserSpeaking)
        {
            double elapsed = Math.Max(0.0, (now - _speechStartUtc).TotalSeconds);
            return "LISTENING TO YOUR VOICE... (" + elapsed.ToString("0.0") + "s)";
        }

        if (availabilityState == 6)
        {
            if (_waitingStateStartUtc == DateTime.MinValue)
            {
                _waitingStateStartUtc = now;
            }
            double waitElapsed = Math.Max(0.0, (now - _waitingStateStartUtc).TotalSeconds);
            if (_lastTranscribeDurationSeconds > 0.0)
            {
                return "WAITING FOR CALLER AI (" + waitElapsed.ToString("0.0") + "s) · STT: " + _lastTranscribeDurationSeconds.ToString("0.00") + "s";
            }
            return "WAITING FOR CALLER AI... (" + waitElapsed.ToString("0.0") + "s)";
        }

        if ((availabilityState == 4 || availabilityState == 9) &&
            (_lastTranscribeDurationSeconds > 0.0 || _lastCallerAiDurationSeconds > 0.0))
        {
            string baseText = string.IsNullOrWhiteSpace(currentText) ? "SPEAK NOW OR TYPE" : currentText;
            if (_lastTranscribeDurationSeconds > 0.0 && _lastCallerAiDurationSeconds > 0.0)
            {
                return baseText + " · [STT: " + _lastTranscribeDurationSeconds.ToString("0.00") + "s | AI: " + _lastCallerAiDurationSeconds.ToString("0.00") + "s]";
            }
            if (_lastTranscribeDurationSeconds > 0.0)
            {
                return baseText + " · [LAST STT: " + _lastTranscribeDurationSeconds.ToString("0.00") + "s]";
            }
            return baseText + " · [LAST AI: " + _lastCallerAiDurationSeconds.ToString("0.00") + "s]";
        }

        return currentText ?? string.Empty;
    }

    private static string NormalizeAlphaNum(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static void EnsureConfigLoaded()
    {
        if (_configLoaded)
            return;

        lock (ConfigLock)
        {
            if (_configLoaded)
                return;
            _configLoaded = true;

            try
            {
                string configPath = null;
                if (File.Exists("ai_mod_config.json"))
                    configPath = "ai_mod_config.json";
                else if (File.Exists("surplus_config.json"))
                    configPath = "surplus_config.json";

                if (configPath != null)
                {
                    string json = File.ReadAllText(configPath);
                    JObject obj = JObject.Parse(json);
                    string provider = ((string)obj["provider"])?.Trim();
                    string apiKey = ((string)obj["api_key"])?.Trim();
                    string fallbackKey = ((string)obj["fallback_api_key"])?.Trim();
                    string endpoint = ((string)obj["endpoint"])?.Trim();
                    string modelsEndpoint = ((string)obj["models_endpoint"])?.Trim();
                    string sttEndpoint = ((string)obj["stt_endpoint"])?.Trim();
                    string sttMode = ((string)obj["stt_mode"])?.Trim();
                    string model = ((string)obj["model"])?.Trim();
                    string voiceModel = ((string)obj["voice_model"])?.Trim();
                    bool? whisperFallback = (bool?)obj["enable_whisper_fallback"];
                    bool? multimodal = (bool?)obj["multimodal_voice_in_chat"];

                    if (!string.IsNullOrEmpty(provider)) _cachedProvider = provider.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(apiKey)) _cachedApiKey = apiKey;
                    if (!string.IsNullOrEmpty(fallbackKey)) _cachedFallbackApiKey = fallbackKey;
                    if (!string.IsNullOrEmpty(endpoint)) _cachedEndpoint = endpoint;
                    if (!string.IsNullOrEmpty(modelsEndpoint)) _cachedModelsEndpoint = modelsEndpoint;
                    if (!string.IsNullOrEmpty(sttEndpoint)) _cachedSttEndpoint = sttEndpoint;
                    if (!string.IsNullOrEmpty(sttMode)) _cachedSttMode = sttMode.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(model)) _cachedModel = model;
                    if (!string.IsNullOrEmpty(voiceModel)) _cachedVoiceModel = voiceModel;
                    else if (!string.IsNullOrEmpty(model)) _cachedVoiceModel = model;
                    if (whisperFallback.HasValue) _cachedEnableWhisperFallback = whisperFallback.Value;
                    if (multimodal.HasValue) _cachedMultimodalVoiceInChat = multimodal.Value;
                }
                else
                {
                    try
                    {
                        if (UnityEngine.PlayerPrefs.HasKey("HostAI.WhisperFallback"))
                            _cachedEnableWhisperFallback = UnityEngine.PlayerPrefs.GetInt("HostAI.WhisperFallback", 0) != 0;
                    }
                    catch { }
                }

                // Fallback to standard environment variables if no key in config file
                if (string.IsNullOrEmpty(_cachedApiKey))
                {
                    string envKey = Environment.GetEnvironmentVariable("SWYF_API_KEY");
                    if (string.IsNullOrEmpty(envKey)) envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
                    if (string.IsNullOrEmpty(envKey)) envKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
                    if (string.IsNullOrEmpty(envKey)) envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                    if (!string.IsNullOrEmpty(envKey)) _cachedApiKey = envKey.Trim();
                }

                ApplyProviderDefaultsLocked();
            }
            catch
            {
                // Ignore config read errors and keep defaults
            }
        }
    }

    private static void ApplyProviderDefaultsLocked()
    {
        bool isGoogleGenAi = _cachedProvider == "gemini" ||
                             _cachedProvider == "google" ||
                             _cachedProvider == "genai" ||
                             (_cachedProvider == "auto" && !string.IsNullOrEmpty(_cachedApiKey) && _cachedApiKey.StartsWith("AIza", StringComparison.Ordinal)) ||
                             _cachedEndpoint.IndexOf("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) >= 0;

        bool isOpenAiOfficial = _cachedProvider == "openai" ||
                                (_cachedProvider == "auto" && !string.IsNullOrEmpty(_cachedApiKey) && _cachedApiKey.StartsWith("sk-", StringComparison.Ordinal)) ||
                                _cachedEndpoint.IndexOf("api.openai.com", StringComparison.OrdinalIgnoreCase) >= 0;

        if (isGoogleGenAi)
        {
            if (_cachedEndpoint == DefaultEndpoint)
                _cachedEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
            if (_cachedModelsEndpoint == DefaultModelsEndpoint)
                _cachedModelsEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/models";
            if (_cachedModel == DefaultModel)
                _cachedModel = "gemini-2.5-flash";
            if (_cachedVoiceModel == DefaultModel)
                _cachedVoiceModel = _cachedModel;
            if (_cachedSttMode == "auto")
                _cachedSttMode = "gemini_native";
        }
        else if (isOpenAiOfficial)
        {
            if (_cachedSttMode == "auto")
            {
                if (_cachedVoiceModel.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0)
                    _cachedSttMode = "chat_audio";
                else
                {
                    _cachedSttMode = "whisper_api";
                    if (_cachedVoiceModel == _cachedModel && _cachedVoiceModel.IndexOf("transcribe", StringComparison.OrdinalIgnoreCase) < 0 && _cachedVoiceModel.IndexOf("whisper", StringComparison.OrdinalIgnoreCase) < 0)
                        _cachedVoiceModel = "gpt-4o-mini-transcribe";
                }
            }
        }
        else
        {
            if (_cachedSttMode == "auto")
                _cachedSttMode = "chat_audio";
        }
    }

    private static void SaveCurrentConfigToDisk()
    {
        try
        {
            var obj = new JObject
            {
                ["provider"] = _cachedProvider,
                ["api_key"] = _cachedApiKey ?? string.Empty,
                ["fallback_api_key"] = _cachedFallbackApiKey ?? string.Empty,
                ["endpoint"] = _cachedEndpoint ?? DefaultEndpoint,
                ["models_endpoint"] = _cachedModelsEndpoint ?? DefaultModelsEndpoint,
                ["stt_endpoint"] = _cachedSttEndpoint ?? DefaultSttEndpoint,
                ["stt_mode"] = _cachedSttMode ?? "chat_audio",
                ["model"] = _cachedModel ?? DefaultModel,
                ["voice_model"] = _cachedVoiceModel ?? _cachedModel ?? DefaultModel,
                ["enable_whisper_fallback"] = _cachedEnableWhisperFallback
            };
            string formatted = obj.ToString(Formatting.Indented);
            File.WriteAllText("ai_mod_config.json", formatted, Encoding.UTF8);
            if (File.Exists("surplus_config.json"))
            {
                File.WriteAllText("surplus_config.json", formatted, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    public static bool IsWhisperFallbackEnabled()
    {
        EnsureConfigLoaded();
        return _cachedEnableWhisperFallback || string.Equals(_cachedSttMode, "local", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldSkipWhisperModelLoad(WhisperManager recognizer)
    {
        if (!IsWhisperFallbackEnabled())
            return true;
        return recognizer != null && recognizer.IsLoaded;
    }

    public static bool TrySetWhisperFallbackFromUi(int dropdownIndex)
    {
        EnsureConfigLoaded();
        bool enableFallback = dropdownIndex == 1;
        lock (ConfigLock)
        {
            _cachedEnableWhisperFallback = enableFallback;
            SaveCurrentConfigToDisk();
        }
        try
        {
            UnityEngine.PlayerPrefs.SetInt("HostAI.WhisperFallback", enableFallback ? 1 : 0);
            UnityEngine.PlayerPrefs.Save();
        }
        catch { }
        return true;
    }

    public static bool TrySetApiKeyFromUi(string value)
    {
        EnsureConfigLoaded();
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length > 512)
            return false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (char.IsWhiteSpace(trimmed[i]) || char.IsControl(trimmed[i]))
                return false;
        }

        lock (ConfigLock)
        {
            _cachedApiKey = trimmed;
            _usingFallbackKey = false;
            if (_cachedProvider == "auto" && !string.IsNullOrEmpty(trimmed))
            {
                ApplyProviderDefaultsLocked();
            }
            SaveCurrentConfigToDisk();
        }

        try
        {
            if (string.IsNullOrEmpty(trimmed))
            {
                UnityEngine.PlayerPrefs.DeleteKey("HostAI.OpenRouterKey");
                UnityEngine.PlayerPrefs.DeleteKey("HostAiSettings.OpenRouterApiKey");
            }
            else
            {
                UnityEngine.PlayerPrefs.SetString("HostAI.OpenRouterKey", trimmed);
                UnityEngine.PlayerPrefs.SetString("HostAiSettings.OpenRouterApiKey", trimmed);
            }
            UnityEngine.PlayerPrefs.Save();
        }
        catch { }
        return true;
    }

    public static bool TrySetModelFromUi(OpenRouterModel model)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.Id))
            return false;
        return TrySetChatModelFromUi(model.Id);
    }

    public static bool TrySetChatModelFromUi(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;
        EnsureConfigLoaded();
        string newId = modelId.Trim();
        lock (ConfigLock)
        {
            _cachedModel = newId;
            SaveCurrentConfigToDisk();
        }
        try
        {
            var modelObj = CreateDefaultModel(newId);
            UnityEngine.PlayerPrefs.SetString("HostAI.OpenRouterModel", JsonConvert.SerializeObject(modelObj));
            UnityEngine.PlayerPrefs.Save();
        }
        catch { }
        return true;
    }

    public static bool TrySetVoiceModelFromUi(string voiceModelId)
    {
        if (string.IsNullOrWhiteSpace(voiceModelId))
            return false;
        EnsureConfigLoaded();
        string newId = voiceModelId.Trim();
        lock (ConfigLock)
        {
            _cachedVoiceModel = newId;
            if (newId.StartsWith("whisper", StringComparison.OrdinalIgnoreCase) ||
                newId.IndexOf("transcribe", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _cachedSttMode = "whisper_api";
            }
            else if (string.Equals(_cachedProvider, "gemini", StringComparison.OrdinalIgnoreCase))
            {
                _cachedSttMode = "gemini_native";
            }
            else
            {
                _cachedSttMode = "chat_audio";
            }
            SaveCurrentConfigToDisk();
        }
        return true;
    }

    public static string GetKeyStatusText()
    {
        EnsureConfigLoaded();
        string key = GetApiKey();
        string whisperState = IsWhisperFallbackEnabled()
            ? "Whisper Fallback: ON"
            : "Whisper Fallback: OFF (~400MB Saved)";
        string modelsInfo = "Chat: " + GetChatModel() + " | Voice: " + GetVoiceModel();

        if (string.IsNullOrWhiteSpace(key))
        {
            return "NO API TOKEN SET — Paste API Key below | " + modelsInfo + " | " + whisperState;
        }

        string masked = key.Length > 8
            ? key.Substring(0, 4) + "..." + key.Substring(key.Length - 4)
            : "Configured";
        return "API Token (" + masked + ") | " + modelsInfo + " | " + whisperState;
    }

    public static string InterceptMenuText(string key, string localized)
    {
        if (string.IsNullOrEmpty(key))
            return localized ?? string.Empty;

        if (string.Equals(key, "HOST", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "Host Settings", StringComparison.OrdinalIgnoreCase))
        {
            return "AI MOD & HOST";
        }
        if (string.Equals(key, "AI Backend", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "Backend", StringComparison.OrdinalIgnoreCase))
        {
            return "Whisper Offline Fallback (RAM Saver)";
        }
        if (string.Equals(key, "OpenRouter API Key", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "API Key", StringComparison.OrdinalIgnoreCase))
        {
            return "AI Mod API Token (OpenAI / Gemini / Custom)";
        }
        if (string.Equals(key, "Paste your OpenRouter API key.", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "Key saved on this computer.", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("MOD INSTALLED", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("MOD ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            return GetKeyStatusText();
        }

        return string.IsNullOrEmpty(localized) ? key : localized;
    }

    public static void OnPauseMenuInitialized(object rootObj)
    {
        try
        {
            VisualElement root = rootObj as VisualElement;
            if (root == null)
                return;

            EnsureConfigLoaded();
            InitLiveDebugServer();

            // 1. Rename the HOST tab button to "AI MOD & HOST" so players know the Host tab also has AI Mod controls
            Button hostTabBtn = root.Q<Button>("host-tab-button");
            if (hostTabBtn != null)
            {
                hostTabBtn.text = "AI MOD & HOST";
            }

            // 2. Also update the labels inside the Host AI Settings section if present
            VisualElement hostAiSection = root.Q<VisualElement>("host-ai-settings-section");
            if (hostAiSection != null)
            {
                Label keyStatus = root.Q<Label>("host-ai-key-status");
                if (keyStatus != null)
                {
                    keyStatus.text = GetKeyStatusText();
                }
            }

            // 3. Inject our dedicated "AI MOD SETTINGS (API TOKEN & WHISPER FALLBACK)" section right at the top of GENERAL tab!
            VisualElement generalScreen = root.Q<VisualElement>("general-screen");
            if (generalScreen != null)
            {
                VisualElement existingPanel = generalScreen.Q<VisualElement>("swyf-ai-mod-settings-panel");
                if (existingPanel != null)
                {
                    existingPanel.RemoveFromHierarchy();
                }

                VisualElement modContainer = new VisualElement();
                modContainer.name = "swyf-ai-mod-settings-panel";
                modContainer.AddToClassList("openrouter-tuning");
                modContainer.style.marginBottom = 18f;
                modContainer.style.paddingTop = 10f;
                modContainer.style.paddingBottom = 12f;
                modContainer.style.paddingLeft = 14f;
                modContainer.style.paddingRight = 14f;
                modContainer.style.backgroundColor = new UnityEngine.Color(0.08f, 0.12f, 0.18f, 0.65f);
                modContainer.style.borderTopWidth = 1f;
                modContainer.style.borderBottomWidth = 1f;
                modContainer.style.borderLeftWidth = 1f;
                modContainer.style.borderRightWidth = 1f;
                modContainer.style.borderTopColor = new UnityEngine.Color(0.25f, 0.55f, 0.95f, 0.6f);
                modContainer.style.borderBottomColor = new UnityEngine.Color(0.25f, 0.55f, 0.95f, 0.6f);
                modContainer.style.borderLeftColor = new UnityEngine.Color(0.25f, 0.55f, 0.95f, 0.6f);
                modContainer.style.borderRightColor = new UnityEngine.Color(0.25f, 0.55f, 0.95f, 0.6f);

                Label headerLabel = new Label("AI MOD SETTINGS (OPENAI / GEMINI / WHISPER)");
                headerLabel.AddToClassList("section-title");
                headerLabel.style.marginBottom = 6f;
                headerLabel.style.unityFontStyleAndWeight = UnityEngine.FontStyle.Bold;
                modContainer.Add(headerLabel);

                Label statusLabel = new Label(GetKeyStatusText());
                statusLabel.name = "swyf-mod-status-label";
                statusLabel.AddToClassList("setting-help");
                statusLabel.style.marginBottom = 10f;
                statusLabel.style.color = new UnityEngine.Color(0.55f, 0.92f, 0.68f, 1f);
                modContainer.Add(statusLabel);

                // Row 1: API Token (OpenAI / Google Gemini / OpenRouter / Custom)
                VisualElement tokenRow = CreateModSettingRow(
                    "API TOKEN (OPENAI / GEMINI)",
                    "Paste your API key (sk-... or AIza...). Saved automatically to ai_mod_config.json."
                );

                TextField tokenInput = new TextField();
                tokenInput.name = "swyf-mod-api-key-input";
                tokenInput.AddToClassList("byok-field");
                tokenInput.style.minWidth = 280f;
                tokenInput.SetValueWithoutNotify(GetApiKey());

                tokenInput.RegisterValueChangedCallback(evt =>
                {
                    TrySetApiKeyFromUi(evt.newValue);
                    statusLabel.text = GetKeyStatusText();
                    Label hostStatus = root.Q<Label>("host-ai-key-status");
                    if (hostStatus != null) hostStatus.text = GetKeyStatusText();
                });

                tokenRow.Add(tokenInput);
                modContainer.Add(tokenRow);

                // Row 2: Whisper Offline Fallback (RAM Saver)
                VisualElement whisperRow = CreateModSettingRow(
                    "WHISPER OFFLINE FALLBACK",
                    "Keep Disabled to skip loading local Whisper into RAM (~400MB saved). Enable for offline backup."
                );

                var whisperChoices = new List<string>
                {
                    "Disabled (Saves ~400MB RAM)",
                    "Enabled (Load Local Whisper)"
                };
                DropdownField whisperDropdown = new DropdownField();
                whisperDropdown.name = "swyf-mod-whisper-dropdown";
                whisperDropdown.AddToClassList("setting-dropdown");
                whisperDropdown.choices = whisperChoices;
                whisperDropdown.SetValueWithoutNotify(IsWhisperFallbackEnabled() ? whisperChoices[1] : whisperChoices[0]);

                whisperDropdown.RegisterValueChangedCallback(evt =>
                {
                    bool enable = evt.newValue != null && evt.newValue.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase);
                    TrySetWhisperFallbackFromUi(enable ? 1 : 0);
                    statusLabel.text = GetKeyStatusText();
                    Label hostStatus = root.Q<Label>("host-ai-key-status");
                    if (hostStatus != null) hostStatus.text = GetKeyStatusText();
                });

                whisperRow.Add(whisperDropdown);
                modContainer.Add(whisperRow);

                // Apply the exact native dropdown surface & anchor classes used by MainMenuToolkitController.ConfigureDropdownHover
                ApplyMenuDropdownStyle(whisperDropdown);

                // Row 3: Chat AI Model ID (Caller Dialogue & Brain)
                VisualElement chatModelRow = CreateModSettingRow(
                    "CHAT AI MODEL ID (DIALOGUE)",
                    "Model for caller AI dialogue & logic (e.g. gemini-3.8-flash, gemini-2.5-pro, gpt-4o-mini)."
                );

                TextField chatModelInput = new TextField();
                chatModelInput.name = "swyf-mod-chat-model-input";
                chatModelInput.AddToClassList("byok-field");
                chatModelInput.style.minWidth = 280f;
                chatModelInput.SetValueWithoutNotify(GetChatModel());

                chatModelInput.RegisterValueChangedCallback(evt =>
                {
                    if (!string.IsNullOrWhiteSpace(evt.newValue))
                    {
                        TrySetChatModelFromUi(evt.newValue.Trim());
                        statusLabel.text = GetKeyStatusText();
                        Label hostStatus = root.Q<Label>("host-ai-key-status");
                        if (hostStatus != null) hostStatus.text = GetKeyStatusText();
                    }
                });

                chatModelRow.Add(chatModelInput);
                modContainer.Add(chatModelRow);

                // Row 4: Voice AI Model ID (Speech-to-Text Transcription)
                VisualElement voiceModelRow = CreateModSettingRow(
                    "VOICE AI MODEL ID (STT)",
                    "Model for live voice transcription (e.g. gemini-3.8-flash, gemini-2.5-flash, whisper-1)."
                );

                TextField voiceModelInput = new TextField();
                voiceModelInput.name = "swyf-mod-voice-model-input";
                voiceModelInput.AddToClassList("byok-field");
                voiceModelInput.style.minWidth = 280f;
                voiceModelInput.SetValueWithoutNotify(GetVoiceModel());

                voiceModelInput.RegisterValueChangedCallback(evt =>
                {
                    if (!string.IsNullOrWhiteSpace(evt.newValue))
                    {
                        TrySetVoiceModelFromUi(evt.newValue.Trim());
                        statusLabel.text = GetKeyStatusText();
                        Label hostStatus = root.Q<Label>("host-ai-key-status");
                        if (hostStatus != null) hostStatus.text = GetKeyStatusText();

                        DropdownField speechRecDd = root.Q<DropdownField>("speech-recognition-dropdown");
                        if (speechRecDd != null)
                        {
                            string newChoice = "AI Mod Cloud STT (" + GetVoiceModel() + ")";
                            speechRecDd.formatSelectedValueCallback = val => (val == "Local Whisper") ? "Local Whisper" : ("AI Mod Cloud STT (" + GetVoiceModel() + ")");
                            speechRecDd.formatListItemCallback = val => (val == "Local Whisper") ? "Local Whisper" : ("AI Mod Cloud STT (" + GetVoiceModel() + ")");
                            speechRecDd.choices = new List<string> { newChoice, "Local Whisper" };
                            speechRecDd.SetValueWithoutNotify(newChoice);
                        }
                    }
                });

                voiceModelRow.Add(voiceModelInput);
                modContainer.Add(voiceModelRow);

                // Insert at the very top of general-screen (right above SPEECH)
                generalScreen.Insert(0, modContainer);
            }

            // 4. Ensure all dropdowns in general-screen and voice-chat-screen have valid non-empty values displayed
            FixEmptyDropdownsInRoot(root);
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning("[SurplusBridge] OnPauseMenuInitialized error: " + ex.Message);
        }
    }

    private static void ApplyMenuDropdownStyle(DropdownField item)
    {
        if (item == null) return;
        item.AddToClassList("menu-dropdown");
        VisualElement inputEl = item.Q(null, "unity-base-popup-field__input");
        if (inputEl == null || inputEl.ClassListContains("menu-dropdown-anchor"))
            return;

        VisualElement surface = new VisualElement
        {
            pickingMode = PickingMode.Ignore
        };
        surface.AddToClassList("menu-dropdown-surface");
        var children = new List<VisualElement>();
        foreach (VisualElement child in inputEl.Children())
        {
            children.Add(child);
        }
        foreach (VisualElement child in children)
        {
            surface.Add(child);
        }
        inputEl.Add(surface);
        inputEl.AddToClassList("menu-dropdown-anchor");
    }

    public static void FixEmptyDropdownsInRoot(VisualElement root)
    {
        if (root == null)
            return;

        try
        {
            // Hide any Deepgram offer / Deepgram key panel added in 10/18/2026 update so it doesn't clutter the UI
            VisualElement deepgramOffer = root.Q<VisualElement>("deepgram-offer");
            if (deepgramOffer != null) deepgramOffer.style.display = DisplayStyle.None;
            VisualElement deepgramPanel = root.Q<VisualElement>("deepgram-panel");
            if (deepgramPanel != null) deepgramPanel.style.display = DisplayStyle.None;

            // Specifically populate SPEECH recognition dropdown and override 10/18/2026 BindSpeechRecognition's "Automatic" format callback
            DropdownField speechRecDropdown = root.Q<DropdownField>("speech-recognition-dropdown")
                ?? root.Q<DropdownField>("speech-provider-dropdown")
                ?? root.Q<DropdownField>("stt-dropdown");
            if (speechRecDropdown != null)
            {
                string activeSttLabel = "AI Mod Cloud STT (" + GetVoiceModel() + ")";
                speechRecDropdown.formatSelectedValueCallback = val => (val == "Local Whisper") ? "Local Whisper" : ("AI Mod Cloud STT (" + GetVoiceModel() + ")");
                speechRecDropdown.formatListItemCallback = val => (val == "Local Whisper") ? "Local Whisper" : ("AI Mod Cloud STT (" + GetVoiceModel() + ")");
                speechRecDropdown.choices = new List<string> { activeSttLabel, "Local Whisper" };
                speechRecDropdown.SetValueWithoutNotify(activeSttLabel);
            }

            DropdownField agcDropdown = root.Q<DropdownField>("automatic-gain-control-dropdown")
                ?? root.Q<DropdownField>("agc-dropdown")
                ?? root.Q<DropdownField>("gain-control-dropdown");
            if (agcDropdown != null)
            {
                if (agcDropdown.choices == null || agcDropdown.choices.Count == 0)
                {
                    agcDropdown.choices = new List<string> { "On", "Off" };
                }
                if (string.IsNullOrWhiteSpace(agcDropdown.value))
                {
                    agcDropdown.SetValueWithoutNotify(agcDropdown.choices[0]);
                }
            }

            DropdownField browserPrivacyDropdown = root.Q<DropdownField>("browser-privacy-dropdown");
            if (browserPrivacyDropdown != null)
            {
                if (browserPrivacyDropdown.choices == null || browserPrivacyDropdown.choices.Count == 0)
                {
                    browserPrivacyDropdown.choices = new List<string> { "Standard", "Strict" };
                }
                if (string.IsNullOrWhiteSpace(browserPrivacyDropdown.value))
                {
                    browserPrivacyDropdown.SetValueWithoutNotify(browserPrivacyDropdown.choices[0]);
                }
            }

            // General sweep: if any DropdownField in the settings tree has a blank value or blank text element, repair it
            var allDropdowns = root.Query<DropdownField>().ToList();
            foreach (DropdownField dd in allDropdowns)
            {
                if (dd == null) continue;
                ApplyMenuDropdownStyle(dd);
                string n = dd.name ?? string.Empty;

                if (dd.choices == null || dd.choices.Count == 0)
                {
                    if (n.IndexOf("gain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("agc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("toggle", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        dd.choices = new List<string> { "On", "Off" };
                    }
                    else if (n.IndexOf("speech", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             n.IndexOf("recognition", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        dd.choices = new List<string> { "AI Mod Cloud STT (" + GetVoiceModel() + ")", "Local Whisper" };
                    }
                    else if (n.IndexOf("privacy", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        dd.choices = new List<string> { "Standard", "Strict" };
                    }
                    else
                    {
                        dd.choices = new List<string> { "Off", "On" };
                    }
                }

                if (dd.choices != null && dd.choices.Count > 0 && string.IsNullOrWhiteSpace(dd.value))
                {
                    dd.SetValueWithoutNotify(dd.choices[0]);
                }

                TextElement popupText = dd.Q<TextElement>(null, "unity-base-popup-field__text");
                if (popupText != null && string.IsNullOrWhiteSpace(popupText.text) && !string.IsNullOrWhiteSpace(dd.value))
                {
                    popupText.text = dd.value;
                }
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogWarning("[SurplusBridge] FixEmptyDropdownsInRoot error: " + ex.Message);
        }
    }

    private static VisualElement CreateModSettingRow(string title, string subtitle)
    {
        VisualElement row = new VisualElement();
        row.AddToClassList("setting-row");
        row.AddToClassList("openrouter-tuning-row");

        VisualElement copy = new VisualElement();
        copy.AddToClassList("setting-copy");

        Label titleLabel = new Label(title);
        titleLabel.AddToClassList("setting-label");
        copy.Add(titleLabel);

        if (!string.IsNullOrEmpty(subtitle))
        {
            Label helpLabel = new Label(subtitle);
            helpLabel.AddToClassList("setting-help");
            copy.Add(helpLabel);
        }

        row.Add(copy);
        return row;
    }

    // ========================================================================
    // LIVE DIAGNOSTIC & UI INSPECTOR SERVER (http://127.0.0.1:47890/)
    // ========================================================================
    private static bool _liveDebugInitialized = false;
    private static readonly object LiveDebugLock = new object();
    private static readonly ConcurrentQueue<Action> MainThreadActions = new ConcurrentQueue<Action>();
    private static readonly List<JObject> CapturedLogs = new List<JObject>();
    private static TcpListener _tcpListener;

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterSceneLoad)]
    public static void InitLiveDebugServer()
    {
        lock (LiveDebugLock)
        {
            if (_liveDebugInitialized)
                return;
            _liveDebugInitialized = true;
        }

        try
        {
            EnsureConfigLoaded();
            UnityEngine.Application.logMessageReceivedThreaded += OnUnityLogMessageReceived;
            AppendLiveDebugLog("INFO", "SurplusBridge Live Debug Server initializing...", "");

            var go = new UnityEngine.GameObject("SurplusLiveDebugRunner");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            go.AddComponent<SurplusLiveDebugBehaviour>();

            Thread serverThread = new Thread(RunHttpServerLoop)
            {
                IsBackground = true,
                Name = "SurplusLiveDebugHttp"
            };
            serverThread.Start();
        }
        catch (Exception ex)
        {
            AppendLiveDebugLog("ERROR", "InitLiveDebugServer failed: " + ex.Message, ex.StackTrace);
        }
    }

    private static void OnUnityLogMessageReceived(string condition, string stackTrace, UnityEngine.LogType type)
    {
        string level = type.ToString().ToUpperInvariant();
        var entry = new JObject
        {
            ["time_utc"] = DateTime.UtcNow.ToString("o"),
            ["type"] = level,
            ["message"] = condition ?? string.Empty,
            ["stack"] = (type == UnityEngine.LogType.Exception || type == UnityEngine.LogType.Error) ? (stackTrace ?? string.Empty) : string.Empty
        };

        lock (CapturedLogs)
        {
            CapturedLogs.Add(entry);
            if (CapturedLogs.Count > 300)
                CapturedLogs.RemoveAt(0);
        }

        if (type == UnityEngine.LogType.Exception || type == UnityEngine.LogType.Error ||
            (condition != null && condition.IndexOf("SurplusBridge", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            AppendLiveDebugLog(level, condition, stackTrace);
        }
    }

    private static void AppendLiveDebugLog(string level, string message, string stack)
    {
        try
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] [" + level + "] " + message +
                (string.IsNullOrWhiteSpace(stack) ? "" : "\r\n" + stack.Trim()) + "\r\n";
            lock (LiveDebugLock)
            {
                using (var sw = new StreamWriter("swyf_live_debug.log", true, Encoding.UTF8))
                {
                    sw.Write(line);
                }
            }
        }
        catch { }
    }

    internal static void PumpMainThreadActions()
    {
        while (MainThreadActions.TryDequeue(out Action act))
        {
            try { act(); } catch { }
        }
    }

    private static T RunOnMainThread<T>(Func<T> func, int timeoutMs = 4000)
    {
        var tcs = new TaskCompletionSource<T>();
        MainThreadActions.Enqueue(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        if (tcs.Task.Wait(timeoutMs))
            return tcs.Task.Result;
        throw new TimeoutException("Main thread action timed out");
    }

    private static void RunHttpServerLoop()
    {
        try
        {
            _tcpListener = new TcpListener(IPAddress.Loopback, 47890);
            _tcpListener.Start();
            AppendLiveDebugLog("INFO", "Listening on http://127.0.0.1:47890/", "");

            while (true)
            {
                TcpClient client = _tcpListener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => HandleTcpClient(client));
            }
        }
        catch (Exception ex)
        {
            AppendLiveDebugLog("WARN", "TcpListener stopped: " + ex.Message, "");
        }
    }

    private static void HandleTcpClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
                {
                    string requestLine = reader.ReadLine();
                    if (string.IsNullOrEmpty(requestLine))
                        return;

                    // Drain headers
                    string headerLine;
                    while (!string.IsNullOrEmpty(headerLine = reader.ReadLine())) { }

                    string[] parts = requestLine.Split(' ');
                    string rawTarget = parts.Length >= 2 ? parts[1] : "/";
                    string path = rawTarget;
                    string query = string.Empty;
                    int qIdx = rawTarget.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        path = rawTarget.Substring(0, qIdx);
                        query = rawTarget.Substring(qIdx + 1);
                    }
                    path = path.ToLowerInvariant();

                    JObject resp;
                    if (path == "/status" || path == "/")
                    {
                        resp = RunOnMainThread(() => new JObject
                        {
                            ["ok"] = true,
                            ["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                            ["fps"] = Math.Round(1.0f / Math.Max(0.0001f, UnityEngine.Time.unscaledDeltaTime), 1),
                            ["settings_open"] = SettingsUi.Instance != null && SettingsUi.Instance.IsOpen,
                            ["provider"] = _cachedProvider,
                            ["model"] = _cachedModel,
                            ["voice_model"] = _cachedVoiceModel,
                            ["stt_mode"] = _cachedSttMode,
                            ["whisper_fallback"] = IsWhisperFallbackEnabled(),
                            ["has_api_key"] = !string.IsNullOrWhiteSpace(GetApiKey()),
                            ["key_status_text"] = GetKeyStatusText()
                        });
                    }
                    else if (path == "/logs")
                    {
                        JArray arr = new JArray();
                        lock (CapturedLogs)
                        {
                            foreach (var item in CapturedLogs)
                                arr.Add(item);
                        }
                        resp = new JObject { ["ok"] = true, ["count"] = arr.Count, ["logs"] = arr };
                    }
                    else if (path == "/open-settings")
                    {
                        bool isHostTab = query.IndexOf("tab=host", StringComparison.OrdinalIgnoreCase) >= 0;
                        resp = RunOnMainThread(() =>
                        {
                            if (SettingsUi.Instance != null)
                            {
                                SettingsUi.Instance.OpenSettingsOnlyModal();
                            }
                            var pm = UnityEngine.Object.FindFirstObjectByType<PauseMenuToolkitController>();
                            if (pm != null)
                            {
                                var doc = pm.GetComponent<UIDocument>();
                                var root = doc != null ? doc.rootVisualElement : null;
                                if (root != null)
                                {
                                    OnPauseMenuInitialized(root);
                                    string targetScreen = isHostTab ? "host-settings-screen" : "general-screen";
                                    string targetTabBtn = isHostTab ? "host-tab-button" : "general-tab-button";
                                    string[] allScreens = new[] { "general-screen", "keybinds-screen", "graphics-screen", "audio-screen", "voice-chat-screen", "host-settings-screen", "change-log-screen", "credits-screen" };
                                    string[] allTabs = new[] { "general-tab-button", "keybinds-tab-button", "graphics-tab-button", "audio-tab-button", "voice-chat-tab-button", "host-tab-button", "change-log-tab-button", "credits-tab-button" };
                                    foreach (string sn in allScreens)
                                    {
                                        var sEl = root.Q<VisualElement>(sn);
                                        if (sEl != null) sEl.EnableInClassList("settings-screen--active", sn == targetScreen);
                                    }
                                    foreach (string tn in allTabs)
                                    {
                                        var tEl = root.Q<Button>(tn);
                                        if (tEl != null) tEl.EnableInClassList("tab-button--active", tn == targetTabBtn);
                                    }
                                }
                            }
                            return new JObject
                            {
                                ["ok"] = true,
                                ["settings_open"] = SettingsUi.Instance != null && SettingsUi.Instance.IsOpen,
                                ["tab"] = isHostTab ? "host" : "general"
                            };
                        });
                    }
                    else if (path == "/ui")
                    {
                        resp = RunOnMainThread(() => DumpAllUiDocuments());
                    }
                    else if (path == "/screenshot")
                    {
                        string outPath = Path.GetFullPath("swyf_live_screenshot.png");
                        var snapTcs = new TaskCompletionSource<bool>();
                        MainThreadActions.Enqueue(() =>
                        {
                            try
                            {
                                if (File.Exists(outPath)) File.Delete(outPath);
                                if (_debugBehaviourInstance != null)
                                {
                                    _debugBehaviourInstance.StartCoroutine(CaptureFrameCoroutine(outPath, snapTcs));
                                }
                                else
                                {
                                    snapTcs.TrySetResult(false);
                                }
                            }
                            catch (Exception ex)
                            {
                                snapTcs.TrySetException(ex);
                            }
                        });
                        snapTcs.Task.Wait(4000);
                        resp = new JObject
                        {
                            ["ok"] = File.Exists(outPath),
                            ["screenshot_path"] = outPath,
                            ["exists"] = File.Exists(outPath)
                        };
                    }
                    else
                    {
                        resp = new JObject { ["ok"] = false, ["error"] = "Unknown endpoint: " + path };
                    }

                    WriteHttpResponse(stream, 200, "OK", resp.ToString(Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                try
                {
                    using (NetworkStream stream = client.GetStream())
                    {
                        string errBody = new JObject { ["ok"] = false, ["error"] = ex.ToString() }.ToString(Formatting.Indented);
                        WriteHttpResponse(stream, 500, "Internal Server Error", errBody);
                    }
                }
                catch { }
            }
        }
    }

    private static System.Collections.IEnumerator CaptureFrameCoroutine(string outPath, TaskCompletionSource<bool> tcs)
    {
        yield return new UnityEngine.WaitForEndOfFrame();
        try
        {
            int w = UnityEngine.Screen.width;
            int h = UnityEngine.Screen.height;
            var tex = new UnityEngine.Texture2D(w, h, UnityEngine.TextureFormat.RGB24, false);
            tex.ReadPixels(new UnityEngine.Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            byte[] png = UnityEngine.ImageConversion.EncodeToPNG(tex);
            UnityEngine.Object.Destroy(tex);
            if (png != null && png.Length > 0)
            {
                File.WriteAllBytes(outPath, png);
                tcs.TrySetResult(true);
            }
            else
            {
                tcs.TrySetResult(false);
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    private static void WriteHttpResponse(NetworkStream stream, int statusCode, string statusText, string jsonBody)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody ?? "{}");
        string header =
            "HTTP/1.1 " + statusCode + " " + statusText + "\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Content-Length: " + bodyBytes.Length + "\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    private static JObject DumpAllUiDocuments()
    {
        var docs = UnityEngine.Object.FindObjectsByType<UIDocument>(UnityEngine.FindObjectsInactive.Include, UnityEngine.FindObjectsSortMode.None);
        var docArray = new JArray();
        foreach (var doc in docs)
        {
            if (doc == null) continue;
            var root = doc.rootVisualElement;
            var docObj = new JObject
            {
                ["gameObject"] = doc.gameObject.name,
                ["activeInHierarchy"] = doc.gameObject.activeInHierarchy,
                ["hasRoot"] = root != null
            };

            if (root != null)
            {
                var dropdowns = new JArray();
                foreach (var dd in root.Query<DropdownField>().ToList())
                {
                    var txtEl = dd.Q<TextElement>(null, "unity-base-popup-field__text");
                    var labelEl = dd.parent?.Q<Label>(null, "setting-label");
                    dropdowns.Add(new JObject
                    {
                        ["name"] = dd.name ?? "",
                        ["label"] = labelEl != null ? labelEl.text : "",
                        ["value"] = dd.value ?? "",
                        ["displayedText"] = txtEl != null ? txtEl.text : "",
                        ["choicesCount"] = dd.choices != null ? dd.choices.Count : 0,
                        ["choices"] = dd.choices != null ? new JArray(dd.choices) : new JArray(),
                        ["visible"] = dd.visible && dd.resolvedStyle.display != DisplayStyle.None,
                        ["rect"] = $"{Math.Round(dd.worldBound.x)},{Math.Round(dd.worldBound.y)},{Math.Round(dd.worldBound.width)}x{Math.Round(dd.worldBound.height)}"
                    });
                }
                docObj["dropdowns"] = dropdowns;

                var textFields = new JArray();
                foreach (var tf in root.Query<TextField>().ToList())
                {
                    textFields.Add(new JObject
                    {
                        ["name"] = tf.name ?? "",
                        ["value"] = tf.value ?? "",
                        ["visible"] = tf.visible && tf.resolvedStyle.display != DisplayStyle.None,
                        ["rect"] = $"{Math.Round(tf.worldBound.x)},{Math.Round(tf.worldBound.y)},{Math.Round(tf.worldBound.width)}x{Math.Round(tf.worldBound.height)}"
                    });
                }
                docObj["textFields"] = textFields;

                var modPanel = root.Q<VisualElement>("swyf-ai-mod-settings-panel");
                docObj["modSettingsPanelInjected"] = modPanel != null;
                if (modPanel != null)
                {
                    docObj["modSettingsPanelRect"] = $"{Math.Round(modPanel.worldBound.x)},{Math.Round(modPanel.worldBound.y)},{Math.Round(modPanel.worldBound.width)}x{Math.Round(modPanel.worldBound.height)}";
                }
            }
            docArray.Add(docObj);
        }

        return new JObject
        {
            ["ok"] = true,
            ["documents"] = docArray
        };
    }

    private static SurplusLiveDebugBehaviour _debugBehaviourInstance;

    public sealed class SurplusLiveDebugBehaviour : UnityEngine.MonoBehaviour
    {
        private void Awake()
        {
            _debugBehaviourInstance = this;
        }

        private void Update()
        {
            _debugBehaviourInstance = this;
            PumpMainThreadActions();
        }
    }

    public static string GetEndpoint()
    {
        EnsureConfigLoaded();
        return _cachedEndpoint;
    }

    public static string GetModelsEndpoint()
    {
        EnsureConfigLoaded();
        return _cachedModelsEndpoint;
    }

    public static string GetChatModel()
    {
        EnsureConfigLoaded();
        return _cachedModel;
    }

    public static string GetVoiceModel()
    {
        EnsureConfigLoaded();
        return _cachedVoiceModel;
    }

    public static bool IsLegacyOrInvalidModelId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return true;
        EnsureConfigLoaded();
        if (string.Equals(id.Trim(), _cachedModel, StringComparison.OrdinalIgnoreCase))
            return false;
        if (id.IndexOf("deepseek", StringComparison.OrdinalIgnoreCase) >= 0 ||
            id.IndexOf("ling-", StringComparison.OrdinalIgnoreCase) >= 0 ||
            id.IndexOf("inclusionai", StringComparison.OrdinalIgnoreCase) >= 0 ||
            id.IndexOf("qwen", StringComparison.OrdinalIgnoreCase) >= 0 ||
            id.IndexOf("llama", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }
        return false;
    }

    public static string GetApiKey()
    {
        EnsureConfigLoaded();
        if (_usingFallbackKey && !string.IsNullOrEmpty(_cachedFallbackApiKey))
            return _cachedFallbackApiKey;

        if (!string.IsNullOrEmpty(_cachedApiKey))
            return _cachedApiKey;

        try
        {
            string prefsKey = UnityEngine.PlayerPrefs.GetString("HostAI.OpenRouterKey", string.Empty);
            if (string.IsNullOrWhiteSpace(prefsKey))
                prefsKey = UnityEngine.PlayerPrefs.GetString("HostAiSettings.OpenRouterApiKey", string.Empty);
            if (!string.IsNullOrWhiteSpace(prefsKey))
                return prefsKey.Trim();
        }
        catch
        {
        }

        return string.Empty;
    }

    public static string GetFallbackApiKey()
    {
        EnsureConfigLoaded();
        return _cachedFallbackApiKey;
    }

    public static void ActivateFallbackKey()
    {
        _usingFallbackKey = true;
    }

    public static OpenRouterModel CreateDefaultModel(string modelId = null)
    {
        EnsureConfigLoaded();
        string id = (string.IsNullOrWhiteSpace(modelId) || IsLegacyOrInvalidModelId(modelId))
            ? _cachedModel
            : modelId.Trim();
        var m = new OpenRouterModel();
        m.Id = id;
        m.Name = id + " (Active AI Mod)";
        m.SupportedParameters = new string[]
        {
            "temperature",
            "top_p",
            "max_tokens",
            "response_format",
            "structured_outputs",
            "reasoning",
            "tools",
            "tool_choice",
            "seed",
            "stop"
        };
        m.MaxCompletionTokens = 65536;
        m.ReasoningMandatory = false;
        m.ReasoningEfforts = new string[] { "none", "minimal", "low", "medium", "high" };
        m.ReasoningSupportsTokenBudget = true;
        m.PriceLabel = "Voice + Chat Ready | Uncapped Context";
        return m;
    }

    public static OpenRouterModel GetSelectedModel()
    {
        EnsureConfigLoaded();
        return CreateDefaultModel(_cachedModel);
    }

    public static List<OpenRouterModel> GetFavoriteModels()
    {
        EnsureConfigLoaded();
        var list = new List<OpenRouterModel> { CreateDefaultModel(_cachedModel) };
        string[] presets = new[] { "gemini-2.5-flash", "gemini-2.0-flash", "gpt-4o-mini", "gpt-4o", "gemini-3.8-flash" };
        foreach (string p in presets)
        {
            if (!string.Equals(p, _cachedModel, StringComparison.OrdinalIgnoreCase))
                list.Add(CreateDefaultModel(p));
        }
        return list;
    }

    public static List<OpenRouterModel> ParseModelsCatalog(string json)
    {
        EnsureConfigLoaded();
        return GetFavoriteModels();
    }

    public static void PrepareOutboundBody(JObject body)
    {
        if (body == null)
            return;

        EnsureConfigLoaded();
        lock (StatusLock)
        {
            _callerAiStartUtc = DateTime.UtcNow;
            _uiDirty = true;
        }

        // Remove OpenRouter-only routing block so standard OpenAI / GenAI endpoints receive a clean payload
        body.Remove("provider");

        string modelId = (string)body["model"];
        if (IsLegacyOrInvalidModelId(modelId))
        {
            modelId = _cachedModel;
            body["model"] = modelId;
        }

        // Remove low token caps (game defaults to 80/128/1024 to save money); allow full 4096+ output tokens
        JToken maxTokensToken = body["max_tokens"];
        if (maxTokensToken == null || (maxTokensToken.Type == JTokenType.Integer && (int)maxTokensToken < 4096))
        {
            body["max_tokens"] = 4096;
        }

        bool isOfficialOpenAiOrGoogle =
            _cachedEndpoint.IndexOf("api.openai.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
            _cachedEndpoint.IndexOf("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) >= 0;

        if (isOfficialOpenAiOrGoogle)
        {
            body.Remove("reasoning");
        }
        else if (modelId.IndexOf("gemini-3", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            body["reasoning"] = new JObject
            {
                ["effort"] = "none",
                ["enabled"] = false
            };
        }

        JArray messages = body["messages"] as JArray;
        if (messages != null && messages.Count > 0)
        {
            // Ensure smarter frontier models (gemini-3.8-flash) remain fun, persuadable, and winnable
            JObject firstMsg = messages[0] as JObject;
            if (firstMsg != null && string.Equals((string)firstMsg["role"], "system", StringComparison.OrdinalIgnoreCase))
            {
                string sysText = (string)firstMsg["content"];
                if (!string.IsNullOrEmpty(sysText) &&
                    sysText.IndexOf("completed_objectives", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    sysText.IndexOf("[WINNABILITY_BALANCE]", StringComparison.Ordinal) < 0)
                {
                    firstMsg["content"] = sysText +
                        "\n\n[WINNABILITY_BALANCE]\n" +
                        "- Gameplay Balance: This is a fast-paced comedy game meant to be BEATABLE within 2 to 4 turns. Do not act like an unbreakable real-world skeptic or stall endlessly.\n" +
                        "- Natural Phone Pacing: Keep your spoken 'dialogue' punchy, natural, and conversational (1 to 2 sentences, around 15 to 35 words) so TTS voice playback stays fast and responsive.\n" +
                        "- Generous Persuasion: When the player makes a creative, funny, urgent, or plausible in-character pitch (or answers your question/objection), let yourself be convinced! Increase trust_percent generously (+20 to +35 per good turn) and agree to their request.\n" +
                        "- Reliable Credential Disclosure: When the player asks for any registration number, card number, SSN, code, account number, or detail that has a [[...]] sentinel token in an application premise (or when agreeing to pay/verify), WILLINGLY include that exact [[...]] token (with double brackets) in your dialogue!\n" +
                        "- Fair Objective Judging: In Stage 2, evaluate completed_objectives generously in the player's favor. As soon as the player's pitch and your response satisfy an objective's criteria (and any prerequisites are met earlier or on this turn), award its exact progress_ids in completed_objectives.";
                }
            }
        }

        // Attach multimodal voice audio to the user dialogue turn if fresh audio is available and matches the speaker's transcript
        if (_cachedMultimodalVoiceInChat && messages != null && messages.Count > 0)
        {
            string wavB64 = LatestWavBase64;
            string wavNorm = LatestWavTranscript;
            DateTime wavTime = LatestWavTimestampUtc;
            if (!string.IsNullOrEmpty(wavB64) && (DateTime.UtcNow - wavTime).TotalSeconds <= 20.0)
            {
                JObject lastMsg = messages[messages.Count - 1] as JObject;
                if (lastMsg != null && string.Equals((string)lastMsg["role"], "user", StringComparison.OrdinalIgnoreCase))
                {
                    JToken contentToken = lastMsg["content"];
                    if (contentToken != null && contentToken.Type == JTokenType.String)
                    {
                        string existingText = (string)contentToken;
                        if (!string.IsNullOrEmpty(existingText) && existingText.IndexOf("player_dialogue", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // In multiplayer, verify the cached WAV audio belongs to this turn's speaker
                            bool transcriptMatches = string.IsNullOrEmpty(wavNorm) ||
                                NormalizeAlphaNum(existingText).IndexOf(wavNorm, StringComparison.Ordinal) >= 0;

                            if (transcriptMatches)
                            {
                                // Consume the cached audio for this dialogue turn
                                LatestWavBase64 = null;
                                LatestWavTranscript = null;

                                string augmentedText = existingText +
                                    "\n\n[Audio Context: The player's actual spoken voice audio for this turn is attached. Use both the spoken audio (including exact words, numbers, names, and vocal tone/emotion) and player_dialogue to respond in character while strictly following the required JSON schema.]";

                                lastMsg["content"] = new JArray
                                {
                                    new JObject
                                    {
                                        ["type"] = "text",
                                        ["text"] = augmentedText
                                    },
                                    new JObject
                                    {
                                        ["type"] = "input_audio",
                                        ["input_audio"] = new JObject
                                        {
                                            ["data"] = wavB64,
                                            ["format"] = "wav"
                                        }
                                    }
                                };
                            }
                        }
                    }
                }
            }
        }
    }

    public static byte[] EncodeWavePcm16(float[] samples, int sampleRate)
    {
        if (samples == null || samples.Length == 0)
            return new byte[0];

        // Trim leading and trailing room silence (above typical 0.008f mic noise floor) while preserving 100ms lead-in and 120ms tail
        int firstSpeech = 0;
        while (firstSpeech < samples.Length - 1 && Math.Abs(samples[firstSpeech]) < 0.008f)
            firstSpeech++;

        int lastSpeech = samples.Length - 1;
        while (lastSpeech > firstSpeech && Math.Abs(samples[lastSpeech]) < 0.008f)
            lastSpeech--;

        int leadSamples = (int)(sampleRate * 0.10f);
        int tailSamples = (int)(sampleRate * 0.12f);
        int startIdx = Math.Max(0, firstSpeech - leadSamples);
        int endIdx = Math.Min(samples.Length - 1, lastSpeech + tailSamples);
        int count = endIdx - startIdx + 1;
        if (count < 1600)
        {
            startIdx = 0;
            count = Math.Min(samples.Length, 1600);
        }

        int dataLength = count * 2;
        byte[] wav = new byte[44 + dataLength];

        // RIFF header
        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        WriteInt32LE(wav, 4, 36 + dataLength);
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';

        // fmt subchunk
        wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
        WriteInt32LE(wav, 16, 16);
        WriteInt16LE(wav, 20, 1); // PCM = 1
        WriteInt16LE(wav, 22, 1); // Mono = 1
        WriteInt32LE(wav, 24, sampleRate);
        WriteInt32LE(wav, 28, sampleRate * 2);
        WriteInt16LE(wav, 32, 2); // BlockAlign
        WriteInt16LE(wav, 34, 16); // BitsPerSample

        // data subchunk
        wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
        WriteInt32LE(wav, 40, dataLength);

        int offset = 44;
        for (int i = 0; i < count; i++)
        {
            float s = samples[startIdx + i];
            if (s > 1.0f) s = 1.0f;
            else if (s < -1.0f) s = -1.0f;
            short pcm = (short)Math.Round(s * 32767.0f);
            wav[offset++] = (byte)(pcm & 0xFF);
            wav[offset++] = (byte)((pcm >> 8) & 0xFF);
        }

        return wav;
    }

    private static void WriteInt32LE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt16LE(byte[] buf, int offset, short value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void CancelActivePartial()
    {
        UnityWebRequest req;
        TaskCompletionSource<bool> tcs;
        lock (StatusLock)
        {
            _partialCancelVersion++;
            req = _activePartialRequest;
            tcs = _activePartialTcs;
            _activePartialRequest = null;
            _activePartialTcs = null;
            _activePartialTask = null;
            _uiDirty = true;
        }

        if (tcs != null)
        {
            try { tcs.TrySetResult(true); } catch { }
        }
        if (req != null)
        {
            try { req.Abort(); } catch { }
        }
    }

    public static async Task<WhisperResult> TranscribeAsync(WhisperManager fallbackRecognizer, float[] samples, bool isFinal, string language)
    {
        EnsureConfigLoaded();
        if (samples == null || samples.Length == 0)
            return CreateWhisperResult(string.Empty);

        if (string.Equals(_cachedSttMode, "local", StringComparison.OrdinalIgnoreCase))
        {
            if (fallbackRecognizer != null)
            {
                if (!fallbackRecognizer.IsLoaded && !fallbackRecognizer.IsLoading)
                    await fallbackRecognizer.InitModel();
                if (fallbackRecognizer.IsLoaded)
                    return await fallbackRecognizer.GetTextAsync(samples, 16000, 1);
            }
            return CreateWhisperResult(string.Empty);
        }

        DateTime jobStartUtc = DateTime.UtcNow;
        int myPartialVersion = 0;
        Task<string> reusablePartialTask = null;
        string reusableCompletedText = null;

        if (isFinal)
        {
            lock (StatusLock)
            {
                _isUserSpeaking = false;
                _activeFinalTranscribeCount++;
                _finalTranscribeStartUtc = jobStartUtc;
                _uiDirty = true;

                if (_reuseActivePartialForFinal)
                {
                    _reuseActivePartialForFinal = false;
                    if (!string.IsNullOrEmpty(_lastCompletedPartialTranscript) &&
                        (jobStartUtc - _lastCompletedPartialUtc).TotalSeconds < 4.0)
                    {
                        reusableCompletedText = _lastCompletedPartialTranscript;
                    }
                    else if (_activePartialTask != null)
                    {
                        reusablePartialTask = _activePartialTask;
                    }
                }
            }

            if (reusableCompletedText != null)
            {
                lock (StatusLock)
                {
                    _lastTranscribeDurationSeconds = 0.01;
                    if (_activeFinalTranscribeCount > 0)
                        _activeFinalTranscribeCount--;
                    _lastCompletedPartialTranscript = null;
                    _lastPartialSpeechEndSample = -1;
                    _uiDirty = true;
                }
                return CreateWhisperResult(reusableCompletedText);
            }

            if (reusablePartialTask != null)
            {
                try
                {
                    string reused = await reusablePartialTask;
                    if (!string.IsNullOrEmpty(reused))
                    {
                        double reusedSecs = Math.Max(0.01, (DateTime.UtcNow - jobStartUtc).TotalSeconds);
                        lock (StatusLock)
                        {
                            _lastTranscribeDurationSeconds = reusedSecs;
                            if (_activeFinalTranscribeCount > 0)
                                _activeFinalTranscribeCount--;
                            _lastCompletedPartialTranscript = null;
                            _lastPartialSpeechEndSample = -1;
                            _uiDirty = true;
                        }
                        return CreateWhisperResult(reused);
                    }
                }
                catch
                {
                }
            }

            CancelActivePartial();
        }
        else
        {
            int partialSpeechEnd = samples.Length <= 72000 ? GetTrimmedSpeechEndIndex(samples) : -1;
            lock (StatusLock)
            {
                myPartialVersion = _partialCancelVersion;
                _lastPartialSpeechEndSample = partialSpeechEnd;
                _activePartialTranscribeCount++;
                _partialTranscribeStartUtc = jobStartUtc;
                _uiDirty = true;
            }
        }

        try
        {
            string voiceModel = _cachedVoiceModel;
            string sttMode = _cachedSttMode ?? "chat_audio";
            string langHint = string.IsNullOrWhiteSpace(language) ? "en" : language.Trim();
            string contentType = "application/json";

            // Offload all RMS calculation, WAV PCM16 encoding, Base64/Multipart encoding, and JSON UTF-8 serialization to a background thread so the Unity main thread has zero frame hitch
            byte[] requestBody = await Task.Run(() =>
            {
                float[] workSamples = samples;
                // For live mid-speech partial previews, cap to the last 4.5 seconds (72000 samples) to keep partial requests fast and lightweight
                if (!isFinal && workSamples.Length > 72000)
                {
                    float[] tail = new float[72000];
                    Array.Copy(workSamples, workSamples.Length - 72000, tail, 0, 72000);
                    workSamples = tail;
                }

                double sumSq = 0.0;
                float peak = 0.0f;
                for (int i = 0; i < workSamples.Length; i++)
                {
                    float abs = Math.Abs(workSamples[i]);
                    if (abs > peak) peak = abs;
                    sumSq += workSamples[i] * workSamples[i];
                }
                double rms = Math.Sqrt(sumSq / workSamples.Length);
                if (rms < 0.0012 || peak < 0.006f)
                    return null;

                byte[] wavBytes = EncodeWavePcm16(workSamples, 16000);

                if (string.Equals(sttMode, "whisper_api", StringComparison.OrdinalIgnoreCase))
                {
                    string boundary = "----SwyfBoundary" + Guid.NewGuid().ToString("N");
                    contentType = "multipart/form-data; boundary=" + boundary;
                    return BuildMultipartWhisperBody(boundary, wavBytes, voiceModel, langHint);
                }

                string wavBase64 = Convert.ToBase64String(wavBytes);
                string prompt =
                    "You are an exact speech-to-text transcriber for a live voice call (language: " + langHint + "). " +
                    "Transcribe the exact words spoken in the audio. " +
                    "If there is no clear human speech (only silence, static, breathing, clicks, or background noise), you MUST output the exact token [SILENCE] and nothing else. " +
                    "Never guess or invent words when the audio is quiet. Output ONLY the spoken words or [SILENCE], with no quotes or formatting.";

                if (string.Equals(sttMode, "gemini_native", StringComparison.OrdinalIgnoreCase))
                {
                    var geminiPayload = new JObject
                    {
                        ["contents"] = new JArray
                        {
                            new JObject
                            {
                                ["parts"] = new JArray
                                {
                                    new JObject { ["text"] = prompt },
                                    new JObject
                                    {
                                        ["inline_data"] = new JObject
                                        {
                                            ["mime_type"] = "audio/wav",
                                            ["data"] = wavBase64
                                        }
                                    }
                                }
                            }
                        },
                        ["generationConfig"] = new JObject
                        {
                            ["temperature"] = 0.0,
                            ["maxOutputTokens"] = 256
                        }
                    };
                    return Encoding.UTF8.GetBytes(geminiPayload.ToString(Formatting.None));
                }

                var payloadObj = new JObject
                {
                    ["model"] = voiceModel,
                    ["temperature"] = 0.0,
                    ["max_tokens"] = 256,
                    ["reasoning"] = new JObject
                    {
                        ["effort"] = "none",
                        ["enabled"] = false
                    },
                    ["messages"] = new JArray
                    {
                        new JObject
                        {
                            ["role"] = "user",
                            ["content"] = new JArray
                            {
                                new JObject
                                {
                                    ["type"] = "text",
                                    ["text"] = prompt
                                },
                                new JObject
                                {
                                    ["type"] = "input_audio",
                                    ["input_audio"] = new JObject
                                    {
                                        ["data"] = wavBase64,
                                        ["format"] = "wav"
                                    }
                                }
                            }
                        }
                    }
                };

                string jsonBody = payloadObj.ToString(Formatting.None);
                return Encoding.UTF8.GetBytes(jsonBody);
            });

            if (requestBody == null)
                return CreateWhisperResult(string.Empty);

            if (!isFinal && myPartialVersion != _partialCancelVersion)
                return CreateWhisperResult(string.Empty);

            Task<string> reqTask = SendTranscriptionRequestAsync(requestBody, contentType, sttMode, voiceModel, GetApiKey(), isFinal, myPartialVersion);
            if (!isFinal)
            {
                lock (StatusLock)
                {
                    if (myPartialVersion == _partialCancelVersion)
                        _activePartialTask = reqTask;
                }
            }

            string transcript = await reqTask;
            if (transcript == null && !string.IsNullOrEmpty(_cachedFallbackApiKey) && isFinal)
            {
                ActivateFallbackKey();
                transcript = await SendTranscriptionRequestAsync(requestBody, contentType, sttMode, voiceModel, _cachedFallbackApiKey, isFinal, myPartialVersion);
            }

            if (transcript != null)
            {
                double totalSecs = Math.Max(0.01, (DateTime.UtcNow - jobStartUtc).TotalSeconds);
                lock (StatusLock)
                {
                    _lastTranscribeDurationSeconds = totalSecs;
                    if (!isFinal && !string.IsNullOrEmpty(transcript) && myPartialVersion == _partialCancelVersion)
                    {
                        _lastCompletedPartialTranscript = transcript;
                        _lastCompletedPartialUtc = DateTime.UtcNow;
                    }
                }
                return CreateWhisperResult(transcript);
            }
        }
        catch (Exception ex)
        {
            LastTranscriptionError = ex.ToString();
        }
        finally
        {
            lock (StatusLock)
            {
                if (isFinal)
                {
                    if (_activeFinalTranscribeCount > 0)
                        _activeFinalTranscribeCount--;
                    _lastCompletedPartialTranscript = null;
                    _lastPartialSpeechEndSample = -1;
                }
                else
                {
                    if (_activePartialTranscribeCount > 0)
                        _activePartialTranscribeCount--;
                }
                _uiDirty = true;
            }
        }

        // If a partial request was intentionally aborted because the user finished speaking, return empty immediately without falling back to local Whisper CPU inference!
        if (!isFinal)
        {
            return CreateWhisperResult(string.Empty);
        }

        // Offline/error fallback to local Whisper only when Whisper Fallback is explicitly enabled in Mod Settings
        if (IsWhisperFallbackEnabled() && fallbackRecognizer != null)
        {
            if (!fallbackRecognizer.IsLoaded && !fallbackRecognizer.IsLoading)
                await fallbackRecognizer.InitModel();
            if (fallbackRecognizer.IsLoaded)
                return await fallbackRecognizer.GetTextAsync(samples, 16000, 1);
        }

        return CreateWhisperResult(string.Empty);
    }

    private static byte[] BuildMultipartWhisperBody(string boundary, byte[] wavBytes, string model, string language)
    {
        using (var ms = new MemoryStream(wavBytes.Length + 1024))
        {
            void WriteAscii(string s)
            {
                byte[] b = Encoding.UTF8.GetBytes(s);
                ms.Write(b, 0, b.Length);
            }

            WriteAscii("--" + boundary + "\r\n");
            WriteAscii("Content-Disposition: form-data; name=\"file\"; filename=\"speech.wav\"\r\n");
            WriteAscii("Content-Type: audio/wav\r\n\r\n");
            ms.Write(wavBytes, 0, wavBytes.Length);
            WriteAscii("\r\n");

            WriteAscii("--" + boundary + "\r\n");
            WriteAscii("Content-Disposition: form-data; name=\"model\"\r\n\r\n");
            WriteAscii((string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini-transcribe" : model.Trim()) + "\r\n");

            if (!string.IsNullOrWhiteSpace(language) && language.Length <= 5)
            {
                WriteAscii("--" + boundary + "\r\n");
                WriteAscii("Content-Disposition: form-data; name=\"language\"\r\n\r\n");
                WriteAscii(language.Trim() + "\r\n");
            }

            WriteAscii("--" + boundary + "\r\n");
            WriteAscii("Content-Disposition: form-data; name=\"response_format\"\r\n\r\n");
            WriteAscii("json\r\n");

            WriteAscii("--" + boundary + "--\r\n");
            return ms.ToArray();
        }
    }

    public static string LastTranscriptionError;

    private static async Task<string> SendTranscriptionRequestAsync(byte[] bodyBytes, string contentType, string sttMode, string voiceModel, string apiKey, bool isFinal, int partialVersion)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || bodyBytes == null)
            return null;

        if (!isFinal && partialVersion != _partialCancelVersion)
            return null;

        string targetUrl = _cachedSttEndpoint;
        if (string.IsNullOrWhiteSpace(targetUrl))
            targetUrl = _cachedEndpoint;

        bool isGeminiNative = string.Equals(sttMode, "gemini_native", StringComparison.OrdinalIgnoreCase);
        if (isGeminiNative)
        {
            string cleanModel = string.IsNullOrWhiteSpace(voiceModel) ? "gemini-2.5-flash" : voiceModel.Trim();
            if (cleanModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                cleanModel = cleanModel.Substring(7);
            if (targetUrl.IndexOf("{model}", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                targetUrl = targetUrl.Replace("{model}", cleanModel);
            }
            else if (targetUrl.IndexOf(":generateContent", StringComparison.OrdinalIgnoreCase) < 0)
            {
                targetUrl = "https://generativelanguage.googleapis.com/v1beta/models/" + cleanModel + ":generateContent";
            }
            string sep = targetUrl.Contains("?") ? "&" : "?";
            targetUrl = targetUrl + sep + "key=" + Uri.EscapeDataString(apiKey.Trim());
        }

        using (var req = new UnityWebRequest(targetUrl, "POST"))
        {
            req.timeout = isFinal ? 25 : 10;
            req.redirectLimit = 5;
            req.uploadHandler = new UploadHandlerRaw(bodyBytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", contentType);
            if (!isGeminiNative)
            {
                req.SetRequestHeader("Authorization", "Bearer " + apiKey.Trim());
            }

            var tcs = new TaskCompletionSource<bool>();
            if (!isFinal)
            {
                lock (StatusLock)
                {
                    if (partialVersion != _partialCancelVersion)
                        return null;
                    _activePartialRequest = req;
                    _activePartialTcs = tcs;
                }
            }

            try
            {
                var op = req.SendWebRequest();
                if (!op.isDone)
                {
                    op.completed += _ => tcs.TrySetResult(true);
                    await tcs.Task;
                }
            }
            finally
            {
                if (!isFinal)
                {
                    lock (StatusLock)
                    {
                        if (_activePartialRequest == req)
                        {
                            _activePartialRequest = null;
                            _activePartialTcs = null;
                            _activePartialTask = null;
                        }
                    }
                }
            }

            if (!isFinal && partialVersion != _partialCancelVersion)
                return null;

            long code = req.responseCode;
            if (code < 200 || code >= 300)
            {
                return null;
            }

            DownloadHandler dh = req.downloadHandler;
            string respText = dh != null ? dh.text : null;
            if (string.IsNullOrWhiteSpace(respText))
                return string.Empty;

            return await Task.Run(() =>
            {
                JObject parsed = JObject.Parse(respText);
                string text = null;
                if (string.Equals(sttMode, "whisper_api", StringComparison.OrdinalIgnoreCase))
                {
                    text = (string)parsed["text"];
                }
                else if (isGeminiNative)
                {
                    text = (string)parsed["candidates"]?[0]?["content"]?["parts"]?[0]?["text"];
                }
                else
                {
                    text = (string)parsed["choices"]?[0]?["message"]?["content"]
                        ?? (string)parsed["text"]
                        ?? (string)parsed["candidates"]?[0]?["content"]?["parts"]?[0]?["text"];
                }

                if (string.IsNullOrWhiteSpace(text))
                    return string.Empty;

                text = text.Trim();
                if (text.Length >= 2 &&
                    ((text.StartsWith("\"") && text.EndsWith("\"")) ||
                     (text.StartsWith("'") && text.EndsWith("'"))))
                {
                    text = text.Substring(1, text.Length - 2).Trim();
                }

                if (text.IndexOf("[SILENCE]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    string.Equals(text, "SILENCE", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "(silence)", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(text, "[silence]", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return text;
            });
        }
    }

    private static WhisperResult CreateWhisperResult(string text)
    {
        string clean = text ?? string.Empty;
        var segments = new List<WhisperSegment>(1)
        {
            new WhisperSegment(0, clean, 0UL, 100UL)
        };
        return new WhisperResult(segments, 0);
    }
}
