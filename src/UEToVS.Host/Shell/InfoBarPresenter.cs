using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Integration;

namespace UEToVS.Host.Shell
{
    /// <summary>Default <see cref="IBannerPresenter"/>: VS InfoBar at the top of the main window. UI thread only.</summary>
    internal sealed class InfoBarPresenter : IBannerPresenter
    {
        private static readonly Logger L = Log.For("Banner");
        private readonly Dictionary<string, Bar> _bars = new Dictionary<string, Bar>(StringComparer.Ordinal);

        public void Show(string id, string text, BannerKind kind, IReadOnlyList<KeyValuePair<string, Action>> actions)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Close(id);
            L.Info("banner.show", text, ("id", id), ("kind", kind.ToString()));
            var bar = new Bar(this, id, actions ?? new KeyValuePair<string, Action>[0]);
            var links = bar.Actions.Select(a => (IVsInfoBarActionItem)new InfoBarHyperlink(a.Key, a.Key)).ToArray();
            ImageMoniker icon = kind == BannerKind.Error ? KnownMonikers.StatusError
                : kind == BannerKind.Warning ? KnownMonikers.StatusWarning : KnownMonikers.StatusInformation;
            var model = new InfoBarModel(new[] { new InfoBarTextSpan(text) }, links, icon, isCloseButtonVisible: true);
            var factory = ServiceProvider.GlobalProvider.GetService(typeof(SVsInfoBarUIFactory)) as IVsInfoBarUIFactory;
            var shell = ServiceProvider.GlobalProvider.GetService(typeof(SVsShell)) as IVsShell;
            if (factory == null || shell == null)
            {
                L.Warn("banner.unavailable", "InfoBar service unavailable", ("id", id));
                return;
            }
            shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObj);
            if (!(hostObj is IVsInfoBarHost host))
            {
                L.Warn("banner.unavailable", "No main window InfoBar host", ("id", id));
                return;
            }
            bar.Element = factory.CreateInfoBar(model);
            bar.Element.Advise(bar, out bar.Cookie);
            host.AddInfoBar(bar.Element);
            _bars[id] = bar;
        }

        public void Close(string id)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_bars.TryGetValue(id, out var bar))
            {
                _bars.Remove(id);
                try { bar.Element?.Close(); }
                catch (Exception ex) { L.Warn("banner.close.failed", "InfoBar close failed", ex, ("id", id)); }
            }
        }

        private sealed class Bar : IVsInfoBarUIEvents
        {
            private readonly InfoBarPresenter _owner;
            public readonly string Id;
            public readonly IReadOnlyList<KeyValuePair<string, Action>> Actions;
            public IVsInfoBarUIElement Element;
            public uint Cookie;

            public Bar(InfoBarPresenter owner, string id, IReadOnlyList<KeyValuePair<string, Action>> actions)
            {
                _owner = owner;
                Id = id;
                Actions = actions;
            }

            public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
            {
                Safe.Execute(L, "banner.closed", () =>
                {
                    infoBarUIElement.Unadvise(Cookie);
                    if (_owner._bars.TryGetValue(Id, out var b) && b == this) _owner._bars.Remove(Id);
                }, ("id", Id));
            }

            public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
            {
                Safe.Execute(L, "banner.action", () =>
                {
                    var key = actionItem.ActionContext as string;
                    var action = Actions.FirstOrDefault(a => a.Key == key).Value;
                    L.Info("banner.clicked", null, ("id", Id), ("action", key));
                    infoBarUIElement.Close();
                    action?.Invoke();
                }, ("id", Id));
            }
        }
    }

    /// <summary>Thread-agnostic entry to the active <see cref="IBannerPresenter"/> (switches to the UI thread).</summary>
    internal static class Banners
    {
        private static readonly Logger L = Log.For("Banner");

        public static void Show(string id, string text, BannerKind kind, params (string Label, Action Action)[] actions)
        {
            var list = actions.Select(a => new KeyValuePair<string, Action>(a.Label, a.Action)).ToList();
            Safe.RunAsync(L, "banner.show", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var presenter = HostIntegration.Banner;
                if (presenter == null)
                {
                    L.Warn("banner.noPresenter", text, ("id", id));
                    return;
                }
                presenter.Show(id, text, kind, list);
            }, ("id", id));
        }

        public static void Close(string id)
        {
            Safe.RunAsync(L, "banner.close", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                HostIntegration.Banner?.Close(id);
            }, ("id", id));
        }
    }

    /// <summary>Status bar text and progress. Safe to call from any thread (switches to the UI thread itself).</summary>
    internal static class VsStatusBar
    {
        private static readonly Logger L = Log.For("StatusBar");
        private static uint _progressCookie;

        public static void SetText(string text)
        {
            Safe.RunAsync(L, "statusbar.text", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar bar)
                {
                    bar.IsFrozen(out int frozen);
                    if (frozen == 0) bar.SetText(text);
                }
            });
        }

        /// <summary>Shows (inProgress) or clears progress. total==0 clears.</summary>
        public static void SetProgress(string label, int done, int total)
        {
            Safe.RunAsync(L, "statusbar.progress", async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!(Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar bar)) return;
                bool active = total > 0 && done < total;
                uint cookie = _progressCookie;
                bar.Progress(ref cookie, active ? 1 : 0, label ?? string.Empty, (uint)Math.Max(0, done), (uint)Math.Max(0, total));
                _progressCookie = active ? cookie : 0;
                if (!active && !string.IsNullOrEmpty(label)) bar.SetText(label);
            });
        }
    }
}
