using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using UEToVS.Host.Logic.IntelliSense;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>
    /// <see cref="ICppSettingsStore"/> for VS's Blueprint support options: pages named "settings:&lt;collection&gt;" are
    /// collections of the VS user settings store (booleans may be stored as Int32), every other page goes to the C/C++
    /// options page store. UI thread only. Discovery walks the user settings store once (bounded) and lists the
    /// Blueprint-related options it finds, so the log shows exactly what exists on this VS.
    /// </summary>
    internal sealed class VsBlueprintSettingsStore : ICppSettingsStore
    {
        private const int MaxCollections = 20000;
        private readonly DteCppSettingsStore _dte;
        private readonly WritableSettingsStore _user;

        public VsBlueprintSettingsStore(DteCppSettingsStore dte, WritableSettingsStore user)
        {
            _dte = dte;
            _user = user;
        }

        /// <summary>Every (page, name) on this VS that may switch the Blueprint scan, plus the number of collections visited.</summary>
        public List<KeyValuePair<string, string>> Discover(IEnumerable<string> dtePages, out int collectionsVisited)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var found = new List<KeyValuePair<string, string>>();
            if (_dte != null)
                foreach (var page in dtePages)
                    foreach (var n in _dte.GetPropertyNames(page) ?? (IReadOnlyList<string>)Array.Empty<string>())
                        if (VsBlueprintSupport.IsCandidateName(n)) found.Add(new KeyValuePair<string, string>(page, n));
            collectionsVisited = 0;
            if (_user == null) return found;
            var queue = new Queue<string>();
            queue.Enqueue("");
            while (queue.Count > 0 && collectionsVisited < MaxCollections)
            {
                string c = queue.Dequeue();
                collectionsVisited++;
                IEnumerable<string> subs;
                try { subs = _user.GetSubCollectionNames(c); }
                catch (Exception) { continue; }
                foreach (var sub in subs) queue.Enqueue(c.Length == 0 ? sub : c + "\\" + sub);
                if (!VsBlueprintSupport.IsUnrealCollection(c)) continue;
                IEnumerable<string> names;
                try { names = _user.GetPropertyNames(c); }
                catch (Exception) { continue; }
                foreach (var n in names)
                {
                    if (!VsBlueprintSupport.IsCandidateName(n)) continue;
                    SettingsType t;
                    try { t = _user.GetPropertyType(c, n); }
                    catch (Exception) { continue; }
                    if (t == SettingsType.Int32 || t == SettingsType.String) found.Add(new KeyValuePair<string, string>(VsBlueprintSupport.SettingsStorePrefix + c, n));
                }
            }
            return found;
        }

        private static bool IsUserPage(string page, out string collection)
        {
            bool user = page != null && page.StartsWith(VsBlueprintSupport.SettingsStorePrefix, StringComparison.Ordinal);
            collection = user ? page.Substring(VsBlueprintSupport.SettingsStorePrefix.Length) : null;
            return user;
        }

        public IReadOnlyList<string> GetPropertyNames(string page)
        {
            if (!IsUserPage(page, out string c)) return _dte?.GetPropertyNames(page);
            try { return new List<string>(_user.GetPropertyNames(c)); }
            catch (Exception) { return null; }
        }

        public bool TryRead(string page, string name, out object value, out string error)
        {
            value = null;
            error = null;
            if (!IsUserPage(page, out string c))
            {
                if (_dte != null) return _dte.TryRead(page, name, out value, out error);
                error = "no options store";
                return false;
            }
            try
            {
                if (_user == null || !_user.PropertyExists(c, name)) { error = "missing"; return false; }
                switch (_user.GetPropertyType(c, name))
                {
                    case SettingsType.Int32:
                        value = _user.GetInt32(c, name) != 0;
                        return true;
                    case SettingsType.String:
                        string s = _user.GetString(c, name);
                        if (bool.TryParse(s, out bool b)) { value = b; return true; }
                        if (s == "0" || s == "1") { value = s == "1"; return true; }
                        value = s;
                        return true;
                    default:
                        value = _user.GetPropertyType(c, name).ToString();
                        return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        public bool TryWrite(string page, string name, object value, out string error)
        {
            error = null;
            if (!IsUserPage(page, out string c))
            {
                if (_dte != null) return _dte.TryWrite(page, name, value, out error);
                error = "no options store";
                return false;
            }
            try
            {
                bool b = value is bool v && v;
                if (_user.GetPropertyType(c, name) == SettingsType.String)
                {
                    string old = _user.GetString(c, name);
                    _user.SetString(c, name, old == "0" || old == "1" ? (b ? "1" : "0") : (b ? "True" : "False"));
                }
                else _user.SetInt32(c, name, b ? 1 : 0);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }
    }
}
