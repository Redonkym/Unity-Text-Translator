using AddressablesTools;
using AddressablesTools.Catalog;
using AddressablesTools.Classes;
using AssetsTools.NET;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.UTF8Encoding.UTF8;
            Console.InputEncoding = System.Text.UTF8Encoding.UTF8;
        }
        catch
        {
            /* ignore */
        }

        if (args.Length < 2 || args[0] != "--patch-near-bundle")
        {
            // ASCII only: parent process decodes redirected stdout as UTF-8; Cyrillic/OEM breaks the log UI.
            Console.WriteLine(
                "Usage: AddressablesCatalogCrcZero --patch-near-bundle <path\\to\\file.bundle>");
            return 64;
        }

        var bundlePath = Path.GetFullPath(args[1]);
        if (!File.Exists(bundlePath))
        {
            Console.WriteLine("ERROR: bundle not found: " + bundlePath);
            return 1;
        }

        var catalogs = FindCatalogPathsNear(bundlePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (catalogs.Count == 0)
        {
            Console.WriteLine(
                "ERROR: no catalog (*catalog*.json|.bin|.bundle) found under aa/ or StreamingAssets near this bundle.");
            return 2;
        }

        var ok = 0;
        foreach (var path in catalogs)
        {
            try
            {
                if (TryPatchCatalogFile(path))
                {
                    ok++;
                    Console.WriteLine("OK patched AssetBundle CRC=0: " + path);
                }
                else
                {
                    Console.WriteLine("SKIP not a catalog or unsupported: " + path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR file \"" + path + "\": " + ex.Message);
            }
        }

        if (ok == 0)
        {
            Console.WriteLine("ERROR: no catalog file could be patched (0 OK).");
            return 3;
        }

        return 0;
    }

    private static IEnumerable<string> FindCatalogPathsNear(string bundlePath)
    {
        var dir = Path.GetDirectoryName(bundlePath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            yield break;

        var aaHint = WalkUpUntilSegmentNamedAa(dir);

        if (!string.IsNullOrEmpty(aaHint))
        {
            foreach (var f in EnumerateCandidateCatalogFiles(aaHint))
                yield return f;

            // Каталог иногда лежит рядом с папкой «aa», а не внутри (StreamingAssets\catalog*.json).
            var streamingOrParent = Path.GetDirectoryName(aaHint);
            if (!string.IsNullOrEmpty(streamingOrParent) && Directory.Exists(streamingOrParent))
            {
                foreach (var f in Directory.GetFiles(streamingOrParent, "*catalog*", SearchOption.TopDirectoryOnly))
                {
                    if (!File.Exists(f))
                        continue;
                    var ext = Path.GetExtension(f.AsSpan());
                    if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".bundle", StringComparison.OrdinalIgnoreCase))
                        yield return f;
                }
            }

            yield break;
        }

        for (var d = dir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)!)
        {
            foreach (var f in Directory.GetFiles(d))
            {
                var name = Path.GetFileName(f.AsSpan());
                if (!name.Contains("catalog", StringComparison.OrdinalIgnoreCase))
                    continue;
                var ext = Path.GetExtension(name);
                if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".bundle", StringComparison.OrdinalIgnoreCase))
                    yield return f;
            }

            var seg = Path.GetFileName(d.AsSpan());
            if (seg.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                break;
        }
    }

    private static IEnumerable<string> EnumerateCandidateCatalogFiles(string aaRootOrTree)
    {
        if (!Directory.Exists(aaRootOrTree))
            yield break;

        foreach (var f in Directory.GetFiles(aaRootOrTree, "*catalog*", SearchOption.AllDirectories))
        {
            if (!File.Exists(f))
                continue;
            var ext = Path.GetExtension(f.AsSpan());
            if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".bin", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".bundle", StringComparison.OrdinalIgnoreCase))
                yield return f;
        }

        var j = Path.Combine(aaRootOrTree, "catalog.json");
        if (File.Exists(j))
            yield return j;

        var b = Path.Combine(aaRootOrTree, "catalog.bin");
        if (File.Exists(b))
            yield return b;
    }

    /// <summary>StreamingAssets\aa\StandaloneWindows64\…</summary>
    private static string? WalkUpUntilSegmentNamedAa(string startDir)
    {
        for (var d = startDir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)!)
        {
            if (Path.GetFileName(d).Equals("aa", StringComparison.OrdinalIgnoreCase))
                return d;

            if (Path.GetFileName(d).EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
                break;
        }

        return null;
    }

    private static bool TryPatchCatalogFile(string path)
    {
        if (!File.Exists(path))
            return false;
        if (!IsReadableSize(path, out var sizeReason))
        {
            Console.WriteLine("SKIP catalog size: " + path + " (" + sizeReason + ")");
            return false;
        }

        var fromBundle = IsUnityFsAssetBundleHeader(path);

        ContentCatalogData ccd;
        CatalogFileType jsonOrBinType = CatalogFileType.None;

        if (fromBundle)
        {
            ccd = AddressablesCatalogFileParser.FromBundle(path);
        }
        else
        {
            using (FileStream fs = File.OpenRead(path))
                jsonOrBinType = AddressablesCatalogFileParser.GetCatalogFileType(fs);

            switch (jsonOrBinType)
            {
                case CatalogFileType.Json:
                    ccd = AddressablesCatalogFileParser.FromJsonString(File.ReadAllText(path));
                    break;
                case CatalogFileType.Binary:
                    ccd = AddressablesCatalogFileParser.FromBinaryData(File.ReadAllBytes(path));
                    break;
                default:
                    return false;
            }
        }

        ZeroAssetBundleProviderCrcs(ccd);

        var tmp = path + ".utt-patchnext-" + Guid.NewGuid().ToString("N") + ".tmp";
        WritePatched(ccd, path, tmp, fromBundle, jsonOrBinType);

        var backupPath = path + ".utt-prev-catalogbak";
        try
        {
            File.Replace(tmp, path, backupPath);
        }
        catch
        {
            File.Copy(path, backupPath, overwrite: true);
            File.Copy(tmp, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
                /* ignore */
            }
        }

        return true;
    }

    private static bool IsReadableSize(string path, out string reason)
    {
        reason = "";
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length <= 32)
            {
                reason = "file too small";
                return false;
            }

            if (fi.Length >= 450L * 1024 * 1024)
            {
                reason = "file larger than 450 MB limit";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static void WritePatched(ContentCatalogData ccd, string originalPath, string tempOutPath,
        bool fromBundle, CatalogFileType jsonOrBinType)
    {
        if (fromBundle)
        {
            AddressablesCatalogFileParser.ToBundle(ccd, originalPath, tempOutPath);
        }
        else
        {
            switch (jsonOrBinType)
            {
                case CatalogFileType.Json:
                    File.WriteAllText(tempOutPath, AddressablesCatalogFileParser.ToJsonString(ccd));
                    break;
                case CatalogFileType.Binary:
                    File.WriteAllBytes(tempOutPath, AddressablesCatalogFileParser.ToBinaryData(ccd));
                    break;
                default:
                    throw new InvalidOperationException("Unknown catalog file type.");
            }
        }
    }

    private static bool IsUnityFsAssetBundleHeader(string path)
    {
        ReadOnlySpan<byte> unityFs = "UnityFS"u8;
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < unityFs.Length)
                return false;
            Span<byte> hdr = stackalloc byte[unityFs.Length];
            if (fs.Read(hdr) != unityFs.Length)
                return false;
            return hdr.SequenceEqual(unityFs);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>См. nesrak1/AddressablesTools Example patchcrc — отключение проверки CRC AssetBundle для модифицированных bundle.</summary>
    private static void ZeroAssetBundleProviderCrcs(ContentCatalogData ccd)
    {
        var seen = new HashSet<ResourceLocation>();
        foreach (var resourceList in ccd.Resources.Values)
        {
            foreach (var rsrc in resourceList)
            {
                if (rsrc.Dependencies != null)
                {
                    PatchCrcRecursive(rsrc, seen);
                    continue;
                }

                if (rsrc.ProviderId == "UnityEngine.ResourceManagement.ResourceProviders.AssetBundleProvider")
                {
                    var data = rsrc.Data;
                    if (data is WrappedSerializedObject { Object: AssetBundleRequestOptions abro })
                        abro.Crc = 0;
                }
            }
        }
    }

    private static void PatchCrcRecursive(ResourceLocation thisRsrc, HashSet<ResourceLocation> seenRsrcs)
    {
        if (seenRsrcs.Contains(thisRsrc))
            return;

        var data = thisRsrc.Data;
        if (data is WrappedSerializedObject { Object: AssetBundleRequestOptions abro })
            abro.Crc = 0;

        seenRsrcs.Add(thisRsrc);

        if (thisRsrc.Dependencies == null)
            return;

        foreach (var child in thisRsrc.Dependencies)
            PatchCrcRecursive(child, seenRsrcs);
    }
}
