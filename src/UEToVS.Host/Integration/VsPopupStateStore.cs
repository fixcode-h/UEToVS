using System;
using System.Collections.Generic;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Shells;

namespace UEToVS.Host.Integration
{
    /// <summary>
    /// <see cref="IPopupStateStore"/> for every UEToVS.UI popup (<see cref="PopupShell.DefaultStateStore"/>): size, preview
    /// split and preview visibility per popup id, kept in memory and written through to the VS per-user settings store
    /// (UEToVS\Popups, one property per id, <see cref="PopupStateText"/> format). Popup geometry is a per-user preference,
    /// so it follows the user across solutions. Called on the UI thread; storage failures are logged and never thrown.
    /// </summary>
    internal sealed class VsPopupStateStore : IPopupStateStore
    {
        private const string Collection = "UEToVS\\Popups";
        private static readonly Logger L = Log.For("Integration");
        private readonly Dictionary<string, PopupState> _cache = new Dictionary<string, PopupState>(StringComparer.Ordinal);
        private readonly HashSet<string> _loaded = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _written = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool TryGet(string id, out PopupState state)
        {
            state = null;
            if (string.IsNullOrEmpty(id)) return false;
            try
            {
                if (_loaded.Add(id))
                {
                    string text = VsUserSettings.Get(Collection, id);
                    if (text != null) _written[id] = text;
                    if (PopupStateText.TryParse(text, out double w, out double h, out double split, out bool? preview))
                        _cache[id] = new PopupState { Width = w, Height = h, PreviewSplit = split, PreviewVisible = preview };
                    else if (text != null)
                        L.Warn("popup.state.invalid", "Stored popup state ignored", ("id", id), ("text", text));
                }
                if (_cache.TryGetValue(id, out var s))
                {
                    state = s.Clone();
                    return true;
                }
            }
            catch (Exception ex)
            {
                L.Warn("popup.state.read.failed", "Reading popup state failed", ex, ("id", id));
            }
            return false;
        }

        public void Save(string id, PopupState state)
        {
            if (string.IsNullOrEmpty(id) || state == null) return;
            try
            {
                _loaded.Add(id);
                _cache[id] = state.Clone();
                string text = PopupStateText.Format(state.Width, state.Height, state.PreviewSplit, state.PreviewVisible);
                if (_written.TryGetValue(id, out var old) && old == text) return; // dismissed without resizing: no store write
                if (VsUserSettings.Set(Collection, id, text)) _written[id] = text;
            }
            catch (Exception ex)
            {
                L.Warn("popup.state.write.failed", "Saving popup state failed", ex, ("id", id));
            }
        }
    }
}
