using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace UnityTextTranslator
{
    internal static class MonoBehaviourScriptResolver
    {
        /// <summary>
        /// Короткое имя класса из MonoScript (как в именах дампов UABEAvalonia: Button, CanvasScaler, …).
        /// </summary>
        internal static string TryGetMonoScriptShortClassName(
            AssetsManager manager,
            AssetsFileInstance fromFile,
            AssetTypeValueField monoBehaviourRoot,
            AssetReadFlags readFlags)
        {
            if (manager == null || fromFile == null || monoBehaviourRoot == null)
                return null;

            try
            {
                var scriptPtr = monoBehaviourRoot["m_Script"];
                if (scriptPtr == null || scriptPtr.IsDummy)
                    return null;

                var ext = manager.GetExtAsset(fromFile, scriptPtr, false, readFlags);
                if (ext.baseField == null)
                    return null;

                var className = ext.baseField["m_ClassName"]?.AsString;
                if (!string.IsNullOrWhiteSpace(className))
                    return className.Trim();

                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
