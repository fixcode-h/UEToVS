using System;
using System.Collections.Generic;
using System.Text;
using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Horde
{
    /// <summary>What the Agent.json computePort should become.</summary>
    public sealed class AgentChange
    {
        /// <summary>False = leave Agent.json alone.</summary>
        public bool Needed { get; set; }

        /// <summary>New computePort; null = remove the key (agent default).</summary>
        public string NewPort { get; set; }

        /// <summary>Why (for the log): alreadyBlocked / block / allow / noBackup / keep / noAgent.</summary>
        public string Reason { get; set; }
    }

    /// <summary>Decides the Agent.json change for a policy; records / clears the earlier port in the manifest.</summary>
    public static class AgentPolicyPlanner
    {
        /// <param name="currentPort">computePort now (null = not set).</param>
        /// <param name="agentConfigExists">Agent.json or the agent itself is present.</param>
        public static AgentChange Plan(HordeAgentPolicy policy, string currentPort, bool agentConfigExists, HordeBackupManifest manifest)
        {
            switch (policy)
            {
                case HordeAgentPolicy.Block:
                    if (!agentConfigExists) return new AgentChange { Reason = "noAgent" };
                    if (AgentJsonConfig.IsBlocked(currentPort)) return new AgentChange { Reason = "alreadyBlocked" };
                    return new AgentChange { Needed = true, NewPort = "0", Reason = "block" };
                case HordeAgentPolicy.Allow:
                    if (manifest.AgentComputePort == null) return new AgentChange { Reason = "noBackup" };
                    string before = manifest.AgentComputePort == HordeBackupManifest.AbsentMarker ? null : manifest.AgentComputePort;
                    if (string.Equals(before, currentPort, StringComparison.Ordinal)) return new AgentChange { Reason = "alreadyAllowed" };
                    return new AgentChange { Needed = true, NewPort = before, Reason = "allow" };
                default:
                    return new AgentChange { Reason = "keep" };
            }
        }

        /// <summary>After the change succeeded: remember the earlier port (block) or forget it (allow).</summary>
        public static void Commit(AgentChange change, string previousPort, HordeBackupManifest manifest)
        {
            if (!change.Needed) return;
            if (change.Reason == "block" && manifest.AgentComputePort == null) manifest.AgentComputePort = previousPort ?? HordeBackupManifest.AbsentMarker;
            else if (change.Reason == "allow") manifest.AgentComputePort = null;
        }
    }

    /// <summary>Snapshot shown in "当前状态" (collected off the UI thread by the host).</summary>
    public sealed class HordeStatusSnapshot
    {
        public Version EngineVersion { get; set; }
        public string EngineRoot { get; set; }
        public string TargetFile { get; set; }
        public bool Enabled { get; set; }
        public int LocalCores { get; set; }
        public int LogicalCores { get; set; }
        public EffectiveValue Server { get; set; }
        public EffectiveValue Pool { get; set; }
        public EffectiveValue UbaExecutor { get; set; }
        public EffectiveValue MaxParallelActions { get; set; }
        /// <summary>With the feature off: UBA turned on by a file UEToVS does not write, or null.</summary>
        public EffectiveValue UbaElsewhere { get; set; }
        public string AgentExecutable { get; set; }
        public string AgentJsonPath { get; set; }
        public bool AgentJsonExists { get; set; }
        public string AgentComputePort { get; set; }
        /// <summary>Service names whose binary is the Horde Agent, with their state ("Running", "Stopped").</summary>
        public List<(string Name, string State)> AgentServices { get; } = new List<(string, string)>();
        public int UbtKeysKnown { get; set; }
        public List<string> Skipped { get; } = new List<string>();
        /// <summary>Result of the last apply ("已应用 …", an error), or null.</summary>
        public string LastResult { get; set; }

        public bool EngineTooOld => EngineVersion != null && EngineVersion < HordeConfigPlan.MinimumEngine;
        public bool AgentInstalled => AgentExecutable != null || AgentJsonExists || AgentServices.Count > 0;
    }

    /// <summary>Texts of the 联合编译（Horde）page.</summary>
    public static class HordeText
    {
        public static string Format(HordeStatusSnapshot s)
        {
            var sb = new StringBuilder();
            string engine = s.EngineVersion == null
                ? Loc.T("引擎：未识别（打开 UE 解决方案后显示）", "Engine: unknown (shown once a UE solution is open)")
                : Loc.F("引擎：UE {0}.{1}", "Engine: UE {0}.{1}", s.EngineVersion.Major, s.EngineVersion.Minor);
            if (s.EngineTooOld) engine += Loc.T("（UBA 联合编译需要 UE 5.4 及以上）", " (UBA distributed builds need UE 5.4 or later)");
            if (s.UbtKeysKnown > 0) engine += Loc.F("，已从 UBT 源码读到 {0} 个配置项", ", {0} config keys read from the UBT sources", s.UbtKeysKnown);
            sb.AppendLine(engine);

            if (s.Enabled)
                sb.AppendLine(Loc.F("联合编译：已启用，本机参与 {0} / {1} 核", "Distributed builds: on, {0} of {1} local cores", s.LocalCores, s.LogicalCores));
            else if (s.UbaElsewhere != null)
                sb.AppendLine(Loc.F("联合编译：插件开关未开，UBA 由 {0} 开启；本机参与 {1} / {2} 核", "Distributed builds: switch off, UBA turned on by {0}; {1} of {2} local cores",
                    s.UbaElsewhere.SourceFile, s.LocalCores, s.LogicalCores));
            else
                sb.AppendLine(Loc.T("联合编译：未启用", "Distributed builds: off"));
            if (s.MaxParallelActions != null && !string.IsNullOrEmpty(s.MaxParallelActions.Value))
                sb.AppendLine(Loc.F("UBT 本机并发：{0}", "UBT local parallel actions: {0}", Describe(s.MaxParallelActions, null)));
            sb.AppendLine(Loc.F("Horde 服务器：{0}", "Horde server: {0}", Describe(s.Server, Loc.T("未设置", "not set"))));
            if (s.Pool != null) sb.AppendLine(Loc.F("机器池：{0}", "Pool: {0}", Describe(s.Pool, null)));
            sb.AppendLine(Loc.F("写入文件：{0}", "Writes: {0}", s.TargetFile ?? Loc.T("（未打开 UE 项目）", "(no UE project open)")));

            if (!s.AgentInstalled) sb.Append(Loc.T("本机 Horde Agent：未安装，别人无法用本机编译", "Horde Agent on this machine: not installed, others cannot build on it"));
            else
            {
                string services = s.AgentServices.Count == 0 ? Loc.T("未找到服务", "no service found") : string.Join(", ", s.AgentServices.ConvertAll(x => x.Name + " " + ServiceState(x.State)));
                string remote = AgentJsonConfig.IsBlocked(s.AgentComputePort)
                    ? Loc.T("已禁止别人用本机编译", "others cannot build on this machine")
                    : Loc.T("别人可以用本机编译", "others can build on this machine");
                sb.Append(Loc.F("本机 Horde Agent：{0}；{1}（computePort={2}）", "Horde Agent on this machine: {0}; {1} (computePort={2})",
                    services, remote, s.AgentComputePort ?? Loc.T("默认", "default")));
            }
            if (s.Skipped.Count > 0)
                sb.Append('\n').Append(Loc.F("这个引擎版本不支持、未写入：{0}", "Not supported by this engine version, not written: {0}", string.Join(", ", s.Skipped)));
            if (!string.IsNullOrEmpty(s.LastResult)) sb.Append('\n').Append(s.LastResult);
            return sb.ToString();
        }

        private static string Describe(EffectiveValue v, string missing)
        {
            if (v == null || string.IsNullOrEmpty(v.Value)) return missing ?? "";
            return Loc.F("{0}（来自 {1}）", "{0} (from {1})", v.Value, v.SourceFile);
        }

        private static string ServiceState(string state)
        {
            switch (state)
            {
                case "Running": return Loc.T("运行中", "running");
                case "Stopped": return Loc.T("已停止", "stopped");
                default: return state ?? "";
            }
        }

        public static string Applied(int written, int restored) =>
            Loc.F("已应用：写入 {0} 项，恢复 {1} 项", "Applied: {0} keys written, {1} restored", written, restored);
        public static string Restored(int keys) => Loc.F("已恢复 {0} 项", "Restored {0} keys", keys);
        public static string NothingToRestore => Loc.T("没有需要恢复的修改", "Nothing to restore");
        public static string NoProject => Loc.T("没有打开 UE 项目，无法确定项目的 BuildConfiguration.xml；可在高级里改写用户配置", "No UE project is open, so the project's BuildConfiguration.xml is unknown; switch to the user file under Advanced");
        public static string NoEngine => Loc.T("还没识别到引擎目录（打开 UE 解决方案后再应用），或在高级里改写用户配置", "The engine folder is not known yet (apply again once a UE solution is open), or write the user file under Advanced");
        public static string EngineTooOld => Loc.T("当前引擎低于 UE 5.4，不支持 UBA 联合编译，未写入", "The engine is older than UE 5.4 and has no UBA; nothing written");
        public static string AgentCancelled => Loc.T("没有获得管理员授权，Agent.json 未修改", "Administrator rights were not granted; Agent.json unchanged");
        public static string AgentFailed(string why) => Loc.F("修改 Agent.json 失败：{0}", "Changing Agent.json failed: {0}", why);
        public static string AgentBlocked => Loc.T("已禁止别人用本机编译，Horde Agent 服务已重启", "Others can no longer build on this machine; the Horde Agent service was restarted");
        public static string AgentAllowed => Loc.T("已恢复别人用本机编译，Horde Agent 服务已重启", "Others can build on this machine again; the Horde Agent service was restarted");
        public static string Failed(string why) => Loc.F("应用失败：{0}", "Apply failed: {0}", why);
        public static string Loading => Loc.T("正在检测…", "Checking…");
        public static string NoConfigFile => Loc.T("配置文件还不存在，先启用联合编译并应用", "The config file does not exist yet; enable distributed builds and apply first");
    }
}
