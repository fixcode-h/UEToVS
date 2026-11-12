using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace UEToVS.UI
{
    /// <summary>Yellow bar at the top of the main window with action links.</summary>
    internal sealed class InfoBar : IVsInfoBarUIEvents
    {
        private readonly Dictionary<string, Action> _actions = new Dictionary<string, Action>();
        private IVsInfoBarUIElement _element;
        private uint _cookie;

        private static InfoBar _current;

        public static void Show(string text, params (string label, Action action)[] actions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _current?.Close();
            var bar = new InfoBar();
            var links = new List<IVsInfoBarActionItem>();
            foreach (var (label, action) in actions)
            {
                bar._actions[label] = action;
                links.Add(new InfoBarHyperlink(label, label));
            }
            var model = new InfoBarModel(new[] { new InfoBarTextSpan(text) }, links, KnownMonikers.StatusInformation, isCloseButtonVisible: true);
            try
            {
                var factory = (IVsInfoBarUIFactory)ServiceProvider.GlobalProvider.GetService(typeof(SVsInfoBarUIFactory));
                var shell = (IVsShell)ServiceProvider.GlobalProvider.GetService(typeof(SVsShell));
                if (factory == null || shell == null) return;
                shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObj);
                if (!(hostObj is IVsInfoBarHost host)) return;
                bar._element = factory.CreateInfoBar(model);
                bar._element.Advise(bar, out bar._cookie);
                host.AddInfoBar(bar._element);
                _current = bar;
            }
            catch (Exception ex)
            {
                OutputLog.Write("InfoBar failed: " + ex.Message + " — " + text);
            }
        }

        public void Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _element?.Close();
        }

        public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            infoBarUIElement.Unadvise(_cookie);
            if (_current == this) _current = null;
        }

        public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (actionItem.ActionContext is string key && _actions.TryGetValue(key, out var action))
            {
                infoBarUIElement.Close();
                action?.Invoke();
            }
        }
    }
}
