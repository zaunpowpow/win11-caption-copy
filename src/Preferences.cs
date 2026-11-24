using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;

namespace CaptionCopy
{
    public sealed class AppPreferences
    {
        private double _opacity = 1;
        public double Opacity
        {
            get { return _opacity; }
            set { _opacity = double.IsNaN(value) || double.IsInfinity(value) ? 1 : Math.Max(0.3, Math.Min(1, value)); }
        }
        public bool ProtectText { get; set; }
        public AppPreferences() { ProtectText = true; }
        public double WindowOpacity { get { return ProtectText ? 1 : Opacity; } }
        public double BackgroundOpacity { get { return ProtectText ? Opacity : 1; } }
    }

    public static class PreferencesStore
    {
        public static string DefaultPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CaptionCopy", "settings.xml"); }
        }
        public static AppPreferences Load(string path)
        {
            var result = new AppPreferences();
            try
            {
                if (!File.Exists(path)) return result;
                var document = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
                double opacity;
                bool protect;
                var root = document.DocumentElement;
                if (root == null || root.Name != "CaptionCopy") return result;
                if (double.TryParse(root.GetAttribute("opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out opacity)) result.Opacity = opacity;
                if (bool.TryParse(root.GetAttribute("protectText"), out protect)) result.ProtectText = protect;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (XmlException) { }
            return result;
        }
        public static bool Save(string path, AppPreferences preferences)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                using (var writer = XmlWriter.Create(path, new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("CaptionCopy");
                    writer.WriteAttributeString("opacity", preferences.Opacity.ToString("R", CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("protectText", preferences.ProtectText.ToString(CultureInfo.InvariantCulture));
                    writer.WriteEndElement();
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public static class SelectionText
    {
        public static string Join(IList<string> rows, int anchor, int end)
        {
            if (anchor < 0 || end < 0 || anchor >= rows.Count || end >= rows.Count) return "";
            var selected = new List<string>();
            for (int i = Math.Min(anchor, end); i <= Math.Max(anchor, end); i++) selected.Add(rows[i]);
            return string.Join(Environment.NewLine, selected);
        }
    }
}
