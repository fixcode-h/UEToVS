using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Logic.Editing;
using UEToVS.Host.Logic.Settings;

namespace UEToVS.Host.Editing
{
    /// <summary>
    /// Copies the bundled Resources\UEToVS.natstepfilter to the per-user Visualizers folder of the running VS when 调试 Step
    /// Filter is on, and removes it (only our own file) when it is off. Runs once at load and on a setting change, in the
    /// background; never touches anything else.
    /// </summary>
    internal static class StepFilterDeployer
    {
        private static readonly Logger L = Log.For("Editing");

        public static Task SyncAsync(HostSettings settings, string reason) => Task.Run(() => Sync(settings?.DebugStepFilter ?? true, reason));

        private static void Sync(bool enabled, string reason)
        {
            var sw = Stopwatch.StartNew();
            string bundledPath = Path.Combine(HostPackage.ExtensionDirectory ?? string.Empty, "Resources", StepFilterPlan.FileName);
            string bundled = File.Exists(bundledPath) ? File.ReadAllText(bundledPath) : null;
            if (enabled && bundled == null)
            {
                L.Warn("editor.stepFilter.missing", "The bundled step filter is missing", null, ("path", bundledPath));
                return;
            }
            int major = VsMajor();
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var targets = StepFilterPlan.Targets(documents, major, Directory.Exists);
            int written = 0, deleted = 0;
            foreach (var target in targets)
            {
                try
                {
                    string existing = File.Exists(target) ? File.ReadAllText(target) : null;
                    switch (StepFilterPlan.Decide(enabled, existing, bundled))
                    {
                        case StepFilterAction.Write:
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            File.WriteAllText(target, bundled, new UTF8Encoding(false));
                            written++;
                            L.Info("editor.stepFilter.deployed", null, ("path", target), ("reason", reason));
                            break;
                        case StepFilterAction.Delete:
                            File.Delete(target);
                            deleted++;
                            L.Info("editor.stepFilter.removed", null, ("path", target), ("reason", reason));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    L.Warn("editor.stepFilter.failed", "Deploying the debug step filter failed", ex, ("path", target), ("enabled", enabled));
                }
            }
            L.Debug("editor.stepFilter.sync", null, ("enabled", enabled), ("vs", major), ("targets", targets.Count), ("written", written), ("deleted", deleted),
                ("ms", sw.ElapsedMilliseconds), ("reason", reason));
        }

        private static int VsMajor()
        {
            try
            {
                using (var p = Process.GetCurrentProcess())
                    return p.MainModule?.FileVersionInfo.FileMajorPart ?? 0;
            }
            catch (Exception ex)
            {
                L.Debug("editor.stepFilter.version", "VS version unknown", ("error", ex.Message));
                return 0;
            }
        }
    }
}
