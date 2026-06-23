using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    public partial class Form1 : Form
    {
        private string currentFolder = "";
        private string lastUnityGameDataFolder = "";
        private readonly List<TranslationItem> translationItems = new List<TranslationItem>();
        private int _jsonSortColumn = -1;
        private SortOrder _jsonSortOrder = SortOrder.None;
        private bool createBackup = true;
        private bool useTranslationMemory = true;
        private string sourceLanguageDisplay = "English (en)";
        private string targetLanguageDisplay = "Russian (ru)";

        /// <summary>Пункт «авто-определение» источника (только для cbSrc). ExtractLangCode → «auto».</summary>
        internal const string AutoDetectSourceOption = "Auto-detect (auto)";

        /// <summary>
        /// Языки перевода (цель), формат «Имя (код)» (код — <see cref="LocalTranslateApi.ExtractLangCode"/> из последних скобок). ISO 639-1 + региональные zh-CN/pt-BR
        /// намеренно (LLM точнее). Существующие строки НЕ переименовывать (привязаны настройки). Источник (cbSrc) получает <see cref="AutoDetectSourceOption"/> первым.
        /// </summary>
        internal static readonly string[] UiLanguageOptions =
        {
            "Afrikaans (af)", "Albanian (sq)", "Amharic (am)", "Arabic (ar)", "Armenian (hy)",
            "Azerbaijani (az)", "Basque (eu)", "Belarusian (be)", "Bengali (bn)", "Bosnian (bs)",
            "Bulgarian (bg)", "Catalan (ca)", "Chinese Simplified (zh-CN)", "Chinese Traditional (zh-TW)",
            "Croatian (hr)", "Czech (cs)", "Danish (da)", "Dutch (nl)", "English (en)", "Estonian (et)",
            "Filipino (tl)", "Finnish (fi)", "French (fr)", "Galician (gl)", "Georgian (ka)",
            "German (de)", "Greek (el)", "Gujarati (gu)", "Hebrew (he)", "Hindi (hi)", "Hungarian (hu)",
            "Icelandic (is)", "Indonesian (id)", "Irish (ga)", "Italian (it)", "Japanese (ja)",
            "Kannada (kn)", "Kazakh (kk)", "Korean (ko)", "Latvian (lv)", "Lithuanian (lt)",
            "Macedonian (mk)", "Malay (ms)", "Malayalam (ml)", "Marathi (mr)", "Mongolian (mn)",
            "Norwegian (no)", "Persian (fa)", "Polish (pl)", "Portuguese (pt-BR)", "Portuguese Portugal (pt-PT)",
            "Punjabi (pa)", "Romanian (ro)", "Russian (ru)", "Serbian (sr)", "Slovak (sk)", "Slovenian (sl)",
            "Spanish (es)", "Swahili (sw)", "Swedish (sv)", "Tamil (ta)", "Telugu (te)", "Thai (th)",
            "Turkish (tr)", "Ukrainian (uk)", "Urdu (ur)", "Vietnamese (vi)", "Welsh (cy)"
        };
        private bool isDarkTheme = false;
        private string currentThemeName = "Translator Purple";
        /// <summary>Код языка интерфейса: «en» или «ru».</summary>
        private string appUiLanguage = "en";
        private string currentSearchText = "";
        private string lastJsonExtractFolder = "";
        private bool applyTableSearchPosted;
        private int jsonTxtFormatSelectedIndex;
        private int jsonCopyModeSelectedIndex = 1;
        /// <summary>Комбо «Способ копирования JSON» на экране настроек; для синхронизации перед экспортом/копированием.</summary>
        private NoWheelComboBox settingsJsonCopyModeCombo;
        private bool translationApiEnabled;
        private string translationApiUrl = "http://localhost:5000";
        private string translationApiKey = "";
        /// <summary>Ключ провайдера: см. <see cref="TranslationBackendKeysCore"/>.</summary>
        private string translationAiBackend = "LibreTranslate";
        /// <summary>Slug модели для POST …/chat/completions (все chat-провайдеры).</summary>
        private string translationOpenRouterModel = "openai/gpt-4o-mini";

        private static readonly string[] TranslationBackendKeysCore =
        {
            "LibreTranslate",
            "OpenRouter",
            "OpenAI",
            "Mistral",
            "DeepSeek",
            "Gemini",
            "Ollama",
            "CustomOpenAiCompatible",
        };

        private static readonly string[] TranslationBackendKeysJunk =
        {
            "Groq",
            "TogetherAI",
            "Qwen",
            "Cohere",
            "Kimi",
            "Nvidia",
            "Cursor",
            "CloudflareWorkersAi",
            "Apify",
        };

        /// <summary>Включить дополнительные опции (редко нужные провайдеры и т.п.). Сохраняется в JSON настроек.</summary>
        private bool junkFeaturesEnabled;

        private string[] GetTranslationBackendKeys() =>
            junkFeaturesEnabled
                ? TranslationBackendKeysCore.Concat(TranslationBackendKeysJunk).ToArray()
                : TranslationBackendKeysCore;

        private static bool IsJunkTranslationBackendKey(string key) =>
            !string.IsNullOrEmpty(key) && Array.IndexOf(TranslationBackendKeysJunk, key) >= 0;

        private void EnsureTranslationBackendKeyAllowed()
        {
            if (IsJunkTranslationBackendKey(translationAiBackend) && !junkFeaturesEnabled)
                translationAiBackend = "LibreTranslate";
            var keys = GetTranslationBackendKeys();
            if (Array.IndexOf(keys, translationAiBackend) < 0)
                translationAiBackend = "LibreTranslate";
        }

        private void RepopulateAiBackendCombo(NoWheelComboBox cb)
        {
            if (cb == null || cb.IsDisposed)
                return;

            cb.Items.Clear();
            cb.Items.Add(L("LibreTranslate — local POST /translate", "LibreTranslate — локальный POST /translate"));
            cb.Items.Add("OpenRouter");
            cb.Items.Add(L("OpenAI", "OpenAI"));
            cb.Items.Add(L("Mistral AI", "Mistral AI"));
            cb.Items.Add(L("DeepSeek", "DeepSeek"));
            cb.Items.Add(L("Google Gemini — Google AI Studio API key", "Google Gemini — ключ Google AI Studio"));
            cb.Items.Add(L("Ollama — local /v1", "Ollama — локальный /v1"));
            cb.Items.Add(L("Custom OpenAI-compatible URL", "Свой OpenAI-совместимый URL"));
            if (junkFeaturesEnabled)
            {
                cb.Items.Add(L("Groq (OpenAI-compatible)", "Groq (OpenAI-совместимый)"));
                cb.Items.Add(L("Together AI", "Together AI"));
                cb.Items.Add(L("Qwen — Alibaba DashScope (OpenAI-compatible)", "Qwen — Alibaba DashScope (OpenAI-совместимый)"));
                cb.Items.Add(L("Cohere — API key", "Cohere — ключ API"));
                cb.Items.Add(L("Kimi (Moonshot) — API key", "Kimi (Moonshot) — ключ API"));
                cb.Items.Add(L("NVIDIA API — integrate.api.nvidia.com", "NVIDIA API — integrate.api.nvidia.com"));
                cb.Items.Add(L("Cursor API (OpenAI-compatible)", "Cursor API (OpenAI-совместимый)"));
                cb.Items.Add(L("Cloudflare Workers AI (REST run)", "Cloudflare Workers AI (REST run)"));
                cb.Items.Add(L("Apify — API token + Actor ID", "Apify — API token + Actor ID"));
            }

            EnsureTranslationBackendKeyAllowed();
            var keys = GetTranslationBackendKeys();
            var ix = Array.IndexOf(keys, translationAiBackend);
            cb.SelectedIndex = ix >= 0 ? ix : 0;
        }
        /// <summary>Полный список моделей последней успешной загрузки для фильтра в настройках.</summary>
        private readonly List<string> _translationChatModelCatalog = new List<string>();

        /// <summary>OpenRouter: id с нулевой ценой prompt+completion в каталоге /api/v1/models.</summary>
        private readonly HashSet<string> _openRouterFreeModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private bool _chatModelComboBulkRefresh;
        /// <summary>Приветствие для новых установок; в JSON как WelcomeShown.</summary>
        private bool welcomeShown;
        private readonly List<string> recentJsonFolders = new List<string>();
        private DateTime? lastManualBackupUtc;
        private int? savedWindowX;
        private int? savedWindowY;
        private int? savedWindowWidth;
        private int? savedWindowHeight;
        private bool savedWindowMaximized;
        /// <summary>Папка, для которой на дашборде показывается число JSON (последнее полное сканирование при извлечении).</summary>
        private string lastDashboardJsonScanFolder = "";
        private int lastDashboardJsonScanTotal;

        /// <summary>Пульс журнала во время пакетного API-перевода (HTTP может висеть минуты без новых строк в foreach).</summary>
        private System.Windows.Forms.Timer _apiBatchHeartbeatTimer;
        private volatile int _apiBatchHeartbeatStep;
        private volatile int _apiBatchHeartbeatTotal;
        private volatile bool _apiBatchHeartbeatActive;
        private int _apiBatchHeartbeatLastSeenStep = -1;
        private int _apiBatchHeartbeatSameStepPulseCount;

        private CancellationTokenSource _apiBatchTranslateCts;
        /// <summary>Отмена длительных операций Unity .assets (экспорт/импорт JSON) по Esc.</summary>
        private CancellationTokenSource _assetsWorkCts;

        /// <summary>Кеш корня UI «Главная» в RAM процесса (не файл/AppData): переиспользуется при возврате на «Главную».</summary>
        private Panel cachedDashboardRoot;
        private int _dashboardContentStamp;
        private int _dashboardCacheBuiltAtStamp = -1;
        private string _cachedDashboardChromeKey = "";
        private Color _themePageBg = Color.FromArgb(246, 248, 250);
        private Color _themeHeaderText = Color.FromArgb(31, 35, 40);
        private Color _themeSubtitleText = Color.FromArgb(87, 96, 106);
        private Color _themeGridBg = Color.White;
        private Color _themeGridHeaderBg = Color.FromArgb(36, 41, 47);
        private Color _themeGridColor = Color.FromArgb(208, 215, 222);
        private Color _themeGridRowBg = Color.White;
        private Color _themeGridRowFore = Color.FromArgb(31, 35, 40);
        /// <summary>Исключает параллельные <see cref="ExtractTextsAsync"/> (очистка/заполнение <see cref="translationItems"/>).</summary>
        private readonly SemaphoreSlim _extractTextsAsyncGate = new SemaphoreSlim(1, 1);

        private sealed class TranslationUndoCell
        {
            /// <summary>Ссылка на элемент (НЕ индекс): отмена находит строку через Tag → устойчиво к пересортировке/фильтру после правки.</summary>
            public TranslationItem Item;
            public string PreviousTranslated;
        }

        /// <summary>История отмены для колонки «Перевод» (один шаг = один список ячеек до правки).</summary>
        private readonly List<List<TranslationUndoCell>> _translationUndoFrames = new List<List<TranslationUndoCell>>();
        private const int MaxTranslationUndoFrames = 120;
        private bool _suppressTranslationUndoRecording;
        private int _translatedEditStartRow = -1;
        private string _translatedEditStartValue;

        /// <summary>Снимок только для чтения-колонок таблицы перевода (вход в режим правки ради выделения и Ctrl+C).</summary>
        private bool _mainGridReadOnlyPreviewActive;
        private object _mainGridReadOnlyPreviewBackup;

        private Label assetsModuleFolderLabel;
        private Button assetsModuleBuildButton;
        private ProgressBar assetsModuleProgressBar;
        internal RichTextBox assetsModuleLogBox;
        private Button assetsModuleExportButton;
        private Button assetsModuleExportSingleAssetButton;
        private Button assetsModuleFindFontsButton;
        private Button assetsModuleImportTmpFontButton;
        private Button assetsModuleTtfToTmpFontButton;
        private Button assetsModulePatchTmpMsdfAtlasButton;
        private Button assetsModuleReplaceAtlasTexturePngButton;
        private Button assetsModuleFindResourcesCrcButton;
        private Button assetsModuleDumpPathIdFieldsButton;
        private DataGridView assetsModuleAssetsGrid;
        private Label assetsModuleAssetsStatsLabel;
        private Button assetsModulePickGameFolderButton;

        private static readonly HashSet<string> SkipKeys = new HashSet<string>(new[]
        {
            "m_FileID", "m_PathID", "m_Script", "m_Material",
            // НЕ добавляйте сюда "Array": в экспорте AssetsTools/UABEA это имя обёртки для List/массивов полей;
            // пропуск целиком вырезает таблицы строк, записи локализации и т.п.
            "m_PersistentCalls", "m_OnCullStateChanged", "m_RaycastPadding",
            "m_Color", "m_RaycastTarget", "m_Maskable", "m_Enabled", "m_Font",
            "m_FontData", "m_LineSpacing", "m_TextStyle",
            // TextMeshPro Font Asset / генерация: не пользовательский текст (метрики, GUID шрифта, служебные имена)
            "m_IgnoreTag",
            "m_FaceInfo",
            "m_CreationSettings",
            // Unity UI Selectable/Button — строки триггеров Animator (Normal/Highlighted…), не текст для перевода
            "m_AnimationTriggers", "m_NormalTrigger", "m_HighlightedTrigger", "m_PressedTrigger", "m_DisabledTrigger",
            "m_SelectedTrigger",
            // SerializeReference / управляемое поле (часто class + asm + ns в экспортированном JSON)
            "class", "asm", "ns",
            // PersistentCall / Timeline — имена типов и методов, не пользовательский текст
            "m_TargetAssemblyTypeName", "m_ObjectArgumentAssemblyTypeName", "m_MethodName",
            "m_CustomPlayableFullTypename",
            "m_InputAxisName",
            // Visual Scripting: связи графа как GUID; машины — внутренние имена событий/действий
            "node", "port", "eventIdentityName", "actionName",
            // Timeline / закладки / свойства — значения GUID
            "eventGuid", "propertyGuid", "bookmarkGuid",
            // TMP Font Asset — ссылки на шрифт / другой asset (вне m_CreationSettings)
            "referencedFontAssetGUID", "m_SourceFontFileGUID", "sourceFontFileGUID",
            // Cinemachine / Input — имена осей, не локализуемые фразы
            "mouseX", "mouseY", "zoomAxis",
            // Animator / переходы — пути или идентификаторы состояний
            "m_From", "m_To",
            // Шрифт TMP (если поля вынесены из m_FaceInfo в плоском JSON)
            "m_FamilyName", "m_StyleName",
            // Служебные строки Unity / ссылок (не игровой текст для локализации)
            "m_TagString", "m_Namespace", "m_Icon", "m_AssetBundleName", "m_AssetBundleVariant",
            "m_SortingLayerName", "m_SpriteSortPoint", "m_ShaderKeywords",
            "type", "guid", "rid",
            // Имя объекта/ассета (Bloom, Controls, UI_en, sharedassets…) — не реплики/UI; переводим m_Localized, m_Text, m_TextContainer…
            "m_Name",
            // Теги / слои частей тела и пр. идентификаторы, не пользовательский текст
            "bodyPartTag", "bodyPartLayer", "currentTag", "objectTag",
            // Unity backing field для Guid в сериализованных ScriptableObject / графах
            "<Guid>k__BackingField",
            // Диалоговые системы / Unity Localization (напр. SarahsHouse): технические поля, не текст для игрока.
            // uid — GUID; variableName — внутренние переменные (lc_mad, dialog_*); m_Key/m_TableCollectionName —
            // ключ и имя таблицы локализации; onDialogBaseAnim — имя анимации (Idle); location — внутренний id сцены (home/store/gym).
            "uid", "variableName", "m_Key", "m_TableCollectionName", "onDialogBaseAnim", "location"
        }, StringComparer.OrdinalIgnoreCase);

        /// <summary>Доп. к <see cref="SkipKeys"/>: поддеревья с этими именами не считаются игровым текстом при удалении JSON «только метаданные».</summary>
        private static readonly HashSet<string> MetadataOnlyJsonKeys = new HashSet<string>(new[]
        {
            "m_EditorClassIdentifier",
            "m_ObjectHideFlags",
            "m_OriginalName",
            "m_LocalIdentifierInFile",
            "m_CustomRenderQueueTag",
            "m_MaterialPresetName",
            "m_DefaultLayerName",
            "m_HorizontalAlignment",
            "m_VerticalAlignment",
            "m_TextAlignment",
            "m_TextWrappingMode",
            "m_OverflowMode",
            "m_VerticalMapping",
            "m_UvLineOffset",
            "m_enableWordWrapping",
            "m_TextPreprocessor",
            "m_IsTextObjectScaleStatic",
            "m_IsOrthographic",
            "m_MaterialReference",
            "m_ActiveFontFeatures",
            "m_LanguageDirectionOverride",
            "m_SubMeshUvs",
            "m_Padding",
            "m_Margin",
            "m_AtlasPopulationMode",
            "m_ElementType",
            "m_KeyboardType",
            "m_LineType",
            "m_InputType",
            "m_ContentType",
            "m_Navigation",
            "m_Transition",
            "m_TargetGraphic",
            "m_Interactable",
            "m_CullTransparentMesh",
            "m_Mesh",
            "m_UpdateMode",
            "m_CullingMode",
            "m_ApplyRootMotion",
            "m_AnimationClip",
            "m_Controller",
            "m_Avatar",
            "m_LinearVelocityBlending",
            "m_StabilizeFeet",
            "m_WarningMessage",
            "m_ParentPrefab",
            "m_Modification",
            "m_SourcePrefab",
            "m_SourceObject",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
        }, StringComparer.OrdinalIgnoreCase);

        /// <summary>Ключ поля строки JSON: здесь текст считают игровым — эвристики «Unity technical string» при удалении только метаданных не режут значение.</summary>
        private static readonly HashSet<string> MetadataPurgeGameplayStringKeys = new HashSet<string>(new[]
        {
            "m_Text",
            "text",
            "localizedString",
            "m_LocalizedString",
            "m_TranslatedString",
            "description",
            "m_Description",
            "message",
            "m_Message",
            "title",
            "m_Title",
            "subtitle",
            "m_Subtitle",
            "hint",
            "m_Hint",
            "tooltip",
            "m_Tooltip",
            "label",
            "m_Label",
            "prompt",
            "m_Prompt",
            "placeholder",
            "m_Placeholder",
            "content",
            "m_Content",
            "body",
            "m_Body",
            "caption",
            "m_Caption",
            "key",
            "m_Key",
            "displayName",
            "m_DisplayName",
            // Unity Localization Package
            "m_Translation",
            "Translation",
        }, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Имена полей/контейнеров, чьи значения при удалении «только метаданные» технические (не текст), даже со «словами».
        /// Проверяется как последний именованный сегмент пути (индексы <c>[N]</c> пропускаются → покрывает <c>scenesForLoad › [0]</c>).
        /// generic <c>name</c>/<c>description</c> команд НЕ входят (похожи на текст).
        /// </summary>
        private static readonly HashSet<string> MetadataPurgeTechnicalPathSegments = new HashSet<string>(new[]
        {
            // Имена сцен для загрузки/выгрузки — это идентификаторы сцен, а не текст для игрока.
            "scenesForLoad",
            "scenesForUnload",
            // Идентификатор локации/сцены (по нему грузится сцена) — перевод ломает загрузку по имени.
            "locname",
            // Наборы имён выражений глаз и т.п. — внутренние идентификаторы.
            "eyeExpNames",
            // Имена стадий (HScene и пр.) — внутренние ключи стадий, не игровой текст.
            "stageNames",
            // Список отключённых полей инспектора (Cinemachine и пр.) — имена C#-полей.
            "m_ExcludedPropertiesInInspector",
            // TMP StyleSheet: значения — это только rich-text-разметка открытия/закрытия тега.
            "m_OpeningDefinition",
            "m_ClosingDefinition",
        }, StringComparer.OrdinalIgnoreCase);

        /// <summary>Поля, которые часто попадают в таблицу, но на экспорт для перевода не нужны (режим «JSON по правилам»).</summary>
        private static readonly HashSet<string> JsonCopyTechnicalLeafHints = new HashSet<string>(new[]
        {
            // Visual Scripting / диалоговые графы — имя свойства узла, не игроковская реплика
            "propertyName",
            // Cinemachine / инспектор — список отключённых полей часто включает m_Script как строку-символ «не заходить»
            "m_ExcludedPropertiesInInspector",
        }, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> MetadataPurgeTechnicalFileExtensions =
            new HashSet<string>(
                new[]
                {
                    ".prefab", ".unity", ".asset", ".mat", ".anim", ".controller", ".overridecontroller",
                    ".mask", ".physicmaterial", ".cs", ".dll", ".shader", ".cginc", ".hlsl",
                    ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".bmp", ".gif",
                    ".wav", ".mp3", ".ogg", ".bank", ".ttf", ".otf",
                    ".shadergraph", ".vfx",
                }, StringComparer.OrdinalIgnoreCase);

        /// <summary>Regex кэшируется — вызывается только при операции массового удаления JSON по метаданным.</summary>
        private static readonly Regex MetadataPurgeGuidWithDashes = new Regex(
            @"^[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}$",
            RegexOptions.CultureInvariant);

        /// <summary>GUID внутри строки — для отсечения технического шума при экспорте/копировании «по правилам».</summary>
        private static readonly Regex JsonCopyEmbeddedGuidPattern = new Regex(
            @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}|\{[0-9a-fA-F]{32}\}|\b[0-9a-fA-F]{32}\b",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Последнее имя поля объекта до текущего значения (пропускает сегменты пути массива <c>[42]</c>).</summary>
        private static string LastNamedJsonPathSegment(List<string> currentPath)
        {
            if (currentPath == null || currentPath.Count == 0)
                return "";

            for (var i = currentPath.Count - 1; i >= 0; i--)
            {
                var s = currentPath[i]?.Trim();
                if (string.IsNullOrEmpty(s))
                    continue;

                if (s.Length >= 3 && s[0] == '[' && s[s.Length - 1] == ']')
                {
                    var inner = s.Substring(1, s.Length - 2).Trim();
                    if (int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        continue;
                    return inner;
                }

                return s;
            }

            return "";
        }

        /// <summary>Значение похоже на служебную строку сериализации Unity (GUID, Assets/..., расширения ресурсов и т.д.).</summary>
        private static bool LooksLikeTechnicalUnitySerializedString(string v)
        {
            if (string.IsNullOrWhiteSpace(v))
                return true;

            v = v.Trim();
            if (v.Length == 0)
                return true;

            if (v.StartsWith("Assets/", StringComparison.Ordinal) ||
                v.StartsWith("Packages/", StringComparison.Ordinal) ||
                v.StartsWith("Library/", StringComparison.Ordinal) ||
                v.StartsWith("ProjectSettings/", StringComparison.Ordinal) ||
                v.StartsWith("StreamingAssets/", StringComparison.Ordinal) ||
                v.StartsWith("Resources/", StringComparison.Ordinal))
                return true;

            // Локальный путь к ресурсам юнити-проекта
            if (v.Length >= 8 && v.IndexOf("Assets", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (v.IndexOf('\\') >= 0 || v.IndexOf('/') >= 0))
                return true;

            if (v.Length == 32 && Regex.IsMatch(v, @"^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant))
                return true;

            if (MetadataPurgeGuidWithDashes.IsMatch(v))
                return true;

            if (v.Contains(", Version=") && v.Contains(", Culture=") && v.Contains(", PublicKeyToken="))
                return true;

            var ext = Path.GetExtension(v);
            if (!string.IsNullOrEmpty(ext) && MetadataPurgeTechnicalFileExtensions.Contains(ext))
                return true;

            // Только #RRGGBB (часто цвета в UI)
            if (v.Length >= 4 && v.Length <= 9 && v[0] == '#')
            {
                var rest = v.Substring(1);
                if (Regex.IsMatch(rest, @"^[a-fA-F0-9]+$", RegexOptions.CultureInvariant))
                    return true;
            }

            return false;
        }

        /// <summary>Qualified type без пробелов из Unity/System — не текст для перевода.</summary>
        private static bool LooksLikeQualifiedUnityEngineTypeName(string v)
        {
            if (string.IsNullOrWhiteSpace(v))
                return false;

            v = v.Trim();
            if (!v.Contains('.') || v.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '/', '\\' }) >= 0)
                return false;

            if (!(v.StartsWith("Unity.", StringComparison.Ordinal) ||
                  v.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                  v.StartsWith("UnityEditor.", StringComparison.Ordinal) ||
                  v.StartsWith("TMPro.", StringComparison.Ordinal) ||
                  v.StartsWith("UnityUI.", StringComparison.Ordinal) ||
                  v.StartsWith("System.", StringComparison.Ordinal)))
                return false;

            foreach (char c in v)
            {
                if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '+' || c == '-' || c == '`' ||
                    c == '[' || c == ']')
                    continue;
                return false;
            }

            return true;
        }

        /// <summary>Учитывать ли строковый лист при решении удалить файл «только метаданные».</summary>
        private static bool CountStringLeafTowardGameplayForMetadataDeletion(List<string> path, string normalizedValue)
        {
            if (string.IsNullOrWhiteSpace(normalizedValue) || normalizedValue == "\"\"" || normalizedValue == "''")
                return false;

            var namedKey = LastNamedJsonPathSegment(path);
            if (string.Equals(namedKey, "m_Name", StringComparison.OrdinalIgnoreCase))
            {
                if (LooksLikeTechnicalUnityIdentifier(normalizedValue))
                    return false;
                return true;
            }

            if (!string.IsNullOrEmpty(namedKey) && MetadataPurgeGameplayStringKeys.Contains(namedKey))
                return true;

            // Поле/контейнер по своему имени — техническое (имена сцен, идентификаторы,
            // разметка TMP). Значение не считаем игровым независимо от содержимого.
            if (!string.IsNullOrEmpty(namedKey) && MetadataPurgeTechnicalPathSegments.Contains(namedKey))
                return false;

            if (LooksLikeTechnicalUnitySerializedString(normalizedValue))
                return false;

            if (LooksLikeQualifiedUnityEngineTypeName(normalizedValue))
                return false;

            // Внутренний идентификатор ассета/объекта (PascalCase/camelCase/GUID/путь) —
            // это не игровой текст, даже если поле не m_Name.
            if (LooksLikeTechnicalUnityIdentifier(normalizedValue))
                return false;

            // «Программная фраза» (enum-значение, CONSTANT_CASE, точечный путь, HEX-цвет,
            // вызов метода, булев литерал) — тоже не человеческий текст. Иначе такие строки
            // удерживали бы файл от удаления в режиме «только метаданные».
            if (LooksLikeProgramPhrase(normalizedValue))
                return false;

            return true;
        }

        /// <summary>
        /// «Программные фразы» после базовой очистки: enum, CONSTANT_CASE, точечные пути, HEX-цвета, вызовы/лямбды, булевы литералы.
        /// Консервативно: строки с пробелами (человеческий текст) проходят.
        /// </summary>
        private static bool LooksLikeProgramPhrase(string value)
        {
            var v = (value ?? string.Empty).Trim();
            if (v.Length == 0)
                return true;

            // Явные кодовые токены / вызовы методов / лямбды (даже с пробелами).
            if (v.IndexOf("=>", StringComparison.Ordinal) >= 0 ||
                v.IndexOf("::", StringComparison.Ordinal) >= 0 ||
                v.IndexOf("();", StringComparison.Ordinal) >= 0 ||
                Regex.IsMatch(v, @"^[A-Za-z_]\w*\s*\([^)]*\)\s*;?\s*$"))
                return true;

            var hasSpace = v.IndexOf(' ') >= 0;

            // Булевы / пустые литералы.
            if (!hasSpace && Regex.IsMatch(v, @"^(true|false|none|null|nan|nil)$", RegexOptions.IgnoreCase))
                return true;

            // HEX-цвет (#RRGGBB / RRGGBBAA или без #).
            if (Regex.IsMatch(v, @"^#?[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$"))
                return true;

            if (!hasSpace)
            {
                // CONSTANT_CASE (MY_FLAG_NAME).
                if (Regex.IsMatch(v, @"^[A-Z][A-Z0-9]*(_[A-Z0-9]+)+$"))
                    return true;

                // Точечный путь / Namespace.Type / a.b.c — без пробелов.
                if (Regex.IsMatch(v, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$"))
                    return true;

                // camelCase / внутренний переход регистра (GetComponent, isOpen, OnClickEvent)
                // — один «слитный» токен без человеческих пробелов.
                if (v.Length >= 3 &&
                    Regex.IsMatch(v, @"^[A-Za-z][A-Za-z0-9_]*$") &&
                    Regex.IsMatch(v, @"[a-z][A-Z]|[A-Z]{2,}[a-z]"))
                    return true;
            }

            return false;
        }

        public Form1()
        {
            SuspendLayout();
            try
            {
                ResetSessionFileLog();
                LoadSettings();
                InitializeLayout();
                SetupNavigation();
                SetupDragAndDrop();
                ApplyTheme();
                UpdateSidebarReadyLabel();
                Load += Form1_DeferredDashboardBoot;
            }
            finally
            {
                ResumeLayout(true);
            }

            Shown += Form1_OnFirstShownWelcome;
        }

        /// <summary>Первая загрузка «Главной» после создания handle окна (BeginInvoke в конструкторе недопустим).</summary>
        private void Form1_DeferredDashboardBoot(object sender, EventArgs e)
        {
            Load -= Form1_DeferredDashboardBoot;
            BeginInvoke(new Action(() =>
            {
                LoadDashboardModule();
                ApplyTheme();
                UpdateSidebarReadyLabel();
                Log(L("Open JSON Files or choose a folder via the File menu.", "Откройте JSON Files или выберите папку через меню «Файл»."));
            }));
        }

        private void Form1_OnFirstShownWelcome(object sender, EventArgs e)
        {
            Shown -= Form1_OnFirstShownWelcome;
            BeginInvoke(new Action(() =>
            {
                if (welcomeShown)
                    return;
                ShowWelcomeOverlay();
            }));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (PromptSaveIfDirtyOnClose(e))
                return; // закрытие отменено пользователем

            try
            {
                SyncBundleLocFieldsFromUi();
                SaveSettings();
                try { _assetsWorkCts?.Cancel(); } catch { }
                _assetsWorkCts?.Dispose();
                _assetsWorkCts = null;
            }
            catch { }

            DisposeCachedDashboardRoot();
            base.OnFormClosing(e);
        }

        /// <summary>Esc: отмена пакетного API-перевода или операции Unity .assets (если идёт).</summary>
        private bool TryRequestCancelViaEscape()
        {
            if (_apiBatchTranslateCts != null && !_apiBatchTranslateCts.IsCancellationRequested)
            {
                try { _apiBatchTranslateCts.Cancel(); } catch { }
                Log(L("Cancel requested: API batch translation.", "Запрошена отмена: пакетный перевод API."));
                return true;
            }

            if (_assetsWorkCts != null && !_assetsWorkCts.IsCancellationRequested)
            {
                try { _assetsWorkCts.Cancel(); } catch { }
                Log(L(
                    "Cancel requested: Unity .assets export/import (stops after the current object; wait a moment).",
                    "Запрошена отмена: экспорт/импорт Unity .assets (остановится после текущего объекта, подождите секунду)."));
                return true;
            }

            return false;
        }

        private CancellationToken BeginNewAssetsWorkCancellation()
        {
            try { _assetsWorkCts?.Cancel(); } catch { }
            _assetsWorkCts?.Dispose();
            _assetsWorkCts = new CancellationTokenSource();
            return _assetsWorkCts.Token;
        }

        private void EndAssetsWorkCancellation()
        {
            try { _assetsWorkCts?.Dispose(); } catch { }
            _assetsWorkCts = null;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F1)
            {
                ShowUserGuideDialog();
                return true;
            }

            if (welcomeOverlayDimPanel != null && welcomeOverlayDimPanel.Visible && keyData == Keys.Escape)
            {
                DismissWelcomeOverlayAndPersist();
                return true;
            }

            if (keyData == Keys.Escape && TryRequestCancelViaEscape())
                return true;

            if (dgv != null && !dgv.IsDisposed && IsJsonTranslatorSurfaceHosted)
            {
                if (dgv.IsCurrentCellInEditMode && keyData != (Keys.Control | Keys.F))
                    return base.ProcessCmdKey(ref msg, keyData);

                switch (keyData)
                {
                    case Keys.Control | Keys.F:
                        ShowJsonTableSearchDialog();
                        return true;
                    case Keys.Escape:
                        if (!dgv.IsCurrentCellInEditMode &&
                            ((jsonSearchPanel != null && !jsonSearchPanel.IsDisposed && jsonSearchPanel.Visible) ||
                             !string.IsNullOrEmpty(currentSearchText)))
                        {
                            HideJsonTableSearchBar(true);
                            return true;
                        }
                        break;
                    case Keys.Control | Keys.S:
                        BtnApply_Click(this, EventArgs.Empty);
                        return true;
                    case Keys.Control | Keys.E:
                        BtnExportTxt_Click(this, EventArgs.Empty);
                        return true;
                    case Keys.Control | Keys.I:
                        BtnImportTxt_Click(this, EventArgs.Empty);
                        return true;
                    case Keys.Control | Keys.O:
                        BtnSelectFolder_Click(this, EventArgs.Empty);
                        return true;
                    case Keys.Control | Keys.Shift | Keys.L:
                        BtnClearLog_Click(this, EventArgs.Empty);
                        return true;
                    case Keys.Control | Keys.Shift | Keys.C:
                        BtnCopySelectedAi_Click(this, EventArgs.Empty);
                        return true;
                    case Keys.Control | Keys.Shift | Keys.V:
                        BtnPasteAi_Click(this, EventArgs.Empty);
                        return true;
                    case Keys.Control | Keys.Z:
                        if (!dgv.IsCurrentCellInEditMode &&
                            IsTranslationUndoHotkeyContext(ActiveControl, dgv) &&
                            TryUndoLastTranslationEdit())
                            return true;
                        break;
                    case Keys.F3:
                        FindNextTableSearchMatch();
                        return true;
                    case Keys.F5:
                        HotkeyRefreshExtract();
                        return true;
                    case Keys.Control | Keys.Shift | Keys.T:
                        MenuTranslateEmptyViaLocalApi_Click(this, EventArgs.Empty);
                        return true;
                    case Keys.Control | Keys.Oem6:
                        NavigateToRelativeUntranslated(1);
                        return true;
                    case Keys.Control | Keys.Oem4:
                        NavigateToRelativeUntranslated(-1);
                        return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void HotkeyRefreshExtract()
        {
            BeginInvoke(new Action(async () =>
            {
                if (!RequireJsonTranslatorSurface("обновление таблицы"))
                    return;
                await ExtractTextsAsync();
            }));
        }

        private void FindNextTableSearchMatch()
        {
            if (!RequireJsonTranslatorSurface("поиск"))
                return;
            if (string.IsNullOrWhiteSpace(currentSearchText))
            {
                Log(L("Set search text: Ctrl+F.", "Задайте текст поиска: Ctrl+F."), true);
                return;
            }

            var q = currentSearchText.Trim();
            var rows = dgv.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && r.Visible).ToList();
            if (rows.Count == 0)
                return;

            int cur = dgv.CurrentCell?.RowIndex ?? -1;
            int startPos = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Index != cur)
                    continue;
                startPos = i + 1;
                break;
            }

            for (int k = 0; k < rows.Count; k++)
            {
                var row = rows[(startPos + k) % rows.Count];
                if (!RowContains(row, q))
                    continue;

                dgv.ClearSelection();
                row.Selected = true;
                int col = dgv.CurrentCell != null ? dgv.CurrentCell.ColumnIndex : 0;
                col = Math.Max(0, Math.Min(col, dgv.Columns.Count - 1));
                dgv.CurrentCell = row.Cells[col];
                try { dgv.FirstDisplayedScrollingRowIndex = row.Index; }
                catch { }

                return;
            }

            Log(L("No matches for the current filter.", "Совпадений по текущему фильтру не найдено."), true);
        }

        private void NavigateToRelativeUntranslated(int direction)
        {
            if (!RequireJsonTranslatorSurface("навигация по строкам"))
                return;
            if (direction == 0)
                return;

            var visible = dgv.Rows.Cast<DataGridViewRow>()
                .Where(r => !r.IsNewRow && r.Visible)
                .OrderBy(r => r.Index)
                .ToList();
            if (visible.Count == 0)
                return;

            int curIdx = dgv.CurrentCell?.RowIndex ?? -1;
            int curPos = -1;
            for (int i = 0; i < visible.Count; i++)
            {
                if (visible[i].Index == curIdx)
                {
                    curPos = i;
                    break;
                }
            }

            if (curPos < 0)
                curPos = direction > 0 ? -1 : visible.Count;

            int step = direction > 0 ? 1 : -1;
            for (int n = 0; n < visible.Count; n++)
            {
                curPos += step;
                if (curPos >= visible.Count)
                    curPos = 0;
                if (curPos < 0)
                    curPos = visible.Count - 1;

                var row = visible[curPos];
                var it = RowItem(row);
                if (it == null)
                    continue;
                if (string.IsNullOrWhiteSpace(it.Original))
                    continue;
                if (!string.IsNullOrWhiteSpace(it.Translated))
                    continue;

                dgv.ClearSelection();
                row.Selected = true;
                int col = dgv.Columns.Contains("Translated") ? dgv.Columns["Translated"].Index : 0;
                dgv.CurrentCell = row.Cells[col];
                try { dgv.FirstDisplayedScrollingRowIndex = row.Index; }
                catch { }

                return;
            }

            Log(L("No untranslated rows among visible ones (empty «Translation»).", "Среди видимых строк нет непереведённых (пустой «Перевод»)."), true);
        }

        private void StartApiBatchTranslationHeartbeat(int totalRows)
        {
            StopApiBatchTranslationHeartbeat();
            _apiBatchHeartbeatTotal = Math.Max(0, totalRows);
            _apiBatchHeartbeatStep = 0;
            _apiBatchHeartbeatActive = true;
            _apiBatchHeartbeatLastSeenStep = -1;
            _apiBatchHeartbeatSameStepPulseCount = 0;
            _apiBatchHeartbeatTimer = new System.Windows.Forms.Timer { Interval = 8000 };
            _apiBatchHeartbeatTimer.Tick += ApiBatchTranslationHeartbeatTimer_Tick;
            _apiBatchHeartbeatTimer.Start();
        }

        private void StopApiBatchTranslationHeartbeat()
        {
            _apiBatchHeartbeatActive = false;
            if (_apiBatchHeartbeatTimer != null)
            {
                _apiBatchHeartbeatTimer.Stop();
                _apiBatchHeartbeatTimer.Tick -= ApiBatchTranslationHeartbeatTimer_Tick;
                _apiBatchHeartbeatTimer.Dispose();
                _apiBatchHeartbeatTimer = null;
            }
        }

        private void ApiBatchTranslationHeartbeatTimer_Tick(object sender, EventArgs e)
        {
            if (!_apiBatchHeartbeatActive || IsDisposed)
                return;

            var step = _apiBatchHeartbeatStep;
            var tot = _apiBatchHeartbeatTotal;
            if (step == _apiBatchHeartbeatLastSeenStep)
                _apiBatchHeartbeatSameStepPulseCount++;
            else
            {
                _apiBatchHeartbeatLastSeenStep = step;
                _apiBatchHeartbeatSameStepPulseCount = 0;
            }

            var stuckRu = _apiBatchHeartbeatSameStepPulseCount >= 2
                ? " Несколько проверок подряд на одном шаге — один HTTP-запрос обрывается ~через 90 с, затем будет ошибка или повтор."
                : "";
            var stuckEn = _apiBatchHeartbeatSameStepPulseCount >= 2
                ? " Same step for several pulses — each HTTP request aborts after ~90s, then you should see an error or retry."
                : "";

            Log(L(
                $"API: still running… batch step ~{step}/{tot}. Waiting on HTTP or rate limit — normal.{stuckEn}",
                $"API: всё ещё работает… шаг пакета ~{step}/{tot}. Ожидание HTTP или лимита — норма.{stuckRu}"));
        }

        private void BtnCancelApiBatchTranslate_Click(object sender, EventArgs e)
        {
            try
            {
                _apiBatchTranslateCts?.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        private async void MenuTranslateEmptyViaLocalApi_Click(object sender, EventArgs e)
        {
            await TranslateEmptyRowsViaLocalApiAsync();
        }

        private async Task TranslateEmptyRowsViaLocalApiAsync()
        {
            if (!RequireJsonTranslatorSurface(L("translation API", "API перевода")))
                return;

            if (!translationApiEnabled || string.IsNullOrWhiteSpace(translationApiUrl))
            {
                MessageBox.Show(this,
                    L(
                        "Enable «Translation API» and enter the server URL in Settings.\r\n\r\nLibreTranslate: POST …/translate.\r\nAI chat providers (OpenRouter, OpenAI, Groq, Together AI, Mistral, DeepSeek, Google Gemini, Qwen DashScope, Ollama, custom …/v1): set provider — URL fills automatically; API key where required + chat model.",
                        "Включите «API перевода» и укажите URL в «Настройках».\r\n\r\nLibreTranslate: POST …/translate.\r\nЧат-провайдеры (OpenRouter, OpenAI, Groq, Together AI, Mistral, DeepSeek, Google Gemini, Qwen (DashScope), Ollama, свой …/v1): выберите провайдера — URL подставится; ключ API где нужен и модель чата."),
                    L("Translation API", "API перевода"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var backend = LocalTranslateApi.ParseTranslationAiBackend(translationAiBackend);
            if (LocalTranslateApi.BackendRequiresBearerKey(backend) && string.IsNullOrWhiteSpace(translationApiKey))
            {
                MessageBox.Show(this,
                    L(
                        "This provider requires an API key (Authorization: Bearer …). Paste it in Settings.",
                        "Для этого провайдера нужен API key (Bearer в заголовке Authorization). Укажите его в «Настройках»."),
                    L("Translation API", "API перевода"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var tgt = LocalTranslateApi.ExtractLangCode(targetLanguageDisplay);
            if (string.IsNullOrWhiteSpace(tgt) || tgt.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    L(
                        "Set target language in Settings (e.g. «Russian (ru)»).",
                        "Укажите целевой язык в настройках (формат «Russian (ru)»)."),
                    L("Translation API", "API перевода"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var src = LocalTranslateApi.ExtractLangCode(sourceLanguageDisplay);

            var indices = new List<int>();
            for (int i = 0; i < translationItems.Count; i++)
            {
                var it = translationItems[i];
                if (string.IsNullOrWhiteSpace(it.Original))
                    continue;
                if (!string.IsNullOrWhiteSpace(it.Translated))
                    continue;
                indices.Add(i);
            }

            if (indices.Count == 0)
            {
                Log(L("No rows with original text and empty translation.", "Нет строк с заполненным оригиналом и пустым переводом."));
                return;
            }

            var apiNoteEn = LocalTranslateApi.BackendUsesChatCompletions(backend)
                ? $"\r\nChat provider: {translationAiBackend}. Model from Settings."
                : (string.IsNullOrWhiteSpace(translationApiKey)
                    ? ""
                    : "\r\nLibreTranslate: key sent as «api_key» in JSON.");
            var apiNoteRu = LocalTranslateApi.BackendUsesChatCompletions(backend)
                ? $"\r\nЧат-провайдер: {translationAiBackend}. Модель из настроек."
                : (string.IsNullOrWhiteSpace(translationApiKey)
                    ? ""
                    : "\r\nLibreTranslate: ключ уходит в JSON как «api_key».");

            var confirm = MessageBox.Show(this,
                L(
                    $"Fill empty translations via API?\r\nRows: {indices.Count}\r\nServer: {translationApiUrl.Trim()}" +
                        apiNoteEn,
                    $"Заполнить пустые переводы через API?\r\nСтрок: {indices.Count}\r\nСервер: {translationApiUrl.Trim()}" +
                        apiNoteRu),
                L("Translation API", "API перевода"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
                return;

            var openRouterFreeNoteEn = backend == TranslationAiBackend.OpenRouter
                ? " OpenRouter «:free» (~8 req/min) can make large batches slow."
                : "";
            var openRouterFreeNoteRu = backend == TranslationAiBackend.OpenRouter
                ? " OpenRouter «:free» (~8 запр./мин) — долго на больших таблицах."
                : "";
            Log(L(
                $"API: batch {indices.Count} rows — duplicate originals share one model call; translation memory first when enabled.{openRouterFreeNoteEn}",
                $"API: пакет {indices.Count} строк — одинаковый оригинал даёт один вызов модели; при включённой TM сначала память.{openRouterFreeNoteRu}"));
            StartApiBatchTranslationHeartbeat(indices.Count);

            _apiBatchTranslateCts?.Dispose();
            _apiBatchTranslateCts = new CancellationTokenSource();
            var translateCt = _apiBatchTranslateCts.Token;

            void SetApiBatchCancelVisible(bool visible)
            {
                if (IsDisposed)
                    return;

                void Apply()
                {
                    if (IsDisposed || btnCancelApiBatchTranslate == null || btnCancelApiBatchTranslate.IsDisposed)
                        return;
                    btnCancelApiBatchTranslate.Visible = visible;
                    btnCancelApiBatchTranslate.Enabled = visible;
                    if (statusStrip != null && !statusStrip.IsDisposed)
                    {
                        statusStrip.PerformLayout();
                        statusStrip.Refresh();
                    }
                }

                if (InvokeRequired)
                    BeginInvoke(new Action(Apply));
                else
                    Apply();
            }

            SetApiBatchCancelVisible(true);

            var undo = new List<TranslationUndoCell>();
            var ok = 0;
            var skipTechnical = 0;
            var fail = 0;
            var fillFromTm = 0;
            var fillFromBatchDedupe = 0;
            var modelOk = 0;
            var apiStep = 0;
            Dictionary<string, string> memLookup = null;
            if (useTranslationMemory)
                memLookup = TranslationMemory.Load();
            var batchOriginalDedupe = new Dictionary<string, string>(StringComparer.Ordinal);

            // пакет работает со ССЫЛКАМИ на элементы, не индексами строк: при смене сортировки во время перевода
            // строку находим заново через row.Tag (RowIndexOfItem), undo пишет ссылку → перевод/отмена в правильную строку
            var workItems = new List<TranslationItem>(indices.Count);
            foreach (var i in indices)
                if (i >= 0 && i < translationItems.Count)
                    workItems.Add(translationItems[i]);

            // Текущая строка ГРИДА элемента (по Tag). Для логов «строка N» и записи в ячейку.
            int CurrentRow(TranslationItem item) => RowIndexOfItem(item);

            void WriteTranslatedCell(TranslationItem item, string value)
            {
                item.Translated = value;
                int cur = CurrentRow(item);
                if (cur >= 0)
                    dgv.Rows[cur].Cells["Translated"].Value = value;
            }

            // Вызывать ДО WriteTranslatedCell — фиксирует прежнее значение для Undo.
            void AddUndoFor(TranslationItem item)
            {
                undo.Add(new TranslationUndoCell { Item = item, PreviousTranslated = item.Translated ?? "" });
            }

            try
            {
                foreach (var rowItem in workItems)
                {
                    apiStep++;
                    _apiBatchHeartbeatStep = apiStep;

                    try
                    {
                        if (backend == TranslationAiBackend.GeminiOpenAi && apiStep > 1)
                            await Task.Delay(1200, translateCt).ConfigureAwait(true);

                        if (ShouldLeaveOriginalUntranslatedForLocalAi(rowItem))
                        {
                            var keep = rowItem.Original ?? "";
                            AddUndoFor(rowItem);
                            WriteTranslatedCell(rowItem, keep);
                            skipTechnical++;
                            continue;
                        }

                        var text = rowItem.Original ?? "";

                        if (memLookup != null && memLookup.TryGetValue(text, out var memHit) &&
                            !string.IsNullOrWhiteSpace(memHit) &&
                            !TranslationMemory.IsLikelyShiftCorruptedPair(text, memHit))
                        {
                            var mtr = memHit.Trim();
                            AddUndoFor(rowItem);
                            WriteTranslatedCell(rowItem, mtr);
                            batchOriginalDedupe[text] = mtr;
                            ok++;
                            fillFromTm++;
                            continue;
                        }

                        if (batchOriginalDedupe.TryGetValue(text, out var dupTr) && !string.IsNullOrWhiteSpace(dupTr))
                        {
                            var d = dupTr.Trim();
                            AddUndoFor(rowItem);
                            WriteTranslatedCell(rowItem, d);
                            ok++;
                            fillFromBatchDedupe++;
                            continue;
                        }

                        var tr = await LocalTranslateApi.TranslateAutoAsync(
                            backend,
                            translationApiUrl,
                            translationApiKey,
                            translationOpenRouterModel ?? "",
                            text,
                            src,
                            tgt,
                            secs =>
                            {
                                if (IsDisposed)
                                    return;
                                try
                                {
                                    BeginInvoke(new Action(() =>
                                    {
                                        if (!IsDisposed)
                                            Log(L(
                                                $"Translator pauses ~{secs}s (OpenRouter :free rate limit)…",
                                                $"Пауза ~{secs} с (лимит OpenRouter для :free)…"));
                                    }));
                                }
                                catch { }
                            },
                            translateCt).ConfigureAwait(true);

                        tr = (tr ?? "").Trim();
                        if (string.IsNullOrEmpty(tr))
                        {
                            fail++;
                            Log(L(
                                $"API [row {CurrentRow(rowItem) + 1}]: empty model reply (skipped).",
                                $"API [строка {CurrentRow(rowItem) + 1}]: пустой ответ модели (пропуск)."),
                                true);
                            continue;
                        }

                        batchOriginalDedupe[text] = tr;

                        AddUndoFor(rowItem);
                        WriteTranslatedCell(rowItem, tr);
                        ok++;
                        modelOk++;
                    }
                    catch (OperationCanceledException)
                    {
                        Log(L(
                            "API translation cancelled (partial results kept; Undo restores cells touched in this run).",
                            "Перевод через API отменён (частичные результаты сохранены; отмена правок — через Undo по строкам этого запуска)."));
                        break;
                    }
                    catch (Exception ex)
                    {
                        fail++;
                        var detail = ex.Message;
                        if (ex.InnerException != null && !string.IsNullOrWhiteSpace(ex.InnerException.Message))
                            detail += " (" + ex.InnerException.Message + ")";
                        Log(L($"API [row {CurrentRow(rowItem) + 1}]: {detail}", $"API [строка {CurrentRow(rowItem) + 1}]: {detail}"), true);
                    }
                }

                if (undo.Count > 0)
                    PushTranslationUndoFrame(undo);

                ApplyTableSearch();
                UpdateRowHighlights();
                UpdateProgressStats();
                UpdateStatus();
                UpdateSidebarReadyLabel();
                Log(L(
                    $"Translation API: filled {ok} rows (API calls: {modelOk}, translation memory: {fillFromTm}, duplicate originals: {fillFromBatchDedupe}); kept original (technical) {skipTechnical}; errors {fail}.",
                    $"API перевода: заполнено строк {ok} (запросов к модели: {modelOk}, память переводов: {fillFromTm}, повторы оригинала: {fillFromBatchDedupe}); оставлен оригинал (технич.) {skipTechnical}; ошибок {fail}."));
                if (ok > 0 || skipTechnical > 0)
                    BumpDashboardContentStamp();
            }
            finally
            {
                SetApiBatchCancelVisible(false);
                try
                {
                    _apiBatchTranslateCts?.Dispose();
                }
                catch { }

                _apiBatchTranslateCts = null;
                StopApiBatchTranslationHeartbeat();
                RestoreUiCursorAfterWait();
            }
        }

        // Показывает встроенную строку поиска над таблицей (Ctrl+F) и ставит в неё фокус.
        private void ShowJsonTableSearchDialog()
        {
            if (!RequireJsonTranslatorSurface("поиск по таблице"))
                return;
            if (jsonSearchPanel == null || jsonSearchPanel.IsDisposed ||
                jsonSearchBox == null || jsonSearchBox.IsDisposed)
                return;

            jsonSearchPanel.Visible = true;
            jsonSearchBox.Text = currentSearchText;
            jsonSearchBox.Focus();
            jsonSearchBox.SelectAll();
        }

        // Прячет строку поиска; при clearFilter сбрасывает фильтр таблицы.
        private void HideJsonTableSearchBar(bool clearFilter)
        {
            if (clearFilter && !string.IsNullOrEmpty(currentSearchText))
            {
                currentSearchText = "";
                ApplyTableSearch();
                UpdateProgressStats();
                UpdateStatus();
            }
            if (jsonSearchPanel != null && !jsonSearchPanel.IsDisposed)
                jsonSearchPanel.Visible = false;
            if (dgv != null && !dgv.IsDisposed)
                dgv.Focus();
        }

        private void SetupNavigation()
        {
            navButtons.Clear();
            navButtonsContainer.Controls.Clear();
            activeNavButton = null;

            AddNavButton("Dashboard", () => LoadDashboardModule());
            AddNavButton("Page", () => LoadJsonTranslatorModule());
            AddNavButton("Toolbox", () => LoadAssetsModule());
            AddNavButton("Fonts", () => LoadFontToolsModule());
            AddNavButton("Textures", () => LoadTextureToolsModule());
            AddNavButton("Bundles", () => LoadBundleLocalizationModule());
            AddNavButton("Settings", () => LoadSettingsModule());
        }

        private void LoadJsonTranslatorModule()
        {
            ShowChromeHeader();
            ClearAssetsModuleRefs();
            ClearBundleLocModuleRefs();
            DetachModuleEvents();
            ClearContentPanel();

            var reuseExistingJsonUi =
                jsonWorkspaceCard != null && !jsonWorkspaceCard.IsDisposed &&
                dgv != null && !dgv.IsDisposed;

            if (!reuseExistingJsonUi)
            {
                DisposeJsonTranslatorWorkspaceIfAny();
                BuildJsonTranslatorUI();
            }
            else
            {
                moduleHostPanel.Controls.Add(jsonWorkspaceCard);
                jsonWorkspaceCard.Dock = DockStyle.Fill;
            }

            AttachModuleEvents();

            BeginInvoke(new Action(() => TryAutoExtractAfterJsonModuleLoad()));

            if (headerPanel != null && !headerPanel.IsDisposed)
                headerPanel.Height = 6;
            if (headerLabel != null && !headerLabel.IsDisposed)
            {
                headerLabel.Text = "";
                headerLabel.Visible = false;
            }
            ApplyTheme();
            Log(L("JSON Files section.", "Раздел JSON Files."));
            UpdateStatus();
        }

        private static string ResolveOsUiLanguage()
        {
            try
            {
                var n = CultureInfo.CurrentUICulture?.TwoLetterISOLanguageName?.ToLowerInvariant() ?? "en";
                if (n == "ru")
                    return "ru";
                // ОС на одном из доп. языков интерфейса → стартуем на нём (перевод подтянется из ui-languages.json).
                if (UiLocalization.IsExtraLanguage(n))
                    return n;
                return "en";
            }
            catch
            {
                return "en";
            }
        }

        /// <summary>Языки интерфейса для пикера: en/ru (зашиты в <see cref="L"/>) + доп. языки из ui-languages.json.</summary>
        internal static IReadOnlyList<(string Code, string Display)> InterfaceLanguageChoices()
        {
            var list = new List<(string Code, string Display)>
            {
                ("en", "English"),
                ("ru", "Русский"),
            };
            list.AddRange(UiLocalization.ExtraLanguages);
            return list;
        }

        /// <summary>Приводит код языка интерфейса к поддерживаемому (en/ru/доп.); неизвестный → en (фолбэк-English).</summary>
        private static string NormalizeUiLanguageCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "en";
            var c = raw.Trim().ToLowerInvariant();
            if (c.StartsWith("ru"))
                return "ru";
            if (UiLocalization.IsExtraLanguage(c))
                return c;
            if (c.Length >= 2 && UiLocalization.IsExtraLanguage(c.Substring(0, 2))) // «pt-br» → «pt»
                return c.Substring(0, 2);
            return "en";
        }

        /// <summary>Держит редактируемый combo языков валидным: на Leave фиксирует ТОЧНОЕ совпадение (обновит <paramref name="stored"/>+настройки), недопечатанный ввод откатывает.</summary>
        private static void SnapLanguageComboToValidItem(ComboBox combo, ref string stored)
        {
            if (combo == null || combo.IsDisposed)
                return;
            int i = combo.FindStringExact(combo.Text);
            if (i >= 0)
            {
                if (combo.SelectedIndex != i)
                    combo.SelectedIndex = i; // вызовет SelectedIndexChanged → обновит stored + SaveSettings
                else
                {
                    var s = combo.SelectedItem?.ToString();
                    if (!string.IsNullOrEmpty(s))
                        stored = s;
                }
            }
            else
            {
                combo.Text = stored ?? ""; // вернуть последнее валидное значение
            }
            // Снять синюю подсветку текста (редактируемый combo сам выделяет текст) в покое.
            combo.SelectionStart = 0;
            combo.SelectionLength = 0;
        }

        private bool UiIsRussian =>
            string.Equals(appUiLanguage, "ru", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Подпись по текущему языку: en/ru мгновенно из аргументов; доп. языки — из ui-languages.json по англ. ключу с откатом на English.
        /// Поэтому новые <c>L(...)</c> НЕ требуют правок файла переводов (непереведённое — по-английски).
        /// </summary>
        private string L(string english, string russian)
        {
            if (UiIsRussian)
                return russian;
            var code = appUiLanguage;
            if (string.IsNullOrEmpty(code) || string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
                return english;
            return UiLocalization.Translate(code, english) ?? english;
        }

        /// <summary>Подписи главного меню (верхняя панель): ключ из <see cref="ToolStripItem.Tag"/>.</summary>
        private string MainMenuText(string key)
        {
            switch (key ?? "")
            {
                case "m_file":
                    return L("File", "Файл");
                case "m_edit":
                    return L("Edit", "Правка");
                case "m_view":
                    return L("View", "Вид");
                case "m_help":
                    return L("Help", "Справка");
                case "file_choose_folder":
                    return L("Choose folder…", "Выбрать папку…");
                case "file_refresh":
                    return L("Refresh", "Обновить");
                case "file_save_json":
                    return L("Save changes to JSON", "Сохранить изменения в JSON");
                case "file_autosave":
                    return L("Autosave (every 2 min)", "Автосохранение (каждые 2 мин)");
                case "file_export_assets_json":
                    return L("Export from .assets to JSON (game folder)", "Экспорт из .assets в JSON (папка игры)");
                case "file_import_assets":
                    return L("Rebuild into .assets", "Собрать обратно в .assets");
                case "file_export_txt":
                    return L("Export TXT", "Экспорт TXT");
                case "file_import_txt":
                    return L("Import TXT", "Импорт TXT");
                case "file_tools":
                    return L("Asset bundles ↔ JSON…", "Asset Bundle ↔ JSON…");
                case "file_exit":
                    return L("Exit", "Выход");
                case "edit_copy_ai":
                    return L("Copy for AI", "Скопировать для ИИ");
                case "edit_paste_buffer":
                    return L("Paste from clipboard", "Вставить из буфера");
                case "edit_search_table":
                    return L("Find in table…", "Поиск в таблице…");
                case "edit_find_next":
                    return L("Find next match", "Следующее совпадение поиска");
                case "edit_refresh_table":
                    return L("Reload table from JSON", "Обновить таблицу из JSON");
                case "edit_next_untranslated":
                    return L("Next row with empty translation", "К следующей непереведённой строке");
                case "edit_prev_untranslated":
                    return L("Previous row with empty translation", "К предыдущей непереведённой строке");
                case "edit_translate_api":
                    return L("AI translation", "Перевод с ИИ");
                case "edit_apply_tm":
                    return L("Apply translation memory (TM)", "Применить память переводов (TM)");
                case "edit_resync_patch":
                    return L("Re-sync after game patch…", "Ре-синк после патча игры…");
                case "edit_qa_check":
                    return L("QA check (validate strings)…", "QA-проверка строк…");
                case "edit_delete_meta_json":
                    return L("Delete JSON metadata-only rows…", "Удалить JSON только метаданные…");
                case "edit_clear_working_folder":
                    return L("Clear working folder (all files)…", "Очистить рабочую папку (все файлы)…");
                case "edit_clear_log":
                    return L("Clear log", "Очистить лог");
                case "view_light_themes":
                    return L("Light themes", "Светлые темы");
                case "view_dark_themes":
                    return L("Dark themes", "Тёмные темы");
                case "help_guide":
                    return L("User guide…", "Помощь по приложению…");
                case "help_about":
                    return L("About", "О программе");
                default:
                    return key ?? "";
            }
        }

        private void RefreshMainMenuLanguage()
        {
            if (mainMenuStrip == null || mainMenuStrip.IsDisposed)
                return;
            ApplyMainMenuTextRecursive(mainMenuStrip.Items);
        }

        private void ApplyMainMenuTextRecursive(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripMenuItem mi && mi.Tag is string key && !string.IsNullOrEmpty(key))
                    mi.Text = MainMenuText(key);
                if (item is ToolStripMenuItem mix)
                    ApplyMainMenuTextRecursive(mix.DropDownItems);
            }
        }

        private string NavCaption(string iconKey)
        {
            switch (iconKey)
            {
                case "Dashboard":
                    return L("Home", "Главная");
                case "Page":
                    return L("JSON Files", "JSON-файлы");
                case "Bundles":
                    return L("Bundles", "Бандлы");
                case "Toolbox":
                    return L("Unity .assets", "Unity .assets");
                case "Fonts":
                    return L("Fonts", "Шрифты");
                case "Settings":
                    return L("Settings", "Настройки");
                default:
                    return iconKey ?? "";
            }
        }

        private void LoadSettingsModule()
        {
            ShowChromeHeader();
            ClearAssetsModuleRefs();
            ClearBundleLocModuleRefs();
            DetachModuleEvents();
            ClearContentPanel();

            ApplyTheme();

            if (headerPanel != null && !headerPanel.IsDisposed)
                headerPanel.Height = 6;
            if (headerLabel != null && !headerLabel.IsDisposed)
            {
                headerLabel.Text = "";
                headerLabel.Visible = false;
            }

            Color CardBgColor() =>
                currentThemeName == "Translator Purple"
                    ? Color.FromArgb(30, 28, 40)
                    : isDarkTheme
                        ? Color.FromArgb(30, 41, 59)
                        : Color.White;

            Color TitleFgColor() => _themeHeaderText;
            Color BodyFgColor() => _themeGridRowFore;
            Color MutedFgColor() => _themeSubtitleText;
            // Приглушённый заголовок — чистый яркий белый на тёмном режет глаза.
            Color SoftHeadFg() => ThemeMix(_themeHeaderText, _themePageBg, 0.18);

            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(12, 8, 12, 20),
                BackColor = _themePageBg
            };
            var innerWrap = new Panel { Dock = DockStyle.Top, BackColor = Color.Transparent };

            const int cardX = 16;
            const int cardGap = 14;
            var appearanceCard = new Panel { BackColor = CardBgColor(), Width = 900 };
            var langsCard = new Panel { BackColor = CardBgColor(), Width = 900 };
            var apiCard = new Panel { BackColor = CardBgColor(), Width = 900 };
            var tmCard = new Panel { BackColor = CardBgColor(), Width = 900 };

            innerWrap.Controls.AddRange(new Control[] { appearanceCard, langsCard, apiCard, tmCard });

            // Карточки растягиваются на всю ширину области (с небольшими полями по краям).
            void SyncCardWidths()
            {
                int w = Math.Max(460, scroll.ClientSize.Width - cardX * 2);
                appearanceCard.Width = langsCard.Width = apiCard.Width = tmCard.Width = w;
            }

            // Вертикальная укладка карточек: каждая занимает ровно свою высоту, без пустот.
            void RestackCards()
            {
                int y = 14;
                appearanceCard.Location = new Point(cardX, y); y = appearanceCard.Bottom + cardGap;
                langsCard.Location = new Point(cardX, y); y = langsCard.Bottom + cardGap;
                apiCard.Location = new Point(cardX, y); y = apiCard.Bottom + cardGap;
                tmCard.Location = new Point(cardX, y); y = tmCard.Bottom + cardGap;
                innerWrap.Height = y + 8;
            }

            // Скругление + рамка + левая акцентная полоса для каждой карточки.
            void StyleSettingsCard(Panel card)
            {
                ApplyDashboardRoundedClip(card, 12);
                card.Paint += (_, e) =>
                {
                    var r = card.ClientRectangle;
                    if (r.Width <= 2 || r.Height <= 2)
                        return;
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var path = CreateRoundedRectPath(new Rectangle(0, 0, r.Width - 1, r.Height - 1), 12))
                    using (var pen = new Pen(_themeGridColor, 1f))
                        e.Graphics.DrawPath(pen, path);
                    // Приглушённая акцентная полоса (не глянцевая), чтобы не резала глаза.
                    using (var br = new SolidBrush(ThemeMix(DashboardAccentPrimary(), CardBgColor(), 0.45)))
                        e.Graphics.FillRectangle(br, 0, 14, 3, Math.Max(0, r.Height - 28));
                };
            }
            StyleSettingsCard(appearanceCard);
            StyleSettingsCard(langsCard);
            StyleSettingsCard(apiCard);
            StyleSettingsCard(tmCard);

            scroll.Resize += (_, __) => { SyncCardWidths(); RestackCards(); };

            var titleLabel = new Label
            {
                Text = L("Appearance", "Внешний вид"),
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = TitleFgColor(),
                AutoSize = true,
                Location = new Point(4, 4)
            };

            var uiTitleLabel = new Label
            {
                Text = L("User interface", "Интерфейс"),
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = TitleFgColor(),
                AutoSize = true,
                Location = new Point(4, 44)
            };

            const int appearanceComboLeft = 278;

            var themeLabel = new Label
            {
                Text = L("Theme:", "Тема:"),
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = BodyFgColor(),
                AutoSize = true,
                Location = new Point(4, 78)
            };

            var themeCombo = new NoWheelComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10f),
                Width = 260,
                Location = new Point(appearanceComboLeft, 74)
            };
            themeCombo.Items.Add("Translator Purple");
            themeCombo.Items.Add("GitHub Light");
            themeCombo.Items.Add("GitHub Dark");
            themeCombo.Items.Add("Visual Studio Dark");
            themeCombo.Items.Add("Dracula");
            themeCombo.Items.Add("Nord");
            themeCombo.Items.Add("Solarized Light");
            themeCombo.SelectedIndex = themeCombo.Items.IndexOf(currentThemeName);
            if (themeCombo.SelectedIndex < 0)
                themeCombo.SelectedIndex = 0;

            var lblUiLang = new Label
            {
                Text = L("Interface language:", "Язык интерфейса:"),
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = BodyFgColor(),
                AutoSize = true,
                Location = new Point(4, 112)
            };

            var uiLangCombo = new NoWheelComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10f),
                Width = 260,
                Location = new Point(appearanceComboLeft, 108)
            };
            var uiChoices = InterfaceLanguageChoices();
            foreach (var choice in uiChoices)
                uiLangCombo.Items.Add(choice.Display);
            // Выбор по КОДУ языка (порядок пунктов = порядок InterfaceLanguageChoices()).
            int uiSel = 0;
            for (int i = 0; i < uiChoices.Count; i++)
                if (string.Equals(uiChoices[i].Code, appUiLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    uiSel = i;
                    break;
                }
            uiLangCombo.SelectedIndex = uiSel;

            var lblJsonCopyMode = new Label
            {
                Text = L("JSON copy method:", "Способ копирования JSON:"),
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = BodyFgColor(),
                AutoSize = true,
                Location = new Point(4, 146)
            };

            var cbJsonCopyMode = new NoWheelComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10f),
                Width = 260,
                Location = new Point(appearanceComboLeft, 142)
            };
            cbJsonCopyMode.Items.AddRange(new object[]
            {
                L("Without technical rows (by rules)", "По правилам (без служебных строк)"),
                L("Copy all rows (no filter)", "Копировать всё (без фильтра)")
            });
            cbJsonCopyMode.SelectedIndex = NormalizeJsonCopyModeIndex(jsonCopyModeSelectedIndex);
            settingsJsonCopyModeCombo = cbJsonCopyMode;

            var chkJunkFeatures = new CheckBox
            {
                Text = " " + L("Enable junk features", "Включить мусорные функции"),
                Location = new Point(4, 180),
                AutoSize = true,
                Checked = junkFeaturesEnabled,
                Font = new Font("Segoe UI", 10f),
                ForeColor = BodyFgColor()
            };

            var lblTxtFmt = new Label
            {
                Text = L("Default TXT table format (export/import dialogs)", "Формат таблицы TXT по умолчанию (диалог экспорт/импорт)"),
                Location = new Point(4, 220),
                AutoSize = true,
                ForeColor = TitleFgColor(),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Visible = junkFeaturesEnabled
            };
            var cbTxtFmt = new NoWheelComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 420,
                Location = new Point(4, 246),
                Font = new Font("Segoe UI", 10f),
                Visible = junkFeaturesEnabled
            };
            cbTxtFmt.Items.AddRange(new object[]
            {
                L("Columns with « | »", "Столбцы через « | »"),
                L("TSV (tab-separated)", "TSV (табуляция)"),
                L("CSV (comma, Excel)", "CSV (запятая, Excel)"),
                L("Plain source lines only (no File / Path columns)", "Только текст «Оригинал» построчно (без столбцов «Файл» и «Путь»)")
            });
            cbTxtFmt.SelectedIndex = NormalizeTxtFormatIndex(jsonTxtFormatSelectedIndex);

            const int cardPad = 22;
            const int labelCol = 244;

            var ap = new SettingsCardStacker(appearanceCard, cardPad, labelCol);
            ap.Title(titleLabel);
            ap.Full(uiTitleLabel, -1, false);
            ap.Field(themeLabel, themeCombo);
            ap.Field(lblUiLang, uiLangCombo);
            ap.Field(lblJsonCopyMode, cbJsonCopyMode);
            ap.Full(chkJunkFeatures, -1, false);
            ap.Full(lblTxtFmt, -1, false);
            ap.Full(cbTxtFmt, -1, true);

            // «Мусорные» строки (формат TXT) видимы по флажку — высота карточки учитывает их только когда они показаны.
            void LayoutAppearanceTail()
            {
                int bottom = junkFeaturesEnabled ? cbTxtFmt.Bottom : chkJunkFeatures.Bottom;
                appearanceCard.Height = bottom + cardPad;
            }
            LayoutAppearanceTail();

            var langsTitle = new Label
            {
                Text = L("Languages", "Языки"),
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = TitleFgColor(),
                AutoSize = true,
                Location = new Point(4, 4)
            };

            var lblSrc = new Label
            {
                Text = L("Source language", "Исходный язык"),
                Location = new Point(4, 42),
                AutoSize = true,
                ForeColor = TitleFgColor(),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            var cbSrc = new NoWheelComboBox
            {
                // Редактируемый + автодополнение по списку = поиск по вводу (с 68 языками без него неудобно).
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                Width = 420,
                Location = new Point(4, 64),
                Font = new Font("Segoe UI", 10f)
            };
            cbSrc.Items.Add(AutoDetectSourceOption);
            cbSrc.Items.AddRange(UiLanguageOptions);
            // Выбор по строке: «авто» прибавлено первым пунктом, поэтому индексы массива сдвинуты.
            int srcIdx = cbSrc.Items.IndexOf(sourceLanguageDisplay);
            cbSrc.SelectedIndex = srcIdx >= 0 ? srcIdx : Math.Max(0, cbSrc.Items.IndexOf("English (en)"));

            var lblTgt = new Label
            {
                Text = L("Target language", "Язык перевода"),
                Location = new Point(4, 102),
                AutoSize = true,
                ForeColor = TitleFgColor(),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            var cbTgt = new NoWheelComboBox
            {
                // Редактируемый + автодополнение по списку = поиск по вводу.
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
                Width = 420,
                Location = new Point(4, 124),
                Font = new Font("Segoe UI", 10f)
            };
            cbTgt.Items.AddRange(UiLanguageOptions);
            var ti = Array.IndexOf(UiLanguageOptions, targetLanguageDisplay);
            if (ti < 0)
                ti = Array.IndexOf(UiLanguageOptions, "Russian (ru)"); // дефолт цели — русский (раньше был жёсткий индекс 1)
            cbTgt.SelectedIndex = Math.Max(0, ti);

            // Редактируемый combo (нужен для поиска) сам выделяет свой текст → синяя подсветка в покое.
            // Снимаем выделение после построения (отложенно, когда хэндлы готовы). На фокусе/наборе — штатно.
            BeginInvoke((Action)(() =>
            {
                foreach (var cb in new[] { cbSrc, cbTgt })
                {
                    if (cb == null || cb.IsDisposed)
                        continue;
                    cb.SelectionStart = 0;
                    cb.SelectionLength = 0;
                }
            }));

            var lg = new SettingsCardStacker(langsCard, cardPad, labelCol);
            lg.Title(langsTitle);
            lg.Field(lblSrc, cbSrc);
            lg.Field(lblTgt, cbTgt);
            lg.Finish(cardPad);

            var apiTitle = new Label
            {
                Text = L("Translation with API key", "Перевод с API ключом"),
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = TitleFgColor(),
                AutoSize = true,
                Location = new Point(4, 4)
            };
            var chkApi = new CheckBox
            {
                Text = " " + L(
                    "Translate empty cells via API (LibreTranslate or an AI chat backend)",
                    "Переводить пустые строки через API (LibreTranslate или ИИ-чат)"),
                Location = new Point(4, 34),
                AutoSize = true,
                Checked = translationApiEnabled,
                Font = new Font("Segoe UI", 10f),
                ForeColor = BodyFgColor()
            };
            var lblAiBackend = new Label
            {
                Text = L("AI / translation provider", "Провайдер перевода / ИИ"),
                Location = new Point(4, 58),
                AutoSize = true,
                ForeColor = TitleFgColor(),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            var cbAiBackend = new NoWheelComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 820,
                Location = new Point(4, 80),
                Font = new Font("Segoe UI", 10f)
            };
            RepopulateAiBackendCombo(cbAiBackend);

            var lblApiUrl = new Label
            {
                Text = L("Base URL", "Базовый URL"),
                Location = new Point(4, 108),
                AutoSize = true,
                ForeColor = TitleFgColor(),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            var tbApiUrl = new TextBox
            {
                Text = translationApiUrl ?? "",
                Location = new Point(4, 130),
                Width = 458,
                Font = new Font("Segoe UI", 10f)
            };
            Button btnDashScopeChinaUrl = null;
            var btnApplyDefaultApiUrl = new Button
            {
                Text = L("Default URL", "URL по умолчанию"),
                Location = new Point(466, 127),
                Size = new Size(168, 28),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.25f),
                Cursor = Cursors.Hand,
                TabStop = true
            };
            btnApplyDefaultApiUrl.FlatAppearance.BorderSize = 1;

            btnDashScopeChinaUrl = new Button
            {
                Text = L("China base URL", "Базовый URL Китая"),
                Location = new Point(638, 127),
                Size = new Size(178, 28),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.25f),
                Cursor = Cursors.Hand,
                TabStop = true,
                Visible = false
            };
            btnDashScopeChinaUrl.FlatAppearance.BorderSize = 1;

            var lblOrModel = new Label
            {
                Text = L("Chat model", "Модель чата"),
                Location = new Point(4, 164),
                AutoSize = true,
                ForeColor = TitleFgColor(),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            var cbChatModelId = new NoWheelComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.None,
                DropDownHeight = 300,
                Location = new Point(4, 186),
                Width = 520,
                Font = new Font("Segoe UI", 10f),
                IntegralHeight = false,
            };
            cbChatModelId.Text = string.IsNullOrWhiteSpace(translationOpenRouterModel)
                ? LocalTranslateApi.DefaultChatModelId(LocalTranslateApi.ParseTranslationAiBackend(translationAiBackend))
                : translationOpenRouterModel.Trim();

            var btnRefreshOpenRouterModels = new Button
            {
                Text = L("Refresh model list", "Обновить список моделей"),
                Location = new Point(528, 182),
                Size = new Size(296, 30),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.25f),
                Cursor = Cursors.Hand,
                TabStop = true
            };
            btnRefreshOpenRouterModels.FlatAppearance.BorderSize = 1;

            TranslationAiBackend CurrentSettingsBackend() =>
                LocalTranslateApi.ParseTranslationAiBackend(translationAiBackend);

            void SyncChatModelFromText(bool forceRewriteText)
            {
                var b = CurrentSettingsBackend();
                var def = LocalTranslateApi.DefaultChatModelId(b);
                translationOpenRouterModel = string.IsNullOrWhiteSpace(cbChatModelId.Text)
                    ? def
                    : cbChatModelId.Text.Trim();
                if (forceRewriteText)
                    ApplyChatModelToSettingsComboBox(cbChatModelId, translationOpenRouterModel, b);
                SaveSettings();
            }

            void ApplyTranslationBackendUiVisibility()
            {
                var b = CurrentSettingsBackend();
                bool chat = LocalTranslateApi.BackendUsesChatCompletions(b);
                lblOrModel.Visible = chat;
                cbChatModelId.Visible = chat;
                btnRefreshOpenRouterModels.Visible = chat;
                if (btnDashScopeChinaUrl != null)
                    btnDashScopeChinaUrl.Visible = b == TranslationAiBackend.Qwen;
            }

            cbChatModelId.DropDown += (_, __) =>
                RefreshChatModelComboDropDownItems(cbChatModelId, CurrentSettingsBackend());

            cbChatModelId.TextChanged += (_, __) =>
            {
                if (_chatModelComboBulkRefresh)
                    return;
                if (!cbChatModelId.DroppedDown)
                    return;

                string preserve = cbChatModelId.Text ?? "";
                int selStart = cbChatModelId.SelectionStart;

                BeginInvoke(new Action(() =>
                {
                    if (cbChatModelId.IsDisposed || !cbChatModelId.DroppedDown)
                        return;

                    RefreshChatModelComboDropDownItems(cbChatModelId, CurrentSettingsBackend());

                    if (!string.Equals(cbChatModelId.Text, preserve, StringComparison.Ordinal))
                    {
                        _chatModelComboBulkRefresh = true;
                        try
                        {
                            cbChatModelId.Text = preserve;
                            cbChatModelId.SelectionStart = Math.Min(Math.Max(0, selStart), cbChatModelId.Text.Length);
                        }
                        finally
                        {
                            _chatModelComboBulkRefresh = false;
                        }
                    }
                    else
                        cbChatModelId.SelectionStart = Math.Min(Math.Max(0, selStart), cbChatModelId.Text.Length);

                    cbChatModelId.DroppedDown = true;
                }));
            };

            cbChatModelId.SelectionChangeCommitted += (_, __) =>
            {
                if (cbChatModelId.SelectedItem != null)
                {
                    translationOpenRouterModel = (cbChatModelId.SelectedItem.ToString() ?? "").Trim();
                    SaveSettings();
                }
            };

            cbChatModelId.KeyDown += (_, e) =>
            {
                if (e.Alt && e.KeyCode == Keys.Down)
                {
                    RefreshChatModelComboDropDownItems(cbChatModelId, CurrentSettingsBackend());
                    cbChatModelId.DroppedDown = true;
                    e.Handled = true;
                }
            };

            btnApplyDefaultApiUrl.Click += (_, __) =>
            {
                translationApiUrl = LocalTranslateApi.DefaultBaseUrl(CurrentSettingsBackend());
                tbApiUrl.Text = translationApiUrl;
                SaveSettings();
            };

            btnDashScopeChinaUrl.Click += (_, __) =>
            {
                translationApiUrl = LocalTranslateApi.DashScopeOpenAiCompatibleChinaBaseUrl;
                tbApiUrl.Text = translationApiUrl;
                SaveSettings();
            };

            btnRefreshOpenRouterModels.Click += async (_, __) =>
            {
                await PopulateAiModelComboAsync(CurrentSettingsBackend(), cbChatModelId, btnRefreshOpenRouterModels);
            };

            var lblApiKey = new Label
            {
                Text = L("API key", "API key"),
                Location = new Point(4, 224),
                AutoSize = true,
                ForeColor = TitleFgColor(),
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            var tbApiKey = new TextBox
            {
                Text = translationApiKey ?? "",
                Location = new Point(4, 246),
                Width = 476,
                Font = new Font("Segoe UI", 10f),
                UseSystemPasswordChar = true,
            };

            Font ApiKeyRevealGlyphFont()
            {
                foreach (var face in new[] { "Segoe UI Emoji", "Segoe MDL2 Assets", "Segoe UI Symbol" })
                {
                    try
                    {
                        return new Font(face, 13f);
                    }
                    catch { }
                }

                return new Font("Segoe UI", 11f);
            }

            var btnToggleApiKeyVisibility = new Button
            {
                Location = new Point(484, 246),
                Size = new Size(44, 28),
                FlatStyle = FlatStyle.Flat,
                TabStop = true,
                Cursor = Cursors.Hand,
                UseCompatibleTextRendering = false,
                AccessibleName = L("Show or hide API key", "Показать или скрыть ключ API"),
            };
            btnToggleApiKeyVisibility.FlatAppearance.BorderSize = 1;

            bool apiKeyUnmasked = false;

            void ApplyApiKeyVisibilityUi()
            {
                tbApiKey.UseSystemPasswordChar = !apiKeyUnmasked;
                btnToggleApiKeyVisibility.Font = ApiKeyRevealGlyphFont();

                string ff = btnToggleApiKeyVisibility.Font.FontFamily.Name ?? "";
                if (ff.IndexOf("Emoji", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    btnToggleApiKeyVisibility.Text = apiKeyUnmasked ? "\u2716" : "\U0001F441";
                }
                else if (ff.IndexOf("MDL2", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    btnToggleApiKeyVisibility.Text = apiKeyUnmasked ? "\uE711" : "\uE890";
                }
                else
                {
                    btnToggleApiKeyVisibility.Text = apiKeyUnmasked ? "[-]" : "(o)";
                }

                btnToggleApiKeyVisibility.AccessibleDescription = apiKeyUnmasked
                    ? L("Hide API key", "Скрыть ключ API")
                    : L("Show API key", "Показать ключ API");
            }

            ApplyApiKeyVisibilityUi();

            btnToggleApiKeyVisibility.Click += (_, __) =>
            {
                apiKeyUnmasked = !apiKeyUnmasked;
                ApplyApiKeyVisibilityUi();
            };

            var ai = new SettingsCardStacker(apiCard, cardPad, labelCol);
            ai.Title(apiTitle);
            ai.Full(chkApi, -1, false);
            ai.Field(lblAiBackend, cbAiBackend);
            ai.Field(lblApiUrl, tbApiUrl);

            // Кнопки URL — отдельной строкой под полем, слева от колонки полей.
            btnApplyDefaultApiUrl.Size = new Size(168, 28);
            btnApplyDefaultApiUrl.Location = new Point(ai.FieldX, ai.Y);
            btnApplyDefaultApiUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            btnDashScopeChinaUrl.Size = new Size(178, 28);
            btnDashScopeChinaUrl.Location = new Point(ai.FieldX + 176, ai.Y);
            btnDashScopeChinaUrl.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            ai.Add(btnApplyDefaultApiUrl);
            ai.Add(btnDashScopeChinaUrl);
            ai.Y += 28 + 14;

            ai.Field(lblOrModel, cbChatModelId);
            btnRefreshOpenRouterModels.Size = new Size(240, 30);
            btnRefreshOpenRouterModels.Location = new Point(ai.FieldX, ai.Y);
            btnRefreshOpenRouterModels.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            ai.Add(btnRefreshOpenRouterModels);
            ai.Y += 30 + 14;

            // Ключ API: подпись | поле + кнопка-«глаз» справа.
            const int keyRowH = 28, eyeW = 44, eyeGap = 6;
            lblApiKey.Location = new Point(ai.Pad, ai.Y + Math.Max(0, (keyRowH - lblApiKey.PreferredSize.Height) / 2));
            tbApiKey.Location = new Point(ai.FieldX, ai.Y);
            tbApiKey.Width = Math.Max(120, ai.FieldWidth - eyeW - eyeGap);
            tbApiKey.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            btnToggleApiKeyVisibility.Size = new Size(eyeW, 26);
            btnToggleApiKeyVisibility.Location = new Point(ai.FieldX + tbApiKey.Width + eyeGap, ai.Y);
            btnToggleApiKeyVisibility.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            ai.Add(lblApiKey);
            ai.Add(tbApiKey);
            ai.Add(btnToggleApiKeyVisibility);
            ai.Y += keyRowH + 12;
            ai.Finish(cardPad);

            var tmTitle = new Label
            {
                Text = L("Translation memory (TM)", "Память переводов (TM)"),
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = TitleFgColor(),
                AutoSize = true,
                Location = new Point(4, 4)
            };

            var chkTm = new CheckBox
            {
                Text = " " + L(
                    "Use translation memory in JSON Files (applied before each API batch call when filling empty rows)",
                    "Использовать память переводов в JSON Files (перед запросом к API при пакетном заполнении пустых строк)"),
                Location = new Point(4, 38),
                AutoSize = true,
                Checked = useTranslationMemory,
                Font = new Font("Segoe UI", 10f),
                ForeColor = BodyFgColor()
            };

            var pairs = TranslationMemory.Load();
            var pathLabel = new Label
            {
                Text = TranslationMemory.MemoryFilePath,
                Location = new Point(4, 68),
                Size = new Size(820, 42),
                ForeColor = BodyFgColor(),
                Font = new Font("Segoe UI", 9.5f)
            };

            var stats = new Label
            {
                Text = L("Entries in memory: ", "Записей в памяти: ") + pairs.Count,
                Location = new Point(4, 114),
                AutoSize = true,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = TitleFgColor()
            };

            Color accentBtn = DashboardAccentPrimary();
            var btnFolder = new Button
            {
                Text = L("Open AppData folder", "Открыть папку AppData"),
                Location = new Point(4, 146),
                Size = new Size(228, 38),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                BackColor = accentBtn,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            btnFolder.FlatAppearance.BorderSize = 0;

            var btnClearTm = new Button
            {
                Text = L("Clear memory", "Очистить память"),
                Location = new Point(244, 146),
                Size = new Size(228, 38),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                BackColor = Color.FromArgb(176, 58, 58),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            btnClearTm.FlatAppearance.BorderSize = 0;

            var btnPurgeTm = new Button
            {
                Text = L("Remove broken pairs", "Убрать битые пары"),
                Size = new Size(228, 38),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                BackColor = Color.FromArgb(150, 96, 32),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            btnPurgeTm.FlatAppearance.BorderSize = 0;

            var hint = new Label
            {
                Text = L(
                    "When enabled, after extracting strings in JSON Files matching segments from memory.json are filled automatically; after «Paste into JSON» memory grows with original → translated pairs.",
                    "При включённой опции после извлечения строк в JSON Files подставляются совпадения из memory.json; после «Вставить в JSON» память дополняется парами original → translated."),
                Location = new Point(4, 194),
                Size = new Size(820, 108),
                ForeColor = MutedFgColor(),
                Font = new Font("Segoe UI", 10f)
            };

            var tm = new SettingsCardStacker(tmCard, cardPad, labelCol);
            tm.Title(tmTitle);
            tm.Full(chkTm, -1, false);
            tm.Full(pathLabel, 38, true);
            tm.Full(stats, -1, false);
            btnFolder.Size = new Size(228, 38);
            btnFolder.Location = new Point(tm.Pad, tm.Y);
            tm.Add(btnFolder);
            btnClearTm.Size = new Size(228, 38);
            btnClearTm.Location = new Point(tm.Pad + 228 + 12, btnFolder.Top);
            tmCard.Controls.Add(btnClearTm);
            btnPurgeTm.Location = new Point(tm.Pad + (228 + 12) * 2, btnFolder.Top);
            tmCard.Controls.Add(btnPurgeTm);
            tm.Y += 38 + 12;
            tm.Full(hint, 84, true);
            tm.Finish(cardPad);

            btnPurgeTm.Click += (_, __) =>
            {
                int removed = TranslationMemory.PurgeShiftCorruptedPairs();
                var left = TranslationMemory.Load().Count;
                stats.Text = L("Entries in memory: ", "Записей в памяти: ") + left;
                MessageBox.Show(
                    L("Removed broken pairs (number ↔ text, shift artifacts): ", "Удалено битых пар (число ↔ текст, артефакты сдвига): ") + removed +
                    (removed > 0 ? L(". A backup memory.json.bak was created.", ". Создан бэкап memory.json.bak.") : ""),
                    L("Translation memory", "Память переводов"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            btnClearTm.Click += (_, __) =>
            {
                var confirm = MessageBox.Show(
                    L("Delete the entire translation memory (memory.json)? This removes all saved original → translated pairs and cannot be undone.",
                      "Удалить всю память переводов (memory.json)? Это сотрёт все сохранённые пары original → translated без возможности отмены."),
                    L("Clear translation memory", "Очистить память переводов"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (confirm != DialogResult.Yes)
                    return;

                int removed = TranslationMemory.Clear();
                stats.Text = L("Entries in memory: ", "Записей в памяти: ") + 0;
                MessageBox.Show(
                    L("Translation memory cleared. Removed entries: ", "Память переводов очищена. Удалено записей: ") + removed,
                    L("Translation memory", "Память переводов"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            btnFolder.Click += (_, __) =>
            {
                try
                {
                    var dir = Path.GetDirectoryName(TranslationMemory.MemoryFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, L("Translation memory", "Память переводов"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };

            void RefreshSettingsVisuals()
            {
                var card = CardBgColor();
                appearanceCard.BackColor = langsCard.BackColor = apiCard.BackColor = tmCard.BackColor = card;
                scroll.BackColor = _themePageBg;
                titleLabel.ForeColor = uiTitleLabel.ForeColor = langsTitle.ForeColor = apiTitle.ForeColor = tmTitle.ForeColor = SoftHeadFg();
                lblSrc.ForeColor = lblTgt.ForeColor = SoftHeadFg();
                lblAiBackend.ForeColor = lblApiUrl.ForeColor = lblOrModel.ForeColor = lblApiKey.ForeColor = SoftHeadFg();
                themeLabel.ForeColor = lblUiLang.ForeColor = lblJsonCopyMode.ForeColor = BodyFgColor();
                pathLabel.ForeColor = BodyFgColor();
                stats.ForeColor = SoftHeadFg();
                hint.ForeColor = MutedFgColor();
                chkTm.ForeColor = chkApi.ForeColor = chkJunkFeatures.ForeColor = BodyFgColor();
                lblTxtFmt.ForeColor = SoftHeadFg();
                if (btnApplyDefaultApiUrl != null && !btnApplyDefaultApiUrl.IsDisposed)
                {
                    btnApplyDefaultApiUrl.ForeColor = BodyFgColor();
                    btnApplyDefaultApiUrl.BackColor = card;
                    btnApplyDefaultApiUrl.FlatAppearance.BorderColor = MutedFgColor();
                }

                if (btnDashScopeChinaUrl != null && !btnDashScopeChinaUrl.IsDisposed)
                {
                    btnDashScopeChinaUrl.ForeColor = BodyFgColor();
                    btnDashScopeChinaUrl.BackColor = card;
                    btnDashScopeChinaUrl.FlatAppearance.BorderColor = MutedFgColor();
                }

                if (btnToggleApiKeyVisibility != null && !btnToggleApiKeyVisibility.IsDisposed)
                {
                    btnToggleApiKeyVisibility.ForeColor = BodyFgColor();
                    btnToggleApiKeyVisibility.BackColor = card;
                    btnToggleApiKeyVisibility.FlatAppearance.BorderColor = MutedFgColor();
                    ApplyApiKeyVisibilityUi();
                }

                if (btnRefreshOpenRouterModels != null && !btnRefreshOpenRouterModels.IsDisposed)
                {
                    btnRefreshOpenRouterModels.ForeColor = BodyFgColor();
                    btnRefreshOpenRouterModels.BackColor = card;
                    btnRefreshOpenRouterModels.FlatAppearance.BorderColor = MutedFgColor();
                }

                var cbBg = _themeGridRowBg;
                var cbFg = _themeGridRowFore;
                cbSrc.BackColor = cbTgt.BackColor = cbTxtFmt.BackColor = themeCombo.BackColor = uiLangCombo.BackColor = cbJsonCopyMode.BackColor =
                    cbAiBackend.BackColor =
                    tbApiUrl.BackColor = tbApiKey.BackColor = cbChatModelId.BackColor = cbBg;
                cbSrc.ForeColor = cbTgt.ForeColor = cbTxtFmt.ForeColor = themeCombo.ForeColor = uiLangCombo.ForeColor = cbJsonCopyMode.ForeColor =
                    cbAiBackend.ForeColor =
                    tbApiUrl.ForeColor = tbApiKey.ForeColor = cbChatModelId.ForeColor = cbFg;

                // Плоский стиль — комбобоксы держат тёмный фон темы (системные были светлыми и резали глаза).
                foreach (var cb in new[] { cbSrc, cbTgt, cbTxtFmt, themeCombo, uiLangCombo, cbJsonCopyMode, cbAiBackend, cbChatModelId })
                {
                    if (cb != null && !cb.IsDisposed)
                        cb.FlatStyle = FlatStyle.Flat;
                }

                btnFolder.BackColor = DashboardAccentPrimary();
            }

            cbSrc.SelectedIndexChanged += (_, __) =>
            {
                sourceLanguageDisplay = cbSrc.SelectedItem?.ToString() ?? sourceLanguageDisplay;
                SaveSettings();
            };

            cbTgt.SelectedIndexChanged += (_, __) =>
            {
                targetLanguageDisplay = cbTgt.SelectedItem?.ToString() ?? targetLanguageDisplay;
                SaveSettings();
            };

            // Поиск делает списки редактируемыми: после ухода фокуса фиксируем выбор на точном пункте,
            // недопустимый ввод откатываем — чтобы в настройки не попала «полупечатанная» строка.
            cbSrc.Leave += (_, __) => SnapLanguageComboToValidItem(cbSrc, ref sourceLanguageDisplay);
            cbTgt.Leave += (_, __) => SnapLanguageComboToValidItem(cbTgt, ref targetLanguageDisplay);

            cbTxtFmt.SelectedIndexChanged += (_, __) =>
            {
                jsonTxtFormatSelectedIndex = NormalizeTxtFormatIndex(cbTxtFmt.SelectedIndex);
                SaveSettings();
            };

            chkTm.CheckedChanged += (_, __) =>
            {
                useTranslationMemory = chkTm.Checked;
                SaveSettings();
            };

            chkApi.CheckedChanged += (_, __) =>
            {
                translationApiEnabled = chkApi.Checked;
                SaveSettings();
            };

            chkJunkFeatures.CheckedChanged += async (_, __) =>
            {
                junkFeaturesEnabled = chkJunkFeatures.Checked;
                lblTxtFmt.Visible = cbTxtFmt.Visible = junkFeaturesEnabled;
                LayoutAppearanceTail();
                RestackCards();
                if (!junkFeaturesEnabled && IsJunkTranslationBackendKey(translationAiBackend))
                {
                    translationAiBackend = "LibreTranslate";
                    translationApiUrl = LocalTranslateApi.DefaultBaseUrl(LocalTranslateApi.ParseTranslationAiBackend(translationAiBackend));
                    tbApiUrl.Text = translationApiUrl;
                    translationOpenRouterModel = LocalTranslateApi.DefaultChatModelId(CurrentSettingsBackend());
                }

                SaveSettings();
                RepopulateAiBackendCombo(cbAiBackend);
                translationApiUrl = LocalTranslateApi.DefaultBaseUrl(CurrentSettingsBackend());
                tbApiUrl.Text = translationApiUrl;
                ApplyTranslationBackendUiVisibility();
                RefreshSettingsVisuals();
                SyncChatModelFromText(true);
                await PopulateAiModelComboAsync(CurrentSettingsBackend(), cbChatModelId, btnRefreshOpenRouterModels)
                    .ConfigureAwait(true);
            };

            cbAiBackend.SelectedIndexChanged += async (_, __) =>
            {
                var keys = GetTranslationBackendKeys();
                int ix = cbAiBackend.SelectedIndex;
                if (ix < 0 || ix >= keys.Length)
                    return;
                translationAiBackend = keys[ix];
                translationApiUrl = LocalTranslateApi.DefaultBaseUrl(CurrentSettingsBackend());
                tbApiUrl.Text = translationApiUrl;
                translationOpenRouterModel = LocalTranslateApi.DefaultChatModelId(CurrentSettingsBackend());
                SaveSettings();
                ApplyTranslationBackendUiVisibility();
                RefreshSettingsVisuals();
                await PopulateAiModelComboAsync(CurrentSettingsBackend(), cbChatModelId, btnRefreshOpenRouterModels)
                    .ConfigureAwait(true);
            };

            tbApiUrl.Leave += (_, __) =>
            {
                translationApiUrl = (tbApiUrl.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(translationApiUrl))
                    translationApiUrl = LocalTranslateApi.DefaultBaseUrl(CurrentSettingsBackend());
                tbApiUrl.Text = translationApiUrl;
                SaveSettings();
            };

            tbApiKey.Leave += (_, __) =>
            {
                translationApiKey = tbApiKey.Text ?? "";
                SaveSettings();
            };

            cbChatModelId.Leave += (_, __) =>
            {
                SyncChatModelFromText(true);
            };

            uiLangCombo.SelectedIndexChanged += (_, __) =>
            {
                var choices = InterfaceLanguageChoices();
                int idx = uiLangCombo.SelectedIndex;
                string next = (idx >= 0 && idx < choices.Count) ? choices[idx].Code : "en";
                if (string.Equals(next, appUiLanguage, StringComparison.OrdinalIgnoreCase))
                    return;
                appUiLanguage = next;
                SaveSettings();
                RefreshMainMenuLanguage();
                UpdateNavButtonsAppearance();
                LoadSettingsModule();
            };

            themeCombo.SelectedIndexChanged += (_, __) =>
            {
                currentThemeName = themeCombo.SelectedItem?.ToString() ?? "Translator Purple";
                isDarkTheme = IsDarkTheme(currentThemeName);
                ApplyTheme();
                RefreshSettingsVisuals();
                SaveSettings();
                Log(L($"Theme changed: {currentThemeName}", $"Тема изменена: {currentThemeName}"));
            };

            cbJsonCopyMode.SelectedIndexChanged += (_, __) =>
            {
                jsonCopyModeSelectedIndex = NormalizeJsonCopyModeIndex(cbJsonCopyMode.SelectedIndex);
                SaveSettings();
            };

            ApplyTranslationBackendUiVisibility();
            SyncCardWidths();
            RestackCards();
            RefreshSettingsVisuals();

            scroll.Controls.Add(innerWrap);
            moduleHostPanel.Controls.Add(scroll);
            ApplyThemedScrollBars(scroll); // тёмная полоса прокрутки как в остальных разделах

            UpdateStatus();

            async void KickoffModelsCatalog()
            {
                await PopulateAiModelComboAsync(CurrentSettingsBackend(), cbChatModelId, btnRefreshOpenRouterModels)
                    .ConfigureAwait(true);
            }

            KickoffModelsCatalog();
        }

        /// <summary>Вертикальный раскладчик карточки настроек: заголовок, строки «подпись|поле», строки во всю ширину; поля тянутся (Anchor), высоту задаёт <see cref="Finish"/>.</summary>
        private sealed class SettingsCardStacker
        {
            private readonly Panel _card;
            private readonly int _pad;
            private readonly int _fieldX;

            public SettingsCardStacker(Panel card, int pad, int labelColWidth)
            {
                _card = card;
                _pad = pad;
                _fieldX = pad + labelColWidth;
                Y = pad;
            }

            /// <summary>Текущая вертикальная позиция — верх следующей строки.</summary>
            public int Y { get; set; }

            public int Pad => _pad;
            public int FieldX => _fieldX;
            public int FieldWidth => Math.Max(140, _card.Width - _fieldX - _pad);
            public int FullWidth => Math.Max(140, _card.Width - 2 * _pad);

            public void Add(Control c) => _card.Controls.Add(c);

            public void Title(Label title, int gapAfter = 14)
            {
                title.AutoSize = true;
                title.Location = new Point(_pad, Y);
                _card.Controls.Add(title);
                Y += title.PreferredSize.Height + gapAfter;
            }

            /// <summary>Подпись слева (по центру строки) + поле справа на всю оставшуюся ширину.</summary>
            public void Field(Control label, Control field, int rowHeight = 28, int gapAfter = 12)
            {
                int labelH = label.PreferredSize.Height;
                label.Location = new Point(_pad, Y + Math.Max(0, (rowHeight - labelH) / 2));
                field.Location = new Point(_fieldX, Y);
                field.Width = FieldWidth;
                field.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                _card.Controls.Add(label);
                _card.Controls.Add(field);
                Y += rowHeight + gapAfter;
            }

            /// <summary>Строка во всю ширину (чекбокс, подзаголовок, многострочная подсказка).</summary>
            public void Full(Control c, int height, bool stretch, int gapAfter = 10)
            {
                c.Location = new Point(_pad, Y);
                if (stretch)
                {
                    c.Width = FullWidth;
                    c.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                }

                bool canSetHeight = c is Label || c is Panel || (c is TextBox tb && tb.Multiline);
                if (height > 0 && canSetHeight)
                    c.Height = height;

                _card.Controls.Add(c);
                int h = height > 0 ? height : c.PreferredSize.Height;
                Y += h + gapAfter;
            }

            public void Finish(int bottomPad)
            {
                _card.Height = Y + bottomPad;
            }
        }

        /// <summary>Синхронизирует отображаемое имя модели с сохранённым значением.</summary>
        private void ApplyChatModelToSettingsComboBox(NoWheelComboBox cb, string modelIdOrEmpty, TranslationAiBackend backend)
        {
            if (cb == null || cb.IsDisposed)
                return;
            var want = string.IsNullOrWhiteSpace(modelIdOrEmpty)
                ? LocalTranslateApi.DefaultChatModelId(backend)
                : modelIdOrEmpty.Trim();
            _chatModelComboBulkRefresh = true;
            try
            {
                cb.Text = want;
            }
            finally
            {
                _chatModelComboBulkRefresh = false;
            }
        }

        private bool IsOpenRouterFreeCatalogModel(string modelId)
        {
            if (string.IsNullOrEmpty(modelId))
                return false;
            if (_openRouterFreeModelIds.Count > 0)
                return _openRouterFreeModelIds.Contains(modelId);
            return modelId.EndsWith(":free", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Перезаполняет выпадающий список моделей по текущей строке фильтра.</summary>
        private void RefreshChatModelComboDropDownItems(NoWheelComboBox cb, TranslationAiBackend backend)
        {
            if (cb == null || cb.IsDisposed)
                return;

            var qtRaw = (cb.Text ?? "").Trim();
            var openRouterFreeOnly = false;
            var q = qtRaw;

            if (backend == TranslationAiBackend.OpenRouter)
            {
                if (qtRaw.Equals("free", StringComparison.OrdinalIgnoreCase))
                {
                    openRouterFreeOnly = true;
                    q = "";
                }
                else if (qtRaw.StartsWith("free ", StringComparison.OrdinalIgnoreCase))
                {
                    openRouterFreeOnly = true;
                    q = qtRaw.Length > 5 ? qtRaw.Substring(5).Trim() : "";
                }
            }

            IEnumerable<string> src = _translationChatModelCatalog.Count > 0
                ? _translationChatModelCatalog
                : LocalTranslateApi.ModelPresetsForBackend(backend);

            var filtered = src.Where(x =>
            {
                if (openRouterFreeOnly && !IsOpenRouterFreeCatalogModel(x))
                    return false;
                return q.Length == 0 || x.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            }).ToList();

            _chatModelComboBulkRefresh = true;
            try
            {
                cb.Items.Clear();
                foreach (var id in filtered)
                    cb.Items.Add(id);

                var exactIx = -1;
                for (var i = 0; i < cb.Items.Count; i++)
                {
                    if (string.Equals((cb.Items[i]?.ToString() ?? "").Trim(), q, StringComparison.OrdinalIgnoreCase))
                    {
                        exactIx = i;
                        break;
                    }
                }

                cb.SelectedIndex = exactIx;
            }
            finally
            {
                _chatModelComboBulkRefresh = false;
            }
        }

        private async Task PopulateAiModelComboAsync(
            TranslationAiBackend backend,
            NoWheelComboBox modelCb,
            Button refreshBtn)
        {
            string restoreBtnCaption = refreshBtn != null && !refreshBtn.IsDisposed ? refreshBtn.Text : "";

            if (backend == TranslationAiBackend.LibreTranslate)
            {
                _translationChatModelCatalog.Clear();
                _openRouterFreeModelIds.Clear();

                if (modelCb != null && !modelCb.IsDisposed)
                {
                    _chatModelComboBulkRefresh = true;
                    try
                    {
                        modelCb.Items.Clear();
                        modelCb.Text = "";
                        modelCb.Enabled = false;
                    }
                    finally
                    {
                        _chatModelComboBulkRefresh = false;
                    }
                }

                if (refreshBtn != null && !refreshBtn.IsDisposed)
                {
                    refreshBtn.Enabled = false;
                    refreshBtn.Text = string.IsNullOrEmpty(restoreBtnCaption)
                        ? L("Refresh model list", "Обновить список моделей")
                        : restoreBtnCaption;
                }

                return;
            }

            if (modelCb != null && !modelCb.IsDisposed)
                modelCb.Enabled = true;

            if (backend != TranslationAiBackend.OpenRouter)
                _openRouterFreeModelIds.Clear();

            try
            {
                if (refreshBtn != null && !refreshBtn.IsDisposed)
                {
                    refreshBtn.Enabled = false;
                    refreshBtn.Text = L("Loading…", "Загрузка…");
                }

                IReadOnlyList<string> ids = Array.Empty<string>();
                switch (backend)
                {
                    case TranslationAiBackend.OpenRouter:
                    {
                        var rows = await LocalTranslateApi.FetchOpenRouterCatalogModelsAsync().ConfigureAwait(true);
                        ids = rows.Select(r => r.Id).ToList();
                        _openRouterFreeModelIds.Clear();
                        foreach (var r in rows)
                        {
                            if (r.IsFree)
                                _openRouterFreeModelIds.Add(r.Id);
                        }

                        break;
                    }
                    case TranslationAiBackend.Ollama:
                        ids = await LocalTranslateApi.FetchOllamaModelNamesAsync(translationApiUrl).ConfigureAwait(true);
                        break;
                    case TranslationAiBackend.CloudflareWorkersAi:
                    case TranslationAiBackend.Apify:
                        ids = Array.Empty<string>();
                        break;
                    default:
                        ids = await LocalTranslateApi.FetchOpenAiCompatibleModelIdsAsync(translationApiUrl, translationApiKey)
                            .ConfigureAwait(true);
                        break;
                }

                if (modelCb != null && modelCb.IsDisposed)
                    return;

                var merged = LocalTranslateApi.MergePresetAndFetchedModels(backend, ids);

                if (backend == TranslationAiBackend.OpenRouter)
                {
                    foreach (var id in merged)
                    {
                        if (id.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
                            _openRouterFreeModelIds.Add(id);
                    }
                }

                _translationChatModelCatalog.Clear();
                _translationChatModelCatalog.AddRange(merged);
                if (modelCb != null && !modelCb.IsDisposed)
                    ApplyChatModelToSettingsComboBox(modelCb, translationOpenRouterModel, backend);

                Log(L($"Models ready: {merged.Count} (built‑in presets merged with server list when available).",
                    $"Моделей в списке: {merged.Count} (встроенные имена объединены с сервером, если он ответил)."));
            }
            catch (Exception ex)
            {
                var fallback = LocalTranslateApi.MergePresetAndFetchedModels(backend, Array.Empty<string>());
                _translationChatModelCatalog.Clear();
                _translationChatModelCatalog.AddRange(fallback);

                if (backend == TranslationAiBackend.OpenRouter)
                {
                    _openRouterFreeModelIds.Clear();
                    foreach (var id in fallback)
                    {
                        if (id.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
                            _openRouterFreeModelIds.Add(id);
                    }
                }

                if (modelCb != null && !modelCb.IsDisposed)
                    ApplyChatModelToSettingsComboBox(modelCb, translationOpenRouterModel, backend);

                Log(L($"Model list failed: {ex.Message}", $"Не удалось загрузить список моделей: {ex.Message}"), true);
            }
            finally
            {
                if (refreshBtn != null && !refreshBtn.IsDisposed)
                {
                    refreshBtn.Enabled = backend != TranslationAiBackend.LibreTranslate;
                    refreshBtn.Text = string.IsNullOrEmpty(restoreBtnCaption)
                        ? L("Refresh model list", "Обновить список моделей")
                        : restoreBtnCaption;
                }

                if (modelCb != null && !modelCb.IsDisposed)
                    modelCb.Enabled = backend != TranslationAiBackend.LibreTranslate;
            }
        }

        private void LoadPlaceholder(string title, string desc)
        {
            ShowChromeHeader();
            ClearAssetsModuleRefs();
            ClearBundleLocModuleRefs();
            DetachModuleEvents();
            ClearContentPanel();
            headerLabel.Text = title;
            var lbl = new Label
            {
                Text = desc,
                Font = new Font("Segoe UI", 12f),
                ForeColor = Color.Gray,
                AutoSize = true,
                Location = new Point(32, 40)
            };
            moduleHostPanel.Controls.Add(lbl);
            UpdateStatus();
        }

        private void ClearContentPanel()
        {
            settingsJsonCopyModeCombo = null;
            moduleHostPanel.Controls.Clear();
        }

        private void NullJsonTranslatorSurfaceRefs()
        {
            jsonWorkspaceCard = null;
            lblJsonModuleTitle = null;
            lblActivityTitle = null;
            toolbarFlow = null;
            btnSelectFolder = null;
            btnApply = null;
            btnExportTxt = null;
            btnTranslateEmptyApi = null;
            btnCopySelectedAi = null;
            btnPasteAi = null;
            btnClearLog = null;
            chkBackup = null;
            progressStatsLabel = null;
            progressBar = null;
            statusStrip = null;
            statusLabel = null;
            btnCancelApiBatchTranslate = null;
            logBox = null;
            dgv = null;
        }

        private void UpdateSidebarReadyLabel()
        {
            if (lblSidebarReady == null || lblSidebarReady.IsDisposed)
                return;

            lblSidebarReady.ForeColor = isDarkTheme
                ? Color.FromArgb(134, 239, 172)
                : Color.FromArgb(22, 101, 52);

            if (translationItems.Count == 0)
                lblSidebarReady.Text = L("Ready", "Готово");
            else
            {
                var done = translationItems.Count(x => !string.IsNullOrWhiteSpace(x.Translated));
                lblSidebarReady.Text = $"{L("Ready", "Готово")} · {done}/{translationItems.Count}";
            }
        }

        /// <summary>Таблица JSON показана в основной области (не свёрнута при переключении на другой раздел).</summary>
        private bool IsJsonTranslatorSurfaceHosted =>
            jsonWorkspaceCard != null && !jsonWorkspaceCard.IsDisposed &&
            ReferenceEquals(jsonWorkspaceCard.Parent, moduleHostPanel);

        private bool RequireJsonTranslatorSurface(string hint)
        {
            if (dgv != null && !dgv.IsDisposed && IsJsonTranslatorSurfaceHosted)
                return true;
            Log(string.IsNullOrWhiteSpace(hint)
                    ? "Откройте раздел «JSON Files»."
                    : "Откройте раздел «JSON Files»: " + hint + ".",
                true);
            return false;
        }

        private void DisposeJsonTranslatorWorkspaceIfAny()
        {
            DetachModuleEvents();
            if (jsonWorkspaceCard != null && !jsonWorkspaceCard.IsDisposed)
                jsonWorkspaceCard.Dispose();
            NullJsonTranslatorSurfaceRefs();
        }

        private async void TryAutoExtractAfterJsonModuleLoad()
        {
            if (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder))
                return;
            if (dgv == null || dgv.IsDisposed)
                return;

            var canReuseCache =
                translationItems.Count > 0 &&
                string.Equals(lastJsonExtractFolder, currentFolder, StringComparison.OrdinalIgnoreCase);

            if (canReuseCache)
            {
                if (dgv.Rows.Count == translationItems.Count && translationItems.Count > 0)
                    return;
                RepopulateJsonGridFromCachedItems();
                return;
            }

            await ExtractTextsAsync();
        }

        /// <summary>Пересобирает таблицу из кеша без повторного чтения всех JSON с диска (при возврате в модуль).</summary>
        private void RepopulateJsonGridFromCachedItems()
        {
            if (dgv == null || dgv.IsDisposed)
                return;

            PopulateJsonGridRowsFast();

            ApplyTableSearch();
            UpdateRowHighlights();
            UpdateProgressStats();
            UpdateStatus();
        }

        /// <summary>
        /// Массово перезаливает строки грида из <see cref="translationItems"/>: на время заливки отключает авторазмер строк
        /// (иначе каждый Add меряет перенос — near-O(n²)) и добавляет одним <c>AddRange</c> вместо поштучных <c>Rows.Add</c>.
        /// </summary>
        private void PopulateJsonGridRowsFast()
        {
            if (dgv == null || dgv.IsDisposed)
                return;

            var prevRowsMode = dgv.AutoSizeRowsMode;
            dgv.SuspendLayout();
            try
            {
                dgv.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
                dgv.Rows.Clear();

                var count = translationItems.Count;
                if (count > 0)
                {
                    var rows = new DataGridViewRow[count];
                    for (int i = 0; i < count; i++)
                    {
                        var item = translationItems[i];
                        var row = new DataGridViewRow();
                        row.CreateCells(dgv, item.FileName, item.DisplayPath, item.Original, item.Translated);
                        // row.Tag хранит ССЫЛКУ на элемент, который отображает строка — единый источник истины
                        // пары «строка↔элемент». Любой код, которому нужен элемент строки грида, берёт его через
                        // Tag (RowItem/RowItemAt/RowIndexOfItem), а НЕ через translationItems[индекс строки]. Это
                        // исключает сдвиг при сортировке/фильтре/частичной перезаливке, когда порядок грида и
                        // списка временно расходятся.
                        row.Tag = item;
                        rows[i] = row;
                    }
                    dgv.Rows.AddRange(rows);
                }
            }
            finally
            {
                dgv.AutoSizeRowsMode = prevRowsMode;
                dgv.ResumeLayout(true);
            }
        }

        /// <summary>Элемент перевода, привязанный к строке грида через <see cref="DataGridViewRow.Tag"/>. null для new-row/без привязки.</summary>
        private static TranslationItem RowItem(DataGridViewRow row) => row?.Tag as TranslationItem;

        /// <summary>Элемент, отображаемый в строке грида <paramref name="rowIndex"/> (через Tag). null, если индекс вне диапазона.</summary>
        private TranslationItem RowItemAt(int rowIndex) =>
            (dgv != null && rowIndex >= 0 && rowIndex < dgv.Rows.Count) ? dgv.Rows[rowIndex].Tag as TranslationItem : null;

        /// <summary>Индекс строки грида, отображающей данный элемент (по ссылке Tag). -1, если строки нет.</summary>
        private int RowIndexOfItem(TranslationItem item)
        {
            if (dgv == null || item == null)
                return -1;
            for (int i = 0; i < dgv.Rows.Count; i++)
                if (ReferenceEquals(dgv.Rows[i].Tag, item))
                    return i;
            return -1;
        }

        /// <summary>Перезаливает колонку «Перевод» из элементов строк (Tag): каждая строка берёт значение из СВОЕГО элемента → порядок не важен, сдвиг невозможен.</summary>
        private void RefreshTranslatedColumnFromItems()
        {
            if (dgv == null || dgv.IsDisposed)
                return;
            foreach (DataGridViewRow row in dgv.Rows)
            {
                if (row.IsNewRow)
                    continue;
                var item = RowItem(row);
                if (item != null)
                    row.Cells["Translated"].Value = item.Translated;
            }
        }

        /// <summary>Сбрасывает кеш таблицы перевода (папка JSON или содержимое сменилось из другого модуля).</summary>
        private void InvalidateJsonTableCache(bool clearTranslationItems)
        {
            BumpDashboardContentStamp();
            lastJsonExtractFolder = "";
            if (clearTranslationItems)
            {
                translationItems.Clear();
                ClearTranslationUndoStack();
            }
        }

        private void LoadDashboardModule()
        {
            ClearAssetsModuleRefs();
            ClearBundleLocModuleRefs();
            DetachModuleEvents();
            ClearContentPanel();

            HideChromeHeaderForDashboard();

            if (TryAttachCachedDashboard())
                return;

            BuildDashboardUi();
        }

        private void ClearAssetsModuleRefs()
        {
            assetsModuleFolderLabel = null;
            assetsModuleBuildButton = null;
            assetsModuleProgressBar = null;
            assetsModuleLogBox = null;
            assetsModuleExportButton = null;
            assetsModuleExportSingleAssetButton = null;
            assetsModuleFindFontsButton = null;
            assetsModuleImportTmpFontButton = null;
            assetsModuleTtfToTmpFontButton = null;
            assetsModulePatchTmpMsdfAtlasButton = null;
            assetsModuleReplaceAtlasTexturePngButton = null;
            assetsModuleFindResourcesCrcButton = null;
            assetsModuleDumpPathIdFieldsButton = null;
            assetsModuleAssetsGrid = null;
            assetsModuleAssetsStatsLabel = null;
            assetsModulePickGameFolderButton = null;
        }

        private enum UnityToolboxMode
        {
            Full,
            FontsOnly
        }

        private void LoadAssetsModule() => LoadUnityToolboxModule(UnityToolboxMode.Full);

        private void LoadFontToolsModule() => LoadUnityToolboxModule(UnityToolboxMode.FontsOnly);

        private void LoadUnityToolboxModule(UnityToolboxMode mode)
        {
            ShowChromeHeader();
            ClearAssetsModuleRefs();
            ClearBundleLocModuleRefs();
            DetachModuleEvents();
            ClearContentPanel();

            if (headerPanel != null && !headerPanel.IsDisposed)
                headerPanel.Height = 6;
            if (headerLabel != null && !headerLabel.IsDisposed)
            {
                headerLabel.Text = "";
                headerLabel.Visible = false;
            }

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0), AutoScroll = false };

            assetsModuleFolderLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(920, 0),
                Margin = new Padding(0, 0, 0, 8),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(17, 24, 39),
                Text = ""
            };

            var actionsFlow = new FlowLayoutPanel
            {
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0, 4, 0, 12),
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };

            assetsModulePickGameFolderButton = CreateModernButton(L("Browse Unity Data…", "Выбрать Unity Data…"), ButtonStyleKind.Primary);
            assetsModulePickGameFolderButton.Width = 236;
            assetsModulePickGameFolderButton.Margin = new Padding(0, 0, 10, 0);
            assetsModulePickGameFolderButton.Click += BtnPickAssetsGameFolder_Click;
            actionsFlow.Controls.Add(assetsModulePickGameFolderButton);

            if (mode == UnityToolboxMode.Full)
            {
                assetsModuleExportButton = CreateModernButton(L("Export JSON…", "Экспорт JSON…"), ButtonStyleKind.Secondary);
                assetsModuleExportButton.Width = 236;
                assetsModuleExportButton.Margin = new Padding(0, 0, 10, 0);
                assetsModuleExportButton.Click += BtnExportFromAssets_Click;
                actionsFlow.Controls.Add(assetsModuleExportButton);

                assetsModuleBuildButton = CreateModernButton(L("Import into .assets…", "Импорт в .assets…"), ButtonStyleKind.Primary);
                assetsModuleBuildButton.Width = 236;
                assetsModuleBuildButton.Margin = new Padding(0, 0, 10, 0);
                assetsModuleBuildButton.Click += BtnImportAsset_Click;
                actionsFlow.Controls.Add(assetsModuleBuildButton);

                // IL2CPP (нет Managed, type tree вырезан): сгенерировать DummyDll → читаются поля MonoBehaviour.
                var il2cppDummyButton = CreateModernButton(L("IL2CPP: dummy DLL", "IL2CPP: dummy-DLL"), ButtonStyleKind.Secondary);
                il2cppDummyButton.Width = 236;
                il2cppDummyButton.Margin = new Padding(0, 0, 10, 0);
                il2cppDummyButton.Click += BtnIl2CppDummy_Click;
                actionsFlow.Controls.Add(il2cppDummyButton);
            }
            else
            {
                assetsModuleExportButton = null;
                assetsModuleBuildButton = null;
            }

            FlowLayoutPanel ttfToTmpFontRow = null;
            if (mode == UnityToolboxMode.FontsOnly)
            {
                assetsModuleFindFontsButton = CreateModernButton(L("Find fonts", "Найти шрифты"), ButtonStyleKind.Secondary);
                assetsModuleFindFontsButton.Width = 236;
                assetsModuleFindFontsButton.Margin = new Padding(0, 0, 10, 0);
                assetsModuleFindFontsButton.Click += BtnFindFonts_Click;
                actionsFlow.Controls.Add(assetsModuleFindFontsButton);

                assetsModuleImportTmpFontButton = null;

                assetsModuleDumpPathIdFieldsButton = CreateModernButton(
                    L("Dump PathID fields…", "Дамп полей PathID…"),
                    ButtonStyleKind.Secondary);
                assetsModuleDumpPathIdFieldsButton.Width = 260;
                assetsModuleDumpPathIdFieldsButton.Margin = new Padding(0, 0, 10, 0);
                assetsModuleDumpPathIdFieldsButton.Click += BtnDumpPathIdFields_Click;
                actionsFlow.Controls.Add(assetsModuleDumpPathIdFieldsButton);

                assetsModuleFindResourcesCrcButton = CreateModernButton(
                    L("Find resources.assets CRC…", "Найти CRC resources.assets…"),
                    ButtonStyleKind.Secondary);
                assetsModuleFindResourcesCrcButton.Width = 280;
                assetsModuleFindResourcesCrcButton.Margin = new Padding(0, 0, 10, 0);
                assetsModuleFindResourcesCrcButton.Click += BtnFindResourcesAssetsCrc_Click;
                actionsFlow.Controls.Add(assetsModuleFindResourcesCrcButton);

                // Пошаговый мастер замены шрифта (4 кнопки слева направо) — см. Form1.FontWizard.cs.
                ttfToTmpFontRow = new FlowLayoutPanel
                {
                    AutoSize = true,
                    WrapContents = true,
                    FlowDirection = FlowDirection.LeftToRight,
                    Margin = new Padding(0, 0, 0, 6),
                    Padding = new Padding(0),
                    BackColor = Color.Transparent
                };
                BuildFontWizardButtons(ttfToTmpFontRow);

                assetsModuleTtfToTmpFontButton = null;
                assetsModulePatchTmpMsdfAtlasButton = null;
                assetsModuleReplaceAtlasTexturePngButton = null;
            }
            else
            {
                assetsModuleFindFontsButton = null;
                assetsModuleImportTmpFontButton = null;
                assetsModuleTtfToTmpFontButton = null;
                assetsModulePatchTmpMsdfAtlasButton = null;
                assetsModuleReplaceAtlasTexturePngButton = null;
                assetsModuleFindResourcesCrcButton = null;
                assetsModuleDumpPathIdFieldsButton = null;
            }

            FlowLayoutPanel singleAssetRow = null;
            if (mode == UnityToolboxMode.Full)
            {
                singleAssetRow = new FlowLayoutPanel
                {
                    AutoSize = true,
                    WrapContents = false,
                    FlowDirection = FlowDirection.LeftToRight,
                    Margin = new Padding(0, 0, 0, 10),
                    Padding = new Padding(0),
                    BackColor = Color.Transparent
                };
                assetsModuleExportSingleAssetButton = CreateModernButton(
                    L("Export selected container to JSON…", "Экспорт выбранного .assets в JSON…"),
                    ButtonStyleKind.Secondary);
                assetsModuleExportSingleAssetButton.Width = 720;
                assetsModuleExportSingleAssetButton.Click += BtnExportSelectedAssetJson_Click;
                singleAssetRow.Controls.Add(assetsModuleExportSingleAssetButton);
            }
            else
                assetsModuleExportSingleAssetButton = null;

            assetsModuleAssetsStatsLabel = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(920, 0),
                Margin = new Padding(0, 0, 0, 6),
                Font = new Font("Segoe UI", 9.25f, FontStyle.Bold),
                ForeColor = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(17, 24, 39),
                Text = L("Containers: choose the Unity Data folder first.", "Контейнеры: сначала укажите папку Unity Data.")
            };

            assetsModuleAssetsGrid = new ClipboardAwareDataGridView
            {
                Width = 920,
                Height = 260,
                Margin = new Padding(0, 0, 0, 8),
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = isDarkTheme ? Color.FromArgb(24, 24, 26) : Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                ColumnHeadersHeight = 30,
                MultiSelect = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable
            };
            assetsModuleAssetsGrid.Columns.Add("AssetName", L("File", "Файл"));
            assetsModuleAssetsGrid.Columns.Add("Kind", L("Type", "Тип"));
            assetsModuleAssetsGrid.Columns.Add("Size", L("Size", "Размер"));
            assetsModuleAssetsGrid.Columns.Add("Sidecars", L("Sidecars", "Связанные файлы"));
            assetsModuleAssetsGrid.Columns.Add("Path", L("Path", "Путь"));
            assetsModuleAssetsGrid.Columns["AssetName"].FillWeight = 150;
            assetsModuleAssetsGrid.Columns["Kind"].FillWeight = 72;
            assetsModuleAssetsGrid.Columns["Size"].FillWeight = 62;
            assetsModuleAssetsGrid.Columns["Sidecars"].FillWeight = 125;
            assetsModuleAssetsGrid.Columns["Path"].FillWeight = 260;
            assetsModuleAssetsGrid.CellDoubleClick += (_, __) => OpenSelectedAssetInExplorer();

            assetsModuleProgressBar = new ProgressBar
            {
                Width = 420,
                Height = 8,
                Margin = new Padding(0, 8, 0, 8),
                Visible = false,
                Style = ProgressBarStyle.Continuous,
                ForeColor = Color.FromArgb(59, 130, 246)
            };

            assetsModuleLogBox = new RichTextBox
            {
                Width = 920,
                Height = 160,
                Margin = new Padding(0, 2, 0, 0),
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9f),
                DetectUrls = false
            };

            // Верхние контролы — стопкой сверху; грид контейнеров тянется на всё свободное место
            // Panel1 (до журнала), прогресс-бар прижат снизу. Журнал «прикреплён» разделителем.
            var topStack = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0),
                BackColor = Color.Transparent
            };

            topStack.Controls.Add(assetsModuleFolderLabel);
            topStack.Controls.Add(actionsFlow);
            if (ttfToTmpFontRow != null)
                topStack.Controls.Add(ttfToTmpFontRow);
            if (singleAssetRow != null)
                topStack.Controls.Add(singleAssetRow);
            topStack.Controls.Add(assetsModuleAssetsStatsLabel);

            assetsModuleAssetsGrid.Dock = DockStyle.Fill;
            assetsModuleProgressBar.Dock = DockStyle.Bottom;
            assetsModuleProgressBar.Margin = new Padding(0);

            assetsModuleLogBox.Dock = DockStyle.Fill;

            var assetsGridLogSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 8,
                FixedPanel = FixedPanel.None,
                Panel1MinSize = 200,
                Panel2MinSize = 80,
                BackColor = Color.Transparent
            };
            // Fill добавляем первым, edge-доки (Bottom/Top) — после.
            assetsGridLogSplit.Panel1.Padding = new Padding(16, 12, 16, 8);
            assetsGridLogSplit.Panel1.Controls.Add(assetsModuleAssetsGrid);
            assetsGridLogSplit.Panel1.Controls.Add(assetsModuleProgressBar);
            assetsGridLogSplit.Panel1.Controls.Add(topStack);
            assetsGridLogSplit.Panel2.Controls.Add(assetsModuleLogBox);
            assetsGridLogSplit.HandleCreated += (_, __) =>
            {
                BeginInvoke(new Action(() =>
                {
                    if (assetsGridLogSplit.Height < 240)
                        return;
                    int panel2 = Math.Min(220, Math.Max(assetsGridLogSplit.Panel2MinSize + 40, assetsGridLogSplit.Height / 4));
                    int dist = assetsGridLogSplit.Height - panel2 - assetsGridLogSplit.SplitterWidth;
                    if (dist >= assetsGridLogSplit.Panel1MinSize)
                        assetsGridLogSplit.SplitterDistance = dist;
                }));
            };

            root.Controls.Add(assetsGridLogSplit);
            moduleHostPanel.Controls.Add(root);

            void SyncAssetsModuleLayout()
            {
                if (root == null || root.IsDisposed)
                    return;

                int contentWidth = Math.Max(640, root.ClientSize.Width - 32);

                assetsModuleFolderLabel.MaximumSize = new Size(contentWidth, 0);
                assetsModuleAssetsStatsLabel.MaximumSize = new Size(contentWidth, 0);

                actionsFlow.WrapContents = true;
                actionsFlow.Width = contentWidth;
                if (singleAssetRow != null && !singleAssetRow.IsDisposed)
                {
                    singleAssetRow.WrapContents = true;
                    singleAssetRow.Width = contentWidth;
                }

                if (ttfToTmpFontRow != null && !ttfToTmpFontRow.IsDisposed)
                    ttfToTmpFontRow.Width = contentWidth;
                if (assetsModuleTtfToTmpFontButton != null && !assetsModuleTtfToTmpFontButton.IsDisposed)
                    assetsModuleTtfToTmpFontButton.Width = Math.Max(360, contentWidth - 8);
                if (assetsModulePatchTmpMsdfAtlasButton != null && !assetsModulePatchTmpMsdfAtlasButton.IsDisposed)
                    assetsModulePatchTmpMsdfAtlasButton.Width = Math.Max(360, contentWidth - 8);
                if (assetsModuleReplaceAtlasTexturePngButton != null && !assetsModuleReplaceAtlasTexturePngButton.IsDisposed)
                    assetsModuleReplaceAtlasTexturePngButton.Width = Math.Max(360, contentWidth - 8);
                if (assetsModuleFindResourcesCrcButton != null && !assetsModuleFindResourcesCrcButton.IsDisposed)
                    assetsModuleFindResourcesCrcButton.Width = Math.Max(280, Math.Min(420, contentWidth - 8));

                if (assetsModuleExportSingleAssetButton != null && !assetsModuleExportSingleAssetButton.IsDisposed)
                    assetsModuleExportSingleAssetButton.Width = Math.Max(360, contentWidth - 8);
            }

            root.Resize += (_, __) => SyncAssetsModuleLayout();
            SyncAssetsModuleLayout();

            ApplyTheme();
            Log(mode == UnityToolboxMode.Full
                ? L("Unity .assets module loaded.", "Модуль Unity .assets активирован.")
                : L("Font replacement module loaded (TMP / TTF / MSDF atlas).", "Модуль замены шрифтов (TMP / TTF / атлас MSDF)."));
            TryAutoAttachDummyDll();
            RefreshAssetsModuleFolderLabel();
            RefreshAssetsBrowser();
            UpdateStatus();
        }

        private void BtnPickAssetsGameFolder_Click(object sender, EventArgs e)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = L("Choose the game folder or *_Data (e.g. TooMuchLight_Data).", "Выберите папку игры или каталог *_Data (например TooMuchLight_Data).");
                if (!string.IsNullOrWhiteSpace(lastUnityGameDataFolder) && Directory.Exists(lastUnityGameDataFolder))
                    fbd.SelectedPath = lastUnityGameDataFolder;

                if (fbd.ShowDialog() != DialogResult.OK)
                    return;

                if (LooksLikeUabeaJsonDumpFolder(fbd.SelectedPath))
                {
                    currentFolder = fbd.SelectedPath;
                    SaveSettings();
                    InvalidateJsonTableCache(true);
                    RefreshAssetsModuleFolderLabel();
                    Log(L(
                            "You selected a JSON dump folder — it is set as the working JSON folder. Choose *_Data to browse Unity containers.",
                            "Вы выбрали папку JSON-дампов — она назначена рабочей папкой JSON. Для списка контейнеров укажите папку *_Data."),
                        true);
                    RefreshAssetsBrowser();
                    UpdateStatus();
                    return;
                }

                var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(fbd.SelectedPath);
                if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
                {
                    Log(L("Could not resolve the game data directory.", "Не удалось определить каталог данных игры."), true);
                    return;
                }

                lastUnityGameDataFolder = resolved;
                SaveSettings();
                TryAutoAttachDummyDll();
                RefreshAssetsModuleFolderLabel();
                RefreshAssetsBrowser();
                UpdateStatus();
            }
        }

        private void RefreshAssetsBrowser()
        {
            if (assetsModuleAssetsGrid == null || assetsModuleAssetsGrid.IsDisposed)
                return;

            assetsModuleAssetsGrid.Rows.Clear();

            var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(lastUnityGameDataFolder);
            if (LooksLikeUabeaJsonDumpFolder(lastUnityGameDataFolder))
            {
                if (assetsModuleAssetsStatsLabel != null && !assetsModuleAssetsStatsLabel.IsDisposed)
                    assetsModuleAssetsStatsLabel.Text = L(
                        "This looks like a JSON dump folder, not Unity Data. Use Browse Unity Data and pick *_Data.",
                        "Это папка JSON-дампов UABEA, а не Unity Data. Нажмите «Выбрать Unity Data» и выберите *_Data.");
                return;
            }

            if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
            {
                if (assetsModuleAssetsStatsLabel != null && !assetsModuleAssetsStatsLabel.IsDisposed)
                    assetsModuleAssetsStatsLabel.Text = L(
                        "Containers: choose the Unity Data folder first.",
                        "Контейнеры: сначала укажите папку Unity Data.");
                return;
            }

            var paths = UnityAssetsGameFolderHelper.EnumerateAssetPathsSorted(resolved);
            var levelCount = 0;
            var sharedCount = 0;
            var totalBytes = 0L;

            foreach (var path in paths)
            {
                var name = Path.GetFileName(path);
                var kind = DescribeUnityAssetKind(name);
                if (kind == "Level")
                    levelCount++;
                else if (kind == "Shared")
                    sharedCount++;

                long length = 0;
                try { length = new FileInfo(path).Length; } catch { }
                totalBytes += length;

                var rowIndex = assetsModuleAssetsGrid.Rows.Add(
                    name,
                    LocalizeUnityAssetKindDisplay(kind),
                    FormatBytes(length),
                    DescribeSidecarFiles(path),
                    UnityAssetsGameFolderHelper.MakeRelativePath(resolved, path));
                assetsModuleAssetsGrid.Rows[rowIndex].Tag = path;
            }

            var managed = UnityAssetsGameFolderHelper.ResolveManagedFolder(resolved);
            var il2 = string.IsNullOrWhiteSpace(managed) &&
                       UnityAssetsGameFolderHelper.IsLikelyIl2CppGameDataFolder(resolved);
            if (assetsModuleAssetsStatsLabel != null && !assetsModuleAssetsStatsLabel.IsDisposed)
            {
                string managedPhrase;
                if (!string.IsNullOrWhiteSpace(managed))
                    managedPhrase = L("found", "найдено");
                else if (il2)
                    managedPhrase = L("(IL2CPP, no Managed folder)", "(IL2CPP, без Managed)");
                else
                    managedPhrase = L("not found", "не найдено");

                assetsModuleAssetsStatsLabel.Text =
                    L($"Containers: {paths.Count} files ({FormatBytes(totalBytes)}), levels: {levelCount}, sharedassets: {sharedCount}, Managed DLL: ",
                        $"Контейнеры: {paths.Count} файлов ({FormatBytes(totalBytes)}), уровни: {levelCount}, sharedassets: {sharedCount}, Managed DLL: ")
                    + managedPhrase;
            }
        }

        private static bool LooksLikeUabeaJsonDumpFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return false;

            try
            {
                var assetContainers = UnityAssetsGameFolderHelper.EnumerateAssetPathsSorted(folder, maxFiles: 3);
                if (assetContainers.Count > 0)
                    return false;

                var jsonFiles = Directory.GetFiles(folder, "*.json", SearchOption.TopDirectoryOnly);
                if (jsonFiles.Length < 3)
                    return false;

                return jsonFiles.Any(p =>
                {
                    var name = Path.GetFileNameWithoutExtension(p);
                    return name.Count(c => c == '-') >= 2 ||
                           UabeaJsonPaths.TryParsePathIdFromFilePath(p, out _);
                });
            }
            catch
            {
                return false;
            }
        }

        private string LocalizeUnityAssetKindDisplay(string kind)
        {
            switch (kind ?? "")
            {
                case "Level":
                    return L("Level", "Уровень");
                case "Shared":
                    return L("Shared", "Shared");
                case "Resources":
                    return L("Resources", "Resources");
                case "Managers":
                    return L("Managers", "Менеджеры");
                default:
                    return L("Asset", "Файл");
            }
        }

        private static string DescribeUnityAssetKind(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "Asset";

            if (fileName.StartsWith("level", StringComparison.OrdinalIgnoreCase))
                return "Level";
            if (fileName.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase))
                return "Shared";
            if (fileName.StartsWith("resources", StringComparison.OrdinalIgnoreCase))
                return "Resources";
            if (fileName.StartsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase))
                return "Managers";
            return "Asset";
        }

        private static string DescribeSidecarFiles(string path)
        {
            var names = new List<string>();
            var directResS = path + ".resS";
            if (File.Exists(directResS))
                names.Add(Path.GetFileName(directResS));

            var noExt = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path));
            var resource = noExt + ".resource";
            if (File.Exists(resource))
                names.Add(Path.GetFileName(resource));

            return names.Count == 0 ? "-" : string.Join(", ", names);
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
                return bytes + " B";
            double value = bytes;
            string[] units = { "KB", "MB", "GB" };
            foreach (var unit in units)
            {
                value /= 1024d;
                if (value < 1024d || unit == "GB")
                    return value.ToString(value >= 100 ? "0" : "0.0") + " " + unit;
            }
            return bytes + " B";
        }

        /// <summary>Строка из таблицы контейнеров: полный путь к файлу Unity (*.assets или extensionless levelN).</summary>
        private static bool TryGetSelectedUnityAssetContainerPath(DataGridView grid, out string fullPath)
        {
            fullPath = null;
            if (grid == null || grid.IsDisposed)
                return false;

            try
            {
                DataGridViewRow row = null;
                var selected = grid.SelectedRows;
                if (selected != null && selected.Count > 0)
                {
                    var sr = selected[0];
                    if (sr != null && !sr.IsNewRow)
                        row = sr;
                }

                if (row == null && grid.CurrentRow != null && !grid.CurrentRow.IsNewRow)
                    row = grid.CurrentRow;

                if (row == null)
                    return false;

                if (!(row.Tag is string raw) || string.IsNullOrWhiteSpace(raw))
                    return false;

                if (!UnityAssetsGameFolderHelper.IsUnityAssetContainerPath(raw))
                    return false;

                fullPath = Path.GetFullPath(raw);
                return true;
            }
            catch
            {
                fullPath = null;
                return false;
            }
        }

        private void OpenSelectedAssetInExplorer()
        {
            if (assetsModuleAssetsGrid == null || assetsModuleAssetsGrid.IsDisposed)
                return;

            if (!TryGetSelectedUnityAssetContainerPath(assetsModuleAssetsGrid, out var path))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log(L("Failed to open file in Explorer: ", "Не удалось открыть файл в проводнике: ") + ex.Message, true);
            }
        }

        private void RefreshAssetsModuleFolderLabel()
        {
            if (assetsModuleFolderLabel == null || assetsModuleFolderLabel.IsDisposed)
                return;

            var json = string.IsNullOrWhiteSpace(currentFolder)
                ? L("(not set)", "(не выбрана)")
                : currentFolder;
            var game = string.IsNullOrWhiteSpace(lastUnityGameDataFolder)
                ? L("(not set)", "(не выбрана)")
                : lastUnityGameDataFolder;
            assetsModuleFolderLabel.Text =
                L("Working JSON folder:", "Рабочая папка JSON:") + "\r\n" + json +
                "\r\n\r\n" + L("Unity Data:", "Unity Data:") + "\r\n" + game;
        }

        private void DetachModuleEvents()
        {
            if (btnSelectFolder != null) btnSelectFolder.Click -= BtnSelectFolder_Click;
            if (btnApply != null) btnApply.Click -= BtnApply_Click;
            if (btnExportTxt != null) btnExportTxt.Click -= BtnExportTxt_Click;
            if (btnImportTxt != null) btnImportTxt.Click -= BtnImportTxt_Click;
            if (btnTranslateEmptyApi != null) btnTranslateEmptyApi.Click -= MenuTranslateEmptyViaLocalApi_Click;
            if (btnCancelApiBatchTranslate != null) btnCancelApiBatchTranslate.Click -= BtnCancelApiBatchTranslate_Click;
            if (btnDeleteJsonWithoutText != null) btnDeleteJsonWithoutText.Click -= BtnDeleteJsonWithoutText_Click;
            if (btnCopySelectedAi != null) btnCopySelectedAi.Click -= BtnCopySelectedAi_Click;
            if (btnPasteAi != null) btnPasteAi.Click -= BtnPasteAi_Click;
            if (btnClearLog != null) btnClearLog.Click -= BtnClearLog_Click;
            if (chkBackup != null) chkBackup.CheckedChanged -= ChkBackup_CheckedChanged;
            if (dgv != null) dgv.CellBeginEdit -= Dgv_CellBeginEdit;
            if (dgv != null) dgv.CellEndEdit -= Dgv_CellEndEdit;
        }

        private void AttachModuleEvents()
        {
            if (btnSelectFolder != null) btnSelectFolder.Click += BtnSelectFolder_Click;
            if (btnApply != null) btnApply.Click += BtnApply_Click;
            if (btnExportTxt != null) btnExportTxt.Click += BtnExportTxt_Click;
            if (btnImportTxt != null) btnImportTxt.Click += BtnImportTxt_Click;
            if (btnTranslateEmptyApi != null) btnTranslateEmptyApi.Click += MenuTranslateEmptyViaLocalApi_Click;
            if (btnCancelApiBatchTranslate != null) btnCancelApiBatchTranslate.Click += BtnCancelApiBatchTranslate_Click;
            if (btnDeleteJsonWithoutText != null) btnDeleteJsonWithoutText.Click += BtnDeleteJsonWithoutText_Click;
            if (btnCopySelectedAi != null) btnCopySelectedAi.Click += BtnCopySelectedAi_Click;
            if (btnPasteAi != null) btnPasteAi.Click += BtnPasteAi_Click;
            if (btnClearLog != null) btnClearLog.Click += BtnClearLog_Click;
            if (chkBackup != null) chkBackup.CheckedChanged += ChkBackup_CheckedChanged;
            if (dgv != null) dgv.CellBeginEdit += Dgv_CellBeginEdit;
            if (dgv != null) dgv.CellEndEdit += Dgv_CellEndEdit;
            if (dgv != null) dgv.ColumnHeaderMouseClick += Dgv_ColumnHeaderMouseClick;
        }

        private static readonly object SessionFileLogGate = new object();

        private static string SessionFileLogPath =>
            Path.Combine(ClassPackageDownloader.AppDataAppFolder, "translator.log");

        private static void ResetSessionFileLog()
        {
            try
            {
                Directory.CreateDirectory(ClassPackageDownloader.AppDataAppFolder);
                File.WriteAllText(SessionFileLogPath, string.Empty, new UTF8Encoding(false));
            }
            catch { }
        }

        private static void AppendSessionFileLog(string line)
        {
            lock (SessionFileLogGate)
            {
                try
                {
                    File.AppendAllText(SessionFileLogPath, line, new UTF8Encoding(false));
                }
                catch { }
            }
        }

        private void Log(string msg, bool isError = false)
        {
            if (InvokeRequired)
            {
                try
                {
                    Invoke(new Action<string, bool>(Log), msg, isError);
                }
                catch (ObjectDisposedException) { }

                return;
            }

            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}";
            AppendSessionFileLog(line);

            void AppendBox(RichTextBox box, Color okColor)
            {
                if (box.InvokeRequired)
                {
                    box.Invoke(new Action(() => AppendBox(box, okColor)));
                    return;
                }

                box.SelectionStart = box.TextLength;
                box.SelectionLength = 0;
                box.SelectionColor = isError ? Color.Salmon : okColor;
                box.AppendText(line);
                box.SelectionColor = box.ForeColor;
                box.ScrollToCaret();
            }

            // Сначала журнал модуля Unity .assets: иначе сообщения уходят в скрытый logBox вкладки JSON
            // (он остаётся в памяти при переключении на Toolbox).
            if (assetsModuleLogBox != null && !assetsModuleLogBox.IsDisposed)
            {
                AppendBox(assetsModuleLogBox, assetsModuleLogBox.ForeColor);
                return;
            }

            if (logBox != null && !logBox.IsDisposed)
            {
                AppendBox(logBox, logBox.ForeColor);
                return;
            }

            System.Diagnostics.Debug.WriteLine(msg);
        }

        /// <summary>Снимает «часы» после долгих async-операций: иногда после await остаётся системный курсор загрузки, если не обнулить <see cref="Cursor.Current"/>.</summary>
        private void RestoreUiCursorAfterWait()
        {
            try
            {
                UseWaitCursor = false;
                Cursor = Cursors.Default;
                Cursor.Current = Cursors.Default;
            }
            catch { }
        }

        private static ProgressBar GetActiveProgressBar(ProgressBar assetsPb, ProgressBar jsonPb)
        {
            if (assetsPb != null && !assetsPb.IsDisposed)
                return assetsPb;
            if (jsonPb != null && !jsonPb.IsDisposed)
                return jsonPb;
            return null;
        }

        private void SetAssetsModuleBusy(bool busy)
        {
            if (assetsModuleBuildButton != null && !assetsModuleBuildButton.IsDisposed)
                assetsModuleBuildButton.Enabled = !busy;
            if (assetsModuleFindFontsButton != null && !assetsModuleFindFontsButton.IsDisposed)
                assetsModuleFindFontsButton.Enabled = !busy;
            if (assetsModuleImportTmpFontButton != null && !assetsModuleImportTmpFontButton.IsDisposed)
                assetsModuleImportTmpFontButton.Enabled = !busy;
            if (assetsModuleTtfToTmpFontButton != null && !assetsModuleTtfToTmpFontButton.IsDisposed)
                assetsModuleTtfToTmpFontButton.Enabled = !busy;
            if (assetsModulePatchTmpMsdfAtlasButton != null && !assetsModulePatchTmpMsdfAtlasButton.IsDisposed)
                assetsModulePatchTmpMsdfAtlasButton.Enabled = !busy;
            if (assetsModuleReplaceAtlasTexturePngButton != null && !assetsModuleReplaceAtlasTexturePngButton.IsDisposed)
                assetsModuleReplaceAtlasTexturePngButton.Enabled = !busy;
            if (assetsModuleFindResourcesCrcButton != null && !assetsModuleFindResourcesCrcButton.IsDisposed)
                assetsModuleFindResourcesCrcButton.Enabled = !busy;
            if (assetsModuleDumpPathIdFieldsButton != null && !assetsModuleDumpPathIdFieldsButton.IsDisposed)
                assetsModuleDumpPathIdFieldsButton.Enabled = !busy;
            if (assetsModuleExportButton != null && !assetsModuleExportButton.IsDisposed)
                assetsModuleExportButton.Enabled = !busy;
            if (assetsModuleExportSingleAssetButton != null && !assetsModuleExportSingleAssetButton.IsDisposed)
                assetsModuleExportSingleAssetButton.Enabled = !busy;
            if (assetsModulePickGameFolderButton != null && !assetsModulePickGameFolderButton.IsDisposed)
                assetsModulePickGameFolderButton.Enabled = !busy;
        }

        private void UpdateStatus()
        {
            if (statusLabel == null) return;
            var translatedCount = translationItems.Count(x => !string.IsNullOrWhiteSpace(x.Translated));
            var folderName = string.IsNullOrWhiteSpace(currentFolder)
                ? L("not selected", "не выбрана")
                : Path.GetFileName(currentFolder);
            statusLabel.Text =
                $"{L("Total", "Всего")}: {translationItems.Count} | {L("Translated", "Переведено")}: {translatedCount} | {L("Folder", "Папка")}: {folderName}";
            UpdateProgressStats();
            UpdateSidebarReadyLabel();
        }

        private void SyncGridToItems()
        {
            if (dgv == null || dgv.IsDisposed)
                return;

            // переносим «Перевод» из ячейки в ЕЁ элемент (row.Tag): строка жёстко связана с элементом → пара та, что видит
            // пользователь, сдвиг невозможен даже при разошедшемся порядке грида/списка (сортировка/частичная заливка)
            foreach (DataGridViewRow row in dgv.Rows)
            {
                if (row.IsNewRow)
                    continue;
                var item = RowItem(row);
                if (item == null)
                    continue;
                item.Translated = row.Cells["Translated"].Value?.ToString() ?? "";
            }

            UpdateStatus();
        }

        private void Dgv_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e == null || e.Button != MouseButtons.Left)
                return;
            if (e.ColumnIndex < 0 || dgv == null || e.ColumnIndex >= dgv.Columns.Count)
                return;
            SortJsonTableByColumn(e.ColumnIndex);
        }

        /// <summary>
        /// Программная сортировка: переставляет САМ список <see cref="translationItems"/> и перезаливает грид (Tag проставляется заново).
        /// Связь строка↔элемент на Tag, не на индексах → подсветка/undo/импорт/ИИ адресуют верно в любом порядке. Повторный клик инвертирует.
        /// </summary>
        private void SortJsonTableByColumn(int columnIndex)
        {
            if (dgv == null || dgv.IsDisposed || translationItems.Count == 0)
                return;

            var newOrder = (_jsonSortColumn == columnIndex && _jsonSortOrder == SortOrder.Ascending)
                ? SortOrder.Descending
                : SortOrder.Ascending;

            // Снимаем незакоммиченные правки ячеек в список, пока индексы ещё согласованы.
            SyncGridToItems();

            Func<TranslationItem, string> key;
            switch (columnIndex)
            {
                case 0: key = it => it.FileName ?? ""; break;
                case 1: key = it => it.DisplayPath ?? ""; break;
                case 2: key = it => it.Original ?? ""; break;
                default: key = it => it.Translated ?? ""; break;
            }

            var cmp = StringComparer.CurrentCultureIgnoreCase;
            // OrderBy/OrderByDescending — стабильная сортировка: одинаковые ключи сохраняют исходный порядок.
            var sorted = (newOrder == SortOrder.Ascending
                ? translationItems.OrderBy(key, cmp)
                : translationItems.OrderByDescending(key, cmp)).ToList();

            translationItems.Clear();
            translationItems.AddRange(sorted);

            _jsonSortColumn = columnIndex;
            _jsonSortOrder = newOrder;

            PopulateJsonGridRowsFast();

            for (int c = 0; c < dgv.Columns.Count; c++)
                dgv.Columns[c].HeaderCell.SortGlyphDirection = c == columnIndex ? newOrder : SortOrder.None;

            ApplyTableSearch(); // вернуть видимость строк под активный фильтр поиска
            UpdateRowHighlights();
            UpdateProgressStats();
            UpdateStatus();
        }

        private void ClearTranslationUndoStack() => _translationUndoFrames.Clear();

        private void PushTranslationUndoFrame(List<TranslationUndoCell> frame)
        {
            if (frame == null || frame.Count == 0)
                return;
            MarkJsonDirty();
            _translationUndoFrames.Add(frame);
            while (_translationUndoFrames.Count > MaxTranslationUndoFrames)
                _translationUndoFrames.RemoveAt(0);
        }

        private static bool IsTranslationUndoHotkeyContext(Control active, DataGridView grid)
        {
            if (grid == null || grid.IsDisposed)
                return false;
            for (var c = active; c != null; c = c.Parent)
            {
                if (c == grid)
                    return true;
            }
            return false;
        }

        private bool TryUndoLastTranslationEdit()
        {
            if (_translationUndoFrames.Count == 0 || dgv == null || dgv.IsDisposed)
                return false;

            var frame = _translationUndoFrames[_translationUndoFrames.Count - 1];
            _translationUndoFrames.RemoveAt(_translationUndoFrames.Count - 1);

            _suppressTranslationUndoRecording = true;
            try
            {
                foreach (var cell in frame)
                {
                    if (cell.Item == null)
                        continue;
                    cell.Item.Translated = cell.PreviousTranslated ?? "";
                    int r = RowIndexOfItem(cell.Item);
                    if (r >= 0)
                        dgv.Rows[r].Cells["Translated"].Value = cell.PreviousTranslated ?? "";
                }

                ApplyTableSearch();
                UpdateRowHighlights();
                UpdateStatus();
                return true;
            }
            finally
            {
                _suppressTranslationUndoRecording = false;
            }
        }

        private void Dgv_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            if (_suppressTranslationUndoRecording || dgv == null || dgv.IsDisposed)
                return;
            if (e.RowIndex < 0 || e.ColumnIndex < 0)
                return;
            var colName = dgv.Columns[e.ColumnIndex].Name;
            if (colName == "File" || colName == "Path" || colName == "Original")
            {
                if (e.RowIndex < dgv.Rows.Count)
                {
                    _mainGridReadOnlyPreviewBackup = dgv.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
                    _mainGridReadOnlyPreviewActive = true;
                }

                return;
            }

            if (colName != "Translated")
                return;
            var editItem = RowItemAt(e.RowIndex);
            if (editItem == null)
                return;
            _translatedEditStartRow = e.RowIndex;
            _translatedEditStartValue = editItem.Translated ?? "";
        }

        /// <summary>Подставляет переводы из memory.json для строк с пустым переводом (точное совпадение по оригиналу).</summary>
        private int ApplyTranslationMemoryFromStore()
        {
            var mem = TranslationMemory.Load();
            if (mem.Count == 0)
                return 0;
            if (dgv == null || dgv.IsDisposed)
                return 0;

            int n = 0;
            var undoFrame = new List<TranslationUndoCell>();
            for (int i = 0; i < translationItems.Count; i++)
            {
                var item = translationItems[i];
                if (string.IsNullOrEmpty(item.Original))
                    continue;
                if (!string.IsNullOrWhiteSpace(item.Translated))
                    continue;
                if (!mem.TryGetValue(item.Original, out var tr))
                    continue;
                // Не подставляем заведомо сдвинутый мусор (число↔текст), даже если он есть в памяти.
                if (TranslationMemory.IsLikelyShiftCorruptedPair(item.Original, tr))
                    continue;
                undoFrame.Add(new TranslationUndoCell { Item = item, PreviousTranslated = item.Translated ?? "" });
                item.Translated = tr;
                n++;
            }

            if (undoFrame.Count > 0)
                PushTranslationUndoFrame(undoFrame);

            RefreshTranslatedColumnFromItems();

            return n;
        }

        private void BtnApplyTranslationMemory_Click(object sender, EventArgs e)
        {
            if (translationItems.Count == 0)
            {
                Log(L("No data.", "Нет данных."), true);
                return;
            }

            if (!RequireJsonTranslatorSurface("память переводов"))
                return;

            SyncGridToItems();

            if (!File.Exists(TranslationMemory.MemoryFilePath))
            {
                Log(L("Translation memory file doesn't exist yet. It appears after «Paste into JSON» with TM enabled, or add pairs manually.", "Файл памяти переводов ещё не создан. Он появится после «Вставить в JSON» с включённой TM или можно добавить pairs вручную."), true);
                return;
            }

            int n = ApplyTranslationMemoryFromStore();
            ApplyTableSearch();
            UpdateRowHighlights();
            UpdateStatus();

            if (n == 0)
                Log(L("TM: no original matches for empty translation cells.", "TM: нет совпадений по оригиналу для пустых ячеек перевода."), true);
            else
            {
                Log(L($"TM: filled translations from memory: {n}.", $"TM: подставлено переводов из памяти: {n}."));
                BumpDashboardContentStamp();
            }
        }

        private void Dgv_CellEndEdit(object sender, DataGridViewCellEventArgs e)
        {
            if (dgv != null && !dgv.IsDisposed && e.RowIndex >= 0 && e.ColumnIndex >= 0 &&
                e.RowIndex < dgv.Rows.Count)
            {
                var colName = dgv.Columns[e.ColumnIndex].Name;
                if ((colName == "File" || colName == "Path" || colName == "Original") &&
                    _mainGridReadOnlyPreviewActive)
                {
                    dgv.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = _mainGridReadOnlyPreviewBackup;
                    _mainGridReadOnlyPreviewActive = false;
                    _mainGridReadOnlyPreviewBackup = null;
                }
                else if (!_suppressTranslationUndoRecording &&
                         colName == "Translated" &&
                         e.RowIndex == _translatedEditStartRow &&
                         RowItemAt(e.RowIndex) is TranslationItem editedItem)
                {
                    var newVal = dgv.Rows[e.RowIndex].Cells["Translated"].Value?.ToString() ?? "";
                    if (!string.Equals(newVal, _translatedEditStartValue, StringComparison.Ordinal))
                    {
                        PushTranslationUndoFrame(new List<TranslationUndoCell>
                        {
                            new TranslationUndoCell { Item = editedItem, PreviousTranslated = _translatedEditStartValue ?? "" }
                        });
                    }
                }
            }

            _translatedEditStartRow = -1;
            _translatedEditStartValue = null;
            SyncGridToItems();
            ApplyTableSearch();
        }

        private async void BtnSelectFolder_Click(object sender, EventArgs e)
        {
            using (var fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Выберите папку с JSON файлами Unity";
                if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
                    fbd.SelectedPath = currentFolder;

                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    currentFolder = fbd.SelectedPath;
                    RememberRecentFolder(currentFolder);
                    Log(L("Folder selected: ", "Выбрана папка: ") + currentFolder);
                    lastJsonExtractFolder = "";

                    if (dgv != null && !dgv.IsDisposed)
                    {
                        translationItems.Clear();
                        dgv.Rows.Clear();
                        UpdateStatus();
                        await ExtractTextsAsync();
                    }
                    else
                    {
                        translationItems.Clear();
                        Log(L("Folder saved. Open JSON Files — the table fills from JSON automatically.", "Папка сохранена. Откройте JSON Files — таблица заполнится из JSON автоматически."));
                        UpdateStatus();
                    }
                }
            }
        }

        private async Task ExtractTextsAsync()
        {
            if (string.IsNullOrEmpty(currentFolder))
            {
                Log(L("Select a folder first!", "Сначала выберите папку!"), true);
                return;
            }

            if (!Directory.Exists(currentFolder))
            {
                Log(L($"JSON folder not found: {currentFolder}", $"Папка JSON не найдена: {currentFolder}"), true);
                return;
            }

            if (!IsJsonTranslatorSurfaceHosted || dgv == null || dgv.IsDisposed)
            {
                Log(L("Open the JSON Files section.", "Откройте раздел JSON Files."), true);
                return;
            }

            await _extractTextsAsyncGate.WaitAsync().ConfigureAwait(true);
            try
            {
            ClearTranslationUndoStack();

            var capturedProgress = progressBar;
            if (capturedProgress != null && !capturedProgress.IsDisposed)
            {
                capturedProgress.Visible = true;
                capturedProgress.Value = 0;
            }
            Log(L("Extracting texts...", "Извлечение текстов..."));

            var scannedJsonTotal = new[] { 0 };
            await Task.Run(() =>
            {
                translationItems.Clear();
                string[] files;
                try
                {
                    files = Directory.GetFiles(currentFolder, "*.json", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    Log(L($"Failed to enumerate JSON: {ex.Message}", $"Не удалось перечислить JSON: {ex.Message}"), true);
                    return;
                }

                int total = files.Length;
                scannedJsonTotal[0] = total;

                // парсинг файлов независим, JToken.Parse — CPU: гоним параллельно. Каждый пишет в СВОЙ список
                // (ExtractStrings не потокобезопасен), сливаем по индексу файла → порядок строк как при последовательном проходе.
                var perFile = new List<TranslationItem>[total];
                int processed = 0;
                int lastPostedPercent = -1;

                Parallel.For(
                    0,
                    total,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) },
                    i =>
                    {
                        var file = files[i];
                        var local = new List<TranslationItem>();
                        try
                        {
                            var json = File.ReadAllText(file);
                            var root = JToken.Parse(json);
                            ExtractStrings(root, new List<string>(), Path.GetFileName(file), local);
                        }
                        catch (Exception ex)
                        {
                            Log(L($"Error {Path.GetFileName(file)}: {ex.Message}", $"Ошибка {Path.GetFileName(file)}: {ex.Message}"), true);
                        }
                        perFile[i] = local;

                        // Прогресс маршалим в UI-поток ТОЛЬКО при смене целого процента (≤100 раз на весь
                        // проход) — прежний BeginInvoke на каждый файл заваливал UI-поток тысячами сообщений.
                        int done = Interlocked.Increment(ref processed);
                        if (total > 0)
                        {
                            int pct = Math.Max(0, Math.Min(100, (int)((double)done / total * 100)));
                            if (Interlocked.Exchange(ref lastPostedPercent, pct) != pct)
                            {
                                SafeMarshalAction(() =>
                                {
                                    if (capturedProgress != null && !capturedProgress.IsDisposed)
                                        capturedProgress.Value = pct;
                                });
                            }
                        }
                    });

                // Слияние в порядке файлов = тот же порядок, что давал последовательный проход.
                translationItems.Capacity = Math.Max(translationItems.Capacity, perFile.Sum(p => p?.Count ?? 0));
                foreach (var local in perFile)
                    if (local != null && local.Count > 0)
                        translationItems.AddRange(local);
            }).ConfigureAwait(true);

            lastDashboardJsonScanFolder = currentFolder ?? "";
            lastDashboardJsonScanTotal = scannedJsonTotal[0];

            if (dgv == null || dgv.IsDisposed)
                return;

            PopulateJsonGridRowsFast();
            ApplyTableSearch();
            UpdateRowHighlights();

            var mem = TranslationMemory.Load();
            int tmOffer = CountTranslationMemoryMatches(mem);
            int tmFilled = 0;
            if (tmOffer > 0)
            {
                var offer = MessageBox.Show(this,
                    "В памяти переводов найдено совпадений по оригиналу (пустые ячейки «Перевод»): " + tmOffer + ".\r\n\r\nПрименить память переводов сейчас?",
                    "Память переводов",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (offer == DialogResult.Yes)
                {
                    tmFilled = ApplyTranslationMemoryFromStore();
                    if (tmFilled > 0)
                    {
                        ApplyTableSearch();
                        UpdateRowHighlights();
                    }
                }
            }

            var fileCount = translationItems.Select(x => x.FileName).Distinct().Count();
            Log(tmFilled > 0
                ? L($"Extracted {translationItems.Count} rows from {fileCount} JSON files. TM: filled from memory {tmFilled}.",
                    $"Извлечено {translationItems.Count} строк из {fileCount} JSON-файлов. TM: подставлено из памяти {tmFilled}.")
                : L($"Extracted {translationItems.Count} rows from {fileCount} JSON files.",
                    $"Извлечено {translationItems.Count} строк из {fileCount} JSON-файлов."));

            if (tmFilled > 0 && translationItems.Count > 0)
            {
                var p = (int)Math.Round(100.0 * tmFilled / translationItems.Count);
                Log(L($"[INFO] TM matches: {tmFilled} ({p}% of rows)", $"[INFO] TM совпадений: {tmFilled} ({p}% строк)"));
            }

            if (capturedProgress != null && !capturedProgress.IsDisposed)
                capturedProgress.Visible = false;
            lastJsonExtractFolder = currentFolder ?? "";
            BumpDashboardContentStamp();
            UpdateStatus();
            }
            finally
            {
                _extractTextsAsyncGate.Release();
            }
        }

        private void SafeMarshalAction(Action action)
        {
            if (action == null)
                return;

            try
            {
                if (this.IsDisposed || !this.IsHandleCreated)
                    return;

                if (this.InvokeRequired)
                {
                    this.BeginInvoke(action);
                }
                else
                {
                    action();
                }
            }
            catch { }
        }

        private async void BtnApply_Click(object sender, EventArgs e)
        {
            if (!RequireJsonTranslatorSurface("сохранение изменений"))
                return;

            if (translationItems.Count == 0) { Log(L("No data.", "Нет данных."), true); return; }

            SyncGridToItems();
            int count = translationItems.Count(x => !string.IsNullOrWhiteSpace(x.Translated));
            if (count == 0) { Log(L("No filled translations!", "Нет заполненных переводов!"), true); return; }

            btnApply.Enabled = false;
            progressBar.Visible = true;
            progressBar.Value = 0;
            Log(L($"Writing {count} translations to JSON files...", $"Запись {count} переводов в JSON файлы..."));

            int updated = 0;
            await Task.Run(() =>
            {
                updated = WriteAllTranslationsToJson((processed, total) =>
                {
                    try
                    {
                        this.Invoke((Action)(() =>
                            progressBar.Value = total > 0 ? (int)((double)processed / total * 100) : 100));
                    }
                    catch { }
                });
            });

            ClearJsonDirty();
            Log(L($"Updated {updated} rows in JSON. Files are ready for Unity.", $"Обновлено {updated} строк в JSON. Файлы готовы для Unity."));

            if (useTranslationMemory)
            {
                try
                {
                    var pairs = translationItems
                        .Where(x => !string.IsNullOrEmpty(x.Original) && !string.IsNullOrWhiteSpace(x.Translated))
                        .Select(x => new KeyValuePair<string, string>(x.Original, x.Translated.Trim()))
                        .ToList();

                    TranslationMemory.SaveMerge(pairs);
                    Log(L($"TM: memory updated ({pairs.Count} pairs → memory.json).", $"TM: память обновлена ({pairs.Count} пар → memory.json)."));
                }
                catch (Exception ex)
                {
                    Log(L($"TM: failed to save memory: {ex.Message}", $"TM: не удалось сохранить память: {ex.Message}"), true);
                }
            }

            btnApply.Enabled = true;
            progressBar.Visible = false;
            UpdateStatus();
            BumpDashboardContentStamp();
        }

        /// <summary>Сообщает пользователю как подменить контейнер в сборке — Unity не загружает имя *.translated.assets.</summary>
        private void LogTranslationAssetsDeploymentHint(string sourceAssetsPath, string builtOutputPath, int companionResourceFilesCopied = 0)
        {
            try
            {
                bool inPlace;
                try
                {
                    inPlace = string.Equals(
                        Path.GetFullPath(sourceAssetsPath ?? ""),
                        Path.GetFullPath(builtOutputPath ?? ""),
                        StringComparison.OrdinalIgnoreCase);
                }
                catch { inPlace = false; }

                // Запись поверх оригинала: переименовывать ничего не нужно, .resS не трогается — без граблей.
                if (inPlace)
                {
                    string name = Path.GetFileName(sourceAssetsPath);
                    Log(L(
                        "[INFO] Saved in place over «" + name + "» — nothing to rename, just launch the game. The original is backed up as «" + name + ".utt-orig». The companion «.resS/.resource» keeps its name, so textures/audio streaming stays intact.",
                        "[INFO] Записано поверх «" + name + "» — переименовывать ничего не нужно, просто запускайте игру. Оригинал в бэкапе «" + name + ".utt-orig». Парный «.resS/.resource» сохраняет имя, поэтому стриминг текстур/звука не ломается."));
                    return;
                }

                string orig = Path.GetFileName(sourceAssetsPath);
                string built = Path.GetFileName(builtOutputPath);
                string dataDir = Path.GetDirectoryName(Path.GetFullPath(sourceAssetsPath));
                Log(L(
                    "[INFO] For the game to see the translation: make a copy of the original file «" + orig + "», then replace it with the built file «" + built + "», renaming it exactly to «" + orig + "» (in the same folder, currently «" + dataDir + "»). The engine only looks for container names that were already in the release (*.translated.assets is not picked up by itself). The «level0» (or other) folder with exported JSON is for the translator only — don't put it into the game.",
                    "[INFO] Чтобы игра видела перевод: сделайте копию оригинального файла «" + orig + "», затем замените его собранным файлом «" + built + "», переименовав его точно в «" + orig + "» (в том же каталоге, сейчас это «" + dataDir + "»). Движок ищет только те имена контейнеров, что уже были в релизе (*.translated.assets сам по себе не подхватывается). Папка «level0» или другая с JSON из экспорта нужна только переводчику, в игру её класть не нужно."));

                var sidecars = DescribeSidecarFiles(sourceAssetsPath);
                if (companionResourceFilesCopied > 0)
                {
                    Log(L(
                        "[INFO] Companion resource files (.resS / .resource) were copied next to the output using the originals from the game. When renaming/replacing, keep the same basename on both files (e.g. level1 + level1.resS — both or neither).",
                        "[INFO] Побочные файлы ресурсов (.resS / .resource) скопированы рядом с результатом из оригинала игры. При подмене переименовывайте парой: тот же «корень» имени у основного контейнера и у .resS/.resource (например level1 и level1.resS — оба файла актуальной пары вместе)."), false);
                }
                else if (!string.Equals(sidecars, "-", StringComparison.Ordinal))
                {
                    Log(L(
                        "[WARN] This container shares data with companion file(s): " + sidecars
                        + ". After import we could not mirror them automatically (missing read access or uncommon layout). Rewriting only the main file can break streaming until you restore matching .resS/.resource beside the rebuilt container.",
                        "[WARN] Рядом с контейнером есть сопоставленные файлы: " + sidecars
                        + ". Побочники автоматически не созданы — пересобранный только основной файл может ломать поток данных, пока рядом нет парного оригинального .resS/.resource с тем же префиксом имени, что у выхода."), true);
                }
            }
            catch
            {
                Log(L("[INFO] After building, rename the output .assets to the exact name of the source container file in the *_Data folder and replace the original (with a backup).", "[INFO] После сборки переименуйте выходной .assets в точное имя исходного файла контейнера в каталоге *_Data и замените им оригинал (с бэкапом)."), false);
            }
        }

        private async void BtnImportAsset_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder))
            {
                Log(L("Export JSON first — it sets the working JSON folder.", "Сначала выполните экспорт JSON — он задаёт рабочую папку JSON."), true);
                return;
            }

            await ClassPackageDownloader.EnsureClassDataPresentAsync(msg => Log(msg)).ConfigureAwait(true);

            var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(lastUnityGameDataFolder);
            if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
            {
                Log(L("Browse Unity Data first.", "Сначала выберите Unity Data."), true);
                return;
            }

            var paths = UnityAssetsGameFolderHelper.EnumerateAssetPathsSorted(resolved);
            if (paths.Count == 0)
            {
                Log(L("No Unity containers in this Data folder.", "В этой папке данных нет контейнеров Unity."), true);
                return;
            }

            string assetsPath = null;
            if (assetsModuleAssetsGrid != null && !assetsModuleAssetsGrid.IsDisposed &&
                TryGetSelectedUnityAssetContainerPath(assetsModuleAssetsGrid, out var gpPath))
            {
                assetsPath = gpPath;
            }

            if (string.IsNullOrWhiteSpace(assetsPath) &&
                !UnityAssetsGameFolderHelper.TryPickAssetsFile(this, resolved, out assetsPath))
                return;

            // по умолчанию пишем ПОВЕРХ оригинала (in-place): текстуры/потоки жёстко ссылаются на исходное имя
            // (resources.assets.resS), поэтому «*.translated.assets» + переименованный .resS = грабли (magenta).
            // In-place не меняет имя и не требует переименований; оригинал бэкапится в «<имя>.utt-orig». В диалоге можно задать другое имя.
            var isLevelContainer = UnityAssetsGameFolderHelper.LooksLikeStreamingSceneLevelContainer(assetsPath);

            var outputPath = assetsPath;

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = L("Save modified .assets (default: overwrite the original)", "Сохранить изменённый .assets (по умолчанию — поверх оригинала)");
                sfd.InitialDirectory = Path.GetDirectoryName(assetsPath);
                // Имя по умолчанию = имя оригинала → запись in-place, парный .resS остаётся корректным.
                // Без авто-расширения, чтобы «resources.assets» не превратился в «resources.assets.assets».
                sfd.AddExtension = false;
                sfd.FileName = Path.GetFileName(assetsPath);
                sfd.Filter = isLevelContainer
                    ? L("All files (*.*)|*.*", "Все файлы (*.*)|*.*")
                    : L("Unity assets (*.assets)|*.assets|All files (*.*)|*.*", "Unity assets (*.assets)|*.assets|Все файлы (*.*)|*.*");

                if (sfd.ShowDialog() != DialogResult.OK)
                    return;

                outputPath = sfd.FileName;
            }

            // Сохранение поверх оригинала (типично для level-файлов: имя должно совпадать) — делаем
            // одноразовый бэкап «<имя>.utt-orig», чтобы оригинал можно было вернуть и перевести заново.
            try
            {
                var sameAsSource =
                    string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(assetsPath),
                        StringComparison.OrdinalIgnoreCase);
                if (sameAsSource)
                {
                    var backupPath = assetsPath + ".utt-orig";
                    if (!File.Exists(backupPath))
                    {
                        File.Copy(assetsPath, backupPath);
                        Log(L(
                            "[Backup] Original saved as «" + Path.GetFileName(backupPath) + "» (delete it to re-create on next overwrite).",
                            "[Бэкап] Оригинал сохранён как «" + Path.GetFileName(backupPath) + "» (удалите его, чтобы пересоздать при следующей перезаписи)."));
                    }
                }
            }
            catch (Exception ex)
            {
                Log(L("[Backup] Could not back up the original: ", "[Бэкап] Не удалось сохранить копию оригинала: ") + ex.Message, true);
            }

            var pb = GetActiveProgressBar(assetsModuleProgressBar, progressBar);

            SetAssetsModuleBusy(true);

            if (pb != null)
            {
                pb.Visible = true;
                pb.Style = ProgressBarStyle.Marquee;
            }

            Log(L("Merging UABEA JSON into .assets… (Esc — cancel)", "Импорт UABEA JSON обратно в .assets… (Esc — отмена)"));

            var token = BeginNewAssetsWorkCancellation();
            try
            {
                var result = await Task.Run(() => UabeaJsonAssetImporter.ImportFolder(assetsPath, currentFolder, outputPath, resolved, token), token).ConfigureAwait(true);
                Log(L(
                    $"Done: imported {result.Imported} of {result.JsonFound} JSON. File: {Path.GetFileName(outputPath)}",
                    $"Готово: импортировано {result.Imported} из {result.JsonFound} JSON. Файл: {Path.GetFileName(outputPath)}"));

                foreach (var message in result.Messages.Take(12))
                    Log(message, result.Failed > 0);

                if (result.Messages.Count > 12)
                    Log(L($"More messages: {result.Messages.Count - 12}", $"Ещё сообщений: {result.Messages.Count - 12}"));

                LogTranslationAssetsDeploymentHint(assetsPath, outputPath, result.CompanionResourceFilesCopied);
            }
            catch (OperationCanceledException)
            {
                Log(L("Import cancelled.", "Импорт отменён."));
            }
            catch (Exception ex)
            {
                Log(L($"Could not build .assets: {ex.Message}", $"Не удалось собрать .assets: {ex.Message}"), true);
            }
            finally
            {
                EndAssetsWorkCancellation();

                if (pb != null)
                {
                    pb.Style = ProgressBarStyle.Continuous;
                    pb.Visible = false;
                }

                SetAssetsModuleBusy(false);

                UpdateStatus();
            }
        }

        private async void BtnExportFromAssets_Click(object sender, EventArgs e)
        {
            const bool monoOnly = true;
            const bool skipGg = true;

            await ClassPackageDownloader.EnsureClassDataPresentAsync(msg => Log(msg)).ConfigureAwait(true);

            var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(lastUnityGameDataFolder);
            if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
            {
                Log(L("Browse Unity Data first (folder with level0, sharedassets…).", "Сначала укажите Unity Data (level0, sharedassets…)."), true);
                return;
            }

            var paths = UnityAssetsGameFolderHelper.EnumerateAssetPathsSorted(resolved);
            if (paths.Count == 0)
            {
                Log(L(
                        "No .assets containers found — wrong folder, or this is a JSON dump instead of *_Data.",
                        "Не найдено контейнеров .assets — возможно, указана не папка *_Data или это папка JSON."),
                    true);
                return;
            }

            using (var fbdOut = new FolderBrowserDialog())
            {
                fbdOut.Description = L("Folder for exported JSON", "Папка для экспортированных JSON");
                if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
                    fbdOut.SelectedPath = currentFolder;

                if (fbdOut.ShowDialog() != DialogResult.OK)
                    return;

                var exportDir = fbdOut.SelectedPath;
                var fileLayout = ReadAssetsExportLayout();
                var pb = GetActiveProgressBar(assetsModuleProgressBar, progressBar);

                SetAssetsModuleBusy(true);
                if (pb != null)
                {
                    pb.Visible = true;
                    pb.Style = ProgressBarStyle.Marquee;
                }

                Log(L(
                        "Exporting MonoBehaviour objects from all .assets… (Esc — cancel)",
                        "Экспорт всех MonoBehaviour из всех .assets в папке игры… (Esc — отмена)")
                    + $"\r\n{L("JSON name layout:", "Режим имён JSON:")} {DescribeAssetsExportLayout(fileLayout)}.");

                var token = BeginNewAssetsWorkCancellation();
                try
                {
                    var result = await Task.Run(() =>
                            UabeaJsonAssetExporter.ExportEntireGameDataFolder(resolved, exportDir, monoOnly, fileLayout, skipGg, token))
                        .ConfigureAwait(true);
                    currentFolder = exportDir;
                    SaveSettings();
                    InvalidateJsonTableCache(true);

                    RefreshAssetsModuleFolderLabel();

                    if (!string.IsNullOrWhiteSpace(result.ManagedAssembliesFolder))
                        Log(L("MonoBehaviour fields via Managed DLL: ", "MonoBehaviour поля читаются через Managed DLL: ") +
                            result.ManagedAssembliesFolder);
                    else if (result.Messages.Any(m => m != null && m.StartsWith("[MonoCecil]", StringComparison.Ordinal)))
                        Log(L(
                                "Managed folder is present but Mono.Cecil could not start — see the next log lines for the error.",
                                "Каталог Managed найден, но Mono.Cecil не запустился — смотрите следующие строки лога с текстом ошибки."),
                            true);
                    else
                        Log(L(
                                "Managed DLL not wired — MonoBehaviour export uses base fields only.",
                                "Managed DLL не подключены: MonoBehaviour будут экспортироваться только с базовыми полями (нет полей скрипта)."),
                            true);

                    if (!string.IsNullOrWhiteSpace(result.UnityVersion))
                        Log("Unity .assets: " + result.UnityVersion + "; classdata.tpk: " +
                            (result.ClassDatabaseLoaded
                                ? L("loaded", "загружена")
                                : L("NOT LOADED", "НЕ ЗАГРУЖЕНА")));

                    Log(L(
                            $"Export: scanned {result.AssetFilesScanned} .assets; wrote {result.Exported} JSON ({result.Failed} errors, {result.TotalCandidates} candidates). Working JSON folder updated.",
                            $"Экспорт: файлов .assets обработано: {result.AssetFilesScanned}; записано {result.Exported} JSON (ошибок {result.Failed}, объектов в выборке {result.TotalCandidates}). Папка JSON назначена как рабочая."));

                    foreach (var message in result.Messages.Take(12))
                        Log(message, result.Failed > 0);

                    if (result.Messages.Count > 12)
                        Log(L($"More messages: {result.Messages.Count - 12}", $"Ещё сообщений: {result.Messages.Count - 12}"));
                }
                catch (OperationCanceledException)
                {
                    Log(L("Export cancelled.", "Экспорт отменён."));
                }
                catch (Exception ex)
                {
                    Log(L($"Export failed: {ex.Message}", $"Экспорт не удался: {ex.Message}"), true);
                }
                finally
                {
                    EndAssetsWorkCancellation();

                    if (pb != null)
                    {
                        pb.Style = ProgressBarStyle.Continuous;
                        pb.Visible = false;
                    }

                    SetAssetsModuleBusy(false);
                    UpdateStatus();
                }
            }
        }

        private async void BtnExportSelectedAssetJson_Click(object sender, EventArgs e)
        {
            const bool monoOnly = true;

            if (assetsModuleAssetsGrid == null || assetsModuleAssetsGrid.IsDisposed)
            {
                Log(L("Select a row in the container list.", "Выберите строку в списке контейнеров."), true);
                return;
            }

            if (!TryGetSelectedUnityAssetContainerPath(assetsModuleAssetsGrid, out var assetPath))
            {
                Log(L(
                        "Pick a row for a Unity asset container (level0, sharedassets, resources.assets… — levels may have no .assets extension).",
                        "Выберите строку с контейнером Unity (level0, sharedassets, resources.assets… — у level файлов часто нет расширения .assets)."),
                    true);
                return;
            }

            if (string.IsNullOrWhiteSpace(lastUnityGameDataFolder) || !Directory.Exists(lastUnityGameDataFolder))
            {
                Log(L("Browse Unity Data first (folder with Managed/, level0…).", "Сначала укажите Unity Data (папка с Managed/, level0…)."), true);
                return;
            }

            await ClassPackageDownloader.EnsureClassDataPresentAsync(msg => Log(msg)).ConfigureAwait(true);

            using (var fbdOut = new FolderBrowserDialog())
            {
                fbdOut.Description = L("Folder for JSON from this asset container", "Папка для JSON из этого контейнера");
                if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
                    fbdOut.SelectedPath = currentFolder;

                if (fbdOut.ShowDialog() != DialogResult.OK)
                    return;

                var exportDir = fbdOut.SelectedPath;
                var fileLayout = ReadAssetsExportLayout();
                var pb = GetActiveProgressBar(assetsModuleProgressBar, progressBar);

                SetAssetsModuleBusy(true);
                if (pb != null)
                {
                    pb.Visible = true;
                    pb.Style = ProgressBarStyle.Marquee;
                }

                Log(L($"Exporting JSON from «{Path.GetFileName(assetPath)}»… (Esc — cancel)", $"Экспорт JSON из «{Path.GetFileName(assetPath)}»… (Esc — отмена)"));

                var token = BeginNewAssetsWorkCancellation();
                try
                {
                    var result = await Task.Run(() =>
                            UabeaJsonAssetExporter.ExportToFolder(assetPath, exportDir, monoOnly, lastUnityGameDataFolder, fileLayout, token))
                        .ConfigureAwait(true);

                    currentFolder = exportDir;
                    SaveSettings();
                    InvalidateJsonTableCache(true);
                    RefreshAssetsModuleFolderLabel();

                    if (!string.IsNullOrWhiteSpace(result.ManagedAssembliesFolder))
                        Log(L("Managed DLL folder: ", "Каталог Managed DLL: ") + result.ManagedAssembliesFolder);
                    else if (result.Messages.Any(m => m != null && m.StartsWith("[MonoCecil]", StringComparison.Ordinal)))
                        Log(L(
                                "Managed folder is present but Mono.Cecil could not start — see the next log lines.",
                                "Каталог Managed есть, но Mono.Cecil не запустился — смотрите строки лога ниже."),
                            true);
                    else
                        Log(L(
                                "Managed DLL folder not used — MonoBehaviour script fields may be missing in JSON.",
                                "Каталог Managed не подключён — в JSON могут не попасть поля скриптов MonoBehaviour."),
                            true);

                    Log(L(
                            $"Done: wrote {result.Exported} JSON ({result.Failed} errors). Name layout: {DescribeAssetsExportLayout(fileLayout)}. Working JSON folder updated.",
                            $"Готово: записано {result.Exported} JSON (ошибок {result.Failed}). Режим имён: {DescribeAssetsExportLayout(fileLayout)}. Папка JSON назначена рабочей."));

                    foreach (var message in result.Messages.Take(12))
                        Log(message, result.Failed > 0);

                    if (result.Messages.Count > 12)
                        Log(L($"More messages: {result.Messages.Count - 12}", $"Ещё сообщений: {result.Messages.Count - 12}"));
                }
                catch (OperationCanceledException)
                {
                    Log(L("Export cancelled.", "Экспорт отменён."));
                }
                catch (Exception ex)
                {
                    Log(L($"Export failed: {ex.Message}", $"Экспорт не удался: {ex.Message}"), true);
                }
                finally
                {
                    EndAssetsWorkCancellation();

                    if (pb != null)
                    {
                        pb.Style = ProgressBarStyle.Continuous;
                        pb.Visible = false;
                    }

                    SetAssetsModuleBusy(false);
                    UpdateStatus();
                }
            }
        }

        private async void BtnFindFonts_Click(object sender, EventArgs e)
        {
            if (assetsModuleAssetsGrid == null || assetsModuleAssetsGrid.IsDisposed)
            {
                Log(L("Select a row in the container list.", "Выберите строку в списке контейнеров."), true);
                return;
            }

            if (!TryGetSelectedUnityAssetContainerPath(assetsModuleAssetsGrid, out var assetsPath))
            {
                Log(L("Pick resources.assets (or another container) first.", "Сначала выберите resources.assets (или другой контейнер)."), true);
                return;
            }

            await ClassPackageDownloader.EnsureClassDataPresentAsync(msg => Log(msg)).ConfigureAwait(true);

            var classDataPath = ClassPackageDownloader.ClassDataPath;
            if (!File.Exists(classDataPath))
            {
                Log(L("classdata.tpk not found.", "classdata.tpk не найден."), true);
                return;
            }

            var pb = GetActiveProgressBar(assetsModuleProgressBar, progressBar);
            SetAssetsModuleBusy(true);
            if (pb != null)
            {
                pb.Visible = true;
                pb.Style = ProgressBarStyle.Marquee;
            }

            Log(L($"Scanning fonts in «{Path.GetFileName(assetsPath)}»…", $"Поиск шрифтов в «{Path.GetFileName(assetsPath)}»…"));

            try
            {
                var lines = await Task.Run(() =>
                {
                    var outLines = new List<string>();
                    var manager = new AssetsManager();
                    manager.LoadClassPackage(classDataPath);
                    var afileInst = manager.LoadAssetsFile(assetsPath, true);
                    manager.LoadClassDatabaseFromPackage(afileInst.file.Metadata.UnityVersion);

                    var count = 0;
                    foreach (var info in afileInst.file.AssetInfos)
                    {
                        if (info.Stripped != 0)
                            continue;

                        var typeId = info.GetTypeId(afileInst.file);
                        var isFont = typeId == (int)AssetClassID.Font;
                        var isTmpFont = false;

                        AssetTypeValueField baseField;
                        try
                        {
                            baseField = manager.GetBaseField(afileInst, info);
                        }
                        catch
                        {
                            continue;
                        }

                        if (!isFont && typeId == (int)AssetClassID.MonoBehaviour)
                        {
                            var scriptClass = MonoBehaviourScriptResolver.TryGetMonoScriptShortClassName(
                                manager, afileInst, baseField, AssetReadFlags.None);
                            isTmpFont = string.Equals(scriptClass, "TMP_FontAsset", StringComparison.Ordinal);
                        }

                        if (!isFont && !isTmpFont)
                            continue;

                        string name;
                        try { name = baseField["m_Name"]?.AsString ?? ""; }
                        catch { name = ""; }

                        var typeName = isFont ? "Font" : "TMP_FontAsset";
                        outLines.Add($"Font found: PathID={info.PathId}, name={name}, type={typeName}");
                        count++;
                    }

                    outLines.Insert(0, L(
                        $"Found font assets: {count}",
                        $"Найдено шрифтовых ассетов: {count}"));
                    return outLines;
                }).ConfigureAwait(true);

                foreach (var line in lines)
                    Log(line);
            }
            catch (Exception ex)
            {
                Log(L($"Font scan failed: {ex.Message}", $"Поиск шрифтов не удался: {ex.Message}"), true);
            }
            finally
            {
                if (pb != null)
                {
                    pb.Style = ProgressBarStyle.Continuous;
                    pb.Visible = false;
                }
                SetAssetsModuleBusy(false);
                UpdateStatus();
            }
        }

        private static bool TryReadAssetRawBytes(AssetsFileInstance fileInst, AssetFileInfo info, out byte[] bytes)
        {
            bytes = null;
            if (fileInst?.file?.Reader == null || info == null)
                return false;
            try
            {
                var pos = info.GetAbsoluteByteOffset(fileInst.file);
                var len = checked((int)info.ByteSize);
                if (len <= 0)
                    return false;
                var r = fileInst.file.Reader;
                r.Position = pos;
                bytes = r.ReadBytes(len);
                return bytes != null && bytes.Length == len;
            }
            catch
            {
                bytes = null;
                return false;
            }
        }

        private static int FindBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || needle.Length > haystack.Length)
                return -1;

            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var ok = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        ok = false;
                        break;
                    }
                }
                if (ok)
                    return i;
            }
            return -1;
        }

        private static string TryResolveResourcesAssetsPath(string dataDir, string selectedPath)
        {
            if (!string.IsNullOrWhiteSpace(selectedPath) &&
                string.Equals(Path.GetFileName(selectedPath), "resources.assets", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(selectedPath))
                return Path.GetFullPath(selectedPath);

            var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(dataDir);
            if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
                return null;

            var direct = Path.Combine(resolved, "resources.assets");
            if (File.Exists(direct))
                return direct;

            return UnityAssetsGameFolderHelper.EnumerateAssetPathsSorted(resolved, maxFiles: 2000)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), "resources.assets", StringComparison.OrdinalIgnoreCase));
        }

        private static string TryResolveGlobalGameManagersPath(string dataDir)
        {
            var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(dataDir);
            if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
                return null;

            return UnityAssetsGameFolderHelper.EnumerateAssetPathsSorted(resolved, maxFiles: 2000)
                .FirstOrDefault(p =>
                {
                    var name = Path.GetFileName(p);
                    return name != null && name.StartsWith("globalgamemanagers", StringComparison.OrdinalIgnoreCase);
                });
        }

        private static void ScanPathIdAcrossAllAssetContainers(string dataDir, long pathId, ICollection<string> lines)
        {
            if (lines == null)
                return;

            var resolved = UnityAssetsGameFolderHelper.ResolveGameDataFolder(dataDir);
            if (string.IsNullOrWhiteSpace(resolved) || !Directory.Exists(resolved))
            {
                lines.Add("[PathID scan] *_Data не найден — общий поиск по контейнерам пропущен.");
                return;
            }

            var assetPaths = UnityAssetsGameFolderHelper.EnumerateAssetPathsSorted(resolved, maxFiles: 2000);
            if (assetPaths.Count == 0)
            {
                lines.Add("[PathID scan] В " + resolved + " не найдено контейнеров Unity.");
                return;
            }

            lines.Add("[PathID scan] Поиск PathID=" + pathId + " во всех контейнерах: " + resolved);

            var hits = 0;
            var failed = 0;
            var manager = new AssetsManager();
            foreach (var f in assetPaths)
            {
                AssetsFileInstance inst = null;
                try
                {
                    inst = manager.LoadAssetsFile(f, false);
                    var info = inst.file.AssetInfos.FirstOrDefault(x => x.PathId == pathId);
                    if (info != null)
                    {
                        lines.Add("[PathID scan] PathID=" + pathId + " найден в: " + f);
                        hits++;
                    }
                }
                catch
                {
                    failed++;
                }
                finally
                {
                    if (inst != null)
                    {
                        try { manager.UnloadAssetsFile(inst); }
                        catch { }
                    }
                }
            }

            if (hits == 0)
                lines.Add("[PathID scan] PathID=" + pathId + " не найден ни в одном контейнере.");
            else
                lines.Add("[PathID scan] Совпадений: " + hits + ".");

            if (failed > 0)
                lines.Add("[PathID scan] Не удалось прочитать контейнеров: " + failed + ".");
        }

        private async void BtnFindResourcesAssetsCrc_Click(object sender, EventArgs e)
        {
            if (assetsModuleAssetsGrid == null || assetsModuleAssetsGrid.IsDisposed)
            {
                Log(L("Select a row in the container list.", "Выберите строку в списке контейнеров."), true);
                return;
            }
            if (!TryGetSelectedUnityAssetContainerPath(assetsModuleAssetsGrid, out var selectedAssetPath))
            {
                Log(L("Pick target .assets first.", "Сначала выберите целевой .assets."), true);
                return;
            }

            var pb = GetActiveProgressBar(assetsModuleProgressBar, progressBar);
            SetAssetsModuleBusy(true);
            if (pb != null)
            {
                pb.Visible = true;
                pb.Style = ProgressBarStyle.Marquee;
            }

            try
            {
                var lines = await Task.Run(() =>
                {
                    var outLines = new List<string>();
                    var dataDir = !string.IsNullOrWhiteSpace(lastUnityGameDataFolder)
                        ? UnityAssetsGameFolderHelper.ResolveGameDataFolder(lastUnityGameDataFolder)
                        : UnityAssetsGameFolderHelper.TryFindParentGameDataFolder(selectedAssetPath);

                    var resourcesPath = TryResolveResourcesAssetsPath(dataDir, selectedAssetPath);
                    if (string.IsNullOrWhiteSpace(resourcesPath) || !File.Exists(resourcesPath))
                        throw new FileNotFoundException("resources.assets not found", resourcesPath ?? "(null)");

                    var ggmPath = TryResolveGlobalGameManagersPath(dataDir);
                    if (string.IsNullOrWhiteSpace(ggmPath) || !File.Exists(ggmPath))
                        throw new FileNotFoundException("globalgamemanagers not found", ggmPath ?? "(null)");

                    outLines.Add("[CRC] resources.assets path = " + resourcesPath);
                    outLines.Add("[CRC] globalgamemanagers path = " + ggmPath);

                    var resourceBytes = File.ReadAllBytes(resourcesPath);
                    var crc = Crc32Utility.Compute(resourceBytes);
                    outLines.Add("[CRC] resources.assets CRC32 = 0x" + crc.ToString("X8") + " (" + crc + ")");

                    var copyPath = Path.Combine(
                        Path.GetDirectoryName(resourcesPath) ?? ".",
                        Path.GetFileNameWithoutExtension(resourcesPath) + ".md5_copy.assets");
                    File.Copy(resourcesPath, copyPath, true);
                    using (var md5 = MD5.Create())
                    {
                        var origHash = BitConverter.ToString(md5.ComputeHash(resourceBytes));
                        var copyHash = BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(copyPath)));
                        outLines.Add("[MD5] original: " + origHash);
                        outLines.Add("[MD5] copy:     " + copyHash);
                        outLines.Add("[MD5] match: " + (origHash == copyHash));
                    }

                    var ggmBytes = File.ReadAllBytes(ggmPath);
                    var crcBytes = BitConverter.GetBytes(crc);
                    var pos = FindBytes(ggmBytes, crcBytes);
                    outLines.Add("[CRC] Найден в globalgamemanagers.assets: " + pos);
                    if (pos >= 0)
                        outLines.Add("[CRC] offset(hex) = 0x" + pos.ToString("X"));

                    return outLines;
                }).ConfigureAwait(true);

                foreach (var line in lines)
                    Log(line);
            }
            catch (Exception ex)
            {
                Log(L($"CRC search failed: {ex.Message}", $"Поиск CRC не удался: {ex.Message}"), true);
            }
            finally
            {
                if (pb != null)
                {
                    pb.Style = ProgressBarStyle.Continuous;
                    pb.Visible = false;
                }
                SetAssetsModuleBusy(false);
                UpdateStatus();
            }
        }

        private static List<(long PathId, string Name)> ScanDonorTmpFontCandidates(string classDataPath, string donorAssetsPath)
        {
            var manager = new AssetsManager();
            manager.LoadClassPackage(classDataPath);
            var donorInst = manager.LoadAssetsFile(donorAssetsPath, true);
            manager.LoadClassDatabaseFromPackage(donorInst.file.Metadata.UnityVersion);

            var candidates = new List<(long PathId, string Name)>();
            foreach (var info in donorInst.file.AssetInfos)
            {
                if (info.Stripped != 0 || info.GetTypeId(donorInst.file) != (int)AssetClassID.MonoBehaviour)
                    continue;
                AssetTypeValueField baseField;
                try { baseField = manager.GetBaseField(donorInst, info); }
                catch { continue; }
                var cls = MonoBehaviourScriptResolver.TryGetMonoScriptShortClassName(
                    manager, donorInst, baseField, AssetReadFlags.None);
                if (!string.Equals(cls, "TMP_FontAsset", StringComparison.Ordinal))
                    continue;
                string name;
                try { name = baseField["m_Name"]?.AsString ?? ""; } catch { name = ""; }
                candidates.Add((info.PathId, name));
            }

            return candidates;
        }

        private static void DonorTmpBytesWriteToTarget(
            string classDataPath,
            string targetAssetsPath,
            long targetPathId,
            string donorAssetsPath,
            long donorPathId,
            string outputPath)
        {
            var manager = new AssetsManager();
            manager.LoadClassPackage(classDataPath);

            var targetInst = manager.LoadAssetsFile(targetAssetsPath, true);
            manager.LoadClassDatabaseFromPackage(targetInst.file.Metadata.UnityVersion);
            var targetInfo = targetInst.file.GetAssetInfo(targetPathId);
            if (targetInfo == null)
                throw new InvalidOperationException("Target PathID not found: " + targetPathId);

            var donorInst = manager.LoadAssetsFile(donorAssetsPath, true);
            manager.LoadClassDatabaseFromPackage(donorInst.file.Metadata.UnityVersion);
            var donorInfo = donorInst.file.GetAssetInfo(donorPathId);
            if (donorInfo == null)
                throw new InvalidOperationException("Donor PathID not found: " + donorPathId);

            if (!TryReadAssetRawBytes(donorInst, donorInfo, out var donorBytes))
                throw new InvalidOperationException("Failed reading donor raw bytes.");

            targetInfo.SetNewData(donorBytes);
            using (var writer = new AssetsFileWriter(outputPath) { BigEndian = false })
                targetInst.file.Write(writer);
        }

        private bool TryAskTargetPathId(out long targetPathId, long defaultPathId = 34196)
        {
            targetPathId = defaultPathId;
            using (var dlg = new Form())
            {
                dlg.Text = L("Target PathID", "Целевой PathID");
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = dlg.MaximizeBox = false;
                dlg.ShowInTaskbar = false;
                dlg.ClientSize = new Size(360, 126);

                var lbl = new Label
                {
                    AutoSize = true,
                    Location = new Point(12, 12),
                    Text = L("PathID to replace in target .assets:", "PathID для замены в целевом .assets:")
                };
                var tb = new TextBox
                {
                    Location = new Point(12, 36),
                    Width = 334,
                    Text = defaultPathId.ToString(CultureInfo.InvariantCulture)
                };
                var ok = new Button
                {
                    DialogResult = DialogResult.OK,
                    Text = L("OK", "ОК"),
                    Location = new Point(176, 78),
                    Width = 80
                };
                var cancel = new Button
                {
                    DialogResult = DialogResult.Cancel,
                    Text = L("Cancel", "Отмена"),
                    Location = new Point(266, 78),
                    Width = 80
                };
                dlg.Controls.Add(lbl);
                dlg.Controls.Add(tb);
                dlg.Controls.Add(ok);
                dlg.Controls.Add(cancel);
                dlg.AcceptButton = ok;
                dlg.CancelButton = cancel;

                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return false;

                if (!long.TryParse((tb.Text ?? "").Trim(), out targetPathId))
                {
                    MessageBox.Show(
                        L("PathID must be an integer.", "PathID должен быть целым числом."),
                        L("Invalid PathID", "Некорректный PathID"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return false;
                }
            }

            return true;
        }

        private async void BtnDumpPathIdFields_Click(object sender, EventArgs e)
        {
            if (assetsModuleAssetsGrid == null || assetsModuleAssetsGrid.IsDisposed)
            {
                Log(L("Select a row in the container list.", "Выберите строку в списке контейнеров."), true);
                return;
            }
            if (!TryGetSelectedUnityAssetContainerPath(assetsModuleAssetsGrid, out var assetsPath))
            {
                Log(L("Pick target .assets first.", "Сначала выберите целевой .assets."), true);
                return;
            }

            const long defaultPathId = 7295L;
            if (!TryAskTargetPathId(out var pathId, defaultPathId))
                return;

            await ClassPackageDownloader.EnsureClassDataPresentAsync(msg => Log(msg)).ConfigureAwait(true);
            var classDataPath = ClassPackageDownloader.ClassDataPath;
            if (!File.Exists(classDataPath))
            {
                Log(L("classdata.tpk not found.", "classdata.tpk не найден."), true);
                return;
            }

            var pb = GetActiveProgressBar(assetsModuleProgressBar, progressBar);
            SetAssetsModuleBusy(true);
            if (pb != null)
            {
                pb.Visible = true;
                pb.Style = ProgressBarStyle.Marquee;
            }

            try
            {
                var lines = new List<string>();
                await Task.Run(() =>
                {
                    var gameDataRoot = !string.IsNullOrWhiteSpace(lastUnityGameDataFolder)
                        ? UnityAssetsGameFolderHelper.ResolveGameDataFolder(lastUnityGameDataFolder)
                        : UnityAssetsGameFolderHelper.TryFindParentGameDataFolder(assetsPath);
                    ScanPathIdAcrossAllAssetContainers(gameDataRoot, pathId, lines);

                    var manager = new AssetsManager();
                    manager.LoadClassPackage(classDataPath);
                    var inst = manager.LoadAssetsFile(assetsPath, true);
                    manager.LoadClassDatabaseFromPackage(inst.file.Metadata.UnityVersion);

                    var monoRoot = gameDataRoot;
                    if (!string.IsNullOrWhiteSpace(monoRoot))
                    {
                        if (UnityAssetsGameFolderHelper.TryAttachMonoCecilTemplateGenerator(manager, monoRoot, out _, lines))
                            lines.Add("[TMP fields] MonoCecil: поля скрипта доступны.");
                        else
                            lines.Add("[TMP fields] " + UnityAssetsGameFolderHelper.GetManagedUnavailableExportHint(monoRoot));
                    }
                    else
                        lines.Add("[TMP fields] *_Data не найден — дамп может быть без полей TMP_FontAsset.");

                    AssetMonoBehaviourFirstLevelDumper.DumpTopLevelFieldsToMessages(
                        manager, inst, pathId, requireTmpFontAsset: true, lines);
                }).ConfigureAwait(true);

                foreach (var line in lines)
                    Log(line);
            }
            catch (Exception ex)
            {
                Log(L($"PathID field dump failed: {ex.Message}", $"Дамп полей PathID не удался: {ex.Message}"), true);
            }
            finally
            {
                if (pb != null)
                {
                    pb.Style = ProgressBarStyle.Continuous;
                    pb.Visible = false;
                }
                SetAssetsModuleBusy(false);
                UpdateStatus();
            }
        }


        private TranslationTxtFormat ReadTranslationTxtFormat()
        {
            switch (NormalizeTxtFormatIndex(jsonTxtFormatSelectedIndex))
            {
                case 1:
                    return TranslationTxtFormat.TabDelimited;
                case 2:
                    return TranslationTxtFormat.CsvComma;
                case 3:
                    return TranslationTxtFormat.OriginalOnlyLines;
                default:
                    return TranslationTxtFormat.PipeColumns;
            }
        }

        private void ApplyTxtFormatToStoredIndex(TranslationTxtFormat format)
        {
            jsonTxtFormatSelectedIndex = format == TranslationTxtFormat.TabDelimited ? 1
                : format == TranslationTxtFormat.CsvComma ? 2
                : format == TranslationTxtFormat.OriginalOnlyLines ? 3
                : 0;
            SaveSettings();
        }

        private static int NormalizeTxtFormatIndex(int idx)
        {
            if (idx < 0 || idx > 3)
                return 0;
            return idx;
        }

        private static int NormalizeJsonCopyModeIndex(int idx)
        {
            if (idx < 0 || idx > 1)
                return 1;
            return idx;
        }

        /// <summary>Подтягивает режим из открытых настроек, чтобы экспорт/копирование совпадали с тем, что видит пользователь.</summary>
        private void SyncJsonCopyModeFromSettingsUiIfAvailable()
        {
            if (settingsJsonCopyModeCombo != null && !settingsJsonCopyModeCombo.IsDisposed)
                jsonCopyModeSelectedIndex = NormalizeJsonCopyModeIndex(settingsJsonCopyModeCombo.SelectedIndex);
        }

        private int CountTranslationMemoryMatches(Dictionary<string, string> mem)
        {
            if (mem == null || mem.Count == 0)
                return 0;
            int n = 0;
            foreach (var item in translationItems)
            {
                if (string.IsNullOrEmpty(item.Original))
                    continue;
                if (!string.IsNullOrWhiteSpace(item.Translated))
                    continue;
                if (mem.TryGetValue(item.Original, out var tr) &&
                    !TranslationMemory.IsLikelyShiftCorruptedPair(item.Original, tr))
                    n++;
            }
            return n;
        }

        private static HashSet<string> BuildSkipKeysIncludingMetadataOnly()
        {
            var h = new HashSet<string>(SkipKeys, StringComparer.OrdinalIgnoreCase);
            h.UnionWith(MetadataOnlyJsonKeys);
            return h;
        }

        private UabeaJsonFileLayout ReadAssetsExportLayout() =>
            UabeaJsonFileLayout.UabeaMonoScriptNameFlat;

        private string DescribeAssetsExportLayout(UabeaJsonFileLayout layout)
        {
            switch (layout)
            {
                case UabeaJsonFileLayout.UabeaMonoScriptNameFlat:
                    return L("UABEA-style (MonoScript-container-PathID)", "как UABEA (MonoScript-контейнер-PathID)");
                case UabeaJsonFileLayout.SubfolderPathIdOnly:
                    return L("subfolder per .assets", "подпапка на каждый .assets");
                case UabeaJsonFileLayout.FlatTypeDashPathId:
                    return L("flat with type id", "плоско с type id");
                default:
                    return L("flat name-PathID", "плоско имя-PathID");
            }
        }

        private void BtnExportTxt_Click(object sender, EventArgs e)
        {
            if (!RequireJsonTranslatorSurface("экспорт TXT"))
                return;

            SyncGridToItems();
            SyncJsonCopyModeFromSettingsUiIfAvailable();
            if (translationItems.Count == 0) { Log(L("No data.", "Нет данных."), true); return; }

            var txtFmt = ReadTranslationTxtFormat();

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "Экспорт таблицы перевода";
                sfd.Filter = TranslationTxtExchange.CombinedSaveFilter();
                sfd.FilterIndex = TranslationTxtExchange.SaveFilterIndexFromFormat(txtFmt);
                sfd.DefaultExt = TranslationTxtExchange.DefaultExtensionForFormat(txtFmt);
                sfd.FileName = TranslationTxtExchange.SuggestedExportFileName(txtFmt);
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    var chosen = TranslationTxtExchange.ResolveFormatAfterDialog(sfd.FileName, sfd.FilterIndex);
                    ApplyTxtFormatToStoredIndex(chosen);
                    var withOriginal = translationItems.Where(x => !string.IsNullOrWhiteSpace(x.Original)).ToList();
                    var exportItems = withOriginal.Where(ShouldIncludeByJsonCopyMode).ToList();

                    if (exportItems.Count == 0)
                    {
                        Log(L("No rows to export after applying the copy rules.", "После применения правил копирования нет строк для экспорта."), true);
                        return;
                    }

                    using (var writer = new StreamWriter(sfd.FileName, false, System.Text.Encoding.UTF8))
                    {
                        if (chosen == TranslationTxtFormat.OriginalOnlyLines)
                            TranslationTxtExchange.WriteOriginalOnlyAiPreamble(writer, sourceLanguageDisplay, targetLanguageDisplay);
                        else
                            TranslationTxtExchange.WritePreamble(writer, chosen);
                        foreach (var item in exportItems)
                            TranslationTxtExchange.WriteRow(writer, chosen, item.FileName, item.DisplayPath, item.Original, item.Translated);
                    }
                    var skippedEmpty = translationItems.Count - withOriginal.Count;
                    var skippedRules = Math.Max(0, withOriginal.Count - exportItems.Count);
                    var skipped = skippedEmpty + skippedRules;
                    var modeName = NormalizeJsonCopyModeIndex(jsonCopyModeSelectedIndex) == 1
                        ? "копировать всё"
                        : "по правилам";
                    if (skipped > 0)
                    {
                        var detail = skippedRules > 0 && skippedEmpty > 0
                            ? $" ({skippedRules} по правилам, {skippedEmpty} без текста в «Оригинале»)"
                            : skippedRules > 0
                                ? $" ({skippedRules} по правилам)"
                                : $" ({skippedEmpty} без текста в «Оригинале»)";
                        Log(L($"Exported ({chosen}): {sfd.FileName}. Mode: {modeName}. Skipped: {skipped}.{detail}", $"Экспортировано ({chosen}): {sfd.FileName}. Режим: {modeName}. Пропущено: {skipped}.{detail}"));
                    }
                    else
                        Log(L($"Exported ({chosen}): {sfd.FileName}. Mode: {modeName}.", $"Экспортировано ({chosen}): {sfd.FileName}. Режим: {modeName}."));
                }
            }
        }

        /// <summary>
        /// Импорт «только оригинал»: строка i блока → «Перевод» той же строки таблицы, что при экспорте (непустой «Оригинал», тот же режим копирования).
        /// Есть маркер <see cref="TranslationTxtExchange.OriginalOnlyDataBeginMarker"/> — берём только ниже него, иначе весь файл. Пустая строка = пустой перевод.
        /// </summary>
        private void ImportOriginalOnlyLinesFromFile(string[] rawLines, TranslationTxtFormat formatTagForLog)
        {
            SyncJsonCopyModeFromSettingsUiIfAvailable();
            var exportOrder = translationItems
                .Where(x => !string.IsNullOrWhiteSpace(x.Original))
                .Where(ShouldIncludeByJsonCopyMode)
                .ToList();

            if (exportOrder.Count == 0)
            {
                Log(L("No rows to match: the table has no non-empty «Original», or nothing to import after JSON-mode filters.", "Нет строк для сопоставления: в таблице нет непустого «Оригинала» или после фильтров режима JSON нечего импортировать."), true);
                return;
            }

            var lines = TranslationTxtExchange.ExtractOriginalOnlyPayloadLines(rawLines);
            var paired = Math.Min(exportOrder.Count, lines.Count);
            var undoImport = new List<TranslationUndoCell>();

            for (var i = 0; i < paired; i++)
            {
                // exportOrder — это ссылки из translationItems, поэтому пишем перевод прямо в элемент.
                var item = exportOrder[i];
                undoImport.Add(new TranslationUndoCell { Item = item, PreviousTranslated = item.Translated ?? "" });
                item.Translated = lines[i];
            }

            if (undoImport.Count > 0)
                PushTranslationUndoFrame(undoImport);

            RefreshTranslatedColumnFromItems();

            ApplyTableSearch();
            UpdateRowHighlights();
            UpdateStatus();

            var extras = lines.Count - paired;
            var shortage = exportOrder.Count - paired;

            if (extras > 0 && shortage > 0)
                Log(L($"Import finished ({formatTagForLog}). Translations written to paired rows: {paired}. Extra rows in file: {extras}; not enough file rows for table rows: {shortage}.", $"Импорт завершён ({formatTagForLog}). Записано переводов в пару строк: {paired}. Лишних строк в файле: {extras}; для строк таблицы не хватило строк в файле: {shortage}."));
            else if (extras > 0)
                Log(L($"Import finished ({formatTagForLog}). Translations written to paired rows: {paired}. Extra rows in file (ignored): {extras}.", $"Импорт завершён ({formatTagForLog}). Записано переводов в пару строк: {paired}. Лишних строк в файле (игнорировано): {extras}."));
            else if (shortage > 0)
                Log(L($"Import finished ({formatTagForLog}). Translations written to paired rows: {paired}. The file lacked rows for {shortage} more table rows (the same ones that line-by-line export would output).", $"Импорт завершён ({formatTagForLog}). Записано переводов в пару строк: {paired}. В файле не хватило строк для ещё {shortage} строк таблицы (те же, что ушли бы в экспорт построчно)."), true);
            else
                Log(L($"Import finished ({formatTagForLog}). Translations written to paired rows: {paired}.", $"Импорт завершён ({formatTagForLog}). Записано переводов в пару строк: {paired}."));
        }

        private void BtnImportTxt_Click(object sender, EventArgs e)
        {
            if (!RequireJsonTranslatorSurface("импорт TXT"))
                return;

            if (translationItems.Count == 0) { Log(L("Extract texts from JSON first.", "Сначала извлеките тексты из JSON."), true); return; }

            var txtFmt = ReadTranslationTxtFormat();

            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Импорт переводов из таблицы";
                ofd.Filter = TranslationTxtExchange.CombinedOpenFilter();
                ofd.FilterIndex = TranslationTxtExchange.SaveFilterIndexFromFormat(txtFmt);
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        SyncGridToItems();
                        var chosen = TranslationTxtExchange.ResolveFormatAfterDialog(ofd.FileName, ofd.FilterIndex);
                        ApplyTxtFormatToStoredIndex(chosen);

                        string[] lines;
                        if (chosen == TranslationTxtFormat.CsvComma)
                        {
                            var fullTextCsv = File.ReadAllText(ofd.FileName, System.Text.Encoding.UTF8);
                            lines = TranslationTxtExchange.EnumerateCsvPhysicalRecordLines(fullTextCsv).ToArray();
                        }
                        else
                            lines = File.ReadAllLines(ofd.FileName, System.Text.Encoding.UTF8);

                        if (chosen == TranslationTxtFormat.OriginalOnlyLines)
                        {
                            ImportOriginalOnlyLinesFromFile(lines, chosen);
                            return;
                        }

                        int matched = 0;
                        var undoImport = new List<TranslationUndoCell>();
                        foreach (var line in lines)
                        {
                            if (line.StartsWith("#", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line))
                                continue;

                            if (!TranslationTxtExchange.TryParseRow(line, chosen, out var fileName, out var displayPath, out var original, out var translated))
                                continue;

                            if (string.IsNullOrWhiteSpace(translated))
                                continue;

                            TranslationItem target = null;
                            foreach (var x in translationItems)
                            {
                                if (x.FileName == fileName && x.DisplayPath == displayPath && x.Original == original)
                                {
                                    target = x;
                                    break;
                                }
                            }

                            if (target == null)
                                continue;

                            undoImport.Add(new TranslationUndoCell { Item = target, PreviousTranslated = target.Translated ?? "" });
                            target.Translated = translated;
                            matched++;
                        }

                        if (undoImport.Count > 0)
                            PushTranslationUndoFrame(undoImport);

                        RefreshTranslatedColumnFromItems();

                        ApplyTableSearch();
                        UpdateRowHighlights();
                        Log(L($"Import finished ({chosen}). Updated {matched} translations.", $"Импорт завершён ({chosen}). Обновлено {matched} переводов."));
                        UpdateStatus();
                    }
                    catch (Exception ex) { Log(L($"Import error: {ex.Message}", $"Ошибка импорта: {ex.Message}"), true); }
                }
            }
        }

        private async void BtnDeleteJsonWithoutText_Click(object sender, EventArgs e)
        {
            await RunDeleteJsonWithoutTextFlowAsync(refreshTranslationGrid: true);
        }

        private async void BtnAssetsDeleteJsonWithoutText_Click(object sender, EventArgs e)
        {
            await RunDeleteJsonWithoutTextFlowAsync(refreshTranslationGrid: false);
        }

        private async Task RunDeleteJsonWithoutTextFlowAsync(bool refreshTranslationGrid)
        {
            if (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder))
            {
                Log(L("Specify a JSON folder first (JSON Files or «JSON dumps folder» in Unity .assets).", "Сначала укажите папку JSON (JSON Files или «Папка JSON-дампов» в Unity .assets)."), true);
                return;
            }

            var folder = currentFolder;
            var confirm = MessageBox.Show(this,
                "Удалить все файлы *.json в этой папке и подпапках, где нет ни одной переводимой строки " +
                "(те же правила, что при заполнении таблицы: игнорируются пустые значения и служебные ключи)?\r\n\r\n" +
                "Файлы с ошибкой разбора JSON не удаляются.\r\nОтменить операцию будет нельзя.",
                "Удаление JSON без текста",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
                return;

            UseWaitCursor = true;
            try
            {
                var stats = await Task.Run(() => DeleteJsonFilesWithNoExtractableStrings(folder)).ConfigureAwait(true);
                Log(L($"Deleted text-less JSON: {stats.Deleted} files; kept (at least one row by rules): {stats.WithText} files; parse errors: {stats.ParseErrors}.", $"Удалено JSON без текста: {stats.Deleted} файлов; не тронуто (есть хотя бы одна строка по правилам): {stats.WithText} файлов; ошибок разбора: {stats.ParseErrors}."));
                if (refreshTranslationGrid)
                    await ExtractTextsAsync();
                UpdateStatus();
                UpdateSidebarReadyLabel();
            }
            catch (Exception ex)
            {
                Log(L($"JSON deletion: {ex.Message}", $"Удаление JSON: {ex.Message}"), true);
            }
            finally
            {
                RestoreUiCursorAfterWait();
            }
        }

        private static (int Deleted, int WithText, int ParseErrors) DeleteJsonFilesWithNoExtractableStrings(string folder)
        {
            var deleted = 0;
            var withText = 0;
            var parseErrors = 0;

            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Не удалось получить список *.json в папке.", ex);
            }

            foreach (var path in files)
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var root = JToken.Parse(json);
                    var dummyPath = new List<string>();
                    var n = CountExtractableStringsInJsonTree(root, dummyPath, SkipKeys, metadataPurgeHeuristics: false, Path.GetFileName(path));
                    // дампы «-resources-N.json» без текста тоже удаляем: оригинальный .assets не трогается, при сборке отсутствующий файл просто не импортируется
                    if (n == 0 && !IsClothInstanceUnityExportJson(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                    else
                        withText++;
                }
                catch
                {
                    parseErrors++;
                }
            }

            return (deleted, withText, parseErrors);
        }

        /// <summary>Меню «Правка»: удаляет все файлы в <see cref="currentFolder"/> (рекурсивно); каталоги не удаляются.</summary>
        private void MenuClearWorkingJsonFolder_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder))
            {
                Log(L("Choose the working JSON folder first (File → Choose folder).", "Сначала выберите рабочую папку JSON (Файл → Выбрать папку)."), true);
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(currentFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                Log(L("Invalid working folder path.", "Некорректный путь к рабочей папке."), true);
                return;
            }

            var root = Path.GetPathRoot(full);
            if (full.Length <= 4 || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            {
                Log(L("Refusing to wipe a drive root or too shallow path.", "Отказ: слишком короткий путь или корень диска."), true);
                return;
            }

            var confirm = MessageBox.Show(this,
                L(
                    "Delete ALL files under the working JSON folder (recursive, irreversible):\n\n" + full +
                    "\n\nSubfolders remain; only files are removed. Continue?",
                    "Удалить ВСЕ файлы в рабочей папке JSON и подпапках (без отката):\n\n" + full +
                    "\n\nСами каталоги не удаляются — только файлы. Продолжить?"),
                L("Clear working folder", "Очистить рабочую папку"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
                return;

            try
            {
                var deleted = 0;
                foreach (var path in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(path);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        Log(L("Skip: ", "Пропуск: ") + path + " — " + ex.Message, true);
                    }
                }

                Log(L($"Working folder cleared: {deleted} file(s) deleted.", $"Рабочая папка очищена: удалено файлов: {deleted}."));
                InvalidateJsonTableCache(true);
                UpdateStatus();
                UpdateSidebarReadyLabel();
            }
            catch (Exception ex)
            {
                Log(L($"Clear folder failed: {ex.Message}", $"Ошибка очистки папки: {ex.Message}"), true);
            }
        }

        private async void BtnDeleteMetadataOnlyJson_Click(object sender, EventArgs e)
        {
            await RunDeleteMetadataOnlyJsonFlowAsync(refreshTranslationGrid: true);
        }

        private async Task RunDeleteMetadataOnlyJsonFlowAsync(bool refreshTranslationGrid)
        {
            if (string.IsNullOrWhiteSpace(currentFolder) || !Directory.Exists(currentFolder))
            {
                Log(L("Specify a JSON folder first (JSON Files or «JSON dumps folder» in Unity .assets).", "Сначала укажите папку JSON (JSON Files или «Папка JSON-дампов» в Unity .assets)."), true);
                return;
            }

            var folder = currentFolder;
            var confirm = MessageBox.Show(this,
                "Удалить все *.json в этой папке и подпапках, где после исключения служебных ключей Unity "
                + "и типичных технических строк (GUID, пути Assets/Packages, имена типов Unity/TMPro/System, строки-с «хвостом» .prefab/.png/.shader и т.п.) "
                + "не остаётся ни одной фразы, которую редактор счёл бы текстом для перевода (названия полей вроде m_Text/description остаются критерием наличия текста)?\r\n\r\n"
                + "Файлы с ошибкой разбора не удаляются. Отменить будет нельзя.",
                "Удаление JSON только метаданные",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes)
                return;

            UseWaitCursor = true;
            try
            {
                var stats = await Task.Run(() => DeleteJsonFilesWithOnlyMetadataStrings(folder)).ConfigureAwait(true);
                Log(L($"Deleted JSON (metadata-only): {stats.Deleted} files; kept (rules still find translatable text): {stats.WithGameplayText} files; parse errors: {stats.ParseErrors}.", $"Удалено JSON (только метаданные): {stats.Deleted} файлов; не тронуто (по правилам всё ещё есть текст для перевода): {stats.WithGameplayText} файлов; ошибок разбора: {stats.ParseErrors}."));
                if (refreshTranslationGrid)
                    await ExtractTextsAsync();
                UpdateStatus();
                UpdateSidebarReadyLabel();
            }
            catch (Exception ex)
            {
                Log(L($"JSON deletion (metadata): {ex.Message}", $"Удаление JSON (метаданные): {ex.Message}"), true);
            }
            finally
            {
                RestoreUiCursorAfterWait();
            }
        }

        private static (int Deleted, int WithGameplayText, int ParseErrors) DeleteJsonFilesWithOnlyMetadataStrings(string folder)
        {
            var deleted = 0;
            var withGameplayText = 0;
            var parseErrors = 0;
            var skip = BuildSkipKeysIncludingMetadataOnly();

            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Не удалось получить список *.json в папке.", ex);
            }

            foreach (var path in files)
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var root = JToken.Parse(json);
                    var dummyPath = new List<string>();
                    var n = CountExtractableStringsInJsonTree(root, dummyPath, skip, metadataPurgeHeuristics: true, Path.GetFileName(path));
                    // дампы «-resources-N.json» без текста тоже удаляем: оригинальный .assets не трогается, при сборке отсутствующий файл просто не импортируется
                    if (n == 0 && !IsClothInstanceUnityExportJson(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                    else
                        withGameplayText++;
                }
                catch
                {
                    parseErrors++;
                }
            }

            return (deleted, withGameplayText, parseErrors);
        }

        /// <summary>Считает строки как <see cref="ExtractStrings"/>, опц. с эвристиками «только метаданные» (GUID/пути Assets//типы Unity не считаются текстом).</summary>
        private static int CountExtractableStringsInJsonTree(JToken token, List<string> currentPath, HashSet<string> skipKeys, bool metadataPurgeHeuristics, string sourceFileName)
        {
            if (token == null)
                return 0;

            if (token.Type == JTokenType.String)
            {
                string value = token.Value<string>();
                var normalized = value?.Trim();
                if (string.IsNullOrWhiteSpace(normalized) || normalized == "\"\"" || normalized == "''")
                    return 0;
                if (!metadataPurgeHeuristics)
                    return 1;
                return CountStringLeafTowardGameplayForMetadataDeletion(currentPath, normalized) ? 1 : 0;
            }

            if (token.Type == JTokenType.Object)
            {
                var sum = 0;
                foreach (JProperty prop in token.Children<JProperty>())
                {
                    if (skipKeys.Contains(prop.Name.Trim()))
                        continue;
                    if (ShouldSkipJsonPropertyForTranslation(currentPath, prop.Name.Trim()))
                        continue;
                    if (ShouldSkipUnityLocaleCodeField(currentPath, prop.Name.Trim()))
                        continue;
                    if (ShouldSkipClothCatalogKeyFields(sourceFileName, prop.Name.Trim()))
                        continue;
                    var newPath = new List<string>(currentPath) { prop.Name };
                    sum += CountExtractableStringsInJsonTree(prop.Value, newPath, skipKeys, metadataPurgeHeuristics, sourceFileName);
                }
                return sum;
            }

            if (token.Type == JTokenType.Array)
            {
                var sum = 0;
                var arr = (JArray)token;
                for (var i = 0; i < arr.Count; i++)
                {
                    var newPath = new List<string>(currentPath) { $"[{i}]" };
                    sum += CountExtractableStringsInJsonTree(arr[i], newPath, skipKeys, metadataPurgeHeuristics, sourceFileName);
                }
                return sum;
            }

            return 0;
        }

        private void BtnClearLog_Click(object sender, EventArgs e)
        {
            if (logBox != null && !logBox.IsDisposed)
                logBox.Clear();
            if (assetsModuleLogBox != null && !assetsModuleLogBox.IsDisposed)
                assetsModuleLogBox.Clear();
        }

        private void ChkBackup_CheckedChanged(object sender, EventArgs e)
        {
            createBackup = chkBackup.Checked;
            SaveSettings();
        }

        /// <summary>Поиск по Enter/кнопке (НЕ живой): фильтрует при смене текста, затем прыгает к совпадению.</summary>
        private void RunJsonTableSearchFromBox()
        {
            if (jsonSearchBox == null || jsonSearchBox.IsDisposed)
                return;

            var text = jsonSearchBox.Text.Trim();
            if (text != currentSearchText)
            {
                currentSearchText = text;
                ApplyTableSearchCore(refreshHighlights: false); // поиск не меняет цвета строк → без перекраски
                UpdateStatus();
            }
            FindNextTableSearchMatch();
        }

        private void ApplyTableSearch()
        {
            if (dgv == null || dgv.IsDisposed)
                return;

            if (!dgv.IsHandleCreated)
            {
                ApplyTableSearchCore();
                return;
            }

            if (applyTableSearchPosted)
                return;

            applyTableSearchPosted = true;
            try
            {
                dgv.BeginInvoke(new Action(ApplyTableSearchPostedRunner));
            }
            catch
            {
                applyTableSearchPosted = false;
                ApplyTableSearchCore();
            }
        }

        private void ApplyTableSearchPostedRunner()
        {
            applyTableSearchPosted = false;
            if (dgv == null || dgv.IsDisposed)
                return;
            ApplyTableSearchCore();
        }

        private void ApplyTableSearchCore(bool refreshHighlights = true)
        {
            if (dgv == null || dgv.IsDisposed)
                return;

            var query = currentSearchText;

            int restoreCol = -1;
            int restoreRow = -1;
            if (dgv.CurrentCell != null)
            {
                restoreCol = dgv.CurrentCell.ColumnIndex;
                restoreRow = dgv.CurrentCell.RowIndex;
            }

            dgv.SuspendLayout();
            SetDgvRedraw(false); // без заморозки каждый row.Visible= перерисовывает грид → O(n²) на большой таблице
            try
            {
                dgv.CurrentCell = null;

                foreach (DataGridViewRow row in dgv.Rows)
                {
                    if (row.IsNewRow)
                        continue;

                    if (RowItem(row) == null)
                        continue;

                    var matchesSearch = string.IsNullOrWhiteSpace(query) || RowContains(row, query);

                    // только при смене — иначе лишний пересчёт грида
                    if (row.Visible != matchesSearch)
                        row.Visible = matchesSearch;
                }

                // фильтр не меняет статус перевода → при поиске перекраска не нужна
                if (refreshHighlights)
                    UpdateRowHighlights();
                UpdateProgressStats();

                if (restoreCol >= 0 && restoreRow >= 0 && restoreRow < dgv.Rows.Count)
                {
                    var row = dgv.Rows[restoreRow];
                    if (row.Visible && !row.IsNewRow && restoreCol < row.Cells.Count)
                        dgv.CurrentCell = row.Cells[restoreCol];
                }
            }
            finally
            {
                SetDgvRedraw(true);
                dgv.ResumeLayout(true);
            }
        }

        private const int WM_SETREDRAW = 0x000B;

        /// <summary>Вкл/выкл перерисовку грида (WM_SETREDRAW); при включении инвалидирует.</summary>
        private void SetDgvRedraw(bool on)
        {
            if (dgv == null || dgv.IsDisposed || !dgv.IsHandleCreated)
                return;
            SendMessage(dgv.Handle, WM_SETREDRAW, on ? 1 : 0, 0);
            if (on)
                dgv.Invalidate();
        }

        private void UpdateRowHighlights()
        {
            if (dgv == null) return;

            // Переведено — зелёный, не переведено — янтарный, «новое после патча» — синий. Явно различимы.
            var translatedBack = isDarkTheme ? Color.FromArgb(28, 48, 38) : Color.FromArgb(226, 245, 232);
            var translatedFore = isDarkTheme ? Color.FromArgb(178, 230, 198) : Color.FromArgb(22, 101, 52);
            var untranslatedBack = isDarkTheme ? Color.FromArgb(54, 43, 28) : Color.FromArgb(254, 243, 222);
            var untranslatedFore = isDarkTheme ? Color.FromArgb(240, 212, 162) : Color.FromArgb(146, 64, 14);
            var newBack = isDarkTheme ? Color.FromArgb(26, 42, 66) : Color.FromArgb(220, 235, 255);
            var newFore = isDarkTheme ? Color.FromArgb(170, 205, 250) : Color.FromArgb(28, 78, 158);

            var translatedSelectionBack = isDarkTheme ? Color.FromArgb(40, 66, 54) : Color.FromArgb(205, 235, 215);
            var untranslatedSelectionBack = isDarkTheme ? Color.FromArgb(72, 58, 38) : Color.FromArgb(248, 233, 200);
            var newSelectionBack = isDarkTheme ? Color.FromArgb(38, 58, 88) : Color.FromArgb(200, 222, 250);

            foreach (DataGridViewRow row in dgv.Rows)
            {
                if (row.IsNewRow) continue;

                var item = RowItem(row);
                if (item == null) continue;
                var isTranslated = !string.IsNullOrWhiteSpace(item.Translated);
                bool isNew = !isTranslated && _resyncNewItems.Contains(item);

                Color back, fore, selBack;
                if (isNew) { back = newBack; fore = newFore; selBack = newSelectionBack; }
                else if (isTranslated) { back = translatedBack; fore = translatedFore; selBack = translatedSelectionBack; }
                else { back = untranslatedBack; fore = untranslatedFore; selBack = untranslatedSelectionBack; }

                row.DefaultCellStyle.BackColor = back;
                row.DefaultCellStyle.ForeColor = fore;
                row.DefaultCellStyle.SelectionBackColor = selBack;
                row.DefaultCellStyle.SelectionForeColor = fore;
            }
        }

        private bool RowContains(DataGridViewRow row, string query)
        {
            // Матчим по полям item (row.Tag), а не по ячейкам грида — .Value/.ToString() на каждую ячейку дорого.
            var item = RowItem(row);
            if (item == null)
                return false;
            return FieldContains(item.FileName, query)
                || FieldContains(item.DisplayPath, query)
                || FieldContains(item.Original, query)
                || FieldContains(item.Translated, query);
        }

        private static bool FieldContains(string text, string query) =>
            !string.IsNullOrEmpty(text) && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;

        private void UpdateProgressStats()
        {
            if (progressStatsLabel == null) return;

            var total = translationItems.Count;
            var translated = translationItems.Count(x => !string.IsNullOrWhiteSpace(x.Translated));
            var remaining = Math.Max(0, total - translated);
            var percent = total == 0 ? 0 : (int)Math.Round(translated * 100d / total);
            var visibleRows = dgv == null ? total : dgv.Rows.Cast<DataGridViewRow>().Count(x => !x.IsNewRow && x.Visible);

            progressStatsLabel.Text =
                $"{L("Progress", "Прогресс")}: {percent}% | {L("Remaining", "Осталось")}: {remaining} | {L("Shown", "Показано")}: {visibleRows}";
        }

        private string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UnityTextTranslator",
            "settings.json");

        /// <summary>Версия формата settings.json; при увеличении можно выполнять одноразовые миграции.</summary>
        private const int CurrentSettingsFormatVersion = 2;

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    isDarkTheme = IsDarkTheme(currentThemeName);
                    appUiLanguage = ResolveOsUiLanguage();
                    return;
                }

                var json = File.ReadAllText(SettingsPath);
                var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                if (settings == null)
                {
                    appUiLanguage = ResolveOsUiLanguage();
                    isDarkTheme = IsDarkTheme(currentThemeName);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(settings.ThemeName))
                    currentThemeName = settings.ThemeName;
                createBackup = settings.CreateBackup;
                junkFeaturesEnabled = settings.JunkFeaturesEnabled;
                autosaveEnabled = settings.AutosaveEnabled;
                currentFolder = settings.LastFolder ?? "";
                lastUnityGameDataFolder = settings.LastUnityGameFolder ?? "";
                bundleLocGameDataFolder = settings.BundleLocGameDataFolder ?? "";
                bundleLocBundlePath = settings.BundleLocBundlePath ?? "";
                bundleLocJsonFolder = settings.BundleLocJsonFolder ?? "";
                bundleLocOutputBundlePath = settings.BundleLocOutputBundlePath ?? "";
                bundleLocLocalesBundlePath = settings.BundleLocLocalesBundlePath ?? "";
                bundleLocLocalesCode = string.IsNullOrWhiteSpace(settings.BundleLocLocalesCode)
                    ? "ru"
                    : settings.BundleLocLocalesCode.Trim();
                bundleLocLocalesOutputPath = settings.BundleLocLocalesOutputPath ?? "";
                if (settings.BundleLocMonoBehaviourOnly.HasValue)
                    bundleLocMonoBehaviourOnlySaved = settings.BundleLocMonoBehaviourOnly.Value;
                if (settings.BundleLocOverwriteSourceAfterBuild.HasValue)
                    bundleLocOverwriteSourceAfterBuildSaved = settings.BundleLocOverwriteSourceAfterBuild.Value;
                useTranslationMemory = settings.UseTranslationMemory ?? true;
                if (!string.IsNullOrWhiteSpace(settings.SourceLanguage))
                    sourceLanguageDisplay = settings.SourceLanguage;
                if (!string.IsNullOrWhiteSpace(settings.TargetLanguage))
                    targetLanguageDisplay = settings.TargetLanguage;
                if (settings.TxtExchangeFormatIndex.HasValue)
                    jsonTxtFormatSelectedIndex = NormalizeTxtFormatIndex(settings.TxtExchangeFormatIndex.Value);
                if (settings.JsonCopyModeIndex.HasValue)
                    jsonCopyModeSelectedIndex = NormalizeJsonCopyModeIndex(settings.JsonCopyModeIndex.Value);
                translationApiEnabled = settings.TranslationApiEnabled;
                if (!string.IsNullOrWhiteSpace(settings.TranslationApiUrl))
                    translationApiUrl = settings.TranslationApiUrl.Trim();
                translationApiKey = settings.TranslationApiKey ?? "";
                if (!string.IsNullOrWhiteSpace(settings.TranslationAiBackend))
                {
                    var kb = settings.TranslationAiBackend.Trim();
                    if (string.Equals(kb, "GeminiOpenAi", StringComparison.OrdinalIgnoreCase))
                        kb = "Gemini";
                    translationAiBackend = LocalTranslateApi.TranslationAiBackendToKey(
                        LocalTranslateApi.ParseTranslationAiBackend(kb));
                }
                else
                {
                    translationAiBackend = LocalTranslateApi.UsesOpenRouter(settings.TranslationApiUrl ?? "")
                        ? "OpenRouter"
                        : "LibreTranslate";
                }

                EnsureTranslationBackendKeyAllowed();
                if (!string.IsNullOrWhiteSpace(settings.TranslationOpenRouterModel))
                    translationOpenRouterModel = settings.TranslationOpenRouterModel.Trim();
                recentJsonFolders.Clear();
                if (settings.RecentJsonFolders != null)
                {
                    foreach (var p in settings.RecentJsonFolders)
                    {
                        if (!string.IsNullOrWhiteSpace(p))
                            recentJsonFolders.Add(p.Trim());
                    }
                }

                if (recentJsonFolders.Count == 0 && !string.IsNullOrWhiteSpace(currentFolder))
                    recentJsonFolders.Add(currentFolder);

                dashboardPanels.Clear();
                if (settings.DashboardPanels != null)
                {
                    foreach (var pn in settings.DashboardPanels)
                    {
                        if (pn == null)
                            continue;
                        dashboardPanels.Add(new DashboardPanelData
                        {
                            Title = pn.Title ?? "",
                            Html = pn.Html ?? ""
                        });
                    }
                }

                lastManualBackupUtc = settings.LastManualBackupUtc;

                if (!string.IsNullOrWhiteSpace(settings.UiLanguage))
                    appUiLanguage = NormalizeUiLanguageCode(settings.UiLanguage);
                else
                    appUiLanguage = ResolveOsUiLanguage();

                if (settings.WindowX.HasValue && settings.WindowY.HasValue &&
                    settings.WindowWidth.HasValue && settings.WindowHeight.HasValue)
                {
                    int w = Math.Max(960, settings.WindowWidth.Value);
                    int h = Math.Max(620, settings.WindowHeight.Value);
                    savedWindowX = settings.WindowX.Value;
                    savedWindowY = settings.WindowY.Value;
                    savedWindowWidth = w;
                    savedWindowHeight = h;
                }
                savedWindowMaximized = settings.WindowMaximized;

                isDarkTheme = IsDarkTheme(currentThemeName);

                welcomeShown = settings.WelcomeShown;
                if (settings.SettingsFormatVersion < CurrentSettingsFormatVersion)
                    welcomeShown = true;
            }
            catch
            {
                currentThemeName = "Translator Purple";
                createBackup = true;
                currentFolder = "";
                useTranslationMemory = true;
                isDarkTheme = IsDarkTheme(currentThemeName);
                appUiLanguage = ResolveOsUiLanguage();
                savedWindowX = savedWindowY = savedWindowWidth = savedWindowHeight = null;
                savedWindowMaximized = false;
            }
        }

        private void RememberRecentFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string full;
            try
            {
                full = Path.GetFullPath(path.Trim());
            }
            catch
            {
                return;
            }

            if (!Directory.Exists(full))
                return;

            recentJsonFolders.RemoveAll(x =>
            {
                if (string.IsNullOrWhiteSpace(x))
                    return false;
                try
                {
                    return string.Equals(Path.GetFullPath(x.Trim()), full, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });

            recentJsonFolders.Insert(0, full);
            const int maxRecent = 12;
            while (recentJsonFolders.Count > maxRecent)
                recentJsonFolders.RemoveAt(recentJsonFolders.Count - 1);

            SaveSettings();
            BumpDashboardContentStamp();
        }

        private void OpenRecentJsonFolderFromDashboard(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string full;
            try
            {
                full = Path.GetFullPath(path.Trim());
            }
            catch
            {
                Log(L($"Invalid path: {path}", $"Некорректный путь: {path}"), true);
                return;
            }

            if (!Directory.Exists(full))
            {
                Log(L($"Folder unavailable (deleted or moved): {full}", $"Папка недоступна (удалена или перемещена): {full}"), true);
                recentJsonFolders.RemoveAll(x =>
                {
                    if (string.IsNullOrWhiteSpace(x))
                        return false;
                    try
                    {
                        return string.Equals(Path.GetFullPath(x.Trim()), full, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return string.Equals(x.Trim(), path.Trim(), StringComparison.OrdinalIgnoreCase);
                    }
                });
                SaveSettings();
                BumpDashboardContentStamp();
                LoadDashboardModule();
                return;
            }

            currentFolder = full;
            RememberRecentFolder(currentFolder);
            Log(L("Folder selected: ", "Выбрана папка: ") + currentFolder);
            lastJsonExtractFolder = "";
            translationItems.Clear();
            LoadJsonTranslatorModule();
        }

        private void SaveSettings()
        {
            try
            {
                SyncBundleLocFieldsFromUi();

                var dir = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var settings = new AppSettings
                {
                    SettingsFormatVersion = CurrentSettingsFormatVersion,
                    ThemeName = currentThemeName,
                    CreateBackup = createBackup,
                    LastFolder = currentFolder,
                    LastUnityGameFolder = lastUnityGameDataFolder,
                    BundleLocGameDataFolder = bundleLocGameDataFolder ?? "",
                    BundleLocBundlePath = bundleLocBundlePath ?? "",
                    BundleLocJsonFolder = bundleLocJsonFolder ?? "",
                    BundleLocOutputBundlePath = bundleLocOutputBundlePath ?? "",
                    BundleLocLocalesBundlePath = bundleLocLocalesBundlePath ?? "",
                    BundleLocLocalesCode = bundleLocLocalesCode ?? "",
                    BundleLocLocalesOutputPath = bundleLocLocalesOutputPath ?? "",
                    BundleLocMonoBehaviourOnly = bundleLocMonoBehaviourOnlySaved,
                    BundleLocOverwriteSourceAfterBuild = bundleLocOverwriteSourceAfterBuildSaved,
                    UseTranslationMemory = useTranslationMemory,
                    SourceLanguage = sourceLanguageDisplay,
                    TargetLanguage = targetLanguageDisplay,
                    TxtExchangeFormatIndex = jsonTxtFormatSelectedIndex,
                    JsonCopyModeIndex = jsonCopyModeSelectedIndex,
                    TranslationApiEnabled = translationApiEnabled,
                    TranslationApiUrl = translationApiUrl ?? "",
                    TranslationApiKey = translationApiKey ?? "",
                    TranslationAiBackend = translationAiBackend ?? "LibreTranslate",
                    TranslationOpenRouterModel = translationOpenRouterModel ?? "",
                    RecentJsonFolders = recentJsonFolders.ToList(),
                    DashboardPanels = dashboardPanels.ToList(),
                    LastManualBackupUtc = lastManualBackupUtc,
                    UiLanguage = appUiLanguage,
                    WelcomeShown = welcomeShown,
                    JunkFeaturesEnabled = junkFeaturesEnabled,
                    AutosaveEnabled = autosaveEnabled,
                    WindowX = WindowState == FormWindowState.Normal ? Left : RestoreBounds.Left,
                    WindowY = WindowState == FormWindowState.Normal ? Top : RestoreBounds.Top,
                    WindowWidth = WindowState == FormWindowState.Normal ? Width : RestoreBounds.Width,
                    WindowHeight = WindowState == FormWindowState.Normal ? Height : RestoreBounds.Height,
                    WindowMaximized = WindowState == FormWindowState.Maximized,
                };
                File.WriteAllText(SettingsPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log(L($"Failed to save settings: {ex.Message}", $"Не удалось сохранить настройки: {ex.Message}"), true);
            }
        }

        private class AppSettings
        {
            public int SettingsFormatVersion { get; set; }
            /// <summary>Показывать приветствие только пока false (первая установка).</summary>
            public bool WelcomeShown { get; set; }
            public string ThemeName { get; set; }
            public bool CreateBackup { get; set; } = true;
            public string LastFolder { get; set; }
            public string LastUnityGameFolder { get; set; }
            public string BundleLocGameDataFolder { get; set; }
            public string BundleLocBundlePath { get; set; }
            public string BundleLocJsonFolder { get; set; }
            public string BundleLocOutputBundlePath { get; set; }
            public string BundleLocLocalesBundlePath { get; set; }
            public string BundleLocLocalesCode { get; set; }
            public string BundleLocLocalesOutputPath { get; set; }
            public bool? BundleLocMonoBehaviourOnly { get; set; }
            public bool? BundleLocOverwriteSourceAfterBuild { get; set; }
            public bool? UseTranslationMemory { get; set; }
            public string SourceLanguage { get; set; }
            public string TargetLanguage { get; set; }
            public int? TxtExchangeFormatIndex { get; set; }
            /// <summary>Режим кнопки «Копировать» в JSON Files (0=все строки, 1=без служебных).</summary>
            public int? JsonCopyModeIndex { get; set; }
            public bool TranslationApiEnabled { get; set; }
            public string TranslationApiUrl { get; set; }
            public string TranslationApiKey { get; set; }
            /// <summary>Ключ провайдера ИИ из выпадающего списка настроек.</summary>
            public string TranslationAiBackend { get; set; }
            /// <summary>Имя модели для chat completions (все чат-провайдеры).</summary>
            public string TranslationOpenRouterModel { get; set; }
            public List<string> RecentJsonFolders { get; set; }
            /// <summary>Пользовательские HTML-панели на «Главной».</summary>
            public List<DashboardPanelData> DashboardPanels { get; set; }
            public DateTime? LastManualBackupUtc { get; set; }
            public string UiLanguage { get; set; }

            /// <summary>Включить редко нужные опции (напр. дополнительные провайдеры API).</summary>
            public bool JunkFeaturesEnabled { get; set; }
            /// <summary>Автосохранение переводов в JSON каждые 2 мин.</summary>
            public bool AutosaveEnabled { get; set; }
            public int? WindowX { get; set; }
            public int? WindowY { get; set; }
            public int? WindowWidth { get; set; }
            public int? WindowHeight { get; set; }
            public bool WindowMaximized { get; set; }
        }

        private bool IsDarkTheme(string themeName)
        {
            return themeName == "Translator Purple"
                || themeName == "GitHub Dark"
                || themeName == "Visual Studio Dark"
                || themeName == "Dracula"
                || themeName == "Nord";
        }

        private void ApplyTheme()
        {
            // Мягкий «почти-белый» (~88%) для текста на тёмном хроме — чистый #FFF режет глаза (гало).
            Color softChromeText = Color.FromArgb(228, 226, 238);

            Color pageBg;
            Color headerText;
            Color subtitleText;
            Color navBg;
            Color gridBg;
            Color gridColor;
            Color gridHeaderBg;
            Color gridRowBg;
            Color gridRowFore;
            Color gridAltRowBg;
            Color logBg;
            Color logFore;
            Color statusBg;
            Color statusFore;

            switch (currentThemeName)
            {
                case "GitHub Dark":
                    // Освежено: чёрный nav/log (1,4,9) поднят до мягкого угольного, чередование строк тише.
                    pageBg = Color.FromArgb(13, 17, 23);
                    headerText = Color.FromArgb(230, 237, 243);
                    subtitleText = Color.FromArgb(139, 148, 158);
                    navBg = Color.FromArgb(10, 13, 18);
                    gridBg = Color.FromArgb(13, 17, 23);
                    gridColor = Color.FromArgb(45, 51, 59);
                    gridHeaderBg = Color.FromArgb(28, 33, 40);
                    gridRowBg = Color.FromArgb(22, 27, 34);
                    gridRowFore = Color.FromArgb(230, 237, 243);
                    gridAltRowBg = Color.FromArgb(27, 33, 40);
                    logBg = Color.FromArgb(10, 13, 18);
                    logFore = Color.FromArgb(86, 211, 100);
                    statusBg = Color.FromArgb(22, 27, 34);
                    statusFore = Color.FromArgb(201, 209, 217);
                    break;
                case "Visual Studio Dark":
                    // Освежено: плоские серые чуть теплее и мягче, культовый синий статус-бар сохранён.
                    pageBg = Color.FromArgb(31, 31, 34);
                    headerText = Color.FromArgb(241, 241, 241);
                    subtitleText = Color.FromArgb(168, 168, 172);
                    navBg = Color.FromArgb(37, 37, 41);
                    gridBg = Color.FromArgb(31, 31, 34);
                    gridColor = Color.FromArgb(60, 60, 68);
                    gridHeaderBg = Color.FromArgb(44, 44, 50);
                    gridRowBg = Color.FromArgb(37, 37, 41);
                    gridRowFore = Color.FromArgb(238, 238, 240);
                    gridAltRowBg = Color.FromArgb(43, 43, 49);
                    logBg = Color.FromArgb(30, 30, 33);
                    logFore = Color.FromArgb(181, 206, 168);
                    statusBg = Color.FromArgb(0, 122, 204);
                    statusFore = Color.White;
                    break;
                case "Dracula":
                    // Официальная палитра Dracula — сохранена аутентичной, чередование строк чуть мягче.
                    pageBg = Color.FromArgb(40, 42, 54);
                    headerText = Color.FromArgb(248, 248, 242);
                    subtitleText = Color.FromArgb(189, 147, 249);
                    navBg = Color.FromArgb(33, 34, 44);
                    gridBg = Color.FromArgb(40, 42, 54);
                    gridColor = Color.FromArgb(68, 71, 90);
                    gridHeaderBg = Color.FromArgb(55, 58, 74);
                    gridRowBg = Color.FromArgb(40, 42, 54);
                    gridRowFore = Color.FromArgb(248, 248, 242);
                    gridAltRowBg = Color.FromArgb(46, 48, 62);
                    logBg = Color.FromArgb(33, 34, 44);
                    logFore = Color.FromArgb(80, 250, 123);
                    statusBg = Color.FromArgb(68, 71, 90);
                    statusFore = Color.FromArgb(248, 248, 242);
                    break;
                case "Nord":
                    // Официальная палитра Nord (Polar Night) — сохранена аутентичной.
                    pageBg = Color.FromArgb(46, 52, 64);
                    headerText = Color.FromArgb(236, 239, 244);
                    subtitleText = Color.FromArgb(136, 192, 208);
                    navBg = Color.FromArgb(36, 41, 51);
                    gridBg = Color.FromArgb(46, 52, 64);
                    gridColor = Color.FromArgb(76, 86, 106);
                    gridHeaderBg = Color.FromArgb(59, 66, 82);
                    gridRowBg = Color.FromArgb(59, 66, 82);
                    gridRowFore = Color.FromArgb(236, 239, 244);
                    gridAltRowBg = Color.FromArgb(64, 72, 90);
                    logBg = Color.FromArgb(36, 41, 51);
                    logFore = Color.FromArgb(163, 190, 140);
                    statusBg = Color.FromArgb(46, 52, 64);
                    statusFore = Color.FromArgb(216, 222, 233);
                    break;
                case "Translator Purple":
                    // Освежено: нейтральный почти-чёрный заменён тёплым угольным с фиолетовым подтоном; белый текст смягчён.
                    pageBg = Color.FromArgb(20, 19, 26);
                    headerText = Color.FromArgb(236, 234, 246);
                    subtitleText = Color.FromArgb(159, 153, 180);
                    navBg = Color.FromArgb(27, 25, 36);
                    gridBg = Color.FromArgb(28, 26, 37);
                    gridColor = Color.FromArgb(52, 49, 68);
                    gridHeaderBg = Color.FromArgb(38, 35, 50);
                    gridRowBg = Color.FromArgb(25, 23, 33);
                    gridRowFore = Color.FromArgb(228, 226, 238);
                    gridAltRowBg = Color.FromArgb(31, 29, 41);
                    logBg = Color.FromArgb(18, 17, 24);
                    logFore = Color.FromArgb(185, 166, 250);
                    statusBg = Color.FromArgb(27, 25, 36);
                    statusFore = Color.FromArgb(190, 185, 206);
                    break;
                case "Solarized Light":
                    // Официальная палитра Solarized Light (Ethan Schoonover) — сохранена аутентичной.
                    pageBg = Color.FromArgb(253, 246, 227);
                    headerText = Color.FromArgb(101, 123, 131);
                    subtitleText = Color.FromArgb(88, 110, 117);
                    navBg = Color.FromArgb(7, 54, 66);
                    gridBg = Color.FromArgb(253, 246, 227);
                    gridColor = Color.FromArgb(238, 232, 213);
                    gridHeaderBg = Color.FromArgb(38, 139, 210);
                    gridRowBg = Color.FromArgb(253, 246, 227);
                    gridRowFore = Color.FromArgb(101, 123, 131);
                    gridAltRowBg = Color.FromArgb(245, 238, 213);
                    logBg = Color.FromArgb(238, 232, 213);
                    logFore = Color.FromArgb(42, 161, 152);
                    statusBg = Color.FromArgb(238, 232, 213);
                    statusFore = Color.FromArgb(101, 123, 131);
                    break;
                default: // GitHub Light
                    // Освежено: тёмный nav-бар стал спокойным сланцевым, сетка-разделитель мягче.
                    pageBg = Color.FromArgb(247, 249, 251);
                    headerText = Color.FromArgb(31, 35, 40);
                    subtitleText = Color.FromArgb(87, 96, 106);
                    navBg = Color.FromArgb(33, 39, 51);
                    gridBg = Color.White;
                    gridColor = Color.FromArgb(216, 222, 230);
                    gridHeaderBg = Color.FromArgb(33, 39, 51);
                    gridRowBg = Color.White;
                    gridRowFore = Color.FromArgb(31, 35, 40);
                    gridAltRowBg = Color.FromArgb(246, 248, 250);
                    logBg = Color.FromArgb(246, 248, 250);
                    logFore = Color.FromArgb(26, 127, 55);
                    statusBg = Color.FromArgb(236, 240, 244);
                    statusFore = Color.FromArgb(87, 96, 106);
                    break;
            }

            _themePageBg = pageBg;
            _themeHeaderText = headerText;
            _themeSubtitleText = subtitleText;
            _themeGridBg = gridBg;
            _themeGridHeaderBg = gridHeaderBg;
            _themeGridColor = gridColor;
            _themeGridRowBg = gridRowBg;
            _themeGridRowFore = gridRowFore;

            this.BackColor = pageBg;
            if (contentPanel != null) contentPanel.BackColor = pageBg;
            if (moduleHostPanel != null) moduleHostPanel.BackColor = Color.Transparent;
            if (headerPanel != null) headerPanel.BackColor = Color.Transparent;
            if (headerLabel != null) headerLabel.ForeColor = headerText;
            if (navPanel != null) navPanel.BackColor = navBg;
            if (navLogoPanel != null) navLogoPanel.BackColor = navBg;
            if (navButtonsContainer != null) navButtonsContainer.BackColor = navBg;

            if (dgv != null)
            {
                dgv.BackgroundColor = gridBg;
                dgv.GridColor = gridColor;
                dgv.ColumnHeadersDefaultCellStyle.BackColor = gridHeaderBg;
                dgv.ColumnHeadersDefaultCellStyle.ForeColor = isDarkTheme
                    ? Color.FromArgb(226, 232, 240)
                    : Color.FromArgb(71, 85, 105);
                dgv.DefaultCellStyle.BackColor = gridRowBg;
                dgv.DefaultCellStyle.ForeColor = gridRowFore;
                dgv.DefaultCellStyle.SelectionBackColor = isDarkTheme
                    ? Color.FromArgb(51, 65, 85)
                    : Color.FromArgb(226, 232, 240);
                dgv.DefaultCellStyle.SelectionForeColor = gridRowFore;
                dgv.AlternatingRowsDefaultCellStyle.BackColor = gridRowBg;
                dgv.AlternatingRowsDefaultCellStyle.ForeColor = dgv.DefaultCellStyle.ForeColor;
                UpdateRowHighlights();
            }

            if (logBox != null)
            {
                logBox.BackColor = logBg;
                logBox.ForeColor = logFore;
            }

            if (statusStrip != null)
                statusStrip.BackColor = statusBg;

            if (statusLabel != null)
                statusLabel.ForeColor = statusFore;

            if (btnCancelApiBatchTranslate != null)
                btnCancelApiBatchTranslate.ForeColor = statusFore;

            if (chkBackup != null)
                chkBackup.ForeColor = statusFore;

            if (progressStatsLabel != null)
                progressStatsLabel.ForeColor = statusFore;

            var assetTitle = isDarkTheme ? Color.FromArgb(226, 232, 240) : Color.FromArgb(17, 24, 39);
            if (assetsModuleFolderLabel != null && !assetsModuleFolderLabel.IsDisposed)
                assetsModuleFolderLabel.ForeColor = assetTitle;

            if (assetsModuleAssetsStatsLabel != null && !assetsModuleAssetsStatsLabel.IsDisposed)
                assetsModuleAssetsStatsLabel.ForeColor = assetTitle;

            if (assetsModuleAssetsGrid != null && !assetsModuleAssetsGrid.IsDisposed)
            {
                assetsModuleAssetsGrid.BackgroundColor = gridBg;
                assetsModuleAssetsGrid.GridColor = gridColor;
                assetsModuleAssetsGrid.ColumnHeadersDefaultCellStyle.BackColor = gridHeaderBg;
                assetsModuleAssetsGrid.ColumnHeadersDefaultCellStyle.ForeColor = isDarkTheme
                    ? Color.FromArgb(226, 232, 240)
                    : Color.FromArgb(71, 85, 105);
                assetsModuleAssetsGrid.DefaultCellStyle.BackColor = gridRowBg;
                assetsModuleAssetsGrid.DefaultCellStyle.ForeColor = gridRowFore;
                assetsModuleAssetsGrid.DefaultCellStyle.SelectionBackColor = isDarkTheme
                    ? Color.FromArgb(51, 65, 85)
                    : Color.FromArgb(226, 232, 240);
                assetsModuleAssetsGrid.DefaultCellStyle.SelectionForeColor = gridRowFore;
                assetsModuleAssetsGrid.AlternatingRowsDefaultCellStyle.BackColor = gridAltRowBg;
                assetsModuleAssetsGrid.AlternatingRowsDefaultCellStyle.ForeColor = gridRowFore;
            }

            if (assetsModuleLogBox != null && !assetsModuleLogBox.IsDisposed)
            {
                assetsModuleLogBox.BackColor = logBg;
                assetsModuleLogBox.ForeColor = logFore;
            }

            if (bundleLocGameDataTextBox != null && !bundleLocGameDataTextBox.IsDisposed)
            {
                bundleLocGameDataTextBox.BackColor = gridRowBg;
                bundleLocGameDataTextBox.ForeColor = gridRowFore;
            }

            if (bundleLocBundlePathTextBox != null && !bundleLocBundlePathTextBox.IsDisposed)
            {
                bundleLocBundlePathTextBox.BackColor = gridRowBg;
                bundleLocBundlePathTextBox.ForeColor = gridRowFore;
            }

            if (bundleLocJsonFolderTextBox != null && !bundleLocJsonFolderTextBox.IsDisposed)
            {
                bundleLocJsonFolderTextBox.BackColor = gridRowBg;
                bundleLocJsonFolderTextBox.ForeColor = gridRowFore;
            }

            if (bundleLocOutputBundleTextBox != null && !bundleLocOutputBundleTextBox.IsDisposed)
            {
                bundleLocOutputBundleTextBox.BackColor = gridRowBg;
                bundleLocOutputBundleTextBox.ForeColor = gridRowFore;
            }

            if (bundleLocLocalesBundleTextBox != null && !bundleLocLocalesBundleTextBox.IsDisposed)
            {
                bundleLocLocalesBundleTextBox.BackColor = gridRowBg;
                bundleLocLocalesBundleTextBox.ForeColor = gridRowFore;
            }

            if (bundleLocLocalesCodeTextBox != null && !bundleLocLocalesCodeTextBox.IsDisposed)
            {
                bundleLocLocalesCodeTextBox.BackColor = gridRowBg;
                bundleLocLocalesCodeTextBox.ForeColor = gridRowFore;
            }

            if (bundleLocLocalesOutputTextBox != null && !bundleLocLocalesOutputTextBox.IsDisposed)
            {
                bundleLocLocalesOutputTextBox.BackColor = gridRowBg;
                bundleLocLocalesOutputTextBox.ForeColor = gridRowFore;
            }

            if (bundleLocMonoBehaviourOnlyCheck != null && !bundleLocMonoBehaviourOnlyCheck.IsDisposed)
                bundleLocMonoBehaviourOnlyCheck.ForeColor = gridRowFore;

            if (bundleLocOverwriteSourceAfterBuildCheck != null && !bundleLocOverwriteSourceAfterBuildCheck.IsDisposed)
                bundleLocOverwriteSourceAfterBuildCheck.ForeColor = gridRowFore;

            if (bundleLocLogBox != null && !bundleLocLogBox.IsDisposed)
            {
                bundleLocLogBox.BackColor = logBg;
                bundleLocLogBox.ForeColor = logFore;
            }

            if (jsonWorkspaceCard != null && !jsonWorkspaceCard.IsDisposed)
            {
                var jsonSurface = currentThemeName == "Translator Purple"
                    ? Color.FromArgb(30, 28, 40)
                    : isDarkTheme
                        ? Color.FromArgb(30, 41, 59)
                        : Color.White;
                jsonWorkspaceCard.BackColor = jsonSurface;
            }

            if (jsonSearchPanel != null && !jsonSearchPanel.IsDisposed)
            {
                Color searchBg = currentThemeName == "Translator Purple"
                    ? Color.FromArgb(40, 38, 52)
                    : isDarkTheme
                        ? Color.FromArgb(38, 38, 44)
                        : Color.FromArgb(238, 240, 244);
                Color searchFore = isDarkTheme || currentThemeName == "Translator Purple"
                    ? Color.FromArgb(238, 236, 248)
                    : Color.FromArgb(17, 24, 39);
                jsonSearchPanel.BackColor = searchBg;
                foreach (Control c in jsonSearchPanel.Controls)
                {
                    if (c is TextBox sb)
                    {
                        sb.BackColor = currentThemeName == "Translator Purple"
                            ? Color.FromArgb(30, 28, 40)
                            : isDarkTheme ? Color.FromArgb(24, 24, 28) : Color.White;
                        sb.ForeColor = searchFore;
                    }
                    else if (c is Button cb)
                    {
                        cb.BackColor = searchBg;
                        cb.ForeColor = searchFore;
                        cb.FlatAppearance.MouseOverBackColor = DashboardAccentPrimary();
                    }
                    else
                    {
                        c.ForeColor = searchFore;
                    }
                }
            }

            if (lblJsonModuleTitle != null && !lblJsonModuleTitle.IsDisposed)
                lblJsonModuleTitle.ForeColor = headerText;
            if (lblActivityTitle != null && !lblActivityTitle.IsDisposed)
                lblActivityTitle.ForeColor = subtitleText;

            void StylePurpleJsonToolbarButton(RoundedToolbarButton rb, ButtonStyleKind kind)
            {
                if (rb == null || rb.IsDisposed || currentThemeName != "Translator Purple")
                    return;

                switch (kind)
                {
                    case ButtonStyleKind.Primary:
                        rb.BackColor = Color.FromArgb(56, 52, 74);
                        rb.ForeColor = Color.FromArgb(232, 230, 242);
                        rb.FlatAppearance.BorderColor = Color.FromArgb(46, 43, 60);
                        rb.HoverBackColor = Color.FromArgb(70, 64, 92);
                        rb.PressedBackColor = Color.FromArgb(46, 43, 62);
                        break;
                    case ButtonStyleKind.Secondary:
                        rb.BackColor = Color.FromArgb(40, 38, 52);
                        rb.ForeColor = Color.FromArgb(232, 230, 242);
                        rb.FlatAppearance.BorderColor = Color.FromArgb(54, 51, 68);
                        rb.HoverBackColor = Color.FromArgb(50, 47, 64);
                        rb.PressedBackColor = Color.FromArgb(34, 32, 44);
                        break;
                    case ButtonStyleKind.Danger:
                        rb.BackColor = Color.FromArgb(118, 32, 32);
                        rb.ForeColor = Color.White;
                        rb.FlatAppearance.BorderColor = Color.FromArgb(88, 26, 26);
                        rb.HoverBackColor = Color.FromArgb(140, 42, 42);
                        rb.PressedBackColor = Color.FromArgb(92, 28, 28);
                        break;
                }

                rb.Invalidate();
            }

            if (currentThemeName == "Translator Purple")
            {
                StylePurpleJsonToolbarButton(btnSelectFolder as RoundedToolbarButton, ButtonStyleKind.Secondary);
                StylePurpleJsonToolbarButton(btnApply as RoundedToolbarButton, ButtonStyleKind.Primary);
                StylePurpleJsonToolbarButton(btnExportTxt as RoundedToolbarButton, ButtonStyleKind.Secondary);
                StylePurpleJsonToolbarButton(btnImportTxt as RoundedToolbarButton, ButtonStyleKind.Secondary);
                StylePurpleJsonToolbarButton(btnTranslateEmptyApi as RoundedToolbarButton, ButtonStyleKind.Secondary);
                StylePurpleJsonToolbarButton(btnDeleteJsonWithoutText as RoundedToolbarButton, ButtonStyleKind.Danger);
                StylePurpleJsonToolbarButton(btnCopySelectedAi as RoundedToolbarButton, ButtonStyleKind.Secondary);
                StylePurpleJsonToolbarButton(btnPasteAi as RoundedToolbarButton, ButtonStyleKind.Secondary);
                StylePurpleJsonToolbarButton(btnClearLog as RoundedToolbarButton, ButtonStyleKind.Danger);
                StylePurpleJsonToolbarButton(assetsModulePickGameFolderButton as RoundedToolbarButton, ButtonStyleKind.Primary);
                if (assetsModuleExportButton != null && !assetsModuleExportButton.IsDisposed)
                    StylePurpleJsonToolbarButton(assetsModuleExportButton as RoundedToolbarButton, ButtonStyleKind.Secondary);
                if (assetsModuleExportSingleAssetButton != null && !assetsModuleExportSingleAssetButton.IsDisposed)
                    StylePurpleJsonToolbarButton(assetsModuleExportSingleAssetButton as RoundedToolbarButton, ButtonStyleKind.Secondary);
                if (assetsModuleBuildButton != null && !assetsModuleBuildButton.IsDisposed)
                    StylePurpleJsonToolbarButton(assetsModuleBuildButton as RoundedToolbarButton, ButtonStyleKind.Primary);
                if (assetsModuleFindFontsButton != null && !assetsModuleFindFontsButton.IsDisposed)
                    StylePurpleJsonToolbarButton(assetsModuleFindFontsButton as RoundedToolbarButton, ButtonStyleKind.Secondary);
                if (assetsModuleImportTmpFontButton != null && !assetsModuleImportTmpFontButton.IsDisposed)
                    StylePurpleJsonToolbarButton(assetsModuleImportTmpFontButton as RoundedToolbarButton, ButtonStyleKind.Secondary);
                if (assetsModuleTtfToTmpFontButton != null && !assetsModuleTtfToTmpFontButton.IsDisposed)
                    StylePurpleJsonToolbarButton(assetsModuleTtfToTmpFontButton as RoundedToolbarButton, ButtonStyleKind.Secondary);
                if (assetsModulePatchTmpMsdfAtlasButton != null && !assetsModulePatchTmpMsdfAtlasButton.IsDisposed)
                    StylePurpleJsonToolbarButton(assetsModulePatchTmpMsdfAtlasButton as RoundedToolbarButton, ButtonStyleKind.Secondary);
                if (assetsModuleReplaceAtlasTexturePngButton != null && !assetsModuleReplaceAtlasTexturePngButton.IsDisposed)
                    StylePurpleJsonToolbarButton(assetsModuleReplaceAtlasTexturePngButton as RoundedToolbarButton, ButtonStyleKind.Secondary);
                if (assetsModuleFindResourcesCrcButton != null && !assetsModuleFindResourcesCrcButton.IsDisposed)
                    StylePurpleJsonToolbarButton(assetsModuleFindResourcesCrcButton as RoundedToolbarButton, ButtonStyleKind.Secondary);
                if (assetsModuleDumpPathIdFieldsButton != null && !assetsModuleDumpPathIdFieldsButton.IsDisposed)
                    StylePurpleJsonToolbarButton(assetsModuleDumpPathIdFieldsButton as RoundedToolbarButton, ButtonStyleKind.Secondary);
            }

            if (progressBar != null && !progressBar.IsDisposed && currentThemeName == "Translator Purple")
                progressBar.ForeColor = Color.FromArgb(96, 165, 250);

            if (mainMenuStrip != null)
            {
                Color menuBg;
                if (currentThemeName == "Translator Purple")
                    menuBg = Color.FromArgb(24, 22, 32);
                else if (isDarkTheme)
                    menuBg = navBg;
                else
                    menuBg = Color.FromArgb(36, 41, 47);

                mainMenuStrip.BackColor = menuBg;
                mainMenuStrip.ForeColor = softChromeText;

                // Выпадающий список рисует Renderer — без него фон остаётся белым.
                Color menuHover = ThemeMix(menuBg, DashboardAccentPrimary(), 0.55);
                Color menuBorder = ThemeMix(menuBg, Color.White, 0.16);
                Color menuSep = ThemeMix(menuBg, Color.White, 0.22);
                Color menuDisabled = ThemeMix(menuBg, softChromeText, 0.45);

                mainMenuStrip.Renderer = new ThemedMenuRenderer(
                    new ThemedMenuColorTable(menuBg, menuHover, menuBorder, menuSep),
                    softChromeText, menuDisabled);
            }

            if (captionChromePanel != null && !captionChromePanel.IsDisposed)
            {
                if (currentThemeName == "Translator Purple")
                    captionChromePanel.BackColor = Color.FromArgb(24, 22, 32);
                else if (isDarkTheme)
                    captionChromePanel.BackColor = navBg;
                else
                    captionChromePanel.BackColor = Color.FromArgb(36, 41, 47);
            }

            if (topChromeHost != null && !topChromeHost.IsDisposed && captionChromePanel != null && !captionChromePanel.IsDisposed)
                topChromeHost.BackColor = captionChromePanel.BackColor;

            if (captionBtnMin != null && !captionBtnMin.IsDisposed)
            {
                captionBtnMin.ForeColor = softChromeText;
                captionBtnMax.ForeColor = softChromeText;
                captionBtnClose.ForeColor = softChromeText;
            }

            UpdateCaptionMaxGlyph();

            if (appTitle != null && !appTitle.IsDisposed)
                appTitle.ForeColor = softChromeText;

            if (sidebarFooterPanel != null && !sidebarFooterPanel.IsDisposed)
                sidebarFooterPanel.BackColor = navBg;

            UpdateSidebarReadyLabel();

            UpdateNavButtonsAppearance();

            ArrangeResizeGrips();

            // Нативные полосы прокрутки (грид, лог, панели) — под текущую тему.
            ApplyThemedScrollBars(this);
        }

        // Цвета выпадающего меню под тему (фон, подсветка, рамка, разделители).
        private sealed class ThemedMenuColorTable : ProfessionalColorTable
        {
            private readonly Color _bg, _hover, _border, _sep;
            public ThemedMenuColorTable(Color bg, Color hover, Color border, Color sep)
            {
                _bg = bg; _hover = hover; _border = border; _sep = sep;
                UseSystemColors = false;
            }
            public override Color ToolStripDropDownBackground => _bg;
            public override Color ImageMarginGradientBegin => _bg;
            public override Color ImageMarginGradientMiddle => _bg;
            public override Color ImageMarginGradientEnd => _bg;
            public override Color MenuStripGradientBegin => _bg;
            public override Color MenuStripGradientEnd => _bg;
            public override Color ToolStripBorder => _bg;
            public override Color MenuItemSelected => _hover;
            public override Color MenuItemSelectedGradientBegin => _hover;
            public override Color MenuItemSelectedGradientEnd => _hover;
            public override Color MenuItemBorder => _hover;
            public override Color MenuItemPressedGradientBegin => _bg;
            public override Color MenuItemPressedGradientMiddle => _bg;
            public override Color MenuItemPressedGradientEnd => _bg;
            public override Color MenuBorder => _border;
            public override Color SeparatorDark => _sep;
            public override Color SeparatorLight => _sep;
        }

        // Рендерер меню: текст и стрелки тоже под тему (иначе остаются чёрными).
        private sealed class ThemedMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly Color _text, _disabled;
            public ThemedMenuRenderer(ProfessionalColorTable colorTable, Color text, Color disabled)
                : base(colorTable)
            {
                _text = text; _disabled = disabled;
                RoundedEdges = false;
            }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = (e.Item != null && e.Item.Enabled) ? _text : _disabled;
                base.OnRenderItemText(e);
            }
            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = _text;
                base.OnRenderArrow(e);
            }
        }

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 2;

        // Состояние эмулированного Aero Snap: запоминаем размер окна ДО прижатия к краю,
        // чтобы при следующем перетаскивании вернуть прежнюю форму (как у обычных окон Windows).
        private bool _isWindowSnapped;
        private Rectangle _preSnapBounds;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private void CaptionChrome_StartDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left)
                return;
            // Тянут за заголовок в максимизированном состоянии — сначала восстанавливаем окно,
            // затем продолжаем перетаскивание (как в обычных окнах Windows).
            if (WindowState == FormWindowState.Maximized)
                ToggleCaptionWindowState();
            else if (_isWindowSnapped)
                RestoreFromSnapUnderCursor();
            ReleaseCapture();
            // SendMessage блокируется на всём цикле перетаскивания и возвращается после отпускания мыши —
            // здесь окно уже стоит там, куда бросили. Эмулируем Aero Snap, которого у borderless-окна нет.
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            TrySnapWindowAfterCaptionDrag();
        }

        /// <summary>Снимает окно со снапа: прежний размер под курсор, заголовок «прихвачен» под указателем (как отрыв снапнутого окна в Windows).</summary>
        private void RestoreFromSnapUnderCursor()
        {
            try
            {
                var cur = Cursor.Position;
                var old = Bounds;
                var restored = _preSnapBounds;
                if (restored.Width <= 0 || restored.Height <= 0)
                {
                    _isWindowSnapped = false;
                    return;
                }

                double ratioX = old.Width > 0 ? (cur.X - old.Left) / (double)old.Width : 0.5;
                if (ratioX < 0) ratioX = 0;
                if (ratioX > 1) ratioX = 1;

                int newLeft = cur.X - (int)(restored.Width * ratioX);
                int capH = captionChromePanel?.Height ?? 40;
                int offsetY = cur.Y - old.Top;
                if (offsetY < 0 || offsetY > capH)
                    offsetY = Math.Min(20, restored.Height);
                int newTop = cur.Y - offsetY;

                Bounds = new Rectangle(newLeft, newTop, restored.Width, restored.Height);
            }
            catch { } // в худшем случае окно не сменит размер до отпускания
            finally
            {
                _isWindowSnapped = false;
            }
        }

        /// <summary>Aero Snap для безрамочного окна: перетаскивание шапки к краю прижимает к левой/правой половине, к верху — разворот.</summary>
        private void TrySnapWindowAfterCaptionDrag()
        {
            if (WindowState != FormWindowState.Normal)
                return;
            try
            {
                const int SnapMargin = 8;
                var cursor = Cursor.Position;
                var wa = Screen.FromPoint(cursor).WorkingArea;

                if (cursor.X <= wa.Left + SnapMargin)
                {
                    _preSnapBounds = Bounds;
                    Bounds = new Rectangle(wa.Left, wa.Top, wa.Width / 2, wa.Height);
                    _isWindowSnapped = true;
                }
                else if (cursor.X >= wa.Right - SnapMargin)
                {
                    _preSnapBounds = Bounds;
                    int w = wa.Width / 2;
                    Bounds = new Rectangle(wa.Right - w, wa.Top, w, wa.Height);
                    _isWindowSnapped = true;
                }
                else if (cursor.Y <= wa.Top + SnapMargin)
                {
                    ToggleCaptionWindowState(); // верхний край — разворот, как в Windows
                }
            }
            catch { } // снап необязателен — ошибки экрана/границ игнорируем
        }

        private void CaptionChrome_ToggleMaximize(object sender, EventArgs e)
        {
            ToggleCaptionWindowState();
        }

        private void ToggleCaptionWindowState()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            UpdateCaptionMaxGlyph();
        }

        private void UpdateCaptionMaxGlyph()
        {
            if (captionBtnMax == null || captionBtnMax.IsDisposed)
                return;
            try
            {
                captionBtnMax.Font = new Font("Segoe MDL2 Assets", 10f);
                captionBtnMax.Text = WindowState == FormWindowState.Maximized ? "\uE923" : "\uE922";
            }
            catch
            {
                captionBtnMax.Font = new Font("Segoe UI", 11f);
                captionBtnMax.Text = WindowState == FormWindowState.Maximized ? "\u2750" : "\u25A1";
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
        }
    }
}
