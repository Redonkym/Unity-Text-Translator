using AssetsTools.NET;
using AssetsTools.NET.Extra;
using System;

namespace UnityTextTranslator
{
    /// <summary>
    /// Вспомогательные сведения об ассетах без UABEA GUI.
    /// </summary>
    internal static class AssetHelper
    {
        /// <summary>
        /// Имя встроенного типа Unity по class ID (<see cref="AssetClassID"/>) или числовой TypeId.
        /// </summary>
        internal static string GetTypeName(AssetsFile file, AssetFileInfo info)
        {
            if (file == null || info == null)
                return "not found";

            try
            {
                var typeId = info.GetTypeId(file);
                if (Enum.IsDefined(typeof(AssetClassID), typeId))
                    return ((AssetClassID)typeId).ToString();
                return "TypeId=" + typeId;
            }
            catch (Exception ex)
            {
                return "TypeId?: " + ex.Message;
            }
        }
    }
}
