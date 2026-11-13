using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UEToVS.Host.Logic.Horde
{
    /// <summary>
    /// Minimal in-place editor for UBT's BuildConfiguration.xml: reads and changes single
    /// <c>&lt;Configuration&gt;&lt;Section&gt;&lt;Key&gt;</c> values and leaves everything else (other keys, comments,
    /// formatting, the XML declaration) as it was. New elements get the file's indentation.
    /// </summary>
    public sealed class BuildConfigurationXml
    {
        public const string Namespace = "https://www.unrealengine.com/BuildConfiguration";

        private const string NewFileText =
            "<?xml version=\"1.0\" encoding=\"utf-8\" ?>\r\n<Configuration xmlns=\"" + Namespace + "\">\r\n</Configuration>\r\n";

        private readonly XDocument _doc;
        private readonly string _indentUnit;
        private readonly string _newLine;
        private readonly string _prefix;
        private readonly string _suffix;

        private BuildConfigurationXml(XDocument doc, string text)
        {
            _doc = doc;
            var open = System.Text.RegularExpressions.Regex.Match(text, @"<(?:\w+:)?" + doc.Root.Name.LocalName + @"\b");
            _prefix = open.Success ? text.Substring(0, open.Index) : string.Empty;
            string close = "</" + doc.Root.Name.LocalName + ">";
            int closeAt = text.LastIndexOf(close, StringComparison.Ordinal);
            if (closeAt < 0)
            {
                int selfClose = text.LastIndexOf("/>", StringComparison.Ordinal);
                _suffix = selfClose >= 0 ? text.Substring(selfClose + 2) : string.Empty;
            }
            else _suffix = text.Substring(closeAt + close.Length);
            _newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            _indentUnit = DetectIndent(text);
        }

        public bool Changed { get; private set; }

        private XElement Root => _doc.Root;

        public static BuildConfigurationXml Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) text = NewFileText;
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            if (doc.Root == null || doc.Root.Name.LocalName != "Configuration")
                throw new InvalidDataException("BuildConfiguration.xml has no <Configuration> root element");
            return new BuildConfigurationXml(doc, text);
        }

        /// <summary>Loads a file, or a new empty configuration when it does not exist.</summary>
        public static BuildConfigurationXml Load(string path) =>
            Parse(File.Exists(path) ? File.ReadAllText(path) : null);

        private static string DetectIndent(string text)
        {
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int i = 0;
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
                if (i > 0 && i < line.Length && line[i] == '<') return line.Substring(0, i);
            }
            return "\t";
        }

        private XName N(string local) => Root.Name.Namespace + local;

        private XElement Section(string section) =>
            Root.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, section, StringComparison.Ordinal));

        private static XElement Child(XElement parent, string key) =>
            parent?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, key, StringComparison.Ordinal));

        /// <summary>Current value (trimmed), or null when the key is absent.</summary>
        public string Get(string section, string key) => Child(Section(section), key)?.Value.Trim();

        /// <summary>Sets a value; null removes the key (and a section left empty).</summary>
        public void Set(string section, string key, string value)
        {
            var sec = Section(section);
            var el = Child(sec, key);
            if (value == null)
            {
                if (el == null) return;
                RemoveWithIndent(el);
                if (!sec.Elements().Any()) RemoveWithIndent(sec);
                Changed = true;
                return;
            }
            if (el != null)
            {
                if (el.Value.Trim() == value) return;
                el.Value = value;
                Changed = true;
                return;
            }
            if (sec == null)
            {
                sec = new XElement(N(section));
                AddChild(Root, sec, string.Empty);
            }
            AddChild(sec, new XElement(N(key), value), IndentOf(sec));
            Changed = true;
        }

        private static string IndentOf(XElement e)
        {
            if (e.PreviousNode is XText t)
            {
                int nl = t.Value.LastIndexOf('\n');
                return nl >= 0 ? t.Value.Substring(nl + 1) : string.Empty;
            }
            return string.Empty;
        }

        private void AddChild(XElement parent, XElement child, string parentIndent)
        {
            string childIndent = parentIndent + _indentUnit;
            var last = parent.Elements().LastOrDefault();
            if (last == null)
            {
                // Only whitespace inside (or nothing): replace it with "\n<indent>child\n<parentIndent>".
                foreach (var t in parent.Nodes().OfType<XText>().Where(t => string.IsNullOrWhiteSpace(t.Value)).ToList()) t.Remove();
                parent.Add(new XText(_newLine + childIndent), child, new XText(_newLine + parentIndent));
                return;
            }
            last.AddAfterSelf(new XText(_newLine + childIndent), child);
        }

        private static void RemoveWithIndent(XElement e)
        {
            if (e.PreviousNode is XText t && string.IsNullOrWhiteSpace(t.Value)) t.Remove();
            e.Remove();
        }

        public override string ToString()
        {
            // Only the root element is serialized; the text around it (declaration, comments, line breaks) is kept as read.
            // The XML parser turns CRLF into LF inside the document; put the file's line breaks back.
            string root = Root.ToString(SaveOptions.DisableFormatting);
            if (_newLine == "\r\n") root = root.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return _prefix + root + _suffix;
        }

        /// <summary>Writes the file (creating its folder) through a temporary file, so a crash never leaves half a file.</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string tmp = path + ".uetovs-tmp";
            File.WriteAllText(tmp, ToString(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            Changed = false;
        }

    }
}
