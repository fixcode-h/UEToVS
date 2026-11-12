using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Fonts;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Fonts
{
    /// <summary>
    /// 设置 > 外观 > 字体 at runtime:
    ///  - UEToVS's own UI / code previews: <see cref="ThemeManager.SetFonts"/> (bundled fonts load from the VSIX, lazily).
    ///  - VS's fonts (replaces Tools &gt; Options &gt; Fonts and Colors for <see cref="VsFontCategories.All"/>): while
    ///    "由 UEToVS 管理 VS 字体" is on, bundled fonts are installed per user, each category's original font is backed up
    ///    once (vs-fonts-backup.json) and the chosen font written; categories set to "不修改" and, when the toggle is
    ///    off, all categories are restored from the backup. Runs at startup and when font settings change; serialized.
    /// Events: font.install, font.vs.categories, font.vs.backup, font.vs.apply, font.vs.restore, font.vs.done.
    /// </summary>
    internal sealed class FontService
    {
        private static readonly Logger L = Log.For("Fonts");
        private readonly AsyncPackage _package;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly string _backupPath = VsFontBackup.DefaultPath;
        private IReadOnlyList<string> _installed;
        private bool _categoriesLogged;
        private System.Windows.Threading.DispatcherTimer _previewTimer;
        private HostSettings _previewSettings;
        private string _previewReason;

        public FontService(AsyncPackage package)
        {
            _package = package;
        }

        /// <summary>Installed font families (WPF names), sorted; enumerated once per session when a font dropdown is first built.</summary>
        public IReadOnlyList<string> InstalledFamilies()
        {
            if (_installed != null) return _installed;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var names = new List<string>();
            try
            {
                foreach (var f in System.Windows.Media.Fonts.SystemFontFamilies)
                    if (!string.IsNullOrWhiteSpace(f.Source)) names.Add(f.Source);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                L.Warn("font.system.enumerate", "Could not enumerate installed fonts", ex);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            _installed = names;
            L.Info("font.system.enumerated", null, ("count", names.Count), ("ms", sw.ElapsedMilliseconds));
            return _installed;
        }

        /// <summary>Package init (UI thread): UI fonts now, VS fonts in the background.</summary>
        public void Initialize(HostSettings s)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ApplyUiFonts(s, "startup");
            _ = Safe.RunAsync(L, "font.vs.startup", () => ApplyVsFontsAsync(s, "startup"));
        }

        public async Task OnSettingsChangedAsync(HostSettings old, HostSettings now)
        {
            if (!FontSettingsSchema.Changed(old, now)) return;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ApplyUiFonts(now, "settingsChanged");
            await ApplyVsFontsAsync(now, "settingsChanged");
        }

        /// <summary>
        /// Settings window: shows fonts while the user picks them (not saved). UEToVS's own UI at once; VS's fonts after
        /// a short pause so stepping a size or scrolling a dropdown does not write every intermediate value. Latest wins.
        /// </summary>
        public void Preview(HostSettings s, string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ApplyUiFonts(s, reason);
            _previewSettings = s;
            _previewReason = reason;
            if (_previewTimer == null)
            {
                _previewTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(250),
                };
                _previewTimer.Tick += (sender, e) =>
                {
                    _previewTimer.Stop();
                    var p = _previewSettings;
                    string r = _previewReason;
                    _ = Safe.RunAsync(L, "font.vs.preview", () => ApplyVsFontsAsync(p, r));
                };
            }
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        private void ApplyUiFonts(HostSettings s, string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var fonts = VsFontPlanner.UiFonts(s);
            // "跟随 VS" for the code font = the Text Editor font; read it before UEToVS may change it.
            if (fonts.Code.Kind == FontChoiceKind.FollowVs)
            {
                var editor = VsFontStorage.TryCreate()?.Read(VsFontCategories.TextEditor);
                if (editor != null && !string.IsNullOrWhiteSpace(editor.Face))
                    ThemeManager.Instance.SetCodeFont(new System.Windows.Media.FontFamily(editor.Face + ", Cascadia Mono, Consolas"), null);
            }
            ThemeManager.Instance.SetFonts(fonts);
            L.Info("font.ui.applied", null, ("reason", reason), ("latin", fonts.UiLatin.ToString()), ("cjk", fonts.UiCjk.ToString()), ("code", fonts.Code.ToString()),
                   ("bundledDir", BundledFonts.Directory), ("bundledPresent", BundledFontCatalog.All.Where(BundledFonts.IsAvailable).Select(b => b.Family).ToList()));
        }

        /// <summary>Brings VS's fonts in line with the settings (see the class summary).</summary>
        public async Task ApplyVsFontsAsync(HostSettings s, string reason)
        {
            await _gate.WaitAsync();
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var storage = VsFontStorage.TryCreate();
                if (storage == null) return;
                LogCategories(storage);

                VsFontBackup backup;
                try
                {
                    backup = VsFontBackup.Load(_backupPath);
                }
                catch (InvalidDataException ex)
                {
                    // Never overwrite a backup we cannot read: it holds the user's original fonts.
                    L.Error("font.vs.backup.corrupt", "VS font backup unreadable; VS fonts left unchanged", ex, ("file", _backupPath));
                    return;
                }
                var targets = VsFontPlanner.Targets(s);
                var plan = VsFontPlanner.Plan(s.ManageVsFonts, targets, backup);
                if (plan.IsEmpty)
                {
                    L.Debug("font.vs.done", null, ("reason", reason), ("manage", s.ManageVsFonts), ("changes", 0));
                    return;
                }

                // Per-user install off the UI thread; a family that fails is not written (VS would fall back silently).
                var failed = new HashSet<BundledFont>();
                if (plan.Install.Count > 0)
                {
                    var install = plan.Install.ToList();
                    await TaskScheduler.Default;
                    foreach (var f in install)
                        if (!FontInstaller.EnsureInstalled(f)) failed.Add(f);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                }

                backup = backup ?? new VsFontBackup();
                int backedUp = 0, written = 0, skipped = 0, restored = 0;
                foreach (var c in plan.Backup)
                {
                    if (plan.Write.Any(t => t.Category == c && t.Bundled != null && failed.Contains(t.Bundled))) continue;
                    var cur = storage.Read(c);
                    if (cur == null) { L.Warn("font.vs.backup", "Could not read the current font; category skipped", ("category", c.Id)); continue; }
                    if (backup.AddIfMissing(new VsFontBackupEntry
                        {
                            Category = c.Id, CategoryGuid = c.Guid, Face = cur.Face, PointSize = cur.PointSize, CharSet = cur.CharSet,
                            WasDefault = cur.IsDefault, ChangedUtc = DateTime.UtcNow,
                        }))
                    {
                        backedUp++;
                        L.Info("font.vs.backup", null, ("category", c.Id), ("face", cur.Face), ("size", cur.PointSize), ("wasDefault", cur.IsDefault));
                    }
                }
                // The backup is on disk before the first write.
                if (backedUp > 0) VsFontBackup.SaveOrDelete(_backupPath, backup);

                foreach (var t in plan.Write)
                {
                    if (backup.Find(t.Category.Id) == null) continue; // not backed up → not touched
                    if (t.Bundled != null && failed.Contains(t.Bundled)) continue;
                    var cur = storage.Read(t.Category);
                    if (cur != null && VsFontPlanner.IsApplied(t, cur.Face, cur.PointSize)) { skipped++; continue; }
                    bool ok = storage.Write(t.Category, t.Face, t.Size);
                    if (ok) written++;
                    L.Info("font.vs.apply", null, ("category", t.Category.Id), ("face", t.Face), ("size", t.Size), ("from", cur?.Face), ("fromSize", cur?.PointSize), ("ok", ok));
                }

                foreach (var e in plan.Restore)
                {
                    var c = VsFontCategories.Find(e.Category);
                    bool ok = c != null && storage.Restore(c, e);
                    if (ok || c == null) { backup.Remove(e.Category); restored++; }
                    L.Info("font.vs.restore", null, ("category", e.Category), ("face", e.Face), ("size", e.PointSize), ("wasDefault", e.WasDefault), ("ok", ok));
                }
                VsFontBackup.SaveOrDelete(_backupPath, backup);
                L.Info("font.vs.done", null, ("reason", reason), ("manage", s.ManageVsFonts), ("backedUp", backedUp), ("written", written), ("alreadySet", skipped),
                       ("restored", restored), ("installFailed", failed.Select(f => f.Family).ToList()), ("backupFile", backup.IsEmpty ? null : _backupPath));
            }
            finally
            {
                _gate.Release();
            }
        }

        private void LogCategories(VsFontStorage storage)
        {
            if (_categoriesLogged) return;
            _categoriesLogged = true;
            var list = VsFontCategories.All.Select(c =>
            {
                var v = storage.Read(c);
                return new Dictionary<string, object>
                {
                    ["id"] = c.Id, ["guid"] = c.Guid.ToString("B"), ["face"] = v?.Face, ["size"] = v?.PointSize, ["default"] = v?.IsDefault,
                };
            }).ToList();
            L.Info("font.vs.categories", null, ("categories", list));
        }
    }
}
