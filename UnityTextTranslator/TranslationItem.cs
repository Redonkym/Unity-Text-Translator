using System.Collections.Generic;

namespace UnityTextTranslator
{
    public class TranslationItem
    {
        public string FileName { get; set; }
        public string DisplayPath { get; set; }
        public List<string> PathKeys { get; set; }
        public string Original { get; set; }
        public string Translated { get; set; }
    }
}