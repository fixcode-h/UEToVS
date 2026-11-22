using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>v0.9.12 写代码辅助的文案：UE0009 / UE0010 修复、说明符编辑器、输入即纠正 . / -&gt;。</summary>
    public static class CodingAssistStrings
    {
        // ---- UE0009 / UE0010
        public static string FixByValue(string property, string type) => Loc.F("把 {0} 改成按值存（{1}）", "Store {0} by value ({1})", property, type);
        public static string FixRemoveUProperty(string property) => Loc.F("去掉 {0} 的 UPROPERTY()（不参与反射）", "Remove UPROPERTY() from {0} (not reflected)", property);
        public static string FixToReference(string parameter, string type) => Loc.F("把参数 {0} 改成 {1}", "Change parameter {0} to {1}", parameter, type);
        public static string FixNumber(string parameter, string from, string to) => Loc.F("把参数 {0} 的类型 {1} 改成 {2}（蓝图支持）", "Change parameter {0} from {1} to {2} (Blueprint-supported)", parameter, from, to);
        public static string FixRemoveUFunction(string function) => Loc.F("去掉 {0} 的 UFUNCTION()（不参与反射，蓝图里不再可用）", "Remove UFUNCTION() from {0} (not reflected, no longer in Blueprints)", function);

        // ---- 说明符编辑器
        public static string EditSpecifiers(string macro) => Loc.F("编辑 {0} 说明符…", "Edit {0} Specifiers...", macro);
        public static string EditorTitle(string macro) => Loc.F("编辑 {0} 说明符", "Edit {0} Specifiers", macro);
        public static string EditorDescription => Loc.T(
            "勾选要用的说明符和 meta 键，带值的在右边填写。互斥的组合会在下面标出；确定后一次改写整个宏的参数，Ctrl+Z 可整体撤销。",
            "Check the specifiers and meta keys to use and fill in values on the right. Conflicting combinations are flagged below; OK rewrites the macro's arguments in one edit (one Ctrl+Z undoes it).");
        public static string Apply => Loc.T("确定", "OK");
        public static string SpecifiersHeader => Loc.T("说明符", "Specifiers");
        public static string MetaHeader => Loc.T("meta 元数据", "meta");
        public static string UnknownHeader => Loc.T("其他（知识库里没有，原样保留）", "Other (not in the knowledge base, kept as written)");
        public static string FilterPlaceholder => Loc.T("筛选说明符…", "Filter specifiers...");
        public static string Preview => Loc.T("结果", "Result");
        public static string ValuePlaceholder(string hint) => Loc.F("值 {0}", "value {0}", hint ?? string.Empty);
        public static string NotOnMacro => Loc.T("光标不在 UPROPERTY / UFUNCTION / UCLASS / USTRUCT 上", "The caret is not on a UPROPERTY / UFUNCTION / UCLASS / USTRUCT");
        public static string Unchanged => Loc.T("说明符没有变化", "The specifiers did not change");
        public static string Changed(string macro) => Loc.F("已更新 {0} 的说明符", "Updated the {0} specifiers", macro);
        public static string Stale => Loc.T("代码已经改动，请重新打开编辑器", "The code changed; open the editor again");
        public static string ReadOnly => Loc.T("文件是只读的", "The file is read-only");

        public static string Problem(UEToVS.Host.Logic.Editing.SpecifierProblem p)
        {
            switch (p.Kind)
            {
                case "conflict": return Loc.F("{0} 和 {1} 不能同时使用", "{0} and {1} cannot be used together", p.A, p.B);
                case "missing-value": return Loc.F("{0} 需要填写值", "{0} needs a value", p.A);
                case "rpc-reliability": return Loc.F("{0} RPC 需要 Reliable 或 Unreliable", "{0} RPCs need Reliable or Unreliable", p.A);
                default: return p.ToString();
            }
        }

        // ---- 命令说明（CommandCatalog 里是常量字符串，这里只给运行时用的）
        public static string KwEditSpecifiers => "specifier uproperty ufunction uclass ustruct meta 说明符 反射 元数据";
    }
}
