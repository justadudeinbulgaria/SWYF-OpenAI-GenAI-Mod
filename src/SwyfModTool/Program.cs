using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

class Program
{
    private static readonly string[] DllFiles = new[]
    {
        "ScriptsAssDef.dll",
        "Assembly-CSharp.dll",
        "MetaVosk.dll",
        "com.whisper.unity.dll"
    };

    private const string UnityRegSubKey = @"Software\Jatater Worldwide\Scam With Your Friends";

    private sealed class ModConfig
    {
        public string Provider = "gemini";
        public string ApiKey = "";
        public string FallbackApiKey = "";
        public string Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
        public string ModelsEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/models";
        public string SttEndpoint = "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
        public string SttMode = "gemini_native";
        public string Model = "gemini-2.5-flash";
        public string VoiceModel = "gemini-2.5-flash";
        public bool EnableWhisperFallback = false;
    }

    static int Main(string[] args)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Scam With Your Friends - OpenAI & Google GenAI Standard API Mod Tool";

        bool noPause = false;
        bool restoreMode = false;
        bool autoInstall = false;
        bool testOnlyMode = false;
        bool forceConfigure = false;
        string customDir = null;
        string cliProvider = null;
        string cliApiKey = null;
        string cliEndpoint = null;
        string cliSttEndpoint = null;
        string cliSttMode = null;
        string cliModel = null;
        string cliVoiceModel = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--no-pause", StringComparison.OrdinalIgnoreCase))
                noPause = true;
            else if (string.Equals(a, "--restore", StringComparison.OrdinalIgnoreCase) || string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase))
                restoreMode = true;
            else if (string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase))
                autoInstall = true;
            else if (string.Equals(a, "--test", StringComparison.OrdinalIgnoreCase))
                testOnlyMode = true;
            else if (string.Equals(a, "--configure", StringComparison.OrdinalIgnoreCase))
                forceConfigure = true;
            else if (string.Equals(a, "--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                customDir = args[++i];
            else if (string.Equals(a, "--provider", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                cliProvider = args[++i];
            else if (string.Equals(a, "--api-key", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                cliApiKey = args[++i];
            else if (string.Equals(a, "--endpoint", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                cliEndpoint = args[++i];
            else if (string.Equals(a, "--stt-endpoint", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                cliSttEndpoint = args[++i];
            else if (string.Equals(a, "--stt-mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                cliSttMode = args[++i];
            else if (string.Equals(a, "--model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                cliModel = args[++i];
            else if (string.Equals(a, "--voice-model", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                cliVoiceModel = args[++i];
        }

        WriteHeader();

        string gameDir = !string.IsNullOrWhiteSpace(customDir) && IsValidGameDirectory(customDir)
            ? Path.GetFullPath(customDir)
            : DetectGameDirectory();

        while (string.IsNullOrEmpty(gameDir) || !IsValidGameDirectory(gameDir))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Could not automatically locate 'Scam With Your Friends'.");
            Console.ResetColor();
            Console.Write("Please paste your 'Scam With Your Friends' folder path: ");
            string input = Console.ReadLine()?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(input) && IsValidGameDirectory(input))
            {
                gameDir = Path.GetFullPath(input);
                break;
            }
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Invalid directory (must contain 'Scam With Your Friends_Data\\Managed\\Assembly-CSharp.dll').\n");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Target Game Directory:");
        Console.ResetColor();
        Console.WriteLine("  " + gameDir);

        ModConfig cfg = LoadExistingConfig(gameDir);
        ApplyCliOverrides(cfg, cliProvider, cliApiKey, cliEndpoint, cliSttEndpoint, cliSttMode, cliModel, cliVoiceModel);

        bool currentlyInstalled = IsModInstalled(gameDir);
        Console.Write("Current Mod Status: ");
        if (currentlyInstalled)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[INSTALLED - Provider: {cfg.Provider} | Model: {cfg.Model} | STT: {cfg.SttMode}]");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[NOT INSTALLED]");
        }
        Console.ResetColor();
        Console.WriteLine();

        if (!autoInstall && !restoreMode && !testOnlyMode)
        {
            Console.WriteLine("Select an action:");
            Console.WriteLine("  [1] Install / Update Mod & Configure API (OpenAI / Google GenAI / Custom) (Press ENTER)");
            Console.WriteLine("  [2] Reconfigure API Provider / Key / Model Only");
            Console.WriteLine("  [3] Test Installed Mod (Live Chat + Voice STT Verification)");
            Console.WriteLine("  [4] Restore Original Unpatched Game (.bak backups)");
            Console.Write("\nChoice [1]: ");
            string choice = Console.ReadLine()?.Trim();
            if (choice == "2")
            {
                forceConfigure = true;
            }
            else if (choice == "3" || string.Equals(choice, "t", StringComparison.OrdinalIgnoreCase) || string.Equals(choice, "test", StringComparison.OrdinalIgnoreCase))
            {
                testOnlyMode = true;
            }
            else if (choice == "4" || string.Equals(choice, "r", StringComparison.OrdinalIgnoreCase) || string.Equals(choice, "restore", StringComparison.OrdinalIgnoreCase))
            {
                restoreMode = true;
            }
        }

        Console.WriteLine();
        int exitCode = 0;
        try
        {
            if (restoreMode)
            {
                RestoreOriginals(gameDir);
            }
            else if (testOnlyMode)
            {
                if (!TestMod(gameDir, cfg))
                    exitCode = 1;
            }
            else if (forceConfigure && currentlyInstalled && !autoInstall)
            {
                PromptInteractiveConfiguration(cfg, true);
                SaveModConfig(gameDir, cfg);
                ConfigureInGameSettingsAndCleanOldModels(gameDir, cfg);
                Console.WriteLine();
                if (!TestMod(gameDir, cfg))
                    exitCode = 1;
            }
            else
            {
                if (!autoInstall && (forceConfigure || string.IsNullOrWhiteSpace(cfg.ApiKey)))
                {
                    PromptInteractiveConfiguration(cfg, true);
                }
                else if (!autoInstall)
                {
                    Console.Write($"Keep current API configuration ({cfg.Provider} / {cfg.Model})? [Y/n]: ");
                    string keep = Console.ReadLine()?.Trim();
                    if (string.Equals(keep, "n", StringComparison.OrdinalIgnoreCase) || string.Equals(keep, "no", StringComparison.OrdinalIgnoreCase))
                    {
                        PromptInteractiveConfiguration(cfg, true);
                    }
                }

                InstallAndPatchWithCecil(gameDir, cfg);
                Console.WriteLine();
                if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                {
                    if (!TestMod(gameDir, cfg))
                        exitCode = 1;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Note: No API key configured yet. Set your key in ai_mod_config.json or environment variables (OPENAI_API_KEY / GEMINI_API_KEY) before playing.");
                    Console.ResetColor();
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\nERROR: " + ex.Message);
            Console.WriteLine("Make sure 'Scam With Your Friends' is closed before running SwyfModTool.");
            Console.ResetColor();
            exitCode = 1;
        }

        if (!noPause)
        {
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey(true);
        }
        return exitCode;
    }

    private static void WriteHeader()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("========================================================================");
        Console.WriteLine("  SCAM WITH YOUR FRIENDS - OPEN-SOURCE AI & VOICE MOD TOOL (Mono.Cecil)");
        Console.WriteLine("========================================================================");
        Console.ResetColor();
        Console.WriteLine("  - Standard APIs : OpenAI Standard API, Google Gemini (GenAI), OpenRouter");
        Console.WriteLine("  - Voice STT     : OpenAI Whisper API, Gemini Native Audio, or Chat Audio");
        Console.WriteLine("  - Live HUD      : Real-Time 4-Stage Voice & AI Status Indicator + Timers");
        Console.WriteLine("  - Performance   : Zero Main-Thread Stutter + Smart Partial Reuse + 0ms Abort");
        Console.WriteLine("  - Context Limit : Uncapped (900k tokens / 4096 turns / 4096 max_tokens)");
        Console.WriteLine("  - Privacy       : Zero Hardcoded Keys & AnalyticsCollector Disabled");
        Console.WriteLine("========================================================================\n");
    }

    private static void PromptInteractiveConfiguration(ModConfig cfg, bool askProvider)
    {
        if (askProvider)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("Select your AI Provider:");
            Console.ResetColor();
            Console.WriteLine("  [1] Google Gemini (GenAI Standard API - generativelanguage.googleapis.com)");
            Console.WriteLine("  [2] OpenAI Standard API (api.openai.com)");
            Console.WriteLine("  [3] OpenRouter API (openrouter.ai)");
            Console.WriteLine("  [4] Custom OpenAI-Compatible API (Ollama / LM Studio / vLLM / Custom Gateway)");
            Console.Write("\nProvider Choice [1]: ");
            string pChoice = Console.ReadLine()?.Trim();

            if (pChoice == "2")
            {
                ApplyProviderPreset(cfg, "openai");
            }
            else if (pChoice == "3")
            {
                ApplyProviderPreset(cfg, "openrouter");
            }
            else if (pChoice == "4")
            {
                cfg.Provider = "custom";
                Console.Write($"Chat Completions Endpoint [{cfg.Endpoint}]: ");
                string ep = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(ep)) cfg.Endpoint = ep;

                Console.WriteLine("Select Voice Speech-to-Text (STT) Mode:");
                Console.WriteLine("  [1] chat_audio    - Multimodal input_audio in /v1/chat/completions");
                Console.WriteLine("  [2] whisper_api   - OpenAI Standard /v1/audio/transcriptions (multipart WAV)");
                Console.WriteLine("  [3] gemini_native - Google GenAI Standard :generateContent (inline_data WAV)");
                Console.WriteLine("  [4] local         - Offline local Whisper CPU fallback");
                Console.Write("STT Mode [1]: ");
                string sm = Console.ReadLine()?.Trim();
                if (sm == "2") cfg.SttMode = "whisper_api";
                else if (sm == "3") cfg.SttMode = "gemini_native";
                else if (sm == "4") cfg.SttMode = "local";
                else cfg.SttMode = "chat_audio";

                if (cfg.SttMode == "whisper_api")
                {
                    string defaultSttEp = DeriveSiblingEndpoint(cfg.Endpoint, "/v1/audio/transcriptions");
                    Console.Write($"STT Transcriptions Endpoint [{defaultSttEp}]: ");
                    string sep = Console.ReadLine()?.Trim();
                    cfg.SttEndpoint = string.IsNullOrWhiteSpace(sep) ? defaultSttEp : sep;
                }
                else
                {
                    cfg.SttEndpoint = cfg.Endpoint;
                }
                cfg.ModelsEndpoint = DeriveSiblingEndpoint(cfg.Endpoint, "/v1/models");
            }
            else
            {
                ApplyProviderPreset(cfg, "gemini");
            }
        }

        Console.Write($"Enter your API Key {(string.IsNullOrEmpty(cfg.ApiKey) ? "" : "[Press ENTER to keep existing key]")}: ");
        string keyInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(keyInput))
            cfg.ApiKey = keyInput;

        Console.Write($"Chat Model ID [{cfg.Model}]: ");
        string modelInput = Console.ReadLine()?.Trim();
        if (!string.IsNullOrWhiteSpace(modelInput))
            cfg.Model = modelInput;

        if (cfg.SttMode != "local")
        {
            string defaultVoice = cfg.Provider == "openai" && cfg.SttMode == "whisper_api"
                ? "gpt-4o-mini-transcribe"
                : cfg.Model;
            Console.Write($"Voice STT Model ID [{defaultVoice}]: ");
            string voiceInput = Console.ReadLine()?.Trim();
            cfg.VoiceModel = string.IsNullOrWhiteSpace(voiceInput) ? defaultVoice : voiceInput;

            Console.Write($"Enable Offline Local Whisper Fallback? (Uses ~400MB extra RAM) [{(cfg.EnableWhisperFallback ? "Y/n" : "y/N")}]: ");
            string wfInput = Console.ReadLine()?.Trim();
            if (!string.IsNullOrWhiteSpace(wfInput))
            {
                cfg.EnableWhisperFallback = string.Equals(wfInput, "y", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(wfInput, "yes", StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(wfInput, "true", StringComparison.OrdinalIgnoreCase) ||
                                            wfInput == "1";
            }
        }
        else
        {
            cfg.EnableWhisperFallback = true;
        }
    }

    private static void ApplyProviderPreset(ModConfig cfg, string provider)
    {
        cfg.Provider = provider.ToLowerInvariant();
        if (cfg.Provider == "openai")
        {
            cfg.Endpoint = "https://api.openai.com/v1/chat/completions";
            cfg.ModelsEndpoint = "https://api.openai.com/v1/models";
            cfg.SttEndpoint = "https://api.openai.com/v1/audio/transcriptions";
            cfg.SttMode = "whisper_api";
            cfg.Model = "gpt-4o-mini";
            cfg.VoiceModel = "gpt-4o-mini-transcribe";
        }
        else if (cfg.Provider == "openrouter")
        {
            cfg.Endpoint = "https://openrouter.ai/api/v1/chat/completions";
            cfg.ModelsEndpoint = "https://openrouter.ai/api/v1/models";
            cfg.SttEndpoint = "https://openrouter.ai/api/v1/chat/completions";
            cfg.SttMode = "chat_audio";
            cfg.Model = "google/gemini-2.5-flash";
            cfg.VoiceModel = "google/gemini-2.5-flash";
        }
        else if (cfg.Provider == "custom")
        {
            cfg.SttMode = "chat_audio";
        }
        else
        {
            cfg.Provider = "gemini";
            cfg.Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
            cfg.ModelsEndpoint = "https://generativelanguage.googleapis.com/v1beta/openai/models";
            cfg.SttEndpoint = "https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
            cfg.SttMode = "gemini_native";
            cfg.Model = "gemini-2.5-flash";
            cfg.VoiceModel = "gemini-2.5-flash";
        }
    }

    private static void ApplyCliOverrides(
        ModConfig cfg,
        string provider,
        string apiKey,
        string endpoint,
        string sttEndpoint,
        string sttMode,
        string model,
        string voiceModel)
    {
        if (!string.IsNullOrWhiteSpace(provider))
            ApplyProviderPreset(cfg, provider.Trim());
        if (!string.IsNullOrWhiteSpace(apiKey))
            cfg.ApiKey = apiKey.Trim();
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            cfg.Endpoint = endpoint.Trim();
            cfg.ModelsEndpoint = DeriveSiblingEndpoint(cfg.Endpoint, "/v1/models");
        }
        if (!string.IsNullOrWhiteSpace(sttEndpoint))
            cfg.SttEndpoint = sttEndpoint.Trim();
        if (!string.IsNullOrWhiteSpace(sttMode))
            cfg.SttMode = sttMode.Trim();
        if (!string.IsNullOrWhiteSpace(model))
        {
            cfg.Model = model.Trim();
            if (string.IsNullOrWhiteSpace(voiceModel) && cfg.SttMode != "whisper_api")
                cfg.VoiceModel = cfg.Model;
        }
        if (!string.IsNullOrWhiteSpace(voiceModel))
            cfg.VoiceModel = voiceModel.Trim();
    }

    private static string DeriveSiblingEndpoint(string chatEndpoint, string targetSuffix)
    {
        if (string.IsNullOrWhiteSpace(chatEndpoint))
            return "";
        int idx = chatEndpoint.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
        if (idx > 0)
        {
            string baseUrl = chatEndpoint.Substring(0, idx);
            if (targetSuffix.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) && baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                return baseUrl + targetSuffix.Substring(3);
            if (targetSuffix.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) && baseUrl.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
                return baseUrl + targetSuffix.Substring(3);
            return baseUrl + targetSuffix;
        }
        return chatEndpoint;
    }

    private static ModConfig LoadExistingConfig(string gameDir)
    {
        var cfg = new ModConfig();
        string[] candidateFiles = new[]
        {
            Path.Combine(gameDir, "ai_mod_config.json"),
            Path.Combine(gameDir, "surplus_config.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai_mod_config.json"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "surplus_config.json")
        };

        foreach (string path in candidateFiles)
        {
            if (!File.Exists(path))
                continue;
            try
            {
                string raw = File.ReadAllText(path);
                string prov = ExtractJsonString(raw, "provider");
                string k1 = ExtractJsonString(raw, "api_key");
                string k2 = ExtractJsonString(raw, "fallback_api_key");
                string ep = ExtractJsonString(raw, "endpoint");
                string mep = ExtractJsonString(raw, "models_endpoint");
                string sep = ExtractJsonString(raw, "stt_endpoint");
                string sm = ExtractJsonString(raw, "stt_mode");
                string md = ExtractJsonString(raw, "model");
                string vmd = ExtractJsonString(raw, "voice_model");

                if (!string.IsNullOrWhiteSpace(prov)) ApplyProviderPreset(cfg, prov);
                if (!string.IsNullOrWhiteSpace(k1) && !IsPlaceholderKey(k1)) cfg.ApiKey = k1;
                if (!string.IsNullOrWhiteSpace(k2) && !IsPlaceholderKey(k2)) cfg.FallbackApiKey = k2;
                if (!string.IsNullOrWhiteSpace(ep)) cfg.Endpoint = ep;
                if (!string.IsNullOrWhiteSpace(mep)) cfg.ModelsEndpoint = mep;
                else if (!string.IsNullOrWhiteSpace(ep)) cfg.ModelsEndpoint = DeriveSiblingEndpoint(ep, "/v1/models");
                if (!string.IsNullOrWhiteSpace(sep)) cfg.SttEndpoint = sep;
                if (!string.IsNullOrWhiteSpace(sm)) cfg.SttMode = sm;
                else if (string.IsNullOrWhiteSpace(prov) && !string.IsNullOrWhiteSpace(ep) && ep.IndexOf("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) < 0 && ep.IndexOf("api.openai.com", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    cfg.Provider = "custom";
                    cfg.SttMode = "chat_audio";
                    cfg.SttEndpoint = cfg.Endpoint;
                }
                if (!string.IsNullOrWhiteSpace(md)) cfg.Model = md;
                if (!string.IsNullOrWhiteSpace(vmd)) cfg.VoiceModel = vmd;
                else if (!string.IsNullOrWhiteSpace(md) && cfg.SttMode != "whisper_api") cfg.VoiceModel = md;
                if (Regex.IsMatch(raw, "\"enable_whisper_fallback\"\\s*:\\s*true", RegexOptions.IgnoreCase))
                    cfg.EnableWhisperFallback = true;
                else if (Regex.IsMatch(raw, "\"enable_whisper_fallback\"\\s*:\\s*false", RegexOptions.IgnoreCase))
                    cfg.EnableWhisperFallback = false;
                break;
            }
            catch
            {
            }
        }

        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            string envKey = Environment.GetEnvironmentVariable("SWYF_API_KEY")
                ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
                cfg.ApiKey = envKey.Trim();
        }

        return cfg;
    }

    private static bool IsPlaceholderKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return true;
        string trimmed = key.Trim();
        return trimmed.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("<YOUR_", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "sk-...", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "AIza...", StringComparison.OrdinalIgnoreCase);
    }

    private static void SaveModConfig(string gameDir, ModConfig cfg)
    {
        string json =
            "{\r\n" +
            "  \"provider\": \"" + EscapeJson(cfg.Provider) + "\",\r\n" +
            "  \"api_key\": \"" + EscapeJson(cfg.ApiKey) + "\",\r\n" +
            "  \"fallback_api_key\": \"" + EscapeJson(cfg.FallbackApiKey) + "\",\r\n" +
            "  \"endpoint\": \"" + EscapeJson(cfg.Endpoint) + "\",\r\n" +
            "  \"models_endpoint\": \"" + EscapeJson(cfg.ModelsEndpoint) + "\",\r\n" +
            "  \"stt_endpoint\": \"" + EscapeJson(cfg.SttEndpoint) + "\",\r\n" +
            "  \"stt_mode\": \"" + EscapeJson(cfg.SttMode) + "\",\r\n" +
            "  \"model\": \"" + EscapeJson(cfg.Model) + "\",\r\n" +
            "  \"voice_model\": \"" + EscapeJson(cfg.VoiceModel) + "\",\r\n" +
            "  \"enable_whisper_fallback\": " + (cfg.EnableWhisperFallback ? "true" : "false") + "\r\n" +
            "}\r\n";

        string aiModConfigPath = Path.Combine(gameDir, "ai_mod_config.json");
        File.WriteAllText(aiModConfigPath, json, new UTF8Encoding(false));
        Console.WriteLine("  + Wrote configuration: " + aiModConfigPath);

        string surplusConfigPath = Path.Combine(gameDir, "surplus_config.json");
        File.WriteAllText(surplusConfigPath, json, new UTF8Encoding(false));
    }

    private static string EscapeJson(string s)
    {
        return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static bool IsModInstalled(string gameDir)
    {
        try
        {
            string bridgePath = Path.Combine(gameDir, @"Scam With Your Friends_Data\Managed\SurplusBridge.dll");
            string bakPath = Path.Combine(gameDir, @"Scam With Your Friends_Data\Managed\Assembly-CSharp.dll.bak");
            return File.Exists(bridgePath) && File.Exists(bakPath);
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractOrCopySurplusBridge(string managedDir)
    {
        string destPath = Path.Combine(managedDir, "SurplusBridge.dll");
        string localBridge = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SurplusBridge.dll");
        if (File.Exists(localBridge))
        {
            File.Copy(localBridge, destPath, true);
            Console.WriteLine("  + Installed SurplusBridge.dll from tool directory");
            return;
        }

        using (Stream res = Assembly.GetExecutingAssembly().GetManifestResourceStream("SurplusBridge.dll"))
        {
            if (res == null)
                throw new FileNotFoundException("SurplusBridge.dll not found as embedded resource or alongside SwyfModTool.exe.");
            using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                res.CopyTo(fs);
            }
            Console.WriteLine("  + Extracted embedded SurplusBridge.dll to Managed directory");
        }
    }

    private static void InstallAndPatchWithCecil(string gameDir, ModConfig cfg)
    {
        string managedDir = Path.Combine(gameDir, @"Scam With Your Friends_Data\Managed");
        string streamingJsonDir = Path.Combine(gameDir, @"Scam With Your Friends_Data\StreamingAssets\kolkata-json");

        Console.WriteLine("[1/4] Backing up / preparing clean game assemblies (.bak)...");
        bool isFreshSteamUpdate = false;
        string scriptsAssDefPath = Path.Combine(managedDir, "ScriptsAssDef.dll");
        if (File.Exists(scriptsAssDefPath))
        {
            using (var checkAsm = AssemblyDefinition.ReadAssembly(scriptsAssDefPath))
            {
                bool hasBridgeRef = checkAsm.MainModule.AssemblyReferences.Any(r => string.Equals(r.Name, "SurplusBridge", StringComparison.OrdinalIgnoreCase));
                if (!hasBridgeRef)
                {
                    isFreshSteamUpdate = true;
                }
            }
        }

        foreach (string dll in DllFiles)
        {
            string fullPath = Path.Combine(managedDir, dll);
            string bakPath = fullPath + ".bak";
            if (!File.Exists(bakPath) || isFreshSteamUpdate)
            {
                File.Copy(fullPath, bakPath, true);
                Console.WriteLine(isFreshSteamUpdate
                    ? "  + Detected fresh Steam game update -> refreshed backup: " + dll + ".bak"
                    : "  + Created backup: " + dll + ".bak");
            }
            else
            {
                File.Copy(bakPath, fullPath, true);
                Console.WriteLine("  = Restored clean base from backup: " + dll + ".bak");
            }
        }

        string modelSettingsPath = Path.Combine(streamingJsonDir, "model-settings.json");
        string modelSettingsBakPath = modelSettingsPath + ".bak";
        if (File.Exists(modelSettingsPath))
        {
            if (!File.Exists(modelSettingsBakPath) || isFreshSteamUpdate)
                File.Copy(modelSettingsPath, modelSettingsBakPath, true);
            else
                File.Copy(modelSettingsBakPath, modelSettingsPath, true);

            string activeModel = string.IsNullOrWhiteSpace(cfg.Model) ? "gemini-2.5-flash" : cfg.Model.Trim();
            string msText = File.ReadAllText(modelSettingsPath);
            msText = msText
                .Replace("deepseek/deepseek-v4.1-flash", activeModel)
                .Replace("inclusionai/ling-3.0-flash", activeModel)
                .Replace("\"max_tokens\": 2048", "\"max_tokens\": 4096");
            File.WriteAllText(modelSettingsPath, msText, new UTF8Encoding(false));
            Console.WriteLine($"  + Updated model-settings.json default model -> {activeModel} (max_tokens=4096)");
        }

        Console.WriteLine("\n[2/4] Installing SurplusBridge.dll & patching game assemblies in-place via Mono.Cecil...");
        ExtractOrCopySurplusBridge(managedDir);
        SaveModConfig(gameDir, cfg);

        string bridgeDestPath = Path.Combine(managedDir, "SurplusBridge.dll");
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDir);

        using (var bridgeAsm = AssemblyDefinition.ReadAssembly(bridgeDestPath, new ReaderParameters { AssemblyResolver = resolver }))
        {
            var bridgeType = bridgeAsm.MainModule.GetType("SurplusBridge");
            PatchWhisperUnity(Path.Combine(managedDir, "com.whisper.unity.dll"), resolver);
            PatchMetaVosk(Path.Combine(managedDir, "MetaVosk.dll"), resolver);
            PatchScriptsAssDef(Path.Combine(managedDir, "ScriptsAssDef.dll"), resolver, bridgeType);
            PatchAssemblyCSharp(Path.Combine(managedDir, "Assembly-CSharp.dll"), resolver, bridgeType, cfg.Model);
        }

        // Verify 100% of assembly & member references against stripped Unity Managed DLLs
        using (var strictResolver = new StrictManagedDirectoryResolver(managedDir))
        {
            foreach (string checkDll in new[] { "SurplusBridge.dll", "ScriptsAssDef.dll", "Assembly-CSharp.dll", "MetaVosk.dll", "com.whisper.unity.dll" })
            {
                using var checkAsm = AssemblyDefinition.ReadAssembly(Path.Combine(managedDir, checkDll), new ReaderParameters { AssemblyResolver = strictResolver });
                foreach (var aref in checkAsm.MainModule.AssemblyReferences)
                {
                    string refPath = Path.Combine(managedDir, aref.Name + ".dll");
                    if (!File.Exists(refPath))
                        throw new Exception($"CRITICAL: {checkDll} references missing assembly '{aref.FullName}' ({refPath} not found)!");
                }

                if (checkDll == "SurplusBridge.dll")
                {
                    int resolvedTypes = 0, resolvedMethods = 0, resolvedFields = 0;
                    foreach (var tr in checkAsm.MainModule.GetTypeReferences())
                    {
                        if (tr.Resolve() == null)
                            throw new Exception($"CRITICAL: SurplusBridge.dll TypeReference '{tr.FullName}' was stripped from Managed!");
                        resolvedTypes++;
                    }
                    foreach (var mr in checkAsm.MainModule.GetMemberReferences())
                    {
                        if (mr is MethodReference methRef)
                        {
                            if (methRef.Resolve() == null)
                                throw new Exception($"CRITICAL: SurplusBridge.dll MethodReference '{methRef.FullName}' was stripped from Managed!");
                            resolvedMethods++;
                        }
                        else if (mr is FieldReference fieldRef)
                        {
                            if (fieldRef.Resolve() == null)
                                throw new Exception($"CRITICAL: SurplusBridge.dll FieldReference '{fieldRef.FullName}' was stripped from Managed!");
                            resolvedFields++;
                        }
                    }
                    Console.WriteLine($"  + Verified SurplusBridge.dll against Unity Managed DLLs ({resolvedTypes} types, {resolvedMethods} methods, {resolvedFields} fields resolved 100%)");
                }

                foreach (var t in checkAsm.MainModule.GetTypes())
                {
                    foreach (var m in t.Methods.Where(x => x.HasBody))
                    {
                        int maxArgs = m.Parameters.Count + (m.IsStatic ? 0 : 1);
                        int maxLocals = m.Body.Variables.Count;
                        var insSet = new HashSet<Instruction>(m.Body.Instructions);
                        foreach (var ins in m.Body.Instructions)
                        {
                            int argIdx = -1;
                            if (ins.OpCode.Code == Code.Ldarg_0) argIdx = 0;
                            else if (ins.OpCode.Code == Code.Ldarg_1) argIdx = 1;
                            else if (ins.OpCode.Code == Code.Ldarg_2) argIdx = 2;
                            else if (ins.OpCode.Code == Code.Ldarg_3) argIdx = 3;
                            else if (ins.Operand is ParameterDefinition pd) argIdx = pd.Index + (m.IsStatic ? 0 : 1);

                            if (argIdx >= maxArgs)
                                throw new Exception($"CRITICAL INVALID IL in {checkDll} {m.FullName}: {ins} accesses arg {argIdx} but maxArgs={maxArgs} (IsStatic={m.IsStatic})!");

                            int locIdx = -1;
                            if (ins.OpCode.Code == Code.Ldloc_0 || ins.OpCode.Code == Code.Stloc_0) locIdx = 0;
                            else if (ins.OpCode.Code == Code.Ldloc_1 || ins.OpCode.Code == Code.Stloc_1) locIdx = 1;
                            else if (ins.OpCode.Code == Code.Ldloc_2 || ins.OpCode.Code == Code.Stloc_2) locIdx = 2;
                            else if (ins.OpCode.Code == Code.Ldloc_3 || ins.OpCode.Code == Code.Stloc_3) locIdx = 3;
                            else if (ins.Operand is VariableDefinition vd) locIdx = vd.Index;

                            if (locIdx >= maxLocals)
                                throw new Exception($"CRITICAL INVALID IL in {checkDll} {m.FullName}: {ins} accesses local {locIdx} but maxLocals={maxLocals}!");

                            if (ins.Operand is Instruction targetIns && !insSet.Contains(targetIns))
                                throw new Exception($"CRITICAL INVALID IL in {checkDll} {m.FullName}: {ins} branches to instruction outside method body!");
                        }
                    }
                }
            }
        }

        Console.WriteLine("\n[3/4] Adjusting in-game settings & cleaning legacy model cache...");
        ConfigureInGameSettingsAndCleanOldModels(gameDir, cfg);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n========================================================================");
        Console.WriteLine("  MOD INSTALLED & VERIFIED SUCCESSFULLY!");
        Console.WriteLine("========================================================================");
        Console.ResetColor();
        Console.WriteLine($"  - Provider     : {cfg.Provider}");
        Console.WriteLine($"  - Chat Model   : {cfg.Model}");
        Console.WriteLine($"  - Voice Model  : {cfg.VoiceModel} (mode: {cfg.SttMode})");
        Console.WriteLine($"  - Config File  : {Path.Combine(gameDir, "ai_mod_config.json")}");
    }

    sealed class StrictManagedDirectoryResolver : IAssemblyResolver
    {
        private readonly string _dir;
        private readonly Dictionary<string, AssemblyDefinition> _cache = new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);
        public StrictManagedDirectoryResolver(string dir) => _dir = dir;
        public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters { AssemblyResolver = this });
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (_cache.TryGetValue(name.Name, out var asm)) return asm;
            string p = Path.Combine(_dir, name.Name + ".dll");
            if (!File.Exists(p)) throw new FileNotFoundException($"Missing in Managed: {p}");
            asm = AssemblyDefinition.ReadAssembly(p, new ReaderParameters { AssemblyResolver = this });
            _cache[name.Name] = asm;
            return asm;
        }
        public void Dispose()
        {
            foreach (var a in _cache.Values) a.Dispose();
            _cache.Clear();
        }
    }

    static void PatchWhisperUnity(string dllPath, IAssemblyResolver resolver)
    {
        string tmpPath = dllPath + ".tmp";
        using (var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { AssemblyResolver = resolver }))
        {
            var wl = asm.MainModule.Types.First(t => t.FullName == "Whisper.WhisperLanguage");
            var getLangStr = wl.Methods.First(m => m.Name == "GetLanguageString");
            getLangStr.Body.Instructions.Clear();
            getLangStr.Body.Variables.Clear();
            getLangStr.Body.ExceptionHandlers.Clear();
            var il = getLangStr.Body.GetILProcessor();
            il.Emit(OpCodes.Ldstr, "en");
            il.Emit(OpCodes.Ret);

            // Prevent WhisperManager.Awake from auto-loading the ~400MB local Whisper model on startup
            var wm = asm.MainModule.Types.First(t => t.FullName == "Whisper.WhisperManager");
            var initOnAwakeField = wm.Fields.First(f => f.Name == "initOnAwake");
            var awakeMethod = wm.Methods.First(m => m.Name == "Awake");
            awakeMethod.Body.Instructions.Clear();
            awakeMethod.Body.Variables.Clear();
            awakeMethod.Body.ExceptionHandlers.Clear();
            var awakeIl = awakeMethod.Body.GetILProcessor();
            awakeIl.Emit(OpCodes.Ldarg_0);
            awakeIl.Emit(OpCodes.Ldc_I4_0);
            awakeIl.Emit(OpCodes.Stfld, initOnAwakeField);
            awakeIl.Emit(OpCodes.Ret);

            asm.Write(tmpPath);
        }
        if (File.Exists(dllPath)) File.Delete(dllPath);
        File.Move(tmpPath, dllPath);
        Console.WriteLine("  + Patched com.whisper.unity.dll (WhisperLanguage.GetLanguageString -> \"en\", disabled auto-load in WhisperManager.Awake to save ~400MB RAM)");
    }

    static void PatchMetaVosk(string dllPath, IAssemblyResolver resolver)
    {
        string tmpPath = dllPath + ".tmp";
        using (var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { AssemblyResolver = resolver }))
        {
            var stp = asm.MainModule.Types.First(t => t.Name == "SpeechTranscriptPostprocessor");
            var isUnwanted = stp.Methods.First(m => m.Name == "IsUnwantedOneWordTranscript");
            isUnwanted.Body.Instructions.Clear();
            isUnwanted.Body.Variables.Clear();
            isUnwanted.Body.ExceptionHandlers.Clear();
            var il = isUnwanted.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            asm.Write(tmpPath);
        }
        if (File.Exists(dllPath)) File.Delete(dllPath);
        File.Move(tmpPath, dllPath);
        Console.WriteLine("  + Patched MetaVosk.dll (Single-word voice filter disabled)");
    }

    static void PatchScriptsAssDef(string dllPath, IAssemblyResolver resolver, TypeDefinition bridgeType)
    {
        string tmpPath = dllPath + ".tmp";
        using (var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { AssemblyResolver = resolver }))
        {
            var mod = asm.MainModule;

            var getApiKeyRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "GetApiKey"));
            var getSelectedModelRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "GetSelectedModel"));
            var getFavoriteModelsRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "GetFavoriteModels"));
            var parseModelsCatalogRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "ParseModelsCatalog"));
            var getModelsEndpointRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "GetModelsEndpoint"));
            var transcribeAsyncRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "TranscribeAsync"));
            var isWhisperFallbackEnabledRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "IsWhisperFallbackEnabled"));
            var shouldSkipWhisperModelLoadRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "ShouldSkipWhisperModelLoad"));
            var trySetWhisperFallbackFromUiRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "TrySetWhisperFallbackFromUi"));
            var trySetApiKeyFromUiRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "TrySetApiKeyFromUi"));
            var trySetModelFromUiRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "TrySetModelFromUi"));
            var getKeyStatusTextRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "GetKeyStatusText"));
            var interceptMenuTextRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "InterceptMenuText"));

            var analyticsCol = mod.Types.First(t => t.Name == "AnalyticsCollection");
            ReplaceWithReturnBool(analyticsCol.Methods.First(m => m.Name == "get_Enabled"), false);
            ReplaceWithReturnBool(analyticsCol.Methods.First(m => m.Name == "ConsumeVoiceActivity"), false);
            ReplaceWithNoOp(analyticsCol.Methods.First(m => m.Name == "add_Emitted"));
            ReplaceWithNoOp(analyticsCol.Methods.First(m => m.Name == "add_EnabledChanged"));

            var sgm = mod.Types.First(t => t.Name == "ScammerGameManager");
            ReplaceWithReturnBool(sgm.Methods.First(m => m.Name == "get_OfficialAiBackend"), false);
            var setOfficial = sgm.Methods.First(m => m.Name == "SetOfficialAiBackend");
            for (int i = 0; i < setOfficial.Body.Instructions.Count; i++)
            {
                if (setOfficial.Body.Instructions[i].OpCode == OpCodes.Ldarg_1)
                    setOfficial.Body.Instructions[i] = Instruction.Create(OpCodes.Ldc_I4_0);
            }

            var hostAi = mod.Types.First(t => t.Name == "HostAiSettings");
            ReplaceWithReturnBool(hostAi.Methods.First(m => m.Name == "get_OpenRouterSelected"), true);
            ReplaceWithReturnBool(hostAi.Methods.First(m => m.Name == "get_DirectSelected"), true);
            ReplaceWithReturnBool(hostAi.Methods.First(m => m.Name == "get_HasSavedKey"), true);
            ReplaceWithReturnBool(hostAi.Methods.First(m => m.Name == "get_CanEdit"), true);
            ReplaceWithCallStatic(hostAi.Methods.First(m => m.Name == "get_ApiKey"), getApiKeyRef);
            ReplaceWithCallStatic(hostAi.Methods.First(m => m.Name == "get_SelectedModel"), getSelectedModelRef);
            ReplaceWithCallStatic(hostAi.Methods.First(m => m.Name == "get_FavoriteModels"), getFavoriteModelsRef);
            ReplaceWithCallStatic1Arg(hostAi.Methods.First(m => m.Name == "TrySetKey" && m.Parameters.Count == 1), trySetApiKeyFromUiRef);
            ReplaceWithCallStatic1Arg(hostAi.Methods.First(m => m.Name == "TrySetBackend"), trySetWhisperFallbackFromUiRef);
            ReplaceWithCallStatic1Arg(hostAi.Methods.First(m => m.Name == "TrySetModel"), trySetModelFromUiRef);

            // Unlock the "AI MOD & HOST" tab in Main Menu & Pause Menu Settings AND inject AI Mod Settings into General tab
            var onPauseMenuInitializedRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "OnPauseMenuInitialized"));
            var pauseCtrl = mod.Types.FirstOrDefault(t => t.Name == "PauseMenuToolkitController");
            if (pauseCtrl != null)
            {
                var initUi = pauseCtrl.Methods.FirstOrDefault(m => m.Name == "InitializeUi");
                var rootField = pauseCtrl.Fields.FirstOrDefault(f => f.Name == "root");
                if (initUi != null && initUi.HasBody && rootField != null)
                {
                    initUi.Body.SimplifyMacros();
                    var ilp = initUi.Body.GetILProcessor();
                    var lastRet = initUi.Body.Instructions.Last();
                    ilp.InsertBefore(lastRet, Instruction.Create(OpCodes.Ldarg_0));
                    ilp.InsertBefore(lastRet, Instruction.Create(OpCodes.Ldfld, rootField));
                    ilp.InsertBefore(lastRet, Instruction.Create(OpCodes.Call, onPauseMenuInitializedRef));
                    initUi.Body.OptimizeMacros();
                }

                var refreshCtx = pauseCtrl.Methods.FirstOrDefault(m => m.Name == "RefreshContextualActions");
                if (refreshCtx != null && refreshCtx.HasBody)
                {
                    refreshCtx.Body.SimplifyMacros();
                    int replacedActiveHost = 0;
                    for (int i = 0; i < refreshCtx.Body.Instructions.Count; i++)
                    {
                        var ins = refreshCtx.Body.Instructions[i];
                        if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == "get_activeHost")
                        {
                            refreshCtx.Body.Instructions[i] = Instruction.Create(OpCodes.Ldc_I4_1);
                            replacedActiveHost++;
                            if (replacedActiveHost >= 2)
                                break;
                        }
                    }
                    refreshCtx.Body.OptimizeMacros();
                }
            }

            // Also hook MainMenuToolkitController.InitializeUi so the Live Debug Server starts immediately on Main Menu load
            var initLiveDebugRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "InitLiveDebugServer"));
            var mainMenuCtrl = mod.Types.FirstOrDefault(t => t.Name == "MainMenuToolkitController");
            if (mainMenuCtrl != null)
            {
                var mmInit = mainMenuCtrl.Methods.FirstOrDefault(m => m.Name == "InitializeUi");
                if (mmInit != null && mmInit.HasBody)
                {
                    mmInit.Body.SimplifyMacros();
                    var ilp = mmInit.Body.GetILProcessor();
                    ilp.InsertBefore(mmInit.Body.Instructions[0], Instruction.Create(OpCodes.Call, initLiveDebugRef));
                    mmInit.Body.OptimizeMacros();
                }
            }

            var cat = mod.Types.First(t => t.Name == "OpenRouterModelCatalog");
            var parseMethod = cat.Methods.First(m => m.Name == "Parse");
            parseMethod.Body.Instructions.Clear();
            parseMethod.Body.Variables.Clear();
            parseMethod.Body.ExceptionHandlers.Clear();
            {
                var ilp = parseMethod.Body.GetILProcessor();
                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Call, parseModelsCatalogRef);
                ilp.Emit(OpCodes.Ret);
            }

            var loadAsyncMoveNext = cat.NestedTypes.First(nt => nt.Name.Contains("LoadAsync")).Methods.First(m => m.Name == "MoveNext");
            for (int i = 0; i < loadAsyncMoveNext.Body.Instructions.Count; i++)
            {
                var ins = loadAsyncMoveNext.Body.Instructions[i];
                if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "https://openrouter.ai/api/v1/models")
                {
                    loadAsyncMoveNext.Body.Instructions[i] = Instruction.Create(OpCodes.Call, getModelsEndpointRef);
                }
            }

            var mmStatus = mod.Types.FirstOrDefault(t => t.Name == "MainMenuConnectionStatus");
            if (mmStatus != null)
            {
                foreach (var m in mmStatus.Methods.Where(x => x.HasBody))
                {
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "AI BACKEND: SIGNED IN")
                            ins.Operand = "MOD INSTALLED: CUSTOM AI + VOICE READY";
                    }
                }
            }

            var hostPanel = mod.Types.FirstOrDefault(t => t.Name == "HostAiSettingsPanel");
            if (hostPanel != null)
            {
                var panelCtor = hostPanel.Methods.FirstOrDefault(m => m.Name == ".ctor");
                if (panelCtor != null && panelCtor.HasBody)
                {
                    panelCtor.Body.SimplifyMacros();
                    for (int i = 0; i < panelCtor.Body.Instructions.Count; i++)
                    {
                        var ins = panelCtor.Body.Instructions[i];
                        if (ins.OpCode == OpCodes.Callvirt && ins.Operand is MethodReference mr && mr.Name == "set_isPasswordField")
                        {
                            if (i > 0 && panelCtor.Body.Instructions[i - 1].OpCode == OpCodes.Ldc_I4)
                                panelCtor.Body.Instructions[i - 1].Operand = 0;
                            else if (i > 0 && panelCtor.Body.Instructions[i - 1].OpCode == OpCodes.Ldc_I4_1)
                                panelCtor.Body.Instructions[i - 1] = Instruction.Create(OpCodes.Ldc_I4_0);
                        }
                    }
                    panelCtor.Body.OptimizeMacros();
                }

                var panelRefresh = hostPanel.Methods.FirstOrDefault(m => m.Name == "Refresh");
                var keyStatusField = hostPanel.Fields.FirstOrDefault(f => f.Name == "keyStatus");
                var setTextRef = panelRefresh?.Body.Instructions
                    .Select(x => x.Operand).OfType<MethodReference>()
                    .FirstOrDefault(mr => mr.Name == "set_text" && mr.DeclaringType.Name == "TextElement");

                if (panelRefresh != null && panelRefresh.HasBody && keyStatusField != null && setTextRef != null)
                {
                    panelRefresh.Body.SimplifyMacros();
                    for (int i = 0; i < panelRefresh.Body.Instructions.Count; i++)
                    {
                        var ins = panelRefresh.Body.Instructions[i];
                        if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == "get_OpenRouterSelected")
                        {
                            panelRefresh.Body.Instructions[i] = Instruction.Create(OpCodes.Call, isWhisperFallbackEnabledRef);
                            break;
                        }
                    }
                    {
                        var ilp = panelRefresh.Body.GetILProcessor();
                        var lastRet = panelRefresh.Body.Instructions.Last();
                        var ldArg0 = Instruction.Create(OpCodes.Ldarg_0);
                        ilp.InsertBefore(lastRet, ldArg0);
                        ilp.InsertBefore(lastRet, Instruction.Create(OpCodes.Ldfld, keyStatusField));
                        ilp.InsertBefore(lastRet, Instruction.Create(OpCodes.Call, getKeyStatusTextRef));
                        ilp.InsertBefore(lastRet, Instruction.Create(OpCodes.Callvirt, setTextRef));
                        foreach (var bIns in panelRefresh.Body.Instructions)
                        {
                            if (bIns.Operand == lastRet)
                                bIns.Operand = ldArg0;
                        }
                    }
                    panelRefresh.Body.OptimizeMacros();
                }

                var allPanelMethods = hostPanel.Methods.Concat(hostPanel.NestedTypes.SelectMany(nt => nt.Methods)).Where(x => x.HasBody);
                foreach (var m in allPanelMethods)
                {
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (ins.OpCode != OpCodes.Ldstr) continue;
                        string s = (string)ins.Operand;
                        if (s == "Official Backend")
                            ins.Operand = "Whisper Fallback: OFF (Saves ~400MB RAM)";
                        else if (s == "OpenRouter (Bring Your Own Key)")
                            ins.Operand = "Whisper Fallback: ON (Local Offline Backup)";
                        else if (s == "Selected: ")
                            ins.Operand = "Active AI Model: ";
                        else if (s == "Loading OpenRouter models.")
                            ins.Operand = "Loading models from configured AI endpoint...";
                        else if (s == " models. Select one to use it.")
                            ins.Operand = " model(s) available — Click any model or edit API Token above";
                    }
                }
            }

            // 10/18/2026 Update Compatibility: disable Moonshine CPU recognizer & Deepgram branches so 100% of voice recognition routes to SurplusBridge.TranscribeAsync without loading Moonshine into RAM
            var winSpeechCompat = mod.Types.FirstOrDefault(t => t.Name == "WindowsSpeechCompatibility");
            if (winSpeechCompat != null)
            {
                var useMoonshine = winSpeechCompat.Methods.FirstOrDefault(m => m.Name == "UseMoonshine");
                if (useMoonshine != null) ReplaceWithReturnBool(useMoonshine, false);
                var preferMoonshine = winSpeechCompat.Methods.FirstOrDefault(m => m.Name == "get_PreferMoonshine");
                if (preferMoonshine != null) ReplaceWithReturnBool(preferMoonshine, false);
                var forceMoonshine = winSpeechCompat.Methods.FirstOrDefault(m => m.Name == "get_ForceMoonshine");
                if (forceMoonshine != null) ReplaceWithReturnBool(forceMoonshine, false);
            }

            var deepgramSettings = mod.Types.FirstOrDefault(t => t.Name == "DeepgramSpeechSettings");
            if (deepgramSettings != null)
            {
                var dgSelected = deepgramSettings.Methods.FirstOrDefault(m => m.Name == "get_Selected");
                if (dgSelected != null) ReplaceWithReturnBool(dgSelected, false);
            }

            var deepgramOffer = mod.Types.FirstOrDefault(t => t.Name == "DeepgramSpeechOffer");
            if (deepgramOffer != null)
            {
                var dgShow = deepgramOffer.Methods.FirstOrDefault(m => m.Name == "Show");
                if (dgShow != null) ReplaceWithNoOp(dgShow);
            }

            var onSpeechStartedRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "OnSpeechStarted"));
            var onSpeechFinalizingRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "OnSpeechFinalizing"));
            var wvc = mod.Types.First(t => t.Name == "WhisperVcPipeline");
            var silenceField = wvc.Fields.First(f => f.Name == "silenceToFinalizeSeconds");
            var partialIntervalField = wvc.Fields.First(f => f.Name == "partialIntervalSeconds");
            var isSpeakingField = wvc.Fields.First(f => f.Name == "isSpeaking");
            var trailingSilenceField = wvc.Fields.First(f => f.Name == "trailingSilenceSamples");
            var speechSamplesField = wvc.Fields.First(f => f.Name == "speechSamples");
            var samplesSincePartialField = wvc.Fields.First(f => f.Name == "samplesSincePartial");
            var utteranceBufferField = wvc.Fields.First(f => f.Name == "utteranceBuffer");
            var queueInferenceMethod = wvc.Methods.First(m => m.Name == "QueueInference");

            queueInferenceMethod.Body.SimplifyMacros();
            foreach (var ins in queueInferenceMethod.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldc_I4 && (int)ins.Operand == 17600)
                    ins.Operand = 1600;
            }
            queueInferenceMethod.Body.OptimizeMacros();

            var ctor = wvc.Methods.First(m => m.Name == ".ctor");
            foreach (var ins in ctor.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)ins.Operand - 1.25f) < 0.01f)
                    ins.Operand = 0.55f;
                else if (ins.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)ins.Operand - 1.0f) < 0.01f)
                    ins.Operand = 1.25f;
            }

            var processFrame = wvc.Methods.First(m => m.Name == "ProcessFrame");
            processFrame.Body.SimplifyMacros();
            {
                var ilp = processFrame.Body.GetILProcessor();
                var first = processFrame.Body.Instructions.First();
                ilp.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
                ilp.InsertBefore(first, Instruction.Create(OpCodes.Ldc_R4, 0.55f));
                ilp.InsertBefore(first, Instruction.Create(OpCodes.Stfld, silenceField));
                ilp.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
                ilp.InsertBefore(first, Instruction.Create(OpCodes.Ldc_R4, 1.25f));
                ilp.InsertBefore(first, Instruction.Create(OpCodes.Stfld, partialIntervalField));

                for (int i = 0; i < processFrame.Body.Instructions.Count; i++)
                {
                    var ins = processFrame.Body.Instructions[i];
                    if (ins.OpCode == OpCodes.Stfld && ins.Operand is FieldReference fr && fr.Name == "isSpeaking")
                    {
                        ilp.InsertAfter(ins, Instruction.Create(OpCodes.Call, onSpeechStartedRef));
                        break;
                    }
                }
            }
            for (int i = 0; i < processFrame.Body.Instructions.Count; i++)
            {
                var ins = processFrame.Body.Instructions[i];
                if (ins.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)ins.Operand - 2.0f) < 0.01f)
                {
                    ins.Operand = 0.65f;
                    break;
                }
            }
            processFrame.Body.OptimizeMacros();

            var finalizeMethod = wvc.Methods.First(m => m.Name == "FinalizeUtterance");
            var toArrayMethodRef = finalizeMethod.Body.Instructions
                .Select(x => x.Operand).OfType<MethodReference>()
                .First(mr => mr.Name == "ToArray");
            var clearMethodRef = finalizeMethod.Body.Instructions
                .Select(x => x.Operand).OfType<MethodReference>()
                .First(mr => mr.Name == "Clear");
            finalizeMethod.Body.Instructions.Clear();
            finalizeMethod.Body.Variables.Clear();
            finalizeMethod.Body.ExceptionHandlers.Clear();
            finalizeMethod.Body.InitLocals = true;
            var samplesVar = new VariableDefinition(new ArrayType(mod.TypeSystem.Single));
            var hasEnoughSpeechVar = new VariableDefinition(mod.TypeSystem.Boolean);
            finalizeMethod.Body.Variables.Add(samplesVar);
            finalizeMethod.Body.Variables.Add(hasEnoughSpeechVar);
            {
                var ilp = finalizeMethod.Body.GetILProcessor();
                var retLabel = Instruction.Create(OpCodes.Ret);

                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Ldfld, speechSamplesField);
                ilp.Emit(OpCodes.Ldc_I4, 2400);
                ilp.Emit(OpCodes.Clt);
                ilp.Emit(OpCodes.Ldc_I4_0);
                ilp.Emit(OpCodes.Ceq);
                ilp.Emit(OpCodes.Stloc, hasEnoughSpeechVar);

                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Ldfld, utteranceBufferField);
                ilp.Emit(OpCodes.Callvirt, toArrayMethodRef);
                ilp.Emit(OpCodes.Stloc, samplesVar);

                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Ldc_I4_0);
                ilp.Emit(OpCodes.Stfld, isSpeakingField);
                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Ldc_I4_0);
                ilp.Emit(OpCodes.Stfld, trailingSilenceField);
                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Ldc_I4_0);
                ilp.Emit(OpCodes.Stfld, speechSamplesField);
                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Ldc_I4_0);
                ilp.Emit(OpCodes.Stfld, samplesSincePartialField);
                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Ldfld, utteranceBufferField);
                ilp.Emit(OpCodes.Callvirt, clearMethodRef);

                ilp.Emit(OpCodes.Ldloc, samplesVar);
                ilp.Emit(OpCodes.Call, onSpeechFinalizingRef);

                ilp.Emit(OpCodes.Ldloc, hasEnoughSpeechVar);
                ilp.Emit(OpCodes.Brfalse, retLabel);
                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Ldloc, samplesVar);
                ilp.Emit(OpCodes.Ldc_I4_1);
                ilp.Emit(OpCodes.Call, queueInferenceMethod);
                ilp.Append(retLabel);
            }

            var calcConf = wvc.Methods.First(m => m.Name == "CalculateConfidence");
            for (int i = calcConf.Body.Instructions.Count - 1; i >= 0; i--)
            {
                var ins = calcConf.Body.Instructions[i];
                if (ins.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)ins.Operand - 0.0f) < 0.001f)
                {
                    ins.Operand = 0.99f;
                    break;
                }
            }

            var ensureReadyType = wvc.NestedTypes.First(nt => nt.Name.Contains("EnsureRecognizerReady"));
            var ensureReadyMoveNext = ensureReadyType.Methods.First(m => m.Name == "MoveNext");
            ensureReadyMoveNext.Body.SimplifyMacros();
            for (int i = 0; i < ensureReadyMoveNext.Body.Instructions.Count; i++)
            {
                var ins = ensureReadyMoveNext.Body.Instructions[i];
                if (ins.OpCode == OpCodes.Callvirt && ins.Operand is MethodReference mr && mr.Name == "get_IsLoaded")
                {
                    ensureReadyMoveNext.Body.GetILProcessor().Replace(ins, Instruction.Create(OpCodes.Call, shouldSkipWhisperModelLoadRef));
                    break;
                }
            }
            ensureReadyMoveNext.Body.OptimizeMacros();

            var runInfType = wvc.NestedTypes.First(nt => nt.Name.Contains("RunInferenceAsync"));
            var runInfMoveNext = runInfType.Methods.First(m => m.Name == "MoveNext");
            var reqField = runInfType.Fields.First(f => f.Name == "request");
            var reqLangField = runInfType.Fields.First(f => f.Name.Contains("requestedLanguage"));
            var isFinalField = reqField.FieldType.Resolve().Fields.First(f => f.Name == "IsFinal");

            runInfMoveNext.Body.SimplifyMacros();
            for (int i = 0; i < runInfMoveNext.Body.Instructions.Count; i++)
            {
                var ins = runInfMoveNext.Body.Instructions[i];
                if (ins.OpCode == OpCodes.Callvirt && ins.Operand is MethodReference mr && mr.Name == "get_IsLoaded")
                {
                    var ilp = runInfMoveNext.Body.GetILProcessor();
                    var popIns = Instruction.Create(OpCodes.Pop);
                    var trueIns = Instruction.Create(OpCodes.Ldc_I4_1);
                    ilp.Replace(ins, popIns);
                    ilp.InsertAfter(popIns, trueIns);
                    break;
                }
            }

            for (int i = 0; i < runInfMoveNext.Body.Instructions.Count; i++)
            {
                var ins = runInfMoveNext.Body.Instructions[i];
                if (ins.OpCode == OpCodes.Callvirt && ins.Operand is MethodReference mr && mr.Name == "GetTextAsync")
                {
                    var ilp = runInfMoveNext.Body.GetILProcessor();
                    var prev2 = runInfMoveNext.Body.Instructions[i - 2];
                    var prev1 = runInfMoveNext.Body.Instructions[i - 1];
                    if (prev2.OpCode.Code == Code.Ldc_I4 && (int)prev2.Operand == 16000 &&
                        prev1.OpCode.Code == Code.Ldc_I4 && (int)prev1.Operand == 1)
                    {
                        ilp.Remove(prev2);
                        ilp.Remove(prev1);

                        ilp.InsertBefore(ins, Instruction.Create(OpCodes.Ldarg_0));
                        ilp.InsertBefore(ins, Instruction.Create(OpCodes.Ldfld, reqField));
                        ilp.InsertBefore(ins, Instruction.Create(OpCodes.Ldfld, isFinalField));
                        ilp.InsertBefore(ins, Instruction.Create(OpCodes.Ldarg_0));
                        ilp.InsertBefore(ins, Instruction.Create(OpCodes.Ldfld, reqLangField));
                        ilp.Replace(ins, Instruction.Create(OpCodes.Call, transcribeAsyncRef));
                        break;
                    }
                }
            }

            for (int i = 0; i < runInfMoveNext.Body.Instructions.Count; i++)
            {
                var ins = runInfMoveNext.Body.Instructions[i];
                if (ins.OpCode == OpCodes.Stfld && ins.Operand is FieldReference fr && fr.Name == "nextPartialInferenceAt")
                {
                    var maxIns = runInfMoveNext.Body.Instructions[i - 2];
                    if (maxIns.OpCode == OpCodes.Call && maxIns.Operand is MethodReference mr && mr.Name == "Max")
                    {
                        var ilp = runInfMoveNext.Body.GetILProcessor();
                        var pop1 = Instruction.Create(OpCodes.Pop);
                        var pop2 = Instruction.Create(OpCodes.Pop);
                        var fastCooldown = Instruction.Create(OpCodes.Ldc_R8, 0.35);
                        ilp.Replace(maxIns, pop1);
                        ilp.InsertAfter(pop1, pop2);
                        ilp.InsertAfter(pop2, fastCooldown);
                    }
                    break;
                }
            }
            runInfMoveNext.Body.OptimizeMacros();

            asm.Write(tmpPath);
        }
        if (File.Exists(dllPath)) File.Delete(dllPath);
        File.Move(tmpPath, dllPath);
        Console.WriteLine("  + Patched ScriptsAssDef.dll (WhisperVcPipeline + HostAiSettings + ModelCatalog)");
    }

    static void PatchAssemblyCSharp(string dllPath, IAssemblyResolver resolver, TypeDefinition bridgeType, string defaultModelId)
    {
        string tmpPath = dllPath + ".tmp";
        string activeModel = string.IsNullOrWhiteSpace(defaultModelId) ? "gemini-2.5-flash" : defaultModelId.Trim();
        using (var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { AssemblyResolver = resolver }))
        {
            var mod = asm.MainModule;

            var getEndpointRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "GetEndpoint"));
            var prepareOutboundBodyRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "PrepareOutboundBody"));

            var acol = mod.Types.First(t => t.Name == "AnalyticsCollector");
            string[] voidAnalyticsMethods = new[]
            {
                "Awake", "OnDestroy", "Toggle", "Update", "Tick", "AddActivityDay",
                "First", "Event", "Capture", "Queue", "Finish", "FlushSoon",
                "OnApplicationPause", "OnApplicationQuit", "ObserveContext", "Enqueue",
                "Remove", "AbortUpload", "ClearDisk", "Checkpoint", "SetScope",
                "RestoreDisk", "DriveDelivery", "RemoveSent", "HandleResponse", "Retry",
                "UpdateProtocol", "OnHello", "Personal", "OnPersonal", "BroadcastContext",
                "OnContext", "WindowActivity", "Interruption", "CaptureInterruptions",
                "Reaction", "ObserveApp", "Access", "Observe", "EndShared",
                "UpdateNetworkContext", "EndCreditInterruption", "CaptureParticipation"
            };
            foreach (string mName in voidAnalyticsMethods)
            {
                var m = acol.Methods.FirstOrDefault(x => x.Name == mName);
                if (m != null)
                    ReplaceWithNoOp(m);
            }

            var kapi = mod.Types.First(t => t.Name == "KolkataApi");
            ReplaceWithReturnBool(kapi.Methods.First(m => m.Name == "get_BackendAuthenticated"), true);
            ReplaceWithReturnInt(kapi.Methods.First(m => m.Name == "get_BackendAuthStatus"), 2);
            ReplaceWithReturnBool(kapi.Methods.First(m => m.Name == "get_AiBanned"), false);
            ReplaceWithReturnBool(kapi.Methods.First(m => m.Name == "get_AiUsageRestricted"), false);
            ReplaceWithReturnBool(kapi.Methods.First(m => m.Name == "get_AiCreditsDepleted"), false);
            ReplaceWithReturnBool(kapi.Methods.First(m => m.Name == "get_SystemClockIncorrect"), false);
            ReplaceWithNoOp(kapi.Methods.First(m => m.Name == "LogOpenRouterPayload"));

            var edgeAuthStatusField = kapi.Fields.First(f => f.Name == "edgeAuthStatus");
            var startEdgeAuth = kapi.Methods.First(m => m.Name == "StartEdgeAuthentication");
            startEdgeAuth.Body.Instructions.Clear();
            startEdgeAuth.Body.Variables.Clear();
            startEdgeAuth.Body.ExceptionHandlers.Clear();
            {
                var ilp = startEdgeAuth.Body.GetILProcessor();
                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Ldc_I4_2);
                ilp.Emit(OpCodes.Stfld, edgeAuthStatusField);
                ilp.Emit(OpCodes.Ret);
            }
            ReplaceWithNoOp(kapi.Methods.First(m => m.Name == "RefreshAiCreditStatus"));
            ReplaceWithNoOp(kapi.Methods.First(m => m.Name == "ForceReauth"));
            ReplaceWithNoOp(kapi.Methods.First(m => m.Name == "RefreshDiscordVipStatus"));
            ReplaceWithNoOp(kapi.Methods.First(m => m.Name == "OpenDiscordVipLink"));

            var oai = mod.Types.First(t => t.Name == "OpenAIConversationProvider");
            ReplaceWithReturnBool(oai.Methods.First(m => m.Name == "get_HasConfiguredBackend"), true);

            var compEdgeLocal = kapi.Methods.First(m => m.Name == "CompleteEdgeLocalAsync");
            var compOpenRouterMethod = kapi.Methods.First(m => m.Name == "CompleteOpenRouterAsync");
            compEdgeLocal.Body.Instructions.Clear();
            compEdgeLocal.Body.Variables.Clear();
            compEdgeLocal.Body.ExceptionHandlers.Clear();
            {
                var ilp = compEdgeLocal.Body.GetILProcessor();
                ilp.Emit(OpCodes.Ldarg_0);
                ilp.Emit(OpCodes.Ldarg_1);
                ilp.Emit(OpCodes.Ldarg_2);
                ilp.Emit(OpCodes.Ldc_I4_0);
                ilp.Emit(OpCodes.Call, compOpenRouterMethod);
                ilp.Emit(OpCodes.Ret);
            }

            var compType = kapi.NestedTypes.First(nt => nt.Name.Contains("CompleteOpenRouterAsync"));
            var compMoveNext = compType.Methods.First(m => m.Name == "MoveNext");
            compMoveNext.Body.SimplifyMacros();
            {
                var ilp = compMoveNext.Body.GetILProcessor();
                var insList = compMoveNext.Body.Instructions;

                for (int i = 0; i < insList.Count; i++)
                {
                    var ins = insList[i];
                    if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "official")
                    {
                        for (int j = i + 1; j < Math.Min(i + 8, insList.Count); j++)
                        {
                            if (insList[j].OpCode == OpCodes.Brtrue)
                            {
                                ilp.Replace(insList[j], Instruction.Create(OpCodes.Pop));
                                break;
                            }
                        }
                    }

                    if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == "get_UseDirectOpenRouter")
                    {
                        if (i + 1 < insList.Count && insList[i + 1].OpCode == OpCodes.Brfalse)
                            ilp.Replace(insList[i + 1], Instruction.Create(OpCodes.Pop));
                    }

                    if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "celeris" &&
                        i + 2 < insList.Count && insList[i + 2].OpCode.Code == Code.Stloc)
                    {
                        var stlocIns = insList[i + 2];
                        var popIns = Instruction.Create(OpCodes.Pop);
                        var zeroIns = Instruction.Create(OpCodes.Ldc_I4_0);
                        ilp.InsertBefore(stlocIns, popIns);
                        ilp.InsertBefore(stlocIns, zeroIns);
                        foreach (var bIns in insList)
                        {
                            if (bIns.Operand == stlocIns)
                                bIns.Operand = popIns;
                        }
                        i += 2;
                    }

                    if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "https://openrouter.ai/api/v1/chat/completions")
                        ilp.Replace(ins, Instruction.Create(OpCodes.Call, getEndpointRef));
                }
            }
            compMoveNext.Body.OptimizeMacros();

            var gpType = mod.Types.First(t => t.Name == "GameplayPrompts");
            var respFormatMethod = gpType.Methods.First(m => m.Name == "ResponseFormat");
            var jObjSetItem = mod.ImportReference(
                kapi.Methods.First(m => m.Name == "PrepareDirectOpenRouterBody")
                    .Body.Instructions.Select(x => x.Operand).OfType<MethodReference>()
                    .First(mr => mr.Name == "set_Item" && mr.DeclaringType.Name == "JObject"));

            var prepBody = kapi.Methods.First(m => m.Name == "PrepareDirectOpenRouterBody");
            prepBody.Body.SimplifyMacros();
            {
                var ilp = prepBody.Body.GetILProcessor();
                var insList = prepBody.Body.Instructions;
                var isDialogueMethod = kapi.Methods.First(m => m.Name == "IsGameplayDialogueRequest");
                var stloc0 = insList.First(x => x.OpCode == OpCodes.Stloc && ((VariableDefinition)x.Operand).Index == 0);
                var afterStloc0 = stloc0.Next;

                ilp.InsertBefore(afterStloc0, Instruction.Create(OpCodes.Ldloc_0));
                ilp.InsertBefore(afterStloc0, Instruction.Create(OpCodes.Call, isDialogueMethod));
                ilp.InsertBefore(afterStloc0, Instruction.Create(OpCodes.Brfalse, afterStloc0));
                ilp.InsertBefore(afterStloc0, Instruction.Create(OpCodes.Ldloc_0));
                ilp.InsertBefore(afterStloc0, Instruction.Create(OpCodes.Ldstr, "response_format"));
                ilp.InsertBefore(afterStloc0, Instruction.Create(OpCodes.Ldc_I4_0));
                ilp.InsertBefore(afterStloc0, Instruction.Create(OpCodes.Ldnull));
                ilp.InsertBefore(afterStloc0, Instruction.Create(OpCodes.Call, respFormatMethod));
                ilp.InsertBefore(afterStloc0, Instruction.Create(OpCodes.Callvirt, jObjSetItem));
            }
            prepBody.Body.OptimizeMacros();

            var getApiKeyForAttemptRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "GetApiKeyForAttempt"));
            var notifyCallerAiCompletedRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "NotifyCallerAiCompleted"));
            var sendType = kapi.NestedTypes.First(nt => nt.Name.Contains("SendDirectOpenRouterAsync"));
            var sendMoveNext = sendType.Methods.First(m => m.Name == "MoveNext");
            var bodyField = sendType.Fields.First(f => f.Name == "body");
            var attemptField = sendType.Fields.First(f => f.Name.Contains("attempt"));
            sendMoveNext.Body.SimplifyMacros();
            {
                var ilp = sendMoveNext.Body.GetILProcessor();
                var insList = sendMoveNext.Body.Instructions;

                for (int i = 0; i < insList.Count; i++)
                {
                    var ins = insList[i];
                    if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == "get_UTF8")
                    {
                        var ldarg0ForPayload = insList[i - 1];
                        ilp.InsertBefore(ldarg0ForPayload, Instruction.Create(OpCodes.Ldarg_0));
                        ilp.InsertBefore(ldarg0ForPayload, Instruction.Create(OpCodes.Ldfld, bodyField));
                        ilp.InsertBefore(ldarg0ForPayload, Instruction.Create(OpCodes.Call, prepareOutboundBodyRef));
                        break;
                    }
                }

                for (int i = 0; i < insList.Count; i++)
                {
                    var ins = insList[i];
                    if (ins.OpCode == OpCodes.Ldfld && ins.Operand is FieldReference fr && fr.Name == "key")
                    {
                        ins.Operand = attemptField;
                        ilp.InsertAfter(ins, Instruction.Create(OpCodes.Call, getApiKeyForAttemptRef));
                        break;
                    }
                }

                for (int i = 0; i < insList.Count; i++)
                {
                    var ins = insList[i];
                    if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.Name == "Parse" && mr.DeclaringType.Name == "JObject")
                    {
                        ilp.InsertAfter(ins, Instruction.Create(OpCodes.Call, notifyCallerAiCompletedRef));
                        break;
                    }
                }

                foreach (var ins in insList)
                {
                    if (ins.OpCode == OpCodes.Ldc_I4 && ((int)ins.Operand == 524288 || (int)ins.Operand == 1048576))
                        ins.Operand = 16777216;
                }
            }
            sendMoveNext.Body.OptimizeMacros();

            var aiconv = mod.Types.First(t => t.Name == "AIConversation");
            ReplaceWithReturnBool(aiconv.Methods.First(m => m.Name == "FitsTransport"), true);
            ReplaceWithNoOp(aiconv.Methods.First(m => m.Name == "TrimRequestHistoryLocked"));
            ReplaceWithNoOp(aiconv.Methods.First(m => m.Name == "TrimHistoryLocked"));
            var compactMethod = aiconv.Methods.First(m => m.Name == "CompactImmutableLocked");
            compactMethod.Body.SimplifyMacros();
            foreach (var ins in compactMethod.Body.Instructions)
            {
                if (ins.OpCode == OpCodes.Ldc_I4 && (int)ins.Operand == 10000)
                    ins.Operand = 900000;
                else if (ins.OpCode == OpCodes.Ldc_I4 && (int)ins.Operand == 128)
                    ins.Operand = 8192;
                else if (ins.OpCode == OpCodes.Ldc_I4 && (int)ins.Operand == 32)
                    ins.Operand = 4096;
            }
            compactMethod.Body.OptimizeMacros();

            var cap = mod.Types.First(t => t.Name == "CallerAiPipeline");
            foreach (var m in cap.Methods.Where(x => x.HasBody))
            {
                m.Body.SimplifyMacros();
                foreach (var ins in m.Body.Instructions)
                {
                    if (m.Name == ".ctor" && ins.OpCode == OpCodes.Ldc_I4 && (int)ins.Operand == 128)
                        ins.Operand = 4096;
                    else if (m.Name == ".ctor" && ins.OpCode == OpCodes.Ldc_I4 && (int)ins.Operand == 32)
                        ins.Operand = 2048;
                    else if (ins.OpCode == OpCodes.Ldc_R8 && Math.Abs((double)ins.Operand - 15.0) < 0.01)
                        ins.Operand = 45.0;
                }
                m.Body.OptimizeMacros();
            }
            foreach (var nt in cap.NestedTypes)
            {
                foreach (var m in nt.Methods.Where(x => x.HasBody))
                {
                    m.Body.SimplifyMacros();
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (ins.OpCode == OpCodes.Ldc_R8 && Math.Abs((double)ins.Operand - 15.0) < 0.01)
                            ins.Operand = 45.0;
                    }
                    m.Body.OptimizeMacros();
                }
            }

            var vcall = mod.Types.First(t => t.Name == "VictimCall");
            foreach (var mName in new[] { "ServerPrepareCall", "EnsureAiPipeline" })
            {
                var vm = vcall.Methods.First(m => m.Name == mName);
                vm.Body.SimplifyMacros();
                foreach (var ins in vm.Body.Instructions)
                {
                    if (ins.OpCode == OpCodes.Ldc_I4 && ((int)ins.Operand == 1024 || (int)ins.Operand == 128 || (int)ins.Operand == 80))
                        ins.Operand = 4096;
                }
                vm.Body.OptimizeMacros();
            }

            foreach (var t in mod.GetTypes())
            {
                foreach (var m in t.Methods.Where(x => x.HasBody))
                {
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string str)
                        {
                            if (str == "inclusionai/ling-3.0-flash" ||
                                str == "deepseek/deepseek-v4.1-flash" ||
                                str == "llama3.1")
                            {
                                ins.Operand = activeModel;
                            }
                        }
                    }
                }
            }

            var shouldForceRefreshRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "ShouldForcePhoneUiRefresh"));
            var resolveDisplayStyleRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "ResolvePhoneStatusDisplayStyle"));
            var formatPhoneStatusTextRef = mod.ImportReference(bridgeType.Methods.First(m => m.Name == "FormatPhoneStatusText"));

            var uiBuilder = mod.Types.First(t => t.Name == "KolkataOsContentBuilder");
            var refreshVoiceMethod = uiBuilder.Methods.First(m => m.Name == "RefreshPhoneVoiceFeedback");
            var compactMethodUi = uiBuilder.Methods.First(m => m.Name == "RefreshCompactTranscriptMessage");

            var textElementGetTextRef = compactMethodUi.Body.Instructions
                .Select(x => x.Operand).OfType<MethodReference>()
                .First(mr => mr.Name == "get_text" && mr.DeclaringType.Name == "TextElement");
            var textElementSetTextRef = refreshVoiceMethod.Body.Instructions
                .Select(x => x.Operand).OfType<MethodReference>()
                .First(mr => mr.Name == "set_text" && mr.DeclaringType.Name == "TextElement");

            refreshVoiceMethod.Body.SimplifyMacros();
            {
                var ilp = refreshVoiceMethod.Body.GetILProcessor();
                var insList = refreshVoiceMethod.Body.Instructions;
                var availStateVar = refreshVoiceMethod.Body.Variables[15];

                for (int i = 0; i < insList.Count; i++)
                {
                    var ins = insList[i];
                    if (ins.OpCode == OpCodes.Ldfld && ins.Operand is FieldReference fr && fr.Name == "ForceRefresh")
                    {
                        var brTrueIns = insList[i + 1];
                        var retIns = insList[i + 2];
                        if (brTrueIns.OpCode == OpCodes.Brtrue && retIns.OpCode == OpCodes.Ret)
                        {
                            var targetAfterRet = (Instruction)brTrueIns.Operand;
                            ilp.InsertBefore(retIns, Instruction.Create(OpCodes.Ldloc, availStateVar));
                            ilp.InsertBefore(retIns, Instruction.Create(OpCodes.Call, shouldForceRefreshRef));
                            ilp.InsertBefore(retIns, Instruction.Create(OpCodes.Brtrue, targetAfterRet));
                        }
                        break;
                    }
                }

                for (int i = 0; i < insList.Count; i++)
                {
                    var ins = insList[i];
                    if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "kolkata-phone-webcam-status")
                    {
                        for (int j = i + 1; j < insList.Count; j++)
                        {
                            if (insList[j].OpCode == OpCodes.Call && insList[j].Operand is MethodReference mr && mr.Name == "op_Implicit")
                            {
                                var opImplicitIns = insList[j];
                                var ldAvailIns = Instruction.Create(OpCodes.Ldloc, availStateVar);
                                var callResolveIns = Instruction.Create(OpCodes.Call, resolveDisplayStyleRef);
                                ilp.InsertBefore(opImplicitIns, ldAvailIns);
                                ilp.InsertBefore(opImplicitIns, callResolveIns);
                                foreach (var bIns in insList)
                                {
                                    if (bIns.Operand == opImplicitIns)
                                        bIns.Operand = ldAvailIns;
                                }
                                break;
                            }
                        }
                        break;
                    }
                }

                for (int i = 0; i < insList.Count; i++)
                {
                    var ins = insList[i];
                    if (ins.OpCode == OpCodes.Ldstr && (string)ins.Operand == "ENABLE YOUR SPEAKER TO LISTEN")
                    {
                        for (int j = i + 1; j < Math.Min(i + 6, insList.Count); j++)
                        {
                            if (insList[j].OpCode == OpCodes.Callvirt && insList[j].Operand is MethodReference mr && mr.Name == "set_text")
                            {
                                var afterStatusSetIns = insList[j + 1];
                                var firstFormatIns = Instruction.Create(OpCodes.Ldarg_2);
                                ilp.InsertBefore(afterStatusSetIns, firstFormatIns);
                                ilp.InsertBefore(afterStatusSetIns, Instruction.Create(OpCodes.Ldarg_2));
                                ilp.InsertBefore(afterStatusSetIns, Instruction.Create(OpCodes.Callvirt, textElementGetTextRef));
                                ilp.InsertBefore(afterStatusSetIns, Instruction.Create(OpCodes.Ldloc, availStateVar));
                                ilp.InsertBefore(afterStatusSetIns, Instruction.Create(OpCodes.Call, formatPhoneStatusTextRef));
                                ilp.InsertBefore(afterStatusSetIns, Instruction.Create(OpCodes.Callvirt, textElementSetTextRef));
                                foreach (var bIns in insList)
                                {
                                    if (bIns.Operand == afterStatusSetIns)
                                        bIns.Operand = firstFormatIns;
                                }
                                break;
                            }
                        }
                        break;
                    }
                }
            }
            refreshVoiceMethod.Body.OptimizeMacros();

            asm.Write(tmpPath);
        }
        if (File.Exists(dllPath)) File.Delete(dllPath);
        File.Move(tmpPath, dllPath);
        Console.WriteLine("  + Patched Assembly-CSharp.dll (KolkataApi + Uncapped Context + Live Voice/AI HUD Timer)");
    }

    static void ReplaceWithNoOp(MethodDefinition m)
    {
        m.Body.Instructions.Clear();
        m.Body.Variables.Clear();
        m.Body.ExceptionHandlers.Clear();
        m.Body.GetILProcessor().Emit(OpCodes.Ret);
    }

    static void ReplaceWithReturnBool(MethodDefinition m, bool val)
    {
        m.Body.Instructions.Clear();
        m.Body.Variables.Clear();
        m.Body.ExceptionHandlers.Clear();
        var il = m.Body.GetILProcessor();
        il.Emit(val ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    static void ReplaceWithReturnInt(MethodDefinition m, int val)
    {
        m.Body.Instructions.Clear();
        m.Body.Variables.Clear();
        m.Body.ExceptionHandlers.Clear();
        var il = m.Body.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4, val);
        il.Emit(OpCodes.Ret);
    }

    static void ReplaceWithCallStatic(MethodDefinition m, MethodReference target)
    {
        m.Body.Instructions.Clear();
        m.Body.Variables.Clear();
        m.Body.ExceptionHandlers.Clear();
        var il = m.Body.GetILProcessor();
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
    }

    static void ReplaceWithCallStatic1Arg(MethodDefinition m, MethodReference target)
    {
        m.Body.Instructions.Clear();
        m.Body.Variables.Clear();
        m.Body.ExceptionHandlers.Clear();
        var il = m.Body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
    }

    private static void ConfigureInGameSettingsAndCleanOldModels(string gameDir, ModConfig cfg)
    {
        try
        {
            string appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"AppData\LocalLow\Jatater Worldwide\Scam With Your Friends\prompts");

            if (Directory.Exists(appDataRoot))
            {
                foreach (string cachedFile in Directory.GetFiles(appDataRoot, "*.json", SearchOption.AllDirectories))
                {
                    string fileName = Path.GetFileName(cachedFile);
                    if (string.Equals(fileName, "model-settings.json", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(fileName, "system.json", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(cachedFile);
                        Console.WriteLine("  + Cleared cached old settings file: " + cachedFile);
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            using (RegistryKey regKey = Registry.CurrentUser.CreateSubKey(UnityRegSubKey))
            {
                if (regKey != null)
                {
                    foreach (string valName in regKey.GetValueNames())
                    {
                        if (valName.StartsWith("HostAI.", StringComparison.OrdinalIgnoreCase) ||
                            valName.StartsWith("GameplayModel.", StringComparison.OrdinalIgnoreCase))
                        {
                            regKey.DeleteValue(valName, false);
                        }
                    }

                    string modelJson =
                        "{\"Id\":\"" + EscapeJson(cfg.Model) + "\"," +
                        "\"Name\":\"" + EscapeJson(cfg.Model) + " (" + EscapeJson(cfg.Provider) + ")\"," +
                        "\"SupportedParameters\":[\"temperature\",\"top_p\",\"max_tokens\",\"response_format\",\"structured_outputs\",\"reasoning\",\"tools\",\"tool_choice\",\"seed\",\"stop\"]," +
                        "\"PriceLabel\":\"Voice + Chat Ready | Uncapped Context\"," +
                        "\"MaxCompletionTokens\":65536," +
                        "\"ReasoningMandatory\":false," +
                        "\"ReasoningEfforts\":[\"none\",\"minimal\",\"low\",\"medium\",\"high\"]," +
                        "\"ReasoningSupportsTokenBudget\":true}";

                    string favsJson = "[" + modelJson + "]";

                    SetUnityPlayerPrefInt(regKey, "HostAI.Backend", 1);
                    if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                    {
                        SetUnityPlayerPrefString(regKey, "HostAI.OpenRouterKey", cfg.ApiKey);
                        SetUnityPlayerPrefString(regKey, "HostAiSettings.OpenRouterApiKey", cfg.ApiKey);
                    }
                    SetUnityPlayerPrefString(regKey, "HostAI.OpenRouterModel", modelJson);
                    SetUnityPlayerPrefString(regKey, "HostAI.OpenRouterFavorites", favsJson);

                    Console.WriteLine($"  + Updated Unity PlayerPrefs (Provider = {cfg.Provider}, Model = {cfg.Model})");
                }
            }
        }
        catch
        {
        }
    }

    private static uint ComputeUnityPlayerPrefsHash(string key)
    {
        uint hash = 5381;
        for (int i = 0; i < key.Length; i++)
            hash = ((hash << 5) + hash) ^ key[i];
        return hash;
    }

    private static void SetUnityPlayerPrefInt(RegistryKey regKey, string key, int value)
    {
        string valueName = key + "_h" + ComputeUnityPlayerPrefsHash(key);
        regKey.SetValue(valueName, value, RegistryValueKind.DWord);
    }

    private static void SetUnityPlayerPrefString(RegistryKey regKey, string key, string value)
    {
        string valueName = key + "_h" + ComputeUnityPlayerPrefsHash(key);
        byte[] utf8 = Encoding.UTF8.GetBytes(value ?? string.Empty);
        byte[] nullTerminated = new byte[utf8.Length + 1];
        Buffer.BlockCopy(utf8, 0, nullTerminated, 0, utf8.Length);
        regKey.SetValue(valueName, nullTerminated, RegistryValueKind.Binary);
    }

    private static bool TestMod(string gameDir, ModConfig cfg)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("[4/4] Running Live API Chat & Voice STT Verification Test...");
        Console.ResetColor();

        Console.Write("  1. Checking installed mod files... ");
        if (!IsModInstalled(gameDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("FAILED (Mod files not installed yet - run Option [1] first).");
            Console.ResetColor();
            return false;
        }
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("PASSED");
        Console.ResetColor();

        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  ! Skipping live network test because api_key is empty.");
            Console.ResetColor();
            return true;
        }

        string apiKey = cfg.ApiKey;
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) })
        {
            Console.Write($"  2. Testing Live AI Chat ({cfg.Model} via {cfg.Provider})... ");
            bool isGeminiModel = cfg.Model != null && cfg.Model.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0 && cfg.Provider != "openai";
            string reasoningField = isGeminiModel ? "\"reasoning\":{\"effort\":\"none\",\"enabled\":false}," : "";
            string chatPayload =
                "{\"model\":\"" + EscapeJson(cfg.Model) + "\"," +
                "\"temperature\":0.2," +
                "\"max_tokens\":128," +
                reasoningField +
                "\"messages\":[{\"role\":\"user\",\"content\":\"Reply with the exact phrase: AI MOD ACTIVE\"}]}";

            var sw = Stopwatch.StartNew();
            string chatResp = SendPostWithFallback(client, cfg.Endpoint, chatPayload, ref apiKey, cfg.FallbackApiKey, out string errMsg);
            sw.Stop();

            if (chatResp == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAILED (" + errMsg + ")");
                Console.ResetColor();
                return false;
            }

            string replyText = ExtractAssistantContent(chatResp);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED ({sw.ElapsedMilliseconds} ms)");
            Console.ResetColor();
            Console.WriteLine($"     -> AI Reply: \"{replyText}\"");

            if (string.Equals(cfg.SttMode, "local", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  3. Voice STT Mode is set to 'local' (offline Whisper CPU fallback).");
                return true;
            }

            Console.Write($"  3. Testing Live Voice-to-Text ({cfg.VoiceModel} [{cfg.SttMode}])... ");
            byte[] silentWav = CreateSilentTestWavBytes(16000, 4000);
            sw.Restart();
            string voiceResp = SendSttTestRequest(client, cfg, silentWav, apiKey, out string voiceErr);
            sw.Stop();

            if (voiceResp == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("FAILED (" + voiceErr + ")");
                Console.ResetColor();
                return false;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"PASSED ({sw.ElapsedMilliseconds} ms)");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n========================================================================");
        Console.WriteLine("  ALL LIVE TESTS PASSED! YOU ARE READY TO LAUNCH THE GAME!");
        Console.WriteLine("========================================================================");
        Console.ResetColor();
        return true;
    }

    private static string SendSttTestRequest(HttpClient client, ModConfig cfg, byte[] wavBytes, string apiKey, out string errorMsg)
    {
        errorMsg = null;
        try
        {
            if (string.Equals(cfg.SttMode, "whisper_api", StringComparison.OrdinalIgnoreCase))
            {
                string url = string.IsNullOrWhiteSpace(cfg.SttEndpoint) ? "https://api.openai.com/v1/audio/transcriptions" : cfg.SttEndpoint;
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                    var form = new MultipartFormDataContent();
                    var fileContent = new ByteArrayContent(wavBytes);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                    form.Add(fileContent, "file", "speech.wav");
                    form.Add(new StringContent(string.IsNullOrWhiteSpace(cfg.VoiceModel) ? "gpt-4o-mini-transcribe" : cfg.VoiceModel), "model");
                    form.Add(new StringContent("json"), "response_format");
                    req.Content = form;
                    using (HttpResponseMessage resp = client.SendAsync(req).GetAwaiter().GetResult())
                    {
                        string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (resp.IsSuccessStatusCode)
                            return text;
                        errorMsg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                        return null;
                    }
                }
            }
            else if (string.Equals(cfg.SttMode, "gemini_native", StringComparison.OrdinalIgnoreCase))
            {
                string cleanModel = string.IsNullOrWhiteSpace(cfg.VoiceModel) ? "gemini-2.5-flash" : cfg.VoiceModel.Trim();
                if (cleanModel.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                    cleanModel = cleanModel.Substring(7);
                string url = string.IsNullOrWhiteSpace(cfg.SttEndpoint)
                    ? $"https://generativelanguage.googleapis.com/v1beta/models/{cleanModel}:generateContent"
                    : cfg.SttEndpoint.Replace("{model}", cleanModel);
                string sep = url.Contains("?") ? "&" : "?";
                url = url + sep + "key=" + Uri.EscapeDataString(apiKey.Trim());

                string wavB64 = Convert.ToBase64String(wavBytes);
                string body =
                    "{\"contents\":[{\"parts\":[" +
                    "{\"text\":\"If this audio clip contains no speech, output [SILENCE] and nothing else.\"}," +
                    "{\"inline_data\":{\"mime_type\":\"audio/wav\",\"data\":\"" + wavB64 + "\"}}" +
                    "]}],\"generationConfig\":{\"temperature\":0.0,\"maxOutputTokens\":32}}";

                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage resp = client.SendAsync(req).GetAwaiter().GetResult())
                    {
                        string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (resp.IsSuccessStatusCode)
                            return text;
                        errorMsg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                        return null;
                    }
                }
            }
            else
            {
                bool isGeminiVoice = cfg.VoiceModel != null && cfg.VoiceModel.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0 && cfg.Provider != "openai";
                string reasoningField = isGeminiVoice ? "\"reasoning\":{\"effort\":\"none\",\"enabled\":false}," : "";
                string wavB64 = Convert.ToBase64String(wavBytes);
                string voicePayload =
                    "{\"model\":\"" + EscapeJson(cfg.VoiceModel) + "\"," +
                    "\"temperature\":0.0," +
                    "\"max_tokens\":64," +
                    reasoningField +
                    "\"messages\":[{\"role\":\"user\",\"content\":[" +
                    "{\"type\":\"text\",\"text\":\"If this audio clip contains no speech, output [SILENCE] and nothing else.\"}," +
                    "{\"type\":\"input_audio\",\"input_audio\":{\"data\":\"" + wavB64 + "\",\"format\":\"wav\"}}" +
                    "]}]}";
                string targetEp = string.IsNullOrWhiteSpace(cfg.SttEndpoint) ? cfg.Endpoint : cfg.SttEndpoint;
                return SendPostWithFallback(client, targetEp, voicePayload, ref apiKey, cfg.FallbackApiKey, out errorMsg);
            }
        }
        catch (Exception ex)
        {
            errorMsg = ex.Message;
            return null;
        }
    }

    private static string SendPostWithFallback(HttpClient client, string endpoint, string jsonBody, ref string primaryKey, string fallbackKey, out string errorMsg)
    {
        errorMsg = null;
        string[] keysToTry = string.IsNullOrWhiteSpace(fallbackKey) || fallbackKey == primaryKey
            ? new[] { primaryKey }
            : new[] { primaryKey, fallbackKey };

        foreach (string key in keysToTry)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
                    req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    using (HttpResponseMessage resp = client.SendAsync(req).GetAwaiter().GetResult())
                    {
                        string respText = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (resp.IsSuccessStatusCode)
                        {
                            primaryKey = key;
                            return respText;
                        }
                        errorMsg = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
                    }
                }
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
            }
        }
        return null;
    }

    private static byte[] CreateSilentTestWavBytes(int sampleRate, int sampleCount)
    {
        int pcmByteLen = sampleCount * 2;
        using (var ms = new MemoryStream(44 + pcmByteLen))
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + pcmByteLen);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)1);
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(pcmByteLen);
            bw.Write(new byte[pcmByteLen]);
            bw.Flush();
            return ms.ToArray();
        }
    }

    private static string ExtractJsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
    }

    private static string ExtractAssistantContent(string json)
    {
        if (string.IsNullOrEmpty(json))
            return string.Empty;
        Match m = Regex.Match(json, "\"content\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value).Trim() : "OK";
    }

    private static void RestoreOriginals(string gameDir)
    {
        Console.WriteLine("Restoring original unpatched game files from .bak backups...");
        int restored = 0;
        string managedDir = Path.Combine(gameDir, @"Scam With Your Friends_Data\Managed");
        foreach (string dll in DllFiles)
        {
            string fullPath = Path.Combine(managedDir, dll);
            string bakPath = fullPath + ".bak";
            if (File.Exists(bakPath))
            {
                File.Copy(bakPath, fullPath, true);
                File.Delete(bakPath);
                Console.WriteLine("  + Restored: " + dll);
                restored++;
            }
        }

        string msPath = Path.Combine(gameDir, @"Scam With Your Friends_Data\StreamingAssets\kolkata-json\model-settings.json");
        if (File.Exists(msPath + ".bak"))
        {
            File.Copy(msPath + ".bak", msPath, true);
            File.Delete(msPath + ".bak");
            Console.WriteLine("  + Restored: model-settings.json");
            restored++;
        }

        string bridgePath = Path.Combine(managedDir, "SurplusBridge.dll");
        if (File.Exists(bridgePath))
        {
            File.Delete(bridgePath);
            Console.WriteLine("  - Removed: SurplusBridge.dll");
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nSUCCESS! Restored {restored} original file(s).");
        Console.ResetColor();
    }

    private static bool IsValidGameDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return false;
        try
        {
            return File.Exists(Path.Combine(dir, @"Scam With Your Friends_Data\Managed\Assembly-CSharp.dll")) &&
                   File.Exists(Path.Combine(dir, @"Scam With Your Friends_Data\Managed\ScriptsAssDef.dll"));
        }
        catch
        {
            return false;
        }
    }

    private static string DetectGameDirectory()
    {
        string exeDir = AppDomain.CurrentDomain.BaseDirectory;
        if (IsValidGameDirectory(exeDir))
            return Path.GetFullPath(exeDir);

        string parentDir = Directory.GetParent(exeDir)?.FullName;
        if (!string.IsNullOrEmpty(parentDir) && IsValidGameDirectory(parentDir))
            return Path.GetFullPath(parentDir);

        string cwd = Directory.GetCurrentDirectory();
        if (IsValidGameDirectory(cwd))
            return Path.GetFullPath(cwd);

        var steamLibraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        };

        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                string sp = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(sp))
                    steamLibraries.Add(sp.Replace('/', '\\'));
            }
        }
        catch { }

        try
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
            {
                string ip = key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrWhiteSpace(ip))
                    steamLibraries.Add(ip.Replace('/', '\\'));
            }
        }
        catch { }

        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                    continue;
                string root = drive.RootDirectory.FullName;
                string[] directCandidates = new[]
                {
                    Path.Combine(root, @"SteamLibrary\steamapps\common\Scam With Your Friends"),
                    Path.Combine(root, @"Steam\steamapps\common\Scam With Your Friends"),
                    Path.Combine(root, @"Games\Steam\steamapps\common\Scam With Your Friends"),
                    Path.Combine(root, @"Program Files (x86)\Steam\steamapps\common\Scam With Your Friends"),
                    Path.Combine(root, @"Program Files\Steam\steamapps\common\Scam With Your Friends"),
                    Path.Combine(root, @"Scam With Your Friends")
                };
                foreach (string candidate in directCandidates)
                {
                    if (IsValidGameDirectory(candidate))
                        return Path.GetFullPath(candidate);
                }

                steamLibraries.Add(Path.Combine(root, "SteamLibrary"));
                steamLibraries.Add(Path.Combine(root, "Steam"));
            }
        }
        catch { }

        var allLibs = new HashSet<string>(steamLibraries, StringComparer.OrdinalIgnoreCase);
        foreach (string lib in steamLibraries)
        {
            try
            {
                string vdfPath = Path.Combine(lib, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdfPath))
                {
                    string vdf = File.ReadAllText(vdfPath);
                    foreach (Match m in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (!string.IsNullOrWhiteSpace(p))
                            allLibs.Add(p);
                    }
                }
            }
            catch { }
        }

        foreach (string lib in allLibs)
        {
            string candidate = Path.Combine(lib, @"steamapps\common\Scam With Your Friends");
            if (IsValidGameDirectory(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }
}
