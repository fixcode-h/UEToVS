using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>
    /// <see cref="ICppSettingsStore"/> over VS 18's unified settings file (settings.json: a "/* Visual Studio Settings File */"
    /// header and a flat object of dotted keys). Only answers <see cref="VsUnrealFeatures.JsonPage"/>. VS writes only the keys
    /// that differ from their default, so an absent key reads as its <see cref="VsUnrealFeatures.JsonDefaults"/> value and writing
    /// the default value removes the key. VS watches the file and applies edits. Every write re-reads the file first (VS may have
    /// written it since), keeps the header and every other key, and never throws.
    /// </summary>
    public sealed class UnifiedSettingsJsonStore : ICppSettingsStore
    {
        private const string DefaultHeader = "/* Visual Studio Settings File */";
        private readonly string _path;
        private readonly IReadOnlyDictionary<string, bool> _defaults;

        public UnifiedSettingsJsonStore(string path, IReadOnlyDictionary<string, bool> defaults = null)
        {
            _path = path;
            _defaults = defaults ?? VsUnrealFeatures.JsonDefaults;
        }

        public string Path => _path;

        public IReadOnlyList<string> GetPropertyNames(string page)
        {
            if (!IsOurs(page) || !TryLoad(out var obj, out _, out _)) return null;
            return obj.Properties().Select(p => p.Name).ToList();
        }

        public bool TryRead(string page, string name, out object value, out string error)
        {
            value = null;
            if (!IsOurs(page)) { error = "not a settings.json page"; return false; }
            if (!TryLoad(out var obj, out _, out error)) return false;
            var prop = Find(obj, name);
            if (prop != null)
            {
                value = prop.Value.Type == JTokenType.Boolean ? (object)prop.Value.Value<bool>() : prop.Value.ToString(Formatting.None);
                return true;
            }
            if (_defaults.TryGetValue(name, out bool d))
            {
                value = d;
                return true;
            }
            error = "key not in settings.json and default unknown";
            return false;
        }

        public bool TryWrite(string page, string name, object value, out string error)
        {
            if (!IsOurs(page)) { error = "not a settings.json page"; return false; }
            if (!(value is bool b)) { error = "only booleans are written"; return false; }
            if (!TryLoad(out var obj, out string header, out error)) return false;
            var prop = Find(obj, name);
            if (_defaults.TryGetValue(name, out bool d) && d == b)
                prop?.Remove();
            else if (prop != null)
                prop.Value = b;
            else
                obj.Add(name, b);
            try
            {
                File.WriteAllText(_path, Serialize(header, obj), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>The file text for a header and an object (2-space indent, like VS writes it).</summary>
        public static string Serialize(string header, JObject obj)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(header)) sb.Append(header.TrimEnd()).Append("\r\n");
            sb.Append(obj.ToString(Formatting.Indented).Replace("\r\n", "\n").Replace("\n", "\r\n"));
            return sb.ToString();
        }

        private static bool IsOurs(string page) => string.Equals(page, VsUnrealFeatures.JsonPage, StringComparison.OrdinalIgnoreCase);

        private static JProperty Find(JObject obj, string name) =>
            obj.Properties().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        private bool TryLoad(out JObject obj, out string header, out string error)
        {
            obj = null;
            header = DefaultHeader;
            error = null;
            if (string.IsNullOrEmpty(_path)) { error = "settings.json path unknown"; return false; }
            string text;
            try
            {
                if (!File.Exists(_path))
                {
                    error = "settings.json not found: " + _path;
                    return false;
                }
                text = File.ReadAllText(_path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            int brace = text.IndexOf('{');
            if (brace < 0)
            {
                if (text.Trim().Length == 0 || text.Trim() == DefaultHeader)
                {
                    obj = new JObject();
                    return true;
                }
                error = "settings.json has no object";
                return false;
            }
            header = text.Substring(0, brace).Trim();
            try
            {
                obj = JObject.Parse(text.Substring(brace), new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
                return true;
            }
            catch (JsonException ex)
            {
                // Never rewrite a file we could not read: VS's own settings would be lost.
                error = "settings.json unreadable: " + ex.Message;
                return false;
            }
        }
    }

    /// <summary>Routes <see cref="VsUnrealFeatures.JsonPage"/> to the settings.json store and every other page to the DTE store.</summary>
    public sealed class CompositeCppSettingsStore : ICppSettingsStore
    {
        private readonly ICppSettingsStore _options;
        private readonly ICppSettingsStore _json;

        public CompositeCppSettingsStore(ICppSettingsStore options, ICppSettingsStore json)
        {
            _options = options;
            _json = json;
        }

        private ICppSettingsStore For(string page) =>
            string.Equals(page, VsUnrealFeatures.JsonPage, StringComparison.OrdinalIgnoreCase) ? _json : _options;

        public IReadOnlyList<string> GetPropertyNames(string page) => For(page)?.GetPropertyNames(page);

        public bool TryRead(string page, string name, out object value, out string error)
        {
            var s = For(page);
            if (s == null) { value = null; error = "store unavailable"; return false; }
            return s.TryRead(page, name, out value, out error);
        }

        public bool TryWrite(string page, string name, object value, out string error)
        {
            var s = For(page);
            if (s == null) { error = "store unavailable"; return false; }
            return s.TryWrite(page, name, value, out error);
        }
    }
}
