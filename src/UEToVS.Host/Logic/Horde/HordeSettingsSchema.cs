using System;
using System.Collections.Generic;
using System.Globalization;
using UEToVS.Host.Logic.Settings;
using UEToVS.UI.Logic;

namespace UEToVS.Host.Logic.Horde
{
    /// <summary>Which BuildConfiguration.xml UEToVS writes.</summary>
    public enum HordeConfigTarget
    {
        /// <summary>&lt;Project&gt;/Saved/UnrealBuildTool/BuildConfiguration.xml: only this project's builds.</summary>
        Project = 0,

        /// <summary>%AppData%\Unreal Engine\UnrealBuildTool\BuildConfiguration.xml: every engine and project of this user.</summary>
        User = 1,
    }

    /// <summary>Whether other people's builds may run on this machine's Horde Agent.</summary>
    public enum HordeAgentPolicy
    {
        /// <summary>Agent.json computePort = 0: the agent stays online but accepts no remote compile work.</summary>
        Block = 0,

        /// <summary>computePort back to what it was before UEToVS changed it.</summary>
        Allow = 1,

        /// <summary>UEToVS never touches Agent.json.</summary>
        Keep = 2,
    }

    /// <summary>
    /// Settings window page "联合编译（Horde）" and its HostSettings codec (kept out of HostSettingsSchema like the
    /// translation rows). The values are applied to UBT's BuildConfiguration.xml and the Horde Agent's Agent.json by
    /// Host/Horde/HordeFeature through <see cref="HordeConfigPlan"/>.
    /// </summary>
    public static class HordeSettingsSchema
    {
        public const string Category = "horde";
        public const string DefaultInstallPath = @"C:\Horde";
        public const string TriDefault = "default";

        public const string EnabledKey = "horde.enabled";
        public const string InstallPathKey = "horde.installPath";
        public const string TargetKey = "horde.target";
        public const string ServerKey = "horde.server";
        public const string PoolKey = "horde.pool";
        public const string MaxCoresKey = "horde.maxCores";
        public const string LocalCoresKey = "horde.localCores";
        public const string AgentPolicyKey = "horde.agentPolicy";
        public const string LinkRemoteKey = "horde.linkRemote";
        public const string PrintSummaryKey = "horde.printSummary";
        public const string ForceRemoteKey = "horde.forceRemote";
        public const string DisableRemoteKey = "horde.disableRemote";
        public const string AllowRetryKey = "horde.allowRetry";
        public const string VisualizerKey = "horde.launchVisualizer";
        public const string ObjCompressedKey = "horde.storeObjCompressed";
        public const string ClusterKey = "horde.cluster";
        public const string ZoneKey = "horde.zone";
        public const string EncryptionKey = "horde.encryption";
        public const string ConnectionModeKey = "horde.connectionMode";
        public const string RootDirKey = "horde.rootDir";
        public const string StoreCapacityKey = "horde.storeCapacityGb";

        /// <summary>Action / info ids resolved by the settings window service.</summary>
        public const string StatusInfo = "settings.horde.status";
        public const string ApplyAction = "settings.horde.apply";
        public const string RestoreAction = "settings.horde.restore";
        public const string OpenConfigAction = "settings.horde.openConfig";

        private const string Keywords = "horde uba unreal build accelerator remote distributed compile 联合编译 分布式 远程编译 agent buildconfiguration";

        /// <summary>Local cores used while Horde is on: a quarter of the logical cores (at least 1) unless set.</summary>
        public static int DefaultLocalCores(int logicalCores) => Math.Max(1, Math.Max(1, logicalCores) / 4);

        public static int EffectiveLocalCores(int stored, int logicalCores) =>
            stored > 0 ? Math.Min(stored, Math.Max(1, logicalCores)) : DefaultLocalCores(logicalCores);

        public static void AddDescriptors(List<SettingDescriptor> list, int logicalCores)
        {
            int cores = Math.Max(1, logicalCores);
            const string gStatus = "状态", gStatusEn = "Status";
            list.Add(Info(StatusInfo, "当前状态", "Current state", gStatus, gStatusEn));
            list.Add(Act(ApplyAction, "apply", "立即应用到配置文件", "Apply to the config files", "应用", "Apply",
                "按下面的设置重写 BuildConfiguration.xml；“别人用我的机器”有变化时会弹出管理员授权，修改 Agent.json 并重启 Horde Agent 服务。点“应用”或“确定”保存设置时也会自动执行。",
                "Rewrites BuildConfiguration.xml from the settings below; when \"Others using this machine\" changed, asks for administrator rights to edit Agent.json and restart the Horde Agent service. Also runs when the settings are saved.",
                gStatus, gStatusEn));
            list.Add(Act(OpenConfigAction, "openConfig", "打开 BuildConfiguration.xml", "Open BuildConfiguration.xml", "打开", "Open",
                "在 VS 里打开 UEToVS 写入的那个 BuildConfiguration.xml。", "Opens the BuildConfiguration.xml UEToVS writes.", gStatus, gStatusEn));
            list.Add(Act(RestoreAction, "restore", "恢复到修改前", "Restore previous values", "恢复", "Restore",
                "把 UEToVS 改过的每一项恢复成修改前的值（没有的就删掉），Agent.json 同样恢复；文件里其他内容不动。",
                "Puts every key UEToVS changed back to its earlier value (or removes it), Agent.json too; nothing else in the files is touched.",
                gStatus, gStatusEn));

            const string gUse = "用别人的机器编译", gUseEn = "Build with other machines";
            list.Add(Row(EnabledKey, SettingKind.Bool, "启用 UBA 联合编译", "Enable UBA distributed builds",
                "写入 bAllowUBAExecutor 等设置，让本机的 UE 编译通过 Horde 借用别人的机器。关闭后把 UEToVS 写过的项恢复成原来的值。需要 UE 5.4 及以上。",
                "Writes bAllowUBAExecutor and the keys below so this machine's UE builds borrow other machines through Horde. Turning it off puts back what UEToVS changed. Needs UE 5.4 or later.",
                "false", gUse, gUseEn));
            list.Add(Row(ServerKey, SettingKind.Text, "Horde 服务器", "Horde server",
                "例如 http://horde:13340；留空沿用其他配置文件里已有的地址（状态里会显示）。",
                "e.g. http://horde:13340; empty = keep the address the other config files already set (shown in the status).",
                "", gUse, gUseEn, EnabledKey));
            list.Add(Row(PoolKey, SettingKind.Text, "机器池", "Pool",
                "要哪一组机器帮忙编译（Horde/Pool）；留空用服务器默认。", "Which group of machines helps (Horde/Pool); empty = the server default.",
                "", gUse, gUseEn, EnabledKey));
            var maxCores = Row(MaxCoresKey, SettingKind.Int, "远程核心上限", "Remote core limit",
                "最多借用多少个远程核心（Horde/MaxCores）；0 = UE 默认。", "Most remote cores borrowed (Horde/MaxCores); 0 = UE default.",
                "0", gUse, gUseEn, EnabledKey);
            maxCores.Min = 0; maxCores.Max = 100000;
            maxCores.AutoLabelZh = "UE 默认"; maxCores.AutoLabelEn = "UE default";
            list.Add(maxCores);
            list.Add(Tri(LinkRemoteKey, "远程链接", "Link remotely",
                "链接也发到远程机器（bLinkRemote）。大模块不一定更快。", "Also sends linking to remote machines (bLinkRemote). Not always faster for big modules.",
                gUse, gUseEn));
            list.Add(Tri(PrintSummaryKey, "编译结束打印统计", "Print a summary after the build",
                "在生成输出里列出 UBA 统计，能看出远程帮了多少（bPrintSummary）。", "Lists UBA statistics in the build output, showing how much remote machines helped (bPrintSummary).",
                gUse, gUseEn, "true"));

            const string gLocal = "本机资源", gLocalEn = "This machine";
            var local = Row(LocalCoresKey, SettingKind.Int, "本机参与编译的核心数", "Local cores used for building",
                "启用联合编译时本机最多同时跑几个编译任务（BuildConfiguration/MaxParallelActions）；0 = 逻辑核心数的 1/4。关闭联合编译时不写，UE 按自己的规则决定。",
                "How many compile actions run on this machine at once while distributed builds are on (BuildConfiguration/MaxParallelActions); 0 = a quarter of the logical cores. Not written while distributed builds are off.",
                "0", gLocal, gLocalEn, EnabledKey);
            local.Min = 0; local.Max = cores;
            local.AutoLabelZh = $"自动（{DefaultLocalCores(cores)} / {cores}）";
            local.AutoLabelEn = $"Auto ({DefaultLocalCores(cores)} / {cores})";
            local.AutoValue = DefaultLocalCores(cores);
            list.Add(local);

            const string gAgent = "别人用我的机器", gAgentEn = "Others using this machine";
            var agent = Row(AgentPolicyKey, SettingKind.Choice, "允许别人用本机编译", "Let others build on this machine",
                "禁止：把本机 Horde Agent 的 computePort 设为 0，Agent 照常在线，但不再接别人的远程编译任务；不影响你借用别人的机器。允许：恢复原来的端口。不修改：UEToVS 不碰 Agent.json。修改需要管理员授权，并会重启 Horde Agent 服务。",
                "Block: sets this machine's Horde Agent computePort to 0, so the agent stays online but takes no remote compile work from others; your own builds can still use other machines. Allow: puts the earlier port back. Keep: UEToVS leaves Agent.json alone. Changing it needs administrator rights and restarts the Horde Agent service.",
                nameof(HordeAgentPolicy.Block), gAgent, gAgentEn);
            agent.Options = new[]
            {
                new SettingOption(nameof(HordeAgentPolicy.Block), "禁止", "Block"),
                new SettingOption(nameof(HordeAgentPolicy.Allow), "允许", "Allow"),
                new SettingOption(nameof(HordeAgentPolicy.Keep), "不修改", "Keep"),
            };
            list.Add(agent);
            list.Add(Row(InstallPathKey, SettingKind.Path, "Horde 安装路径", "Horde install folder",
                "用来检测本机的 Horde Agent。", "Used to detect this machine's Horde Agent.", DefaultInstallPath, gAgent, gAgentEn));

            const string gAdv = "高级", gAdvEn = "Advanced";
            var target = Row(TargetKey, SettingKind.Choice, "写入哪个配置文件", "Config file written",
                "项目：<项目>/Saved/UnrealBuildTool/BuildConfiguration.xml，只影响这个项目。用户：%AppData%\\Unreal Engine\\UnrealBuildTool\\BuildConfiguration.xml，影响这台机器上所有引擎和项目。",
                "Project: <Project>/Saved/UnrealBuildTool/BuildConfiguration.xml, only this project. User: %AppData%\\Unreal Engine\\UnrealBuildTool\\BuildConfiguration.xml, every engine and project of this user.",
                nameof(HordeConfigTarget.Project), gAdv, gAdvEn);
            target.Options = new[]
            {
                new SettingOption(nameof(HordeConfigTarget.Project), "项目", "Project"),
                new SettingOption(nameof(HordeConfigTarget.User), "用户", "User"),
            };
            list.Add(target);
            list.Add(Tri(DisableRemoteKey, "仅本地（不要远程帮忙）", "Local only (no remote help)",
                "保留 UBA 的缓存和调度，但不用远程机器（bDisableRemote）。", "Keeps UBA caching and scheduling without remote machines (bDisableRemote).", gAdv, gAdvEn));
            list.Add(Tri(ForceRemoteKey, "强制全部远程", "Force everything remote",
                "能远程的任务全部发到远程（bForceBuildAllRemote）。没有可用的远程机器时编译会一直等。", "Sends every remotable action to remote machines (bForceBuildAllRemote). The build waits forever when none is available.",
                gAdv, gAdvEn));
            list.Add(Tri(AllowRetryKey, "远程失败后在本机重试", "Retry remote failures locally",
                "bForcedRetryRemote。", "bForcedRetryRemote.", gAdv, gAdvEn));
            list.Add(Tri(VisualizerKey, "打开编译可视化窗口", "Open the build visualizer",
                "实时显示每台机器在编什么（bLaunchVisualizer）。", "Shows live what each machine compiles (bLaunchVisualizer).", gAdv, gAdvEn));
            list.Add(Tri(ObjCompressedKey, "压缩 .obj 文件", "Compress .obj files",
                "省磁盘，稍耗 CPU（bStoreObjFilesCompressed）。", "Saves disk, costs a little CPU (bStoreObjFilesCompressed).", gAdv, gAdvEn));
            list.Add(Row(ClusterKey, SettingKind.Text, "集群", "Cluster", "Horde/Cluster；留空不写。", "Horde/Cluster; empty = not written.", "", gAdv, gAdvEn, EnabledKey));
            list.Add(Row(ZoneKey, SettingKind.Text, "区域", "Zone", "UnrealBuildAccelerator/Zone；留空不写。", "UnrealBuildAccelerator/Zone; empty = not written.", "", gAdv, gAdvEn, EnabledKey));
            list.Add(Row(EncryptionKey, SettingKind.Text, "加密", "Encryption", "Horde/Encryption（如 aes）；留空不写。", "Horde/Encryption (e.g. aes); empty = not written.", "", gAdv, gAdvEn, EnabledKey));
            list.Add(Row(ConnectionModeKey, SettingKind.Text, "连接方式", "Connection mode", "Horde/ConnectionMode（如 direct、tunnel、relay）；留空不写。", "Horde/ConnectionMode (e.g. direct, tunnel, relay); empty = not written.", "", gAdv, gAdvEn, EnabledKey));
            list.Add(Row(RootDirKey, SettingKind.Path, "UBA 缓存目录", "UBA cache folder", "UnrealBuildAccelerator/RootDir；留空不写。放到更快的盘能提速。", "UnrealBuildAccelerator/RootDir; empty = not written. A faster disk helps.", "", gAdv, gAdvEn, EnabledKey));
            var cap = Row(StoreCapacityKey, SettingKind.Int, "UBA 缓存容量（GB）", "UBA cache size (GB)",
                "UnrealBuildAccelerator/StoreCapacityGb；0 = UE 默认。", "UnrealBuildAccelerator/StoreCapacityGb; 0 = UE default.", "0", gAdv, gAdvEn, EnabledKey);
            cap.Min = 0; cap.Max = 10000;
            cap.AutoLabelZh = "UE 默认"; cap.AutoLabelEn = "UE default";
            list.Add(cap);
        }

        private static SettingDescriptor Row(string key, SettingKind kind, string zh, string en, string descZh, string descEn, string def,
                                             string groupZh, string groupEn, string enabledWhen = null) =>
            new SettingDescriptor
            {
                Category = Category, Key = key, Kind = kind, TitleZh = zh, TitleEn = en, DescriptionZh = descZh, DescriptionEn = descEn,
                Default = def, GroupZh = groupZh, GroupEn = groupEn, Keywords = Keywords, EnabledWhenKey = enabledWhen,
            };

        private static SettingDescriptor Tri(string key, string zh, string en, string descZh, string descEn, string groupZh, string groupEn, string def = TriDefault)
        {
            var d = Row(key, SettingKind.Choice, zh, en, descZh, descEn, def, groupZh, groupEn, EnabledKey);
            d.Options = new[]
            {
                new SettingOption(TriDefault, "UE 默认", "UE default"),
                new SettingOption("true", "开", "On"),
                new SettingOption("false", "关", "Off"),
            };
            return d;
        }

        private static SettingDescriptor Info(string id, string zh, string en, string groupZh, string groupEn)
        {
            var d = Row("horde.status", SettingKind.Info, zh, en, null, null, null, groupZh, groupEn);
            d.ActionId = id;
            return d;
        }

        private static SettingDescriptor Act(string id, string keySuffix, string zh, string en, string labelZh, string labelEn, string descZh, string descEn, string groupZh, string groupEn)
        {
            var d = Row("horde." + keySuffix, SettingKind.Action, zh, en, descZh, descEn, null, groupZh, groupEn);
            d.ActionId = id;
            d.ActionLabelZh = labelZh;
            d.ActionLabelEn = labelEn;
            return d;
        }

        private static string Tri(string v) => v == "true" || v == "false" ? v : TriDefault;

        public static void ToValues(HostSettings s, IDictionary<string, string> d)
        {
            d[EnabledKey] = s.HordeEnabled ? "true" : "false";
            d[InstallPathKey] = s.HordeInstallPath ?? "";
            d[TargetKey] = s.HordeTarget.ToString();
            d[ServerKey] = s.HordeServer ?? "";
            d[PoolKey] = s.HordePool ?? "";
            d[MaxCoresKey] = s.HordeMaxCores.ToString(CultureInfo.InvariantCulture);
            d[LocalCoresKey] = s.HordeLocalCores.ToString(CultureInfo.InvariantCulture);
            d[AgentPolicyKey] = s.HordeAgentPolicy.ToString();
            d[LinkRemoteKey] = s.HordeLinkRemote ?? TriDefault;
            d[PrintSummaryKey] = s.HordePrintSummary ?? TriDefault;
            d[ForceRemoteKey] = s.HordeForceRemote ?? TriDefault;
            d[DisableRemoteKey] = s.HordeDisableRemote ?? TriDefault;
            d[AllowRetryKey] = s.HordeAllowRetry ?? TriDefault;
            d[VisualizerKey] = s.HordeLaunchVisualizer ?? TriDefault;
            d[ObjCompressedKey] = s.HordeStoreObjCompressed ?? TriDefault;
            d[ClusterKey] = s.HordeCluster ?? "";
            d[ZoneKey] = s.HordeZone ?? "";
            d[EncryptionKey] = s.HordeEncryption ?? "";
            d[ConnectionModeKey] = s.HordeConnectionMode ?? "";
            d[RootDirKey] = s.HordeRootDir ?? "";
            d[StoreCapacityKey] = s.HordeStoreCapacityGb.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TrySet(HostSettings s, string key, string v)
        {
            v = v ?? "";
            switch (key)
            {
                case EnabledKey: s.HordeEnabled = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase); return true;
                case InstallPathKey: s.HordeInstallPath = string.IsNullOrWhiteSpace(v) ? DefaultInstallPath : v.Trim(); return true;
                case TargetKey: s.HordeTarget = Enum.TryParse(v, true, out HordeConfigTarget t) && Enum.IsDefined(typeof(HordeConfigTarget), t) ? t : HordeConfigTarget.Project; return true;
                case ServerKey: s.HordeServer = v.Trim(); return true;
                case PoolKey: s.HordePool = v.Trim(); return true;
                case MaxCoresKey: s.HordeMaxCores = Int(v); return true;
                case LocalCoresKey: s.HordeLocalCores = Int(v); return true;
                case AgentPolicyKey: s.HordeAgentPolicy = Enum.TryParse(v, true, out HordeAgentPolicy p) && Enum.IsDefined(typeof(HordeAgentPolicy), p) ? p : HordeAgentPolicy.Block; return true;
                case LinkRemoteKey: s.HordeLinkRemote = Tri(v); return true;
                case PrintSummaryKey: s.HordePrintSummary = Tri(v); return true;
                case ForceRemoteKey: s.HordeForceRemote = Tri(v); return true;
                case DisableRemoteKey: s.HordeDisableRemote = Tri(v); return true;
                case AllowRetryKey: s.HordeAllowRetry = Tri(v); return true;
                case VisualizerKey: s.HordeLaunchVisualizer = Tri(v); return true;
                case ObjCompressedKey: s.HordeStoreObjCompressed = Tri(v); return true;
                case ClusterKey: s.HordeCluster = v.Trim(); return true;
                case ZoneKey: s.HordeZone = v.Trim(); return true;
                case EncryptionKey: s.HordeEncryption = v.Trim(); return true;
                case ConnectionModeKey: s.HordeConnectionMode = v.Trim(); return true;
                case RootDirKey: s.HordeRootDir = v.Trim(); return true;
                case StoreCapacityKey: s.HordeStoreCapacityGb = Int(v); return true;
                default: return false;
            }
        }

        private static int Int(string v) => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? Math.Max(0, i) : 0;

        /// <summary>For the session header / diagnostics dump.</summary>
        public static void Describe(HostSettings s, IDictionary<string, object> d)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            ToValues(s, values);
            foreach (var kv in values) d[kv.Key] = kv.Value;
        }

        /// <summary>Whether a change between two settings touches the files (BuildConfiguration.xml side).</summary>
        public static bool BuildConfigChanged(HostSettings a, HostSettings b)
        {
            var x = new Dictionary<string, string>(StringComparer.Ordinal);
            var y = new Dictionary<string, string>(StringComparer.Ordinal);
            ToValues(a, x);
            ToValues(b, y);
            foreach (var kv in x)
            {
                if (kv.Key == AgentPolicyKey || kv.Key == InstallPathKey) continue;
                if (!y.TryGetValue(kv.Key, out var other) || !string.Equals(other, kv.Value, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
