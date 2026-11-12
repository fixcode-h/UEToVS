using System;
using System.Collections.Generic;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using UEToVS.Host.Logic.IntelliSense;

namespace UEToVS.Host.IntelliSense
{
    /// <summary>
    /// <see cref="ICppSettingsStore"/> over DTE.Properties["TextEditor", page]. UI thread only. Property names are
    /// resolved case-insensitively against the page's enumerated names (exact casing differs between VS versions).
    /// Every access is individually guarded: one broken property never stops the others.
    /// </summary>
    internal sealed class DteCppSettingsStore : ICppSettingsStore
    {
        private readonly DTE _dte;
        private readonly Dictionary<string, Properties> _pages = new Dictionary<string, Properties>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public DteCppSettingsStore(DTE dte)
        {
            _dte = dte ?? throw new ArgumentNullException(nameof(dte));
        }

        /// <summary>Page lookup failures (page name → error), for the log.</summary>
        public Dictionary<string, string> PageErrors { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Properties Page(string page)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_pages.TryGetValue(page, out var p)) return p;
            try
            {
                p = _dte.get_Properties("TextEditor", page);
            }
            catch (Exception ex)
            {
                PageErrors[page] = ex.GetType().Name + ": " + ex.Message;
                p = null;
            }
            _pages[page] = p;
            return p;
        }

        public IReadOnlyList<string> GetPropertyNames(string page)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_names.TryGetValue(page, out var cached)) return cached;
            var props = Page(page);
            List<string> names = null;
            if (props != null)
            {
                names = new List<string>();
                try
                {
                    foreach (Property p in props)
                    {
                        try { names.Add(p.Name); }
                        catch (Exception) { /* a single unreadable entry */ }
                    }
                }
                catch (Exception ex)
                {
                    PageErrors[page + "#enumerate"] = ex.GetType().Name + ": " + ex.Message;
                    names = null;
                }
            }
            _names[page] = names;
            return names;
        }

        private string ActualName(string page, string name)
        {
            var names = GetPropertyNames(page);
            if (names == null) return name; // cannot enumerate: try the name as given
            foreach (var n in names)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return n;
            return null;
        }

        public bool TryRead(string page, string name, out object value, out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            value = null;
            error = null;
            var props = Page(page);
            if (props == null)
            {
                error = "page not available";
                return false;
            }
            string actual = ActualName(page, name);
            if (actual == null)
            {
                error = "not found";
                return false;
            }
            try
            {
                value = props.Item(actual).Value;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        public bool TryWrite(string page, string name, object value, out string error)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            error = null;
            var props = Page(page);
            if (props == null)
            {
                error = "page not available";
                return false;
            }
            string actual = ActualName(page, name);
            if (actual == null)
            {
                error = "not found";
                return false;
            }
            try
            {
                props.Item(actual).Value = value;
                // Verify: some properties silently ignore writes.
                object back = props.Item(actual).Value;
                if (!Equals(back, value))
                {
                    error = "write did not stick (read back " + (back ?? "null") + ")";
                    return false;
                }
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
