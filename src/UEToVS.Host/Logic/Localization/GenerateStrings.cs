using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>Generate menu texts (v0.4.2).</summary>
    public static class GenerateStrings
    {
        public static string CmdConstructor => Loc.T("生成构造函数", "Generate Constructor");
        public static string CmdReplication => Loc.T("生成 GetLifetimeReplicatedProps", "Generate GetLifetimeReplicatedProps");
        public static string CmdRpc => Loc.T("生成 RPC（_Implementation / _Validate）", "Generate RPC (Implementation / Validate)");

        public static readonly string[] Keywords = { "generate", "生成", "constructor", "构造", "replication", "复制", "DOREPLIFETIME", "rpc", "server", "_Implementation", "_Validate" };

        public static string NotHeader => Loc.T("请在头文件的类里执行生成命令", "Run Generate inside a class in a header file");
        public static string NoSource => Loc.T("没有找到对应的 .cpp，只修改了头文件", "No matching .cpp found; only the header was changed");

        /// <summary>Status bar text for a generator problem code (UeCodeGenerator).</summary>
        public static string Problem(string code)
        {
            switch (code)
            {
                case "no-class": return Loc.T("光标不在类 / 结构体里", "The caret is not inside a class / struct");
                case "exists": return Loc.T("要生成的代码已经存在", "The code to generate already exists");
                case "no-replicated": return Loc.T("这个类没有 Replicated / ReplicatedUsing 属性", "This class has no Replicated / ReplicatedUsing properties");
                case "no-function": return Loc.T("请把光标放在函数声明上", "Put the caret on a function declaration");
                default: return code ?? string.Empty;
            }
        }

        public static string Done(string what, string cls) => Loc.F("已生成：{0}（{1}）", "Generated: {0} ({1})", what, cls);
    }
}
