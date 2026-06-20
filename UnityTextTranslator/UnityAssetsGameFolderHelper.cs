using AssetsTools.NET.Extra;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace UnityTextTranslator
{
    /// <summary>
    /// Поиск каталога *_Data, перечисление и предзагрузка .assets как в UABEA Next (чтобы разрешались ссылки MonoBehaviour).
    /// </summary>
    internal static class UnityAssetsGameFolderHelper
    {
        private static readonly FieldInfo AssetsFileInstancePathField =
            typeof(AssetsFileInstance).GetField("path", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly Regex StreamingLevelBuiltinFileName =
            new Regex(@"^level\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// В билде стриминговые сцены — файлы без расширения <c>level0</c>, <c>level1</c> и т.д. с парным <c>levelN.resS</c>.
        /// Перезапись контейнера через сторонние инструменты часто ломает согласование с .resS (corrupted / Position out of bounds).
        /// </summary>
        public static bool LooksLikeStreamingSceneLevelContainer(string assetContainerPath)
        {
            if (string.IsNullOrWhiteSpace(assetContainerPath))
                return false;

            try
            {
                var leaf = Path.GetFileName(assetContainerPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
                return !string.IsNullOrEmpty(leaf) && StreamingLevelBuiltinFileName.IsMatch(leaf);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Поднимаемся по каталогам от пути bundle (обычно в <c>Name_Data\StreamingAssets\...</c>) пока имя текущего каталога
        /// не станет вида GameName_Data. Нужно, чтобы не путать <c>SarahsHouse_Data</c> и <c>TooMuchLight_Data</c> для Addressables bundle.
        /// </summary>
        public static string TryInferGameDataAncestorFromBundlePath(string bundleFilePath)
        {
            if (string.IsNullOrWhiteSpace(bundleFilePath) || !File.Exists(bundleFilePath))
                return null;

            try
            {
                var dir = Path.GetFullPath(Path.GetDirectoryName(bundleFilePath));
                for (var n = 0; n < 48 && !string.IsNullOrEmpty(dir); n++)
                {
                    var leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(leaf) && leaf.EndsWith("_Data", StringComparison.OrdinalIgnoreCase) &&
                        Directory.Exists(dir))
                        return dir;

                    var parent = Directory.GetParent(dir);
                    if (parent == null)
                        break;
                    dir = parent.FullName;
                }
            }
            catch
            {
                /* ignore */
            }

            return null;
        }

        internal static string NormalizeGameDataFolderPathOrNull(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Пользователь может указать папку с exe или сам каталог Name_Data.</summary>
        public static string ResolveGameDataFolder(string userSelectedPath)
        {
            if (string.IsNullOrWhiteSpace(userSelectedPath))
                return null;

            string full;
            try
            {
                full = Path.GetFullPath(userSelectedPath);
            }
            catch
            {
                return null;
            }

            if (!Directory.Exists(full))
                return null;

            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                return full;

            try
            {
                var sub = Directory.GetDirectories(full);
                var dataDirs = sub
                    .Where(d => Path.GetFileName(d).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (dataDirs.Count == 1)
                    return dataDirs[0];
            }
            catch
            {
                // используем выбранную папку как есть
            }

            return full;
        }

        /// <summary>Поднимается от пути к файлу или каталогу вверх и ищет родительский <c>*_Data</c>.</summary>
        public static string TryFindParentGameDataFolder(string anyFileOrFolderPath)
        {
            try
            {
                var path = anyFileOrFolderPath;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    path = Path.GetDirectoryName(Path.GetFullPath(path));
                var dir = path;
                for (var depth = 0; depth < 12 && !string.IsNullOrEmpty(dir); depth++)
                {
                    var leaf = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    if (leaf.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                        return dir;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch
            {
                /* ignore */
            }
            return null;
        }

        public static List<string> EnumerateAssetPathsSorted(string dataFolder, int maxFiles = 750)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder))
                return result;

            try
            {
                foreach (var path in Directory.GetFiles(dataFolder, "*", SearchOption.AllDirectories))
                {
                    if (!IsLikelyAssetsFile(path))
                        continue;

                    try
                    {
                        result.Add(Path.GetFullPath(path));
                    }
                    catch
                    {
                        // skip
                    }

                    if (result.Count >= maxFiles)
                        break;
                }
            }
            catch
            {
                return result;
            }

            result.Sort((a, b) =>
            {
                var c = PriorityRank(a).CompareTo(PriorityRank(b));
                return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            return result;
        }

        public static bool IsLikelyAssetsFile(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return false;

            var name = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(name))
                return false;

            if (name.EndsWith(".assets", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".config", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".info", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(name, "globalgamemanagers", StringComparison.OrdinalIgnoreCase))
                return true;

            if (name.StartsWith("level", StringComparison.OrdinalIgnoreCase))
            {
                var rest = name.Substring("level".Length);
                return rest.Length > 0 && rest.All(char.IsDigit);
            }

            return false;
        }

        /// <summary>Существующий файл контейнера Unity: *.assets, extensionless levelN, globalgamemanagers.</summary>
        public static bool IsUnityAssetContainerPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return false;

            string norm;
            try
            {
                norm = Path.GetFullPath(fullPath);
            }
            catch
            {
                return false;
            }

            return File.Exists(norm) && IsLikelyAssetsFile(norm);
        }

        private static int PriorityRank(string fullPath)
        {
            var name = Path.GetFileName(fullPath).ToLowerInvariant();
            if (name == "globalgamemanagers.assets")
                return 0;
            if (name.StartsWith("globalgamemanagers", StringComparison.Ordinal))
                return 1;
            if (name == "resources.assets")
                return 2;
            if (name.StartsWith("sharedassets", StringComparison.Ordinal))
                return 3;
            if (name.StartsWith("level", StringComparison.Ordinal) &&
                (name.EndsWith(".assets", StringComparison.Ordinal) ||
                 name.Substring("level".Length).All(char.IsDigit)))
                return 4;

            return 10;
        }

        public static string GetAssetsFileInstancePath(AssetsFileInstance inst)
        {
            var p = AssetsFileInstancePathField?.GetValue(inst) as string;
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }

        public static HashSet<string> CollectLoadedAssetFullPaths(AssetsManager manager)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssetsFileInstance inst in manager.Files)
            {
                var p = AssetsFileInstancePathField?.GetValue(inst) as string;
                if (string.IsNullOrEmpty(p))
                    continue;

                try
                {
                    set.Add(Path.GetFullPath(p));
                }
                catch
                {
                    // skip
                }
            }

            return set;
        }

        public static string ResolveManagedFolder(string dataFolder)
        {
            if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder))
                return null;

            var direct = Path.Combine(dataFolder, "Managed");
            if (Directory.Exists(direct))
                return direct;

            try
            {
                foreach (var dir in Directory.GetDirectories(dataFolder, "Managed", SearchOption.AllDirectories))
                {
                    if (File.Exists(Path.Combine(dir, "Assembly-CSharp.dll")) ||
                        File.Exists(Path.Combine(dir, "UnityEngine.UI.dll")) ||
                        Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).Length > 0)
                        return dir;
                }
            }
            catch
            {
                // ignored
            }

            return null;
        }

        /// <summary>
        /// У IL2CPP-standalone нет <c>Managed/*.dll</c>: нативный <c>GameAssembly.dll</c> и каталог <c>il2cpp_data</c>
        /// (расшифровку полей скриптов через Mono.Cecil, как для Mono-сборки, не применить).
        /// </summary>
        public static bool IsLikelyIl2CppGameDataFolder(string dataFolder)
        {
            if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder))
                return false;

            if (Directory.Exists(Path.Combine(dataFolder, "Managed")))
                return false;

            if (Directory.Exists(Path.Combine(dataFolder, "il2cpp_data")))
                return true;

            try
            {
                if (File.Exists(Path.Combine(dataFolder, "GameAssembly.dll")))
                    return true;
            }
            catch
            {
                // ignored
            }

            try
            {
                var parent = Directory.GetParent(dataFolder)?.FullName;
                if (!string.IsNullOrEmpty(parent) &&
                    File.Exists(Path.Combine(parent, "GameAssembly.dll")))
                    return true;
            }
            catch
            {
                // ignored
            }

            return false;
        }

        /// <summary>Сообщение для лога экспорта, когда <see cref="TryAttachMonoCecilTemplateGenerator"/> вернул false.</summary>
        public static string GetManagedUnavailableExportHint(string dataFolder)
        {
            if (IsLikelyIl2CppGameDataFolder(dataFolder))
            {
                return
                    "IL2CPP-сборка: каталога Managed с .dll нет (типично есть GameAssembly.dll и il2cpp_data) — это нормально. " +
                    "Поля MonoBehaviour через Mono.Cecil не восстанавливаются; экспорт зависит от type tree и classdata.tpk. " +
                    "Игровой текст часто только в локализации/бандлах/TextAsset.";
            }

            return
                "Managed/*.dll не найдены: укажите папку *_Data Mono-сборки (с каталогом Managed и .dll). " +
                "Иначе MonoBehaviour экспортируются только с базовыми полями, игровой текст в JSON может не попасть.";
        }

        /// <summary>
        /// Папка с .dll, заменяющая Managed (например DummyDll от Il2CppDumper для IL2CPP-игр).
        /// Если задана и существует — используется вместо штатного Managed при разборе MonoBehaviour.
        /// </summary>
        public static string ManagedFolderOverride { get; set; }

        /// <param name="diagnostics">Если задано, сюда пишется причина сбоя инициализации Cecil (папка Managed при этом может существовать).</param>
        public static bool TryAttachMonoCecilTemplateGenerator(AssetsManager manager, string dataFolder, out string managedFolder,
            ICollection<string> diagnostics = null)
        {
            managedFolder =
                (!string.IsNullOrWhiteSpace(ManagedFolderOverride) && Directory.Exists(ManagedFolderOverride))
                    ? ManagedFolderOverride
                    : ResolveManagedFolder(dataFolder);
            if (string.IsNullOrWhiteSpace(managedFolder))
                return false;

            try
            {
                // Rocks иначе подгружается только по AssemblyResolve при первом обращении к Cecil;
                // MonoCecilTempGenerator может упасть раньше, тогда снаружи выглядит как «нет Managed».
                string exeDir = null;
                try
                {
                    exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                }
                catch
                {
                    /* ignore */
                }

                Program.TryLoadMonoCecilRocksAssembly(exeDir);
                manager.MonoTempGenerator = new MonoCecilTempGenerator(managedFolder);
                return true;
            }
            catch (Exception ex)
            {
                diagnostics?.Add(
                    "[MonoCecil] Не удалось инициализировать разбор Managed/* .dll: " + ex.GetType().Name + " — " + ex.Message +
                    " (каталог: «" + managedFolder + "»).");
                managedFolder = null;
                return false;
            }
        }

        /// <summary>
        /// Mono.Cecil.Rocks.dll лежит в том же NuGet-пакете, что и Mono.Cecil.dll, но MSBuild копирует в выход только явно
        /// указанные ссылки. Если Rocks нет рядом с exe, <see cref="MonoCecilTempGenerator"/> падает с FileNotFoundException при разборе типов.
        /// </summary>
        public static string GetMonoCecilSatelliteAssemblyDiagnosticOrNull()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (string.Equals(asm.GetName().Name, "Mono.Cecil.Rocks", StringComparison.OrdinalIgnoreCase))
                            return null;
                    }
                    catch
                    {
                        // skip
                    }
                }

                var self = Assembly.GetExecutingAssembly();
                if (self.GetManifestResourceStream(Program.EmbeddedMonoCecilRocksResourceName) != null)
                    return null;

                var dir = Path.GetDirectoryName(self.Location);
                if (string.IsNullOrEmpty(dir))
                    return null;

                var rocks = Path.Combine(dir, "Mono.Cecil.Rocks.dll");
                if (File.Exists(rocks))
                    return null;

                return "Не удалось подключить Mono.Cecil.Rocks (нужна Mono.Cecil для AssetsTools.NET.MonoCecil). Пересоберите приложение: Rocks встроен в exe или должен лежать рядом с UnityTextTranslator.exe.";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Загружает до <paramref name="maxRootLoads"/> файлов .assets из дерева <paramref name="dataFolder"/> (порядок приоритетный).
        /// </summary>
        /// <summary>Сгенерированный нами артефакт (вывод сборки / временный файл импорта), а не оригинальный контейнер игры.</summary>
        public static bool IsUttGeneratedAssetsArtifact(string fileNameOrPath)
        {
            if (string.IsNullOrEmpty(fileNameOrPath))
                return false;
            var name = Path.GetFileName(fileNameOrPath);
            return name.EndsWith(".translated.assets", StringComparison.OrdinalIgnoreCase)
                || name.IndexOf("_utt_import_tmp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void PreloadAllAssetsFromDataFolder(AssetsManager manager, string dataFolder, int maxRootLoads = 480)
        {
            var paths = EnumerateAssetPathsSorted(dataFolder);
            var loaded = CollectLoadedAssetFullPaths(manager);
            var attempts = 0;

            foreach (var p in paths)
            {
                if (attempts >= maxRootLoads)
                    break;

                string full;
                try
                {
                    full = Path.GetFullPath(p);
                }
                catch
                {
                    continue;
                }

                if (loaded.Contains(full))
                    continue;

                // НЕ подгружаем наши собственные артефакты: вывод «*.translated.assets» и временный
                // «*_utt_import_tmp». Иначе менеджер держит их открытыми на чтение, и запись результата
                // в тот же «*.translated.assets» падает «файл используется другим процессом» (lock самим
                // приложением). Реальные зависимости игры ссылаются на оригинальные имена, не на эти.
                if (IsUttGeneratedAssetsArtifact(full))
                    continue;

                try
                {
                    manager.LoadAssetsFile(full, loadDeps: true);
                    attempts++;
                    foreach (var x in CollectLoadedAssetFullPaths(manager))
                        loaded.Add(x);
                }
                catch
                {
                    // часть ресурсов может быть недоступна или не assets
                }
            }
        }

        public static AssetsFileInstance GetOrLoadPrimaryAssetsFile(AssetsManager manager, string primaryAssetsPath)
        {
            string full;
            try
            {
                full = Path.GetFullPath(primaryAssetsPath);
            }
            catch (Exception ex)
            {
                throw new FileNotFoundException("Некорректный путь к контейнеру Unity.", primaryAssetsPath, ex);
            }

            if (!File.Exists(full))
                throw new FileNotFoundException("Файл контейнера Unity не найден.", primaryAssetsPath);

            foreach (AssetsFileInstance inst in manager.Files)
            {
                var p = AssetsFileInstancePathField?.GetValue(inst) as string;
                if (string.IsNullOrEmpty(p))
                    continue;

                try
                {
                    if (string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase))
                        return inst;
                }
                catch
                {
                    // skip
                }
            }

            return manager.LoadAssetsFile(full, loadDeps: true);
        }

        /// <summary>Относительный путь для отображения (совместимо с .NET Framework 4.8).</summary>
        public static string MakeRelativePath(string rootFolder, string fullFilePath)
        {
            try
            {
                var root = Path.GetFullPath(rootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var target = Path.GetFullPath(fullFilePath);
                if (!root.EndsWith("" + Path.DirectorySeparatorChar))
                    root += Path.DirectorySeparatorChar;

                if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return target.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                // fallback ниже
            }

            return Path.GetFileName(fullFilePath);
        }

        public static bool TryPickAssetsFile(IWin32Window owner, string dataRoot, out string selectedFullPath)
        {
            selectedFullPath = null;
            var paths = EnumerateAssetPathsSorted(dataRoot);
            if (paths.Count == 0)
                return false;

            if (paths.Count == 1)
            {
                selectedFullPath = paths[0];
                return true;
            }

            using (var form = new Form())
            {
                form.Text = "Unity asset-контейнер";
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.Width = 640;
                form.Height = 460;

                var lbl = new Label
                {
                    Dock = DockStyle.Top,
                    AutoSize = false,
                    Height = 52,
                    Padding = new Padding(12, 10, 12, 6),
                    Text = "Файлы уже загружены из Unity Data. Выбери контейнер (level0, sharedassets*.assets, resources.assets) — источник объектов или цель сборки."
                };

                var bottom = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(8),
                    Height = 48,
                    AutoSize = false
                };

                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 100, Margin = new Padding(6) };
                var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Width = 100, Margin = new Padding(6) };
                bottom.Controls.Add(cancel);
                bottom.Controls.Add(ok);

                var list = new ListBox
                {
                    Dock = DockStyle.Fill,
                    Font = new System.Drawing.Font("Segoe UI", 9f)
                };

                foreach (var p in paths)
                    list.Items.Add(MakeRelativePath(dataRoot, p));

                var preferred = 0;
                for (var i = 0; i < paths.Count; i++)
                {
                    if (Path.GetFileName(paths[i]).IndexOf("sharedassets", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        preferred = i;
                        break;
                    }
                }

                list.SelectedIndex = preferred;

                form.Controls.Add(bottom);
                form.Controls.Add(lbl);
                form.Controls.Add(list);

                form.AcceptButton = ok;
                form.CancelButton = cancel;

                if (form.ShowDialog(owner) != DialogResult.OK || list.SelectedIndex < 0)
                    return false;

                selectedFullPath = paths[list.SelectedIndex];
                return true;
            }
        }
    }
}
