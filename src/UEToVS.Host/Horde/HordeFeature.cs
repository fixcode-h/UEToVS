using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Horde;
using UEToVS.Host.Logic.Settings;
using Task = System.Threading.Tasks.Task;

namespace UEToVS.Host.Horde
{
    /// <summary>
    /// 联合编译（Horde / UBA）: applies the settings page to UBT's BuildConfiguration.xml (no admin needed) and to the Horde
    /// Agent's Agent.json computePort (through one elevated PowerShell run that also restarts the agent service), keeps the
    /// earlier values in %LocalAppData%\UEToVS\horde-backup.json for "恢复到修改前", and builds the status text of the page.
    /// Nothing runs in the background: work happens on save, on the page's buttons, when a UE solution opens with the
    /// feature on, and a status refresh (a few small file reads + a service query) at most every 5 s while the page shows.
    /// </summary>
    internal static class HordeFeature
    {
        private static readonly Logger L = Log.For("Horde");
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static readonly TimeSpan StatusMaxAge = TimeSpan.FromSeconds(5);
        private static HostPackage _package;
        private static string _statusText;
        private static DateTime _statusAt;
        private static int _refreshing;
        private static string _lastResult;
        private static List<string> _lastSkipped = new List<string>();
        private static string _keysEngineRoot;
        private static UbtConfigKeys _keys;

        private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        private static string ManifestPath => Path.Combine(LocalAppData, "UEToVS", "horde-backup.json");
        private static string AgentJsonPath => HordeLocations.AgentJson(ProgramData);

        public static void Initialize(HostPackage package)
        {
            _package = package;
            package.Settings.Changed += (s, e) => Safe.Execute(L, "horde.settings", () =>
            {
                bool files = HordeSettingsSchema.BuildConfigChanged(e.Old, e.New);
                bool agent = e.Old.HordeAgentPolicy != e.New.HordeAgentPolicy;
                if (!files && !agent) return;
                L.Info("horde.settings.changed", null, ("files", files), ("agent", agent), ("policy", e.New.HordeAgentPolicy.ToString()));
                _ = Safe.RunInBackgroundAsync(L, "horde.apply.onSave", async () => { _lastResult = await ApplyAsync(e.New, agent, "settings"); });
            });
            L.Info("horde.initialized", null);
        }

        /// <summary>
        /// A UE solution opened: the engine's file gets the keys. Also runs with the feature off, because UBA may be turned on
        /// in a hand-made BuildConfiguration.xml and the local core limit still applies then.
        /// </summary>
        public static void OnUnrealSolutionOpened()
        {
            var s = _package?.Settings?.Effective;
            InvalidateStatus();
            if (s == null) return;
            _ = Safe.RunInBackgroundAsync(L, "horde.apply.onOpen", async () =>
            {
                // For a game project the engine root comes from the UEToVS engine's first status; wait for it (up to 2 min).
                for (int i = 0; i < 60 && s.HordeTarget == HordeConfigTarget.Engine && EngineRoot == null; i++) await Task.Delay(2000).ConfigureAwait(false);
                _lastResult = await ApplyAsync(_package.Settings.Effective, false, "solutionOpened");
            });
        }

        private static string ProjectDirectory
        {
            get
            {
                string project = _package?.Unreal?.ProjectFile;
                return string.IsNullOrEmpty(project) ? null : Path.GetDirectoryName(project);
            }
        }

        private static string EngineRoot => _package == null ? null : Unreal.UnrealTools.EngineRoot(_package);

        private static UbtConfigKeys KnownKeys(string engineRoot)
        {
            if (string.IsNullOrEmpty(engineRoot)) return UbtConfigKeys.Unknown;
            if (_keys != null && string.Equals(_keysEngineRoot, engineRoot, StringComparison.OrdinalIgnoreCase)) return _keys;
            var sw = Stopwatch.StartNew();
            var keys = UbtConfigKeys.Scan(engineRoot, (path, ex) => L.Debug("horde.ubtScan.fileFailed", null, ("path", path), ("error", ex.Message)));
            L.Info("horde.ubtScan", null, ("engineRoot", engineRoot), ("files", keys.FilesScanned), ("keys", keys.Count),
                   ("sections", string.Join(",", keys.Sections)), ("ms", sw.ElapsedMilliseconds));
            _keys = keys;
            _keysEngineRoot = engineRoot;
            return keys;
        }

        // ------------------------------------------------------------------ apply / restore

        /// <summary>The file to write; the engine target falls back to the user file when the engine folder is read-only.</summary>
        private static string ResolveTarget(HostSettings s, string engineRoot, out HordeConfigTarget kind)
        {
            kind = s.HordeTarget;
            string path = HordeLocations.TargetFile(kind, engineRoot, ProjectDirectory, AppData);
            if (kind == HordeConfigTarget.Engine && path != null && !HordeLocations.CanWrite(path))
            {
                L.Info("horde.target.engineReadOnly", "engine Saved folder not writable; using the user BuildConfiguration.xml", ("path", path));
                kind = HordeConfigTarget.User;
                path = HordeLocations.TargetFile(kind, engineRoot, ProjectDirectory, AppData);
            }
            return path;
        }

        /// <summary>
        /// bAllowUBAExecutor=true set by a file UEToVS does not write. The project file is left out: UBT does not read it for
        /// builds started from VS.
        /// </summary>
        private static EffectiveValue UbaElsewhere(string engineRoot, string target) =>
            EffectiveBuildConfiguration.UbaTurnedOnElsewhere(HordeLocations.BuildConfigurationFiles(engineRoot, null, AppData, Documents), target,
                (path, ex) => L.Warn("horde.apply.readFailed", null, ("path", path), ("error", ex.Message)));

        /// <summary>Writes BuildConfiguration.xml (and Agent.json when <paramref name="agent"/>); returns the text for the status row.</summary>
        public static async Task<string> ApplyAsync(HostSettings s, bool agent, string reason)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var messages = new List<string>();
                string engineRoot = EngineRoot;
                var version = EngineVersionReader.Read(engineRoot);
                string target = ResolveTarget(s, engineRoot, out var targetKind);
                var manifest = HordeBackupManifest.Load(ManifestPath);

                if (version != null && version < HordeConfigPlan.MinimumEngine && s.HordeEnabled)
                {
                    messages.Add(HordeText.EngineTooOld);
                    L.Info("horde.apply.engineTooOld", null, ("version", version.ToString()));
                }
                else if (target == null)
                {
                    if (s.HordeEnabled) messages.Add(s.HordeTarget == HordeConfigTarget.Project ? HordeText.NoProject : HordeText.NoEngine);
                }
                else
                {
                    var ubaElsewhere = UbaElsewhere(engineRoot, target);
                    var plan = HordeConfigPlan.Build(s, Environment.ProcessorCount, KnownKeys(engineRoot), ubaElsewhere != null);
                    _lastSkipped = plan.Skipped;
                    var result = BuildConfigurationWriter.Apply(target, plan, manifest);
                    // Files written for another target get their earlier values back (e.g. the project file v0.5.4 wrote, which
                    // UBT ignores for builds started from VS). Other engines' own Saved files stay while the target is the engine.
                    int restoredElsewhere = 0;
                    var others = new List<string>();
                    foreach (var other in manifest.Files.Keys.ToList())
                    {
                        if (string.Equals(other, target, StringComparison.OrdinalIgnoreCase)) continue;
                        if (targetKind == HordeConfigTarget.Engine && HordeLocations.IsEngineSavedFile(other)) continue;
                        restoredElsewhere += BuildConfigurationWriter.Apply(other, HordeConfigPlan.Build(null, 1), manifest).Restored.Count;
                        others.Add(other);
                    }
                    L.Info("horde.apply.buildConfiguration", null, ("reason", reason), ("file", target), ("enabled", s.HordeEnabled),
                           ("written", result.Written), ("restored", result.Restored), ("restoredOtherFiles", others), ("restoredOtherKeys", restoredElsewhere), ("target", targetKind.ToString()),
                           ("ubaElsewhere", ubaElsewhere?.SourceFile), ("skipped", plan.Skipped), ("localCores", plan.LocalCores), ("engineVersion", version?.ToString()), ("changed", result.FileChanged));
                    if (result.Written.Count > 0 || result.Restored.Count > 0 || restoredElsewhere > 0)
                        messages.Add(HordeText.Applied(result.Written.Count, result.Restored.Count + restoredElsewhere));
                }
                manifest.Save(ManifestPath);

                if (agent)
                {
                    string agentMessage = await ApplyAgentPolicyAsync(s.HordeAgentPolicy, manifest).ConfigureAwait(false);
                    if (agentMessage != null) messages.Add(agentMessage);
                    manifest.Save(ManifestPath);
                }
                InvalidateStatus();
                return messages.Count == 0 ? null : string.Join("；", messages);
            }
            catch (Exception ex)
            {
                L.Error("horde.apply.failed", null, ex, ("reason", reason));
                InvalidateStatus();
                return HordeText.Failed(ex.Message);
            }
            finally
            {
                Gate.Release();
            }
        }

        public static async Task<string> RestoreAsync()
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var manifest = HordeBackupManifest.Load(ManifestPath);
                if (manifest.IsEmpty) return HordeText.NothingToRestore;
                var results = BuildConfigurationWriter.RestoreAll(manifest);
                int keys = results.Sum(r => r.Restored.Count);
                L.Info("horde.restore.buildConfiguration", null, ("files", results.Select(r => r.Path).ToList()), ("keys", keys));
                manifest.Save(ManifestPath);
                string agentMessage = null;
                if (manifest.AgentComputePort != null)
                {
                    agentMessage = await ApplyAgentPolicyAsync(HordeAgentPolicy.Allow, manifest).ConfigureAwait(false);
                    manifest.Save(ManifestPath);
                }
                InvalidateStatus();
                string text = HordeText.Restored(keys);
                return agentMessage == null ? text : text + "；" + agentMessage;
            }
            catch (Exception ex)
            {
                L.Error("horde.restore.failed", null, ex);
                return HordeText.Failed(ex.Message);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<string> ApplyAgentPolicyAsync(HordeAgentPolicy policy, HordeBackupManifest manifest)
        {
            string path = AgentJsonPath;
            string text = null;
            bool exists = path != null && File.Exists(path);
            if (exists) text = File.ReadAllText(path);
            string current = AgentJsonConfig.ReadComputePort(text);
            bool agentPresent = exists || HordeLocations.FindAgentExecutable(_package?.Settings?.Effective?.HordeInstallPath) != null || FindAgentServices().Count > 0;
            var change = AgentPolicyPlanner.Plan(policy, current, agentPresent, manifest);
            L.Info("horde.agent.plan", null, ("policy", policy.ToString()), ("current", current), ("needed", change.Needed), ("newPort", change.NewPort), ("reason", change.Reason), ("file", path));
            if (!change.Needed) return null;

            string newText = AgentJsonConfig.WithComputePort(text, change.NewPort);
            string backup = Path.Combine(LocalAppData, "UEToVS", "horde-backup", "Agent.json." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak");
            var outcome = await RunElevatedAgentUpdateAsync(path, newText, exists ? backup : null).ConfigureAwait(false);
            if (outcome != null) return outcome;
            AgentPolicyPlanner.Commit(change, current, manifest);
            manifest.AgentFile = path;
            return change.Reason == "block" ? HordeText.AgentBlocked : HordeText.AgentAllowed;
        }

        /// <summary>Copies the new Agent.json into ProgramData and restarts the agent service as administrator; null = success.</summary>
        private static async Task<string> RunElevatedAgentUpdateAsync(string agentJson, string newText, string backup)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "uetovs-agent-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(tmp, newText, new UTF8Encoding(false));
            if (backup != null) Directory.CreateDirectory(Path.GetDirectoryName(backup));
            string Q(string p) => "'" + (p ?? string.Empty).Replace("'", "''") + "'";
            var script = new StringBuilder()
                .AppendLine("$ErrorActionPreference = 'Stop'")
                .AppendLine("$dst = " + Q(agentJson))
                .AppendLine("New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null")
                .AppendLine(backup == null ? string.Empty : "if (Test-Path $dst) { Copy-Item $dst " + Q(backup) + " -Force }")
                .AppendLine("Copy-Item " + Q(tmp) + " $dst -Force")
                .AppendLine("$svc = Get-CimInstance Win32_Service | Where-Object { $_.PathName -match 'HordeAgent' }")
                .AppendLine("foreach ($s in $svc) { if ($s.State -eq 'Running') { Restart-Service -Name $s.Name -Force } }")
                .ToString();
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + encoded)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    if (p == null) return HordeText.AgentFailed("powershell");
                    bool exited = await Task.Run(() => p.WaitForExit(120000)).ConfigureAwait(false);
                    if (!exited) return HordeText.AgentFailed("timeout");
                    L.Info("horde.agent.elevated", null, ("exitCode", p.ExitCode), ("file", agentJson), ("backup", backup));
                    return p.ExitCode == 0 ? null : HordeText.AgentFailed("exit code " + p.ExitCode);
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the UAC prompt was declined
            {
                L.Info("horde.agent.uacDeclined", null);
                return HordeText.AgentCancelled;
            }
            finally
            {
                try { File.Delete(tmp); } catch (Exception) { /* temp file */ }
            }
        }

        // ------------------------------------------------------------------ status

        /// <summary>Services whose binary is the Horde Agent (read from the registry; no admin needed).</summary>
        private static List<string> FindAgentServices()
        {
            var names = new List<string>();
            try
            {
                using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
                {
                    if (services == null) return names;
                    foreach (var name in services.GetSubKeyNames())
                    {
                        using (var key = services.OpenSubKey(name))
                        {
                            if (key?.GetValue("ImagePath") is string image && image.IndexOf("HordeAgent", StringComparison.OrdinalIgnoreCase) >= 0)
                                names.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                L.Debug("horde.services.failed", ex.Message);
            }
            return names;
        }

        private static HordeStatusSnapshot Collect()
        {
            var s = _package?.Settings?.Effective ?? new HostSettings();
            string engineRoot = EngineRoot;
            var snap = new HordeStatusSnapshot
            {
                EngineRoot = engineRoot,
                EngineVersion = EngineVersionReader.Read(engineRoot),
                TargetFile = HordeLocations.TargetFile(s.HordeTarget, engineRoot, ProjectDirectory, AppData),
                Enabled = s.HordeEnabled,
                LogicalCores = Environment.ProcessorCount,
                LocalCores = HordeSettingsSchema.EffectiveLocalCores(s.HordeLocalCores, Environment.ProcessorCount),
                AgentExecutable = HordeLocations.FindAgentExecutable(s.HordeInstallPath),
                AgentJsonPath = AgentJsonPath,
                LastResult = _lastResult,
                UbtKeysKnown = string.Equals(_keysEngineRoot, engineRoot, StringComparison.OrdinalIgnoreCase) ? _keys?.Count ?? 0 : 0,
            };
            snap.Skipped.AddRange(_lastSkipped);
            var files = HordeLocations.BuildConfigurationFiles(engineRoot, ProjectDirectory, AppData, Documents);
            var effective = EffectiveBuildConfiguration.Read(files, HordeConfigPlan.ManagedKeys, (path, ex) => L.Warn("horde.status.readFailed", null, ("path", path), ("error", ex.Message)));
            effective.TryGetValue(HordeConfigPlan.SectionHorde + "/Server", out var server);
            effective.TryGetValue(HordeConfigPlan.SectionHorde + "/Pool", out var pool);
            effective.TryGetValue(HordeConfigPlan.SectionBuild + "/bAllowUBAExecutor", out var uba);
            effective.TryGetValue(HordeConfigPlan.SectionBuild + "/MaxParallelActions", out var parallel);
            snap.Server = server;
            snap.Pool = pool;
            snap.UbaExecutor = uba;
            snap.MaxParallelActions = parallel;
            if (!s.HordeEnabled) snap.UbaElsewhere = UbaElsewhere(engineRoot, snap.TargetFile);
            try
            {
                snap.AgentJsonExists = snap.AgentJsonPath != null && File.Exists(snap.AgentJsonPath);
                if (snap.AgentJsonExists) snap.AgentComputePort = AgentJsonConfig.ReadComputePort(File.ReadAllText(snap.AgentJsonPath));
            }
            catch (Exception ex)
            {
                L.Warn("horde.status.agentJson", null, ("path", snap.AgentJsonPath), ("error", ex.Message));
            }
            foreach (var name in FindAgentServices())
            {
                string state;
                try
                {
                    using (var sc = new ServiceController(name)) state = sc.Status.ToString();
                }
                catch (Exception) { state = "?"; }
                snap.AgentServices.Add((name, state));
            }
            return snap;
        }

        private static void InvalidateStatus() => _statusAt = DateTime.MinValue;

        /// <summary>Status row text; refreshed in the background when older than 5 s (the window polls every second).</summary>
        public static string StatusText()
        {
            if (DateTime.UtcNow - _statusAt > StatusMaxAge && Interlocked.Exchange(ref _refreshing, 1) == 0)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        // The UBT scan is part of the first refresh so the status can show what this engine supports.
                        KnownKeys(EngineRoot);
                        _statusText = HordeText.Format(Collect());
                        _statusAt = DateTime.UtcNow;
                    }
                    catch (Exception ex)
                    {
                        L.Warn("horde.status.failed", null, ex);
                        _statusAt = DateTime.UtcNow;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _refreshing, 0);
                    }
                });
            }
            return _statusText ?? HordeText.Loading;
        }

        /// <summary>"打开 BuildConfiguration.xml".</summary>
        public static async Task<string> OpenConfigAsync()
        {
            var s = _package?.Settings?.Effective;
            string target = s == null ? null : ResolveTarget(s, EngineRoot, out _);
            if (target == null) return HordeText.NoEngine;
            if (!File.Exists(target)) return HordeText.NoConfigFile;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            VsShellUtilities.OpenDocument(_package, target);
            return null;
        }
    }
}
