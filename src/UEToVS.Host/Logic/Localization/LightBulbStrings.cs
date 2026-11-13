using System.Collections.Generic;
using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>VS 灯泡 / Alt+Enter 里新增项的文案（UE 规则修复、生成、导航、翻译、包围）。</summary>
    public static class LightBulbStrings
    {
        public static string GroupNavigate => Loc.T("导航", "Navigate");
        public static string GroupEdit => Loc.T("编辑", "Edit");

        // ---- UE 规则修复（UE0002–UE0006）
        public static string AddModuleDependency(string module, string owner, bool isPublic) => isPublic
            ? Loc.F("将模块 {0} 加到 {1}.Build.cs 的 Public 依赖", "Add module {0} to the public dependencies in {1}.Build.cs", module, owner)
            : Loc.F("将模块 {0} 加到 {1}.Build.cs 的 Private 依赖", "Add module {0} to the private dependencies in {1}.Build.cs", module, owner);

        public static string InsertGeneratedBody(string type) => Loc.F("在 {0} 中插入 GENERATED_BODY()", "Insert GENERATED_BODY() into {0}", type);
        public static string RenameToImplementation(string name) => Loc.F("改名为 {0}_Implementation", "Rename to {0}_Implementation", name);
        public static string CreateRepNotify(string function) => Loc.F("生成 {0}()（UFUNCTION 声明和实现）", "Create {0}() (UFUNCTION declaration and implementation)", function);

        public static string RegisterReplicated(IList<string> props) => props.Count == 1
            ? Loc.F("用 DOREPLIFETIME 注册 {0}", "Register {0} with DOREPLIFETIME", props[0])
            : Loc.F("用 DOREPLIFETIME 注册 {0} 个复制属性（{1}）", "Register {0} replicated properties with DOREPLIFETIME ({1})", props.Count, string.Join(", ", props));

        public static string FixApplied(string title) => Loc.F("已修复：{0}", "Fixed: {0}", title);
        public static string NoSourceForRepNotify(string function) =>
            Loc.F("已添加 {0} 的声明；没找到对应的 .cpp，实现请用 Alt+Enter“生成实现”", "Declared {0}; no matching .cpp was found, use Alt+Enter > Create implementation", function);

        public static string FixProblem(string problem)
        {
            switch (problem)
            {
                case "exists": return Loc.T("要生成的内容已经存在", "It already exists");
                case "stale": return Loc.T("代码已经改动，请等波浪线刷新后再试", "The code changed; wait for the squiggle to refresh and try again");
                case "in-class": return Loc.T("类体里的内联定义不能自动改名", "Inline definitions in the class body cannot be renamed automatically");
                case "brace-not-at-line-end": return Loc.T("类的 { 后面还有内容，请手动插入", "Something follows the class's '{'; insert it by hand");
                case "readonly": return Loc.T("文件是只读的", "The file is read-only");
                default: return Loc.F("无法自动修复（{0}）", "Cannot fix automatically ({0})", problem ?? "?");
            }
        }

        // ---- 生成
        public static string GenerateConstructor(string cls) => Loc.F("生成 {0} 的构造函数", "Generate constructor of {0}", cls);
        public static string GenerateReplication(int count) =>
            Loc.F("生成 / 补全 GetLifetimeReplicatedProps（{0} 个复制属性）", "Generate / complete GetLifetimeReplicatedProps ({0} replicated properties)", count);
        public static string GenerateServerRpc(string function, string rpc) => Loc.F("为 {0} 生成 Server RPC {1}", "Generate Server RPC {1} for {0}", function, rpc);

        // ---- 导航
        public static string GoTo(string name) => Loc.F("转到 {0}", "Go to {0}", name);
        public static string OpenFile(string file) => Loc.F("打开 {0}", "Open {0}", file);
        public static string GeneratedHeaderNotFound(string file) =>
            Loc.F("没找到 {0}（还没编译过，或者索引里没有 Intermediate 目录）", "{0} was not found (not built yet, or Intermediate is not indexed)", file);

        // ---- 翻译 / 包围
        public static string TranslateSelection => Loc.T("翻译选中内容", "Translate selection");
        public static string TranslateComment => Loc.T("翻译此注释", "Translate this comment");
        public static string SurroundWith => Loc.T("用 if / for / {} 等包围…", "Surround with if / for / {}...");
    }
}
