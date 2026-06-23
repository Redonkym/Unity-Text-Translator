using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityTextTranslator
{
    /// <summary>Провайдер для перевода пустых строк через HTTP API.</summary>
    internal enum TranslationAiBackend
    {
        LibreTranslate,
        OpenRouter,
        OpenAI,
        Groq,
        TogetherAI,
        Mistral,
        DeepSeek,
        GeminiOpenAi,
        Qwen,
        Ollama,
        CustomOpenAiCompatible,
        /// <summary>OpenAI-compatible слой Cohere (ключ в настройках).</summary>
        Cohere,
        /// <summary>Moonshot Kimi — OpenAI-compatible.</summary>
        Kimi,
        /// <summary>NVIDIA build.cloud / integrate OpenAI-compatible API.</summary>
        Nvidia,
        /// <summary>Cursor API (OpenAI-compatible endpoint).</summary>
        Cursor,
        /// <summary>REST POST …/accounts/{id}/ai/run/{model} — см. junk features.</summary>
        CloudflareWorkersAi,
        /// <summary>Apify.com — запуск Actors (translation / OpenAI-compatible) через REST API.</summary>
        Apify,
    }

    /// <summary>
    /// LibreTranslate: POST …/translate (q/source/target/format → translatedText). Остальные: POST …/chat/completions (OpenAI), Bearer+модель из настроек.
    /// </summary>
    internal static class LocalTranslateApi
    {
        private static readonly HttpClient Http = CreateHttpClient();

        /// <summary>Короткая подсказка только если в строке есть разметка — экономия токенов на простом тексте.</summary>
        private const string ChatMarkupHintWhenNeeded =
            "Keep TMP/<?…?> tags and line breaks. ";

        internal static bool TextLikelyHasGameMarkup(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            return s.IndexOf('<') >= 0 || s.IndexOf('[') >= 0 || s.IndexOf('{') >= 0;
        }

        /// <summary>
        /// Модель ведёт скрытый reasoning (deepseek-reasoner, *-thinking, o1/o3/o4, qwq, magistral…): нужен больший бюджет вывода,
        /// иначе скрытый &lt;think&gt; съедает лимит и ответ обрезается в пустоту.
        /// </summary>
        internal static bool ModelLikelyUsesHiddenReasoning(string modelId)
        {
            if (string.IsNullOrEmpty(modelId))
                return false;
            var m = modelId.ToLowerInvariant();
            return m.Contains("reason")            // deepseek-reasoner, command-a-reasoning, *-reasoning
                || m.Contains("think")             // *-thinking, qwen3 thinking-снапшоты
                || m.Contains("qwq")
                || m.Contains("deepseek-r1") || m.Contains("/r1") || m.EndsWith("-r1")
                || m.Contains("deepseek-v4-pro")   // pro-режим v4 = thinking
                || m.Contains("magistral")         // Mistral reasoning
                || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4")
                || m.Contains("/o1") || m.Contains("/o3") || m.Contains("/o4")
                || m.Contains("-o1-") || m.Contains("-o3-") || m.Contains("-o4-")
                || m.EndsWith("-o1") || m.EndsWith("-o3") || m.EndsWith("-o4");
        }

        /// <summary>Потолок output-токенов: тесный для обычных моделей (экономия + бережёт TPM-квоту), просторный — для «думающих».</summary>
        /// <remarks>
        /// Обычной модели перевод редко длиннее ~2× оригинала → компактный потолок (≤2048): не режет ответ, не даёт лишнего,
        /// а max_tokens у многих провайдеров резервируется против TPM. «Думающие» (<see cref="ModelLikelyUsesHiddenReasoning"/>) — 512…8192.
        /// </remarks>
        internal static int ComputeChatMaxOutputTokens(string userText, string modelId = null)
        {
            int n = userText?.Length ?? 0;
            // ~ n + n/2 + запас: грубая оценка длины перевода в токенах с поправкой на кириллицу/расширение.
            int est = n + n / 2 + 64;

            if (ModelLikelyUsesHiddenReasoning(modelId))
                return Math.Min(8192, Math.Max(512, est + 256));

            return Math.Min(2048, Math.Max(128, est));
        }

        private static readonly Regex ThinkBlockRegex =
            new Regex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Убирает рассуждения: парный &lt;think&gt;…&lt;/think&gt; вырезает, усечённый (без закрытия) отбрасывает до конца.</summary>
        internal static string StripModelThinkingArtifacts(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s ?? "";
            var t = ThinkBlockRegex.Replace(s, "");
            var open = t.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (open >= 0)
                t = t.Substring(0, open);
            return t.Trim();
        }

        private static string BuildChatSystemPrompt(string sourceCode, string targetCode, string userTextSample)
        {
            var srcHint = string.IsNullOrWhiteSpace(sourceCode) || sourceCode.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? "Detect source"
                : "From " + sourceCode.Trim();
            var tgt = string.IsNullOrWhiteSpace(targetCode) ? "en" : targetCode.Trim();
            var markup = TextLikelyHasGameMarkup(userTextSample) ? ChatMarkupHintWhenNeeded : "";
            // компактно: промпт уходит на КАЖДОМ запросе → короче = меньше входных токенов (ограничения сохранены)
            return "Game UI translator. " + srcHint + " → " + tgt + ". " + markup +
                   "Reply with only the translation (no quotes/fences/notes).";
        }

        static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy,
                UseDefaultCredentials = true,
                PreAuthenticate = true,
            };

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMinutes(2),
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "UnityTextTranslator/1.0 (.NET Framework; Windows)");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            return client;
        }

        /// <summary>OpenRouter «:free» модели часто ограничены ~8 req/min — соблюдаем интервал между запросами.</summary>
        private static readonly object OpenRouterFreeThrottleLock = new object();

        private static DateTime _openRouterFreeNextUtc = DateTime.UtcNow;

        /// <summary>Чуть больше 60/8 с, чтобы не упираться в RPM на бесплатном тарифе.</summary>
        private const double OpenRouterFreeTierSpacingSeconds = 8.25;

        private const int ChatCompletionHttpMaxAttempts = 12;

        /// <summary>Один POST chat/completions: при «висящем» OpenRouter иначе ждём до глобальных 2 мин HttpClient.</summary>
        private const int ChatCompletionPerAttemptTimeoutSeconds = 90;

        private static bool OpenRouterModelLikelyFreeTier(string modelId) =>
            !string.IsNullOrEmpty(modelId) && modelId.IndexOf(":free", StringComparison.OrdinalIgnoreCase) >= 0;

        private static async Task WaitOpenRouterFreeTierSpacingAsync(
            Action<int> notifyOpenRouterThrottleWaitSeconds,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                TimeSpan delay;
                lock (OpenRouterFreeThrottleLock)
                {
                    var now = DateTime.UtcNow;
                    var ms = (_openRouterFreeNextUtc - now).TotalMilliseconds;
                    if (ms <= 0)
                    {
                        _openRouterFreeNextUtc = now.AddSeconds(OpenRouterFreeTierSpacingSeconds);
                        return;
                    }

                    delay = TimeSpan.FromMilliseconds(Math.Min(ms, 600000));
                }

                if (delay.TotalSeconds >= 2 && notifyOpenRouterThrottleWaitSeconds != null)
                    notifyOpenRouterThrottleWaitSeconds(Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)));

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        private static TimeSpan? ParseRetryAfterDelay(HttpResponseMessage resp)
        {
            var ra = resp?.Headers?.RetryAfter;
            if (ra == null)
                return null;

            TimeSpan? raw = null;
            if (ra.Delta.HasValue)
                raw = ra.Delta.Value;
            else if (ra.Date.HasValue)
            {
                var d = ra.Date.Value - DateTimeOffset.UtcNow;
                raw = d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(3);
            }

            if (!raw.HasValue)
                return null;

            return NormalizeRetryWait(raw.Value);
        }

        /// <summary>Сервер иногда отдаёт огромный Retry-After — без ограничения UI «зависает» без записей в журнал.</summary>
        private static TimeSpan NormalizeRetryWait(TimeSpan t)
        {
            const int maxSeconds = 90;
            if (t <= TimeSpan.Zero)
                return TimeSpan.FromSeconds(3);
            var cap = TimeSpan.FromSeconds(maxSeconds);
            return t > cap ? cap : t;
        }

        /// <summary>POST chat/completions с повторами при 429 и паузой для OpenRouter :free.</summary>
        private static async Task<string> SendChatCompletionRequestAsync(
            TranslationAiBackend backend,
            Uri url,
            string bearerApiKey,
            JObject payload,
            string modelId,
            Action<int> notifyOpenRouterThrottleWaitSeconds = null,
            CancellationToken cancellationToken = default)
        {
            // Один интервал на логический запрос перевода; повторы после 429 не должны снова занимать слот throttle,
            // иначе суммируются многоминутные паузы без сообщений в журнале.
            if (backend == TranslationAiBackend.OpenRouter && OpenRouterModelLikelyFreeTier(modelId))
                await WaitOpenRouterFreeTierSpacingAsync(notifyOpenRouterThrottleWaitSeconds, cancellationToken)
                    .ConfigureAwait(false);

            for (var attempt = 1; attempt <= ChatCompletionHttpMaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    if (!string.IsNullOrWhiteSpace(bearerApiKey))
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearerApiKey.Trim());

                    if (backend == TranslationAiBackend.OpenRouter)
                    {
                        req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://localhost/");
                        req.Headers.TryAddWithoutValidation("X-Title", "Unity Text Translator");
                    }

                    req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        linked.CancelAfter(TimeSpan.FromSeconds(ChatCompletionPerAttemptTimeoutSeconds));
                        HttpResponseMessage resp;
                        try
                        {
                            resp = await Http.SendAsync(req, linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException ex)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                throw;

                            throw new InvalidOperationException(
                                "Нет ответа от Chat API за " + ChatCompletionPerAttemptTimeoutSeconds +
                                " с (таймаут одного запроса). Проверьте, что сервер модели запущен и отвечает; " +
                                "при бесплатных облачных очередях помогает другая модель или платный endpoint.",
                                ex);
                        }

                        using (resp)
                        {
                            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                            if ((int)resp.StatusCode == 429 && attempt < ChatCompletionHttpMaxAttempts)
                            {
                                var wait = ParseRetryAfterDelay(resp)
                                    ?? NormalizeRetryWait(TimeSpan.FromSeconds(Math.Min(90, 5 + attempt * 5)));
                                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            if (!resp.IsSuccessStatusCode)
                                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {SummarizeHttpErrorBody(body)}");

                            var jo = JObject.Parse(body);
                            var err = jo["error"]?["message"]?.Value<string>();
                            if (!string.IsNullOrEmpty(err))
                                throw new InvalidOperationException(err);

                            var content = jo["choices"]?[0]?["message"]?["content"]?.Value<string>();
                            return StripModelThinkingArtifacts((content ?? "").Trim());
                        }
                    }
                }
            }

            throw new InvalidOperationException(
                "Слишком много ответов HTTP 429 от API перевода. Подождите минуту, смените модель без :free или провайдера.");
        }

        /// <summary>Запасной список при недоступности API.</summary>
        public static readonly string[] OpenRouterModelPresets =
        {
            // Пресеты-подсказки; полный список дотягивается live через GET …/models.
            "openai/gpt-5.4-mini",
            "openai/gpt-5.4-nano",
            "openai/gpt-4o-mini",
            "anthropic/claude-haiku-4.5",
            "anthropic/claude-sonnet-4.5",
            "google/gemini-2.5-flash-lite",
            "google/gemini-2.5-flash",
            "google/gemini-3.5-flash",
            "meta-llama/llama-3.3-70b-instruct",
            "mistralai/mistral-small-3.1-24b-instruct",
            "deepseek/deepseek-chat",
            "qwen/qwen-2.5-72b-instruct",
        };

        public static readonly string[] OpenAiCompatibleModelPresets =
        {
            "gpt-5.4-mini",
            "gpt-5.4-nano",
            "gpt-5.4",
            "gpt-5.5",
            "gpt-4.1-mini",
            "gpt-4.1-nano",
            "gpt-4.1",
            "gpt-4o-mini",
            "gpt-4o",
            "o4-mini",
        };

        public static readonly string[] OllamaModelPresets =
        {
            "llama3.2",
            "llama3.1",
            "mistral",
            "qwen2.5",
            "gemma2",
        };

        /// <summary>Запасные id для Groq (OpenAI-совместимый каталог).</summary>
        public static readonly string[] GroqModelPresets =
        {
            // llama-3.1-70b-versatile / mixtral-8x7b сняты Groq — оставлены только живые production-модели.
            "llama-3.3-70b-versatile",
            "llama-3.1-8b-instant",
            "openai/gpt-oss-120b",
            "openai/gpt-oss-20b",
        };

        public static readonly string[] TogetherAiModelPresets =
        {
            "meta-llama/Llama-3.3-70B-Instruct-Turbo",
            "meta-llama/Llama-3.2-3B-Instruct-Turbo",
            "mistralai/Mixtral-8x7B-Instruct-v0.1",
            "Qwen/Qwen2.5-72B-Instruct-Turbo",
        };

        public static readonly string[] MistralModelPresets =
        {
            "mistral-small-latest",
            "mistral-large-latest",
            "pixtral-12b-latest",
            "open-mistral-nemo",
        };

        public static readonly string[] DeepSeekModelPresets =
        {
            "deepseek-v4-flash",
            "deepseek-v4-pro",
            "deepseek-chat",     // алиас → v4-flash (non-thinking); снимается 2026-07-24
            "deepseek-reasoner", // алиас → v4-flash (thinking); снимается 2026-07-24
        };

        /// <summary>Qwen / Model Studio — OpenAI-compatible; международная консоль чаще всего использует Singapore (intl).</summary>
        public static readonly string[] QwenDashScopeModelPresets =
        {
            // Singapore / intl: по документации Alibaba бесплатная квота чаще привязана к qwen-turbo-* с суффиксом latest/дата (не ко всем коротким id вроде qwen-flash).
            "qwen-turbo-latest",
            "qwen-turbo-2025-04-28",
            "qwen-turbo-2024-11-01",
            "qwen-turbo",
            "qwen-flash",
            "qwen3.5-flash",
            "qwen2.5-7b-instruct",
            "qwen2.5-14b-instruct",
            "qwen2.5-32b-instruct",
            "qwen2.5-72b-instruct",
            "qwen-plus",
            "qwen-plus-latest",
            "qwen-max",
            "qwen-max-latest",
            "qwen3-max",
            "qwen3-max-preview",
            "qwen3.5-plus",
            "qwen3-coder-plus",
            "qwen3-coder-flash",
            "qwq-plus",
            "qwen-long",
            "qwen3-32b",
            "qwen3-14b",
        };

        public static readonly string[] CohereModelPresets =
        {
            "command-a-03-2025",
            "command-a-plus-05-2026",
            "command-a-reasoning-08-2025",
        };

        public static readonly string[] KimiMoonshotModelPresets =
        {
            "kimi-latest",
            "kimi-k2.6",
            "moonshot-v1-8k",
            "moonshot-v1-32k",
            "moonshot-v1-128k",
        };

        public static readonly string[] NvidiaIntegrateModelPresets =
        {
            "meta/llama-3.1-8b-instruct",
            "meta/llama-3.1-70b-instruct",
            "mistralai/mistral-7b-instruct-v0.2",
            "microsoft/phi-3-mini-128k-instruct",
        };

        /// <summary>Пресеты имён моделей REST Workers AI (POST …/ai/run/&lt;model&gt;).</summary>
        public static readonly string[] CloudflareWorkersAiModelPresets =
        {
            // Meta Llama (основные)
            "@cf/meta/llama-3.1-8b-instruct",
            "@cf/meta/llama-3.2-3b-instruct",
            "@cf/meta/llama-3.3-70b-instruct-fp8-fast",
            "@cf/meta/llama-4-scout-17b-16e-instruct",
            // Mistral
            "@cf/mistral/mistral-7b-instruct-v0.1",
            "@cf/mistral/mistral-small-3.1-24b-instruct",
            // Qwen / reasoning
            "@cf/qwen/qwen2.5-72b-instruct",
            "@cf/qwen/qwq-32b",
            "@cf/qwen/qwen2.5-coder-32b-instruct",
            // Google
            "@cf/google/gemma-3-12b-it",
            "@cf/google/gemma-4-26b-a4b",
            // Moonshot / Kimi
            "@cf/moonshot/kimi-k2.5",
            // NVIDIA
            "@cf/nvidia/nemotron-3-super-120b",
        };

        /// <summary>Имена моделей Gemini для слоя OpenAI (Google AI) — дополняют ответ GET …/openai/models.</summary>
        public static readonly string[] GeminiOpenAiModelPresets =
        {
            // 2.0/1.5 серии отключены (2.0 — 2026-06-01); ниже только живые на 2026.
            "gemini-2.5-flash-lite",
            "gemini-2.5-flash",
            "gemini-2.5-pro",
            "gemini-3.1-flash-lite",
            "gemini-3.5-flash",
            "gemini-3.1-pro-preview",
        };

        /// <summary>OpenAI-compatible Singapore / международная консоль (в URL есть «-intl»).</summary>
        public const string DashScopeOpenAiCompatibleIntlBaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";

        /// <summary>OpenAI-compatible материковый Китай (Beijing) — без «-intl»; ключ должен быть с той же вкладки региона в консоли.</summary>
        public const string DashScopeOpenAiCompatibleChinaBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1";

        const string OpenRouterModelsListUrl = "https://openrouter.ai/api/v1/models";

        public static TranslationAiBackend ParseTranslationAiBackend(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return TranslationAiBackend.LibreTranslate;
            switch (s.Trim())
            {
                case "OpenRouter":
                    return TranslationAiBackend.OpenRouter;
                case "OpenAI":
                    return TranslationAiBackend.OpenAI;
                case "Groq":
                    return TranslationAiBackend.Groq;
                case "TogetherAI":
                    return TranslationAiBackend.TogetherAI;
                case "Mistral":
                    return TranslationAiBackend.Mistral;
                case "DeepSeek":
                    return TranslationAiBackend.DeepSeek;
                case "Gemini":
                case "GeminiOpenAi":
                    return TranslationAiBackend.GeminiOpenAi;
                case "Qwen":
                    return TranslationAiBackend.Qwen;
                case "Ollama":
                    return TranslationAiBackend.Ollama;
                case "CustomOpenAiCompatible":
                    return TranslationAiBackend.CustomOpenAiCompatible;
                case "Cohere":
                    return TranslationAiBackend.Cohere;
                case "Kimi":
                    return TranslationAiBackend.Kimi;
                case "Nvidia":
                    return TranslationAiBackend.Nvidia;
                case "Cursor":
                    return TranslationAiBackend.Cursor;
                case "CloudflareWorkersAi":
                    return TranslationAiBackend.CloudflareWorkersAi;
                case "Apify":
                    return TranslationAiBackend.Apify;
                default:
                    return TranslationAiBackend.LibreTranslate;
            }
        }

        public static string TranslationAiBackendToKey(TranslationAiBackend backend)
        {
            switch (backend)
            {
                case TranslationAiBackend.OpenRouter:
                    return "OpenRouter";
                case TranslationAiBackend.OpenAI:
                    return "OpenAI";
                case TranslationAiBackend.Groq:
                    return "Groq";
                case TranslationAiBackend.TogetherAI:
                    return "TogetherAI";
                case TranslationAiBackend.Mistral:
                    return "Mistral";
                case TranslationAiBackend.DeepSeek:
                    return "DeepSeek";
                case TranslationAiBackend.GeminiOpenAi:
                    return "Gemini";
                case TranslationAiBackend.Qwen:
                    return "Qwen";
                case TranslationAiBackend.Ollama:
                    return "Ollama";
                case TranslationAiBackend.CustomOpenAiCompatible:
                    return "CustomOpenAiCompatible";
                case TranslationAiBackend.Cohere:
                    return "Cohere";
                case TranslationAiBackend.Kimi:
                    return "Kimi";
                case TranslationAiBackend.Nvidia:
                    return "Nvidia";
                case TranslationAiBackend.Cursor:
                    return "Cursor";
                case TranslationAiBackend.CloudflareWorkersAi:
                    return "CloudflareWorkersAi";
                case TranslationAiBackend.Apify:
                    return "Apify";
                default:
                    return "LibreTranslate";
            }
        }

        /// <summary>Типичный базовый URL для провайдера (без /chat/completions).</summary>
        public static string DefaultBaseUrl(TranslationAiBackend backend)
        {
            switch (backend)
            {
                case TranslationAiBackend.OpenRouter:
                    return "https://openrouter.ai/api/v1";
                case TranslationAiBackend.OpenAI:
                    return "https://api.openai.com/v1";
                case TranslationAiBackend.Groq:
                    return "https://api.groq.com/openai/v1";
                case TranslationAiBackend.TogetherAI:
                    return "https://api.together.ai/v1";
                case TranslationAiBackend.Mistral:
                    return "https://api.mistral.ai/v1";
                case TranslationAiBackend.DeepSeek:
                    return "https://api.deepseek.com/v1";
                case TranslationAiBackend.GeminiOpenAi:
                    return "https://generativelanguage.googleapis.com/v1beta/openai";
                case TranslationAiBackend.Qwen:
                    // Model Studio: ключ и endpoint должны быть одного региона (intl Singapore vs материк Beijing).
                    return DashScopeOpenAiCompatibleIntlBaseUrl;
                case TranslationAiBackend.Ollama:
                    return "http://localhost:11434/v1";
                case TranslationAiBackend.CustomOpenAiCompatible:
                    return "http://127.0.0.1:1234/v1";
                case TranslationAiBackend.Cohere:
                    return "https://api.cohere.ai/compatibility/v1";
                case TranslationAiBackend.Kimi:
                    return "https://api.moonshot.cn/v1";
                case TranslationAiBackend.Nvidia:
                    return "https://integrate.api.nvidia.com/v1";
                case TranslationAiBackend.Cursor:
                    return "https://api.cursor.com/v1";
                case TranslationAiBackend.CloudflareWorkersAi:
                    return "https://api.cloudflare.com/client/v4/accounts/YOUR_ACCOUNT_ID";
                case TranslationAiBackend.Apify:
                    return "https://api.apify.com/v2/acts/YOUR_ACTOR_ID/run-sync";
                default:
                    return "http://localhost:5000";
            }
        }

        public static bool BackendUsesChatCompletions(TranslationAiBackend backend)
        {
            return backend != TranslationAiBackend.LibreTranslate;
        }

        public static bool BackendRequiresBearerKey(TranslationAiBackend backend)
        {
            switch (backend)
            {
                case TranslationAiBackend.LibreTranslate:
                case TranslationAiBackend.Ollama:
                case TranslationAiBackend.CustomOpenAiCompatible:
                    return false;
                default:
                    return BackendUsesChatCompletions(backend);
            }
        }

        public static string DefaultChatModelId(TranslationAiBackend backend)
        {
            switch (backend)
            {
                case TranslationAiBackend.OpenRouter:
                    return "openai/gpt-5.4-mini";
                case TranslationAiBackend.OpenAI:
                    return "gpt-5.4-mini";
                case TranslationAiBackend.CustomOpenAiCompatible:
                    return "gpt-4o-mini"; // нейтральный плейсхолдер для локальных серверов (пользователь задаёт свою модель)
                case TranslationAiBackend.Groq:
                    return "llama-3.3-70b-versatile";
                case TranslationAiBackend.TogetherAI:
                    return "meta-llama/Llama-3.3-70B-Instruct-Turbo";
                case TranslationAiBackend.Mistral:
                    return "mistral-small-latest";
                case TranslationAiBackend.DeepSeek:
                    return "deepseek-v4-flash"; // deepseek-chat/reasoner выводятся из обращения 2026-07-24
                case TranslationAiBackend.GeminiOpenAi:
                    return "gemini-2.5-flash-lite"; // gemini-2.0-flash/-lite отключены 2026-06-01
                case TranslationAiBackend.Qwen:
                    return "qwen-turbo-latest";
                case TranslationAiBackend.Ollama:
                    return "llama3.2";
                case TranslationAiBackend.Cohere:
                    return "command-a-03-2025"; // command-r/-r-plus сняты; command-a — актуальная эффективная модель
                case TranslationAiBackend.Kimi:
                    return "kimi-latest"; // moonshot-v1/kimi-k2 сняты 2026-05-25; latest = текущая (k2.6)
                case TranslationAiBackend.Nvidia:
                    return "meta/llama-3.1-8b-instruct";
                case TranslationAiBackend.Cursor:
                    return "gpt-4o-mini";
                case TranslationAiBackend.CloudflareWorkersAi:
                    return "@cf/meta/llama-3.1-8b-instruct";
                case TranslationAiBackend.Apify:
                    return "YOUR_ACTOR_ID";
                default:
                    return "gpt-4o-mini";
            }
        }

        public static IReadOnlyList<string> ModelPresetsForBackend(TranslationAiBackend backend)
        {
            switch (backend)
            {
                case TranslationAiBackend.OpenRouter:
                    return OpenRouterModelPresets;
                case TranslationAiBackend.OpenAI:
                    return OpenAiCompatibleModelPresets;
                case TranslationAiBackend.Groq:
                    return GroqModelPresets;
                case TranslationAiBackend.TogetherAI:
                    return TogetherAiModelPresets;
                case TranslationAiBackend.Mistral:
                    return MistralModelPresets;
                case TranslationAiBackend.DeepSeek:
                    return DeepSeekModelPresets;
                case TranslationAiBackend.GeminiOpenAi:
                    return GeminiOpenAiModelPresets;
                case TranslationAiBackend.Qwen:
                    return QwenDashScopeModelPresets;
                case TranslationAiBackend.CustomOpenAiCompatible:
                    return OpenAiCompatibleModelPresets;
                case TranslationAiBackend.Ollama:
                    return OllamaModelPresets;
                case TranslationAiBackend.Cohere:
                    return CohereModelPresets;
                case TranslationAiBackend.Kimi:
                    return KimiMoonshotModelPresets;
                case TranslationAiBackend.Nvidia:
                    return NvidiaIntegrateModelPresets;
                case TranslationAiBackend.Cursor:
                    return OpenAiCompatibleModelPresets;
                case TranslationAiBackend.CloudflareWorkersAi:
                    return CloudflareWorkersAiModelPresets;
                case TranslationAiBackend.Apify:
                    return Array.Empty<string>(); // id Actor в поле модели
                default:
                    return Array.Empty<string>();
            }
        }

        /// <summary>Список моделей для OpenAI-совместимого GET …/models.</summary>
        public static async Task<IReadOnlyList<string>> FetchOpenAiCompatibleModelIdsAsync(string apiBaseUrl, string bearerApiKey)
        {
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
                return Array.Empty<string>();

            var root = apiBaseUrl.Trim().TrimEnd('/');
            var url = root.IndexOf("/models", StringComparison.OrdinalIgnoreCase) >= 0 &&
                      root.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
                ? root
                : root + "/models";

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (!string.IsNullOrWhiteSpace(bearerApiKey))
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearerApiKey.Trim());

                    using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {SummarizeHttpErrorBody(body)}");

                        var jo = JObject.Parse(body);
                        var arr = jo["data"] as JArray;
                        if (arr == null || arr.Count == 0)
                            arr = jo["models"] as JArray;

                        if (arr == null || arr.Count == 0)
                            return Array.Empty<string>();

                        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var item in arr)
                        {
                            var id = item["id"]?.Value<string>() ?? item["name"]?.Value<string>();
                            if (string.IsNullOrWhiteSpace(id))
                                continue;
                            ids.Add(id.Trim());
                        }

                        return ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                throw WrapNetworkException(ex);
            }
            catch (TaskCanceledException ex)
            {
                throw WrapTimeoutException(ex);
            }
        }

        /// <summary>Список имён моделей Ollama (GET /api/tags от корня хоста).</summary>
        public static async Task<IReadOnlyList<string>> FetchOllamaModelNamesAsync(string ollamaOpenAiBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(ollamaOpenAiBaseUrl))
                return Array.Empty<string>();

            Uri u;
            try
            {
                u = new Uri(ollamaOpenAiBaseUrl.Trim(), UriKind.Absolute);
            }
            catch (UriFormatException ex)
            {
                throw new InvalidOperationException("Неверный URL Ollama. Пример: http://localhost:11434/v1", ex);
            }

            var root = $"{u.Scheme}://{u.Authority}";
            var tagsUrl = new Uri(root + "/api/tags", UriKind.Absolute);

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, tagsUrl))
                using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                {
                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {SummarizeHttpErrorBody(body)}");

                    var jo = JObject.Parse(body);
                    var arr = jo["models"] as JArray;
                    if (arr == null || arr.Count == 0)
                        return Array.Empty<string>();

                    var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in arr)
                    {
                        var id = item["name"]?.Value<string>();
                        if (string.IsNullOrWhiteSpace(id))
                            continue;
                        ids.Add(id.Trim());
                    }

                    return ids.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
            catch (HttpRequestException ex)
            {
                throw WrapNetworkException(ex);
            }
            catch (TaskCanceledException ex)
            {
                throw WrapTimeoutException(ex);
            }
        }

        /// <summary>Строка каталога OpenRouter с признаком нулевой стоимости prompt/completion.</summary>
        internal sealed class OpenRouterCatalogModel
        {
            public OpenRouterCatalogModel(string id, bool isFree)
            {
                Id = id ?? "";
                IsFree = isFree;
            }

            public string Id { get; }
            public bool IsFree { get; }
        }

        internal static bool OpenRouterPricingLooksFree(JToken pricingTok)
        {
            if (pricingTok == null || pricingTok.Type != JTokenType.Object)
                return false;
            var pStr = pricingTok["prompt"]?.Value<string>();
            var cStr = pricingTok["completion"]?.Value<string>();
            if (!decimal.TryParse(pStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var prompt))
                return false;
            if (!decimal.TryParse(cStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var completion))
                return false;
            return prompt == 0m && completion == 0m;
        }

        /// <summary>Все модели с публичного каталога OpenRouter (GET /api/v1/models), с признаком free по полю pricing.</summary>
        public static async Task<IReadOnlyList<OpenRouterCatalogModel>> FetchOpenRouterCatalogModelsAsync()
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, OpenRouterModelsListUrl))
            {
                req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://localhost/");
                req.Headers.TryAddWithoutValidation("X-Title", "Unity Text Translator");

                try
                {
                    using (var resp = await Http.SendAsync(req).ConfigureAwait(false))
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {SummarizeHttpErrorBody(body)}");

                        var jo = JObject.Parse(body);
                        var arr = jo["data"] as JArray;
                        if (arr == null || arr.Count == 0)
                            return Array.Empty<OpenRouterCatalogModel>();

                        var rows = new List<OpenRouterCatalogModel>();
                        foreach (var item in arr)
                        {
                            var id = item["id"]?.Value<string>();
                            if (string.IsNullOrWhiteSpace(id))
                                continue;
                            id = id.Trim();
                            var free = OpenRouterPricingLooksFree(item["pricing"]);
                            rows.Add(new OpenRouterCatalogModel(id, free));
                        }

                        return rows.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
                    }
                }
                catch (HttpRequestException ex)
                {
                    throw WrapNetworkException(ex);
                }
                catch (TaskCanceledException ex)
                {
                    throw WrapTimeoutException(ex);
                }
            }
        }

        /// <summary>Все id моделей с публичного каталога OpenRouter (GET /api/v1/models).</summary>
        public static async Task<IReadOnlyList<string>> FetchOpenRouterModelIdsAsync()
        {
            var rows = await FetchOpenRouterCatalogModelsAsync().ConfigureAwait(false);
            return rows.Select(x => x.Id).ToList();
        }

        public static string ExtractLangCode(string displayLanguageOption)
        {
            if (string.IsNullOrWhiteSpace(displayLanguageOption))
                return "auto";
            var m = Regex.Match(displayLanguageOption.Trim(), @"\(([^)]+)\)\s*$");
            return m.Success ? m.Groups[1].Value.Trim() : "auto";
        }

        public static bool UsesOpenRouter(string endpointBaseOrFullUrl)
        {
            if (string.IsNullOrWhiteSpace(endpointBaseOrFullUrl))
                return false;
            try
            {
                var u = new Uri(endpointBaseOrFullUrl.Trim(), UriKind.Absolute);
                return u.Host.IndexOf("openrouter", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (UriFormatException)
            {
                return false;
            }
        }

        /// <summary>LibreTranslate или POST …/chat/completions в зависимости от выбранного провайдера.</summary>
        /// <param name="notifyOpenRouterThrottleWaitSeconds">
        /// Если задано и активна пауза под лимит OpenRouter (:free), вызывается с числом секунд ожидания (для журнала UI).
        /// </param>
        public static Task<string> TranslateAutoAsync(
            TranslationAiBackend backend,
            string endpointBaseOrFullUrl,
            string apiKey,
            string chatModelId,
            string text,
            string sourceCode,
            string targetCode,
            Action<int> notifyOpenRouterThrottleWaitSeconds = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(endpointBaseOrFullUrl))
                throw new ArgumentException("Укажите URL сервера перевода.", nameof(endpointBaseOrFullUrl));

            var trimmed = endpointBaseOrFullUrl.Trim().TrimEnd('/');
            if (backend == TranslationAiBackend.LibreTranslate)
                return TranslateLibreCompatAsync(trimmed, apiKey, text, sourceCode, targetCode, cancellationToken);

            if (backend == TranslationAiBackend.CloudflareWorkersAi)
                return TranslateWorkersAiAsync(trimmed, apiKey, chatModelId, text, sourceCode, targetCode, cancellationToken);

            if (backend == TranslationAiBackend.Apify)
                return TranslateApifyAsync(trimmed, apiKey, chatModelId, text, sourceCode, targetCode, cancellationToken);

            return TranslateChatCompletionsAsync(
                backend,
                trimmed,
                apiKey,
                chatModelId,
                text,
                sourceCode,
                targetCode,
                notifyOpenRouterThrottleWaitSeconds,
                cancellationToken);
        }

        internal static Uri ResolveChatCompletionsUri(TranslationAiBackend backend, string endpointBaseOrFullUrl)
        {
            var t = endpointBaseOrFullUrl.Trim().TrimEnd('/');
            Uri u;
            try
            {
                u = new Uri(t, UriKind.Absolute);
            }
            catch (UriFormatException ex)
            {
                throw new InvalidOperationException(
                    "Неверный URL для Chat API. Пример: https://openrouter.ai/api/v1 или http://localhost:11434/v1", ex);
            }

            var full = t;
            if (full.IndexOf("chat/completions", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Uri(full, UriKind.Absolute);

            if (backend == TranslationAiBackend.OpenRouter &&
                u.Host.IndexOf("openrouter", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var path = u.AbsolutePath.TrimEnd('/');
                if (path.Length == 0 || path == "/")
                    return new Uri($"{u.Scheme}://{u.Authority}/api/v1/chat/completions");
            }

            return new Uri(full + "/chat/completions", UriKind.Absolute);
        }

        public static async Task<string> TranslateChatCompletionsAsync(
            TranslationAiBackend backend,
            string endpointBaseOrFullUrl,
            string bearerApiKey,
            string modelId,
            string text,
            string sourceCode,
            string targetCode,
            Action<int> notifyOpenRouterThrottleWaitSeconds = null,
            CancellationToken cancellationToken = default)
        {
            if (BackendRequiresBearerKey(backend) && string.IsNullOrWhiteSpace(bearerApiKey))
            {
                string msg;
                switch (backend)
                {
                    case TranslationAiBackend.OpenAI:
                        msg = "OpenAI: укажите API key (Bearer) в настройках.";
                        break;
                    case TranslationAiBackend.OpenRouter:
                        msg = "OpenRouter: укажите API key в настройках.";
                        break;
                    case TranslationAiBackend.GeminiOpenAi:
                        msg = "Google Gemini: укажите ключ API в поле «API key» — тот же ключ, что в Google AI Studio (передаётся как Bearer).";
                        break;
                    case TranslationAiBackend.Qwen:
                        msg = "Qwen (Model Studio / DashScope): укажите API Key из консоли (Bearer). Для ключа с modelstudio.console.alibabacloud.com обычно нужен базовый URL Singapore: …/dashscope-intl.aliyuncs.com/compatible-mode/v1";
                        break;
                    case TranslationAiBackend.Groq:
                        msg = "Groq: укажите API key в настройках.";
                        break;
                    case TranslationAiBackend.TogetherAI:
                        msg = "Together AI: укажите API key в настройках.";
                        break;
                    case TranslationAiBackend.Mistral:
                        msg = "Mistral AI: укажите API key в настройках.";
                        break;
                    case TranslationAiBackend.DeepSeek:
                        msg = "DeepSeek: укажите API key в настройках.";
                        break;
                    case TranslationAiBackend.Cohere:
                        msg = "Cohere: укажите API key в настройках (Bearer).";
                        break;
                    case TranslationAiBackend.Kimi:
                        msg = "Kimi (Moonshot): укажите API key с платформы Moonshot.";
                        break;
                    case TranslationAiBackend.Nvidia:
                        msg = "NVIDIA: укажите ключ API (Bearer) для integrate.api.nvidia.com.";
                        break;
                    default:
                        msg = "Укажите API key в настройках для выбранного провайдера.";
                        break;
                }

                throw new InvalidOperationException(msg);
            }

            text = text ?? "";
            modelId = string.IsNullOrWhiteSpace(modelId) ? DefaultChatModelId(backend) : modelId.Trim();

            var url = ResolveChatCompletionsUri(backend, endpointBaseOrFullUrl);

            var systemPrompt = BuildChatSystemPrompt(sourceCode, targetCode, text);

            var payload = new JObject
            {
                ["model"] = modelId,
                ["temperature"] = 0.2,
                ["max_tokens"] = ComputeChatMaxOutputTokens(text, modelId),
                ["messages"] = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JObject { ["role"] = "user", ["content"] = text }
                }
            };

            try
            {
                return await SendChatCompletionRequestAsync(
                    backend,
                    url,
                    bearerApiKey,
                    payload,
                    modelId,
                    notifyOpenRouterThrottleWaitSeconds,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw WrapNetworkException(ex);
            }
        }

        /// <summary>Встроенные пресеты + ответ сервера — без дубликатов, для выпадающего списка моделей.</summary>
        public static List<string> MergePresetAndFetchedModels(TranslationAiBackend backend, IReadOnlyList<string> fetched)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in ModelPresetsForBackend(backend))
                set.Add(x);

            if (fetched != null)
            {
                foreach (var x in fetched)
                {
                    if (!string.IsNullOrWhiteSpace(x))
                        set.Add(x.Trim());
                }
            }

            return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        static Uri WorkersAiRunUri(string accountsApiRoot, string modelId)
        {
            var root = (accountsApiRoot ?? "").Trim().TrimEnd('/');
            var model = (modelId ?? "").Trim();
            if (model.Length == 0)
                throw new InvalidOperationException("Cloudflare Workers AI: укажите имя модели (например @cf/meta/llama-3.1-8b-instruct).");

            var encodedPath = string.Join("/", model.Trim('/').Split('/').Select(Uri.EscapeDataString));
            try
            {
                return new Uri($"{root}/ai/run/{encodedPath}", UriKind.Absolute);
            }
            catch (UriFormatException ex)
            {
                throw new InvalidOperationException(
                    "Cloudflare Workers AI: неверный URL. Ожидается база вида https://api.cloudflare.com/client/v4/accounts/<ACCOUNT_ID>", ex);
            }
        }

        /// <summary>Workers AI REST: POST …/accounts/&lt;id&gt;/ai/run/&lt;model&gt; с телом {{ "prompt": "…" }}.</summary>
        public static async Task<string> TranslateWorkersAiAsync(
            string accountsApiRoot,
            string bearerApiToken,
            string modelId,
            string text,
            string sourceCode,
            string targetCode,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(bearerApiToken))
                throw new InvalidOperationException(
                    "Cloudflare Workers AI: создайте Workers AI API Token в дашборде Cloudflare и вставьте его в поле «API key».");

            var root = (accountsApiRoot ?? "").Trim().TrimEnd('/');
            if (root.IndexOf("/accounts/", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException(
                    "Cloudflare Workers AI: базовый URL должен содержать …/accounts/&lt;ваш Account ID&gt; " +
                    "(например https://api.cloudflare.com/client/v4/accounts/xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx).");

            if (root.IndexOf("YOUR_ACCOUNT_ID", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException(
                    "Cloudflare Workers AI: замените YOUR_ACCOUNT_ID в URL на реальный Account ID (Workers AI → Use REST API).");

            text = text ?? "";
            modelId = string.IsNullOrWhiteSpace(modelId)
                ? DefaultChatModelId(TranslationAiBackend.CloudflareWorkersAi)
                : modelId.Trim();

            var systemPrompt = BuildChatSystemPrompt(sourceCode, targetCode, text);
            var tgt = string.IsNullOrWhiteSpace(targetCode) ? "en" : targetCode.Trim();

            var url = WorkersAiRunUri(root, modelId);

            try
            {
                var tryTranslationSchema = false;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    JObject payload;
                    if (tryTranslationSchema)
                    {
                        payload = new JObject
                        {
                            ["text"] = text,
                            ["target_lang"] = tgt
                        };
                        if (!string.IsNullOrWhiteSpace(sourceCode) && !sourceCode.Equals("auto", StringComparison.OrdinalIgnoreCase))
                            payload["source_lang"] = sourceCode.Trim();
                    }
                    else
                    {
                        payload = new JObject
                        {
                            ["prompt"] = systemPrompt + "\n\n" + text,
                        };
                    }

                    using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearerApiToken.Trim());
                        req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

                        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            linked.CancelAfter(TimeSpan.FromSeconds(ChatCompletionPerAttemptTimeoutSeconds));
                            HttpResponseMessage resp;
                            try
                            {
                                resp = await Http.SendAsync(req, linked.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException ex)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                throw new InvalidOperationException(
                                    "Нет ответа от Workers AI за " + ChatCompletionPerAttemptTimeoutSeconds +
                                    " с (таймаут запроса). Проверьте модель, лимиты и Account ID.",
                                    ex);
                            }

                            using (resp)
                            {
                                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                                if (!resp.IsSuccessStatusCode)
                                {
                                    // Некоторые модели Workers AI требуют schema { text, target_lang }.
                                    if (!tryTranslationSchema &&
                                        (int)resp.StatusCode == 400 &&
                                        body.IndexOf("text,target_lang", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        tryTranslationSchema = true;
                                        continue;
                                    }

                                    throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {SummarizeHttpErrorBody(body)}");
                                }

                                var jo = JObject.Parse(body);
                                var ok = jo["success"]?.Value<bool>() ?? false;
                                if (!ok)
                                {
                                    var errs = jo["errors"] as JArray;
                                    var detail = "";
                                    if (errs != null && errs.Count > 0)
                                    {
                                        detail = string.Join("; ",
                                            errs.Select(t =>
                                                t["message"]?.Value<string>()
                                                ?? t["code"]?.Value<string>()
                                                ?? t.ToString()));
                                    }

                                    if (string.IsNullOrWhiteSpace(detail))
                                        detail = SummarizeHttpErrorBody(body);
                                    throw new InvalidOperationException("Workers AI: " + detail);
                                }

                                var response =
                                    jo["result"]?["response"]?.Value<string>()
                                    ?? jo["result"]?["translated_text"]?.Value<string>()
                                    ?? jo["result"]?["text"]?.Value<string>()
                                    ?? jo["result"]?.ToString();
                                return (response ?? "").Trim();
                            }
                        }
                    }
                }

                throw new InvalidOperationException("Workers AI: пустой ответ после повторной попытки с alternate schema.");
            }
            catch (HttpRequestException ex)
            {
                throw WrapNetworkException(ex);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        /// <summary>Apify: POST …/v2/acts/{actorId}/run-sync?token=… с {text,source,target}. Bearer-заголовок или ?token= в URL.</summary>
        public static async Task<string> TranslateApifyAsync(
            string baseUrl,
            string apiToken,
            string actorId,
            string text,
            string sourceCode,
            string targetCode,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
                throw new InvalidOperationException(
                    "Apify: укажите API Token в поле «API key» (можно получить в Apify Console → Integrations).");

            var id = (actorId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id) || id.Equals("YOUR_ACTOR_ID", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Apify: введите ID Actor-а в поле «Модель чата» (например your-actor-id).");

            var root = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(root))
                root = "https://api.apify.com/v2";

            var urlStr = root;
            if (urlStr.IndexOf("YOUR_ACTOR_ID", StringComparison.OrdinalIgnoreCase) >= 0)
                urlStr = Regex.Replace(urlStr, "YOUR_ACTOR_ID", id, RegexOptions.IgnoreCase);
            else if (urlStr.IndexOf("/acts/", StringComparison.OrdinalIgnoreCase) < 0)
                urlStr = urlStr.TrimEnd('/') + "/acts/" + Uri.EscapeDataString(id);
            if (urlStr.IndexOf("/run-sync", StringComparison.OrdinalIgnoreCase) < 0)
                urlStr = urlStr.TrimEnd('/') + "/run-sync";

            // предпочтительно Bearer, но добавим токен и в query для совместимости
            Uri full;
            try
            {
                full = new Uri(urlStr, UriKind.Absolute);
            }
            catch (UriFormatException ex)
            {
                throw new InvalidOperationException(
                    "Apify: неверный URL. Ожидается https://api.apify.com/v2/acts/YOUR_ACTOR_ID/run-sync", ex);
            }

            var src = string.IsNullOrWhiteSpace(sourceCode) || sourceCode.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? ""
                : sourceCode.Trim();
            var tgt = string.IsNullOrWhiteSpace(targetCode) ? "en" : targetCode.Trim();

            var payload = new JObject
            {
                ["text"] = text ?? "",
                ["target_lang"] = tgt
            };
            if (src.Length > 0)
                payload["source_lang"] = src;

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, full))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiToken.Trim());
                    req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        linked.CancelAfter(TimeSpan.FromSeconds(ChatCompletionPerAttemptTimeoutSeconds));

                        HttpResponseMessage resp;
                        try
                        {
                            resp = await Http.SendAsync(req, linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException ex)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw new InvalidOperationException(
                                "Нет ответа от Apify за " + ChatCompletionPerAttemptTimeoutSeconds +
                                " с (таймаут запроса). Проверьте Actor ID и доступность актера.",
                                ex);
                        }

                        using (resp)
                        {
                            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode)
                                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {SummarizeHttpErrorBody(body)}");

                            var jo = JObject.Parse(body);

                            // Apify успешный ответ часто содержит data.items / data[0].translatedText / data.translation
                            var translated =
                                jo["data"]?["items"]?[0]?["translatedText"]?.Value<string>()
                                ?? jo["data"]?["translation"]?.Value<string>()
                                ?? jo["data"]?["translatedText"]?.Value<string>()
                                ?? jo["translatedText"]?.Value<string>()
                                ?? jo["translation"]?.Value<string>()
                                ?? jo["output"]?.Value<string>()
                                ?? jo["result"]?.Value<string>();

                            return (translated ?? "").Trim();
                        }
                    }
                }
            }
            catch (HttpRequestException ex)
            {
                throw WrapNetworkException(ex);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        public static async Task<string> TranslateLibreCompatAsync(
            string endpointBaseOrFullUrl,
            string apiKey,
            string text,
            string sourceCode,
            string targetCode,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(endpointBaseOrFullUrl))
                throw new ArgumentException("Укажите URL сервера перевода.", nameof(endpointBaseOrFullUrl));

            text = text ?? "";
            var trimmed = endpointBaseOrFullUrl.TrimEnd('/');
            var url = trimmed.EndsWith("/translate", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : trimmed + "/translate";

            var payload = new JObject
            {
                ["q"] = text,
                ["source"] = string.IsNullOrWhiteSpace(sourceCode) || sourceCode.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? "auto"
                    : sourceCode,
                ["target"] = string.IsNullOrWhiteSpace(targetCode) ? "en" : targetCode,
                ["format"] = "text"
            };

            if (!string.IsNullOrWhiteSpace(apiKey))
                payload["api_key"] = apiKey.Trim();

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        linked.CancelAfter(TimeSpan.FromSeconds(ChatCompletionPerAttemptTimeoutSeconds));

                        HttpResponseMessage resp;
                        try
                        {
                            resp = await Http.SendAsync(req, linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException ex)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            throw WrapTimeoutException(new TaskCanceledException(ex.Message, ex));
                        }

                        using (resp)
                        {
                            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!resp.IsSuccessStatusCode)
                                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {SummarizeHttpErrorBody(body)}");

                            var jo = JObject.Parse(body);
                            var translated = jo["translatedText"]?.Value<string>()
                                           ?? jo["data"]?["translatedText"]?.Value<string>();

                            return translated ?? "";
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw WrapNetworkException(ex);
            }
        }

        static InvalidOperationException WrapTimeoutException(TaskCanceledException ex)
        {
            const string intro = "Таймаут HTTP (2 мин.) или запрос отменён. / HTTP timeout or request canceled.";
            return new InvalidOperationException(intro + " " + FormatNetworkProblem(ex), ex);
        }

        static InvalidOperationException WrapNetworkException(Exception ex)
        {
            return new InvalidOperationException(FormatNetworkProblem(ex), ex);
        }

        static string FormatNetworkProblem(Exception ex)
        {
            var msgs = new List<string>();
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                var m = (e.Message ?? "").Trim();
                if (m.Length == 0)
                    continue;
                var dup = false;
                foreach (var x in msgs)
                {
                    if (string.Equals(x, m, StringComparison.OrdinalIgnoreCase))
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                    msgs.Add(m);
            }

            var core = msgs.Count > 0 ? string.Join(" → ", msgs) : (ex.Message ?? "");
            return core;
        }

        static bool LooksLikeDashScopePurchaseOrEligibilityIssue(string summarized, string rawBody)
        {
            var blob = ((summarized ?? "") + "\n" + (rawBody ?? "")).ToLowerInvariant();
            if (blob.IndexOf("unpurchased", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (blob.IndexOf("access to model denied", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (blob.IndexOf("eligible for using the model", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        static string AppendDashScopeAccessDeniedHint(string summarized, string rawBody)
        {
            if (string.IsNullOrEmpty(summarized))
                summarized = "";
            if (!LooksLikeDashScopePurchaseOrEligibilityIssue(summarized, rawBody))
                return summarized;

            return summarized.TrimEnd()
                + " — DashScope: по FAQ Alibaba код Unpurchased при любой модели чаще всего значит, что Model Studio ещё не активирован (примите условия в консоли), либо регион ключа и базового URL не совпадают: ключ Singapore → "
                + DashScopeOpenAiCompatibleIntlBaseUrl
                + "; ключ материкового Китая (Beijing) → "
                + DashScopeOpenAiCompatibleChinaBaseUrl
                + ". Ключ Coding Plan (sk-sp-…) использует другой endpoint. Документация: раздел 403-AccessDenied.Unpurchased в Model Studio troubleshooting.";
        }

        internal static string SummarizeHttpErrorBody(string body)
        {
            if (string.IsNullOrEmpty(body))
                return "";

            var t = body.TrimStart();
            if (t.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                if (body.IndexOf("openrouter", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "HTML вместо JSON: проверьте URL (для OpenRouter нужен …/api/v1/chat/completions или база https://openrouter.ai/api/v1).";
                return "HTML вместо JSON — проверьте базовый URL и endpoint.";
            }

            if (t.StartsWith("{", StringComparison.Ordinal) || t.StartsWith("[", StringComparison.Ordinal))
            {
                try
                {
                    var jo = JObject.Parse(body);
                    var errTok = jo["error"];
                    string msg = null;
                    string codeStr = null;

                    if (errTok is JObject errObj)
                    {
                        msg = errObj["message"]?.Value<string>() ?? errObj["msg"]?.Value<string>();
                        var codeTok = errObj["code"];
                        if (codeTok != null && codeTok.Type != JTokenType.Null)
                            codeStr = codeTok.ToString();
                    }
                    else if (errTok != null && errTok.Type == JTokenType.String)
                        msg = errTok.Value<string>();

                    if (string.IsNullOrWhiteSpace(msg))
                        msg = jo["message"]?.Value<string>();

                    if (!string.IsNullOrWhiteSpace(msg))
                    {
                        msg = msg.Trim();
                        if (!string.IsNullOrWhiteSpace(codeStr) && msg.IndexOf(codeStr, StringComparison.Ordinal) < 0)
                            msg = $"[{codeStr}] {msg}";

                        var lower = msg.ToLowerInvariant();
                        if (lower.Contains("insufficient credits") || lower.Contains("never purchased credits"))
                            msg += " — Нужно пополнить баланс OpenRouter: https://openrouter.ai/settings/credits";

                        return AppendDashScopeAccessDeniedHint(msg, body);
                    }
                }
                catch { } // не JSON — режем как текст ниже
            }

            const int max = 500;
            if (body.Length <= max)
                return AppendDashScopeAccessDeniedHint(body.Trim(), body);
            return AppendDashScopeAccessDeniedHint(body.Substring(0, max) + "…", body);
        }
    }
}
