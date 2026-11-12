using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Localization
{
    /// <summary>
    /// User-visible strings of the v0.4.5 refactorings (batch 2): Change Signature, Move Implementation, Add Forward
    /// Declaration, Introduce Variable, Extract Method, Encapsulate Field / Getter-Setter. Both languages.
    /// </summary>
    public static class Refactor2Strings
    {
        // ---- commands (Find Action / menus)
        public static string CmdChangeSignature => Loc.T("更改签名…", "Change Signature...");
        public static string CmdMoveImplementation => Loc.T("移动实现（头文件 ↔ 源文件）", "Move Implementation (Header ↔ Source)");
        public static string CmdMoveClassImplementations => Loc.T("将类的全部内联实现移到源文件", "Move All Inline Implementations of the Class to Source");
        public static string CmdAddForwardDeclaration => Loc.T("添加前置声明", "Add Forward Declaration");
        public static string CmdIntroduceVariable => Loc.T("引入变量…", "Introduce Variable...");
        public static string CmdExtractMethod => Loc.T("提取方法…", "Extract Method...");
        public static string CmdEncapsulateField => Loc.T("封装字段…", "Encapsulate Field...");
        public static string CmdGenerateGetterSetter => Loc.T("生成 Getter / Setter…", "Generate Getter / Setter...");

        public static readonly string[] KwChangeSignature = { "change signature", "parameters", "reorder parameters", "add parameter", "remove parameter", "更改签名", "参数", "调整参数", "签名" };
        public static readonly string[] KwMoveImplementation = { "move implementation", "move to cpp", "move to header", "inline", "移动实现", "移到源文件", "移到头文件", "内联" };
        public static readonly string[] KwAddForwardDeclaration = { "forward declaration", "forward declare", "class", "前置声明", "前向声明" };
        public static readonly string[] KwIntroduceVariable = { "introduce variable", "extract variable", "local", "引入变量", "提取变量", "局部变量" };
        public static readonly string[] KwExtractMethod = { "extract method", "extract function", "提取方法", "提取函数" };
        public static readonly string[] KwEncapsulateField = { "encapsulate field", "getter", "setter", "property", "blueprint getter", "封装字段", "访问器" };

        // ---- Alt+Enter items
        public static string ChangeSignatureOf(string name) => Loc.T($"更改 {name} 的签名…", $"Change signature of {name}...");
        public static string MoveToSource(string name) => Loc.T($"将 {name} 的实现移到源文件", $"Move implementation of {name} to the source file");
        public static string MoveToHeader(string name) => Loc.T($"将 {name} 的实现移到头文件", $"Move implementation of {name} to the header");
        public static string MoveClassToSource(string cls, int count) => Loc.T($"将 {cls} 的全部内联实现（{count} 个）移到源文件", $"Move all {count} inline implementations of {cls} to the source file");
        public static string AddForwardDeclarationOf(string declaration) => Loc.T($"添加前置声明 {declaration}", $"Add forward declaration {declaration}");
        public static string IntroduceVariableItem => Loc.T("引入变量…", "Introduce variable...");
        public static string ExtractMethodItem => Loc.T("提取方法…", "Extract method...");
        public static string EncapsulateFieldOf(string name) => Loc.T($"为 {name} 生成 Getter / Setter…", $"Generate getter / setter for {name}...");

        // ---- dialogs
        public static string ChangeSignatureTitle(string name) => Loc.T($"更改 {name} 的签名", $"Change Signature of {name}");
        public static string ChangeSignatureDescription => Loc.T(
            "编辑参数（类型、名称、默认值）。新参数在调用处填入“调用值”；默认值只写在声明中。",
            "Edit the parameters (type, name, default). New parameters get the \"call value\" at call sites; defaults stay in declarations only.");
        public static string ParameterType => Loc.T("类型", "Type");
        public static string ParameterName => Loc.T("名称", "Name");
        public static string ParameterDefault => Loc.T("默认值", "Default");
        public static string ParameterCallValue => Loc.T("调用值（新参数）", "Call value (new)");
        public static string AddParameter => Loc.T("添加参数", "Add parameter");
        public static string RemoveParameter => Loc.T("删除", "Remove");
        public static string MoveUp => Loc.T("上移", "Up");
        public static string MoveDown => Loc.T("下移", "Down");
        public static string IncludeOverrides => Loc.T("同时修改基类声明与所有重写（override 链）", "Also change the base declarations and all overrides (override chain)");
        public static string SignaturePreview => Loc.T("新签名", "New signature");
        public static string Next => Loc.T("下一步", "Next");
        public static string ChangeSignatureButton => Loc.T("更改签名", "Change Signature");
        public static string PreviewTitle(string name) => Loc.T($"预览：更改 {name} 的签名", $"Preview: Change Signature of {name}");
        public static string PreviewDescription(int project, int engine, int problems) => Loc.T(
            $"项目中 {project} 处将被修改；引擎 / 第三方文件中的 {engine} 处只列出，不修改；{problems} 处无法自动修改（只列出）。取消勾选可跳过某处。",
            $"{project} location(s) in the project will change; {engine} in engine / third-party files are listed only; {problems} cannot be changed automatically (listed only). Uncheck to skip one.");

        public static string VariableTitle => Loc.T("引入变量", "Introduce Variable");
        public static string VariableDescription(string expression, string type) => Loc.T($"表达式：{expression}\n类型：{type}", $"Expression: {expression}\nType: {type}");
        public static string VariableName => Loc.T("变量名", "Variable name");
        public static string ReplaceAll(int count) => Loc.T($"替换函数中全部 {count} 处相同表达式", $"Replace all {count} identical expressions in the function");
        public static string IntroduceButton => Loc.T("引入", "Introduce");

        public static string ExtractTitle => Loc.T("提取方法", "Extract Method");
        public static string ExtractDescription(string cls) => Loc.T($"在 {cls} 中新建 private 成员函数：声明加在头文件，定义加在当前函数之后。",
            $"A new private member of {cls}: declared in the header, defined after the current function.");
        public static string MethodName => Loc.T("函数名", "Function name");
        public static string ExtractButton => Loc.T("提取", "Extract");

        public static string EncapsulateTitle(string name) => Loc.T($"为 {name} 生成 Getter / Setter", $"Generate Getter / Setter for {name}");
        public static string EncapsulateDescription => Loc.T("访问器加在类的 public 区域；可同时在 UPROPERTY 上加 BlueprintGetter / BlueprintSetter。",
            "Accessors go into the class's public section; BlueprintGetter / BlueprintSetter can be added to the UPROPERTY too.");
        public static string GenerateGetter(string name) => Loc.T($"生成 Getter：{name}()", $"Generate getter: {name}()");
        public static string GenerateSetter(string name) => Loc.T($"生成 Setter：{name}(…)", $"Generate setter: {name}(...)");
        public static string BlueprintGetter => Loc.T("加入 BlueprintGetter（UFUNCTION(BlueprintGetter)）", "Add BlueprintGetter (UFUNCTION(BlueprintGetter))");
        public static string BlueprintSetter => Loc.T("加入 BlueprintSetter（UFUNCTION(BlueprintSetter)）", "Add BlueprintSetter (UFUNCTION(BlueprintSetter))");
        public static string GenerateButton => Loc.T("生成", "Generate");

        // ---- validation
        public static string NameTaken => Loc.T("名称已被使用", "The name is already used");
        public static string InvalidName => Loc.T("不是合法的 C++ 标识符", "Not a valid C++ identifier");

        public static string ValidationError(string code) => code switch
        {
            "empty-type" => Loc.T("有参数缺少类型", "A parameter has no type"),
            "invalid-type" => Loc.T("参数类型无效", "A parameter type is invalid"),
            "unbalanced" => Loc.T("括号或尖括号不配对", "Unbalanced brackets"),
            "empty-name" => Loc.T("有参数缺少名称", "A parameter has no name"),
            "invalid-name" => Loc.T("参数名不是合法的 C++ 标识符", "A parameter name is not a valid C++ identifier"),
            "duplicate-name" => Loc.T("参数名重复", "Duplicate parameter name"),
            "default-order" => Loc.T("带默认值的参数之后不能再有不带默认值的参数", "Parameters without a default cannot follow a parameter with one"),
            "missing-call-value" => Loc.T("新参数需要调用值（或默认值）", "A new parameter needs a call value (or a default)"),
            _ => code,
        };

        // ---- status bar
        public static string Unchanged => Loc.T("UEToVS：签名没有变化", "UEToVS: the signature did not change");
        public static string Searching(string name) => Loc.T($"UEToVS：正在查找 {name} 的调用与声明…", $"UEToVS: finding the calls and declarations of {name}...");
        public static string SignatureChanged(string name, int edits, int files) =>
            Loc.T($"UEToVS：已更改 {name} 的签名（{edits} 处，{files} 个文件）", $"UEToVS: changed the signature of {name} ({edits} edits in {files} files)");
        public static string Moved(int count, string file) => Loc.T($"UEToVS：已移动 {count} 个实现到 {file}", $"UEToVS: moved {count} implementation(s) to {file}");
        public static string ForwardDeclared(string declaration) => Loc.T($"UEToVS：已添加 {declaration}", $"UEToVS: added {declaration}");
        public static string VariableIntroduced(string name, int count) => Loc.T($"UEToVS：已引入变量 {name}（替换 {count} 处）", $"UEToVS: introduced {name} ({count} replaced)");
        public static string MethodExtracted(string name) => Loc.T($"UEToVS：已提取方法 {name}", $"UEToVS: extracted method {name}");
        public static string AccessorsGenerated(string names) => Loc.T($"UEToVS：已生成 {names}", $"UEToVS: generated {names}");
        public static string SelectFirst => Loc.T("UEToVS：请先选中要处理的表达式或语句", "UEToVS: select the expression or statements first");
        public static string Skipped(int count) => Loc.T($"UEToVS：{count} 处因文件内容已变化或互相重叠而跳过", $"UEToVS: {count} location(s) skipped (file changed or overlapping)");

        /// <summary>Why an operation is not possible (planner problem codes).</summary>
        public static string Problem(string code) => "UEToVS：" + (code switch
        {
            "not-function" => Loc.T("光标不在函数的声明或定义上", "the caret is not on a function declaration or definition"),
            "template" => Loc.T("不支持模板函数", "templates are not supported"),
            "unparsable" => Loc.T("无法可靠地解析这段代码，未做修改", "this code could not be parsed reliably; nothing was changed"),
            "engine-base" => Loc.T("基类声明在引擎 / 第三方代码中，无法更改签名", "the base declaration is in engine / third-party code; the signature cannot change"),
            "no-base" => Loc.T("这是一个 override，但找不到基类声明", "this is an override but its base declaration was not found"),
            "no-member" => Loc.T("光标处没有可移动的内联实现", "no inline implementation at the caret"),
            "no-definition" => Loc.T("光标处没有类外定义", "no out-of-line definition at the caret"),
            "no-declaration" => Loc.T("头文件中找不到匹配的声明", "no matching declaration in the header"),
            "exists" => Loc.T("目标文件中已有该实现", "the implementation already exists in the target file"),
            "ambiguous" => Loc.T("头文件中有多个可能匹配的声明", "several declarations in the header could match"),
            "shared-line" => Loc.T("实现与其他代码在同一行，未移动", "the implementation shares a line with other code; not moved"),
            "overlap" => Loc.T("修改范围互相重叠，未移动", "the edits overlap; not moved"),
            "ue-generated" => Loc.T("_Implementation / _Validate 必须留在源文件中", "_Implementation / _Validate must stay in the source file"),
            "no-counterpart" => Loc.T("找不到对应的源文件 / 头文件", "the matching source / header file was not found"),
            "no-type" => Loc.T("光标不在类型名上", "the caret is not on a type name"),
            "included" => Loc.T("该类型已被包含，无需前置声明", "the type is already included; no forward declaration needed"),
            "declared" => Loc.T("已有前置声明或定义", "a forward declaration or the definition is already there"),
            "by-value" => Loc.T("该类型按值使用，需要 #include 而不是前置声明", "the type is used by value; it needs an #include, not a forward declaration"),
            "inline-use" => Loc.T("内联代码使用了该类型的成员，需要 #include", "inline code uses the type's members; it needs an #include"),
            "nested" => Loc.T("嵌套类型无法前置声明", "nested types cannot be forward declared"),
            "enum-underlying" => Loc.T("非 enum class 且没有指定底层类型的枚举无法前置声明", "an enum without enum class or an underlying type cannot be forward declared"),
            "not-header" => Loc.T("前置声明只加在头文件中", "forward declarations go into headers only"),
            "empty" => Loc.T("选区为空", "the selection is empty"),
            "not-expression" => Loc.T("选区不是一个完整的表达式", "the selection is not a complete expression"),
            "not-in-function" => Loc.T("选区不在函数体中", "the selection is not inside a function body"),
            "lvalue" => Loc.T("选中的表达式被赋值或取地址，引入变量会改变语义", "the expression is assigned or its address is taken; a variable would change the meaning"),
            "partial" => Loc.T("选区只是表达式的一部分", "the selection is only part of an expression"),
            "loop" => Loc.T("表达式在循环条件中，每次都要重新求值", "the expression is in a loop head and must be evaluated every time"),
            "short-circuit" => Loc.T("表达式在 && / || / ?: 之后，可能不会被求值", "the expression follows && / || / ?: and may not run"),
            "braceless" => Loc.T("表达式在没有花括号的语句中", "the expression is in a statement without braces"),
            "case" => Loc.T("表达式在 case 标签之后", "the expression follows a case label"),
            "identifier" => Loc.T("选中的已经是一个变量", "the selection is already a variable"),
            "partial-statement" => Loc.T("选区必须是完整的语句", "the selection must be whole statements"),
            "lambda" => Loc.T("不支持 lambda 中的代码", "code inside lambdas is not supported"),
            "jump" => Loc.T("选区中有跳出选区的 return / break / continue / goto", "the selection has return / break / continue / goto leaving it"),
            "label" => Loc.T("选区中有标签或 case", "the selection has a label or a case"),
            "preprocessor" => Loc.T("选区中有预处理指令", "the selection has preprocessor lines"),
            "several-results" => Loc.T("选区修改了多个之后还要用的变量", "the selection changes several variables used afterwards"),
            "unknown-type" => Loc.T("无法确定某个变量的类型", "the type of a variable is unknown"),
            "reference-result" => Loc.T("返回值是引用，无法提取", "the result is a reference; it cannot be extracted"),
            "not-member" => Loc.T("只支持在类的成员函数中提取方法", "methods can only be extracted from member functions"),
            "in-header" => Loc.T("只支持源文件（.cpp）中的函数", "only functions in source (.cpp) files are supported"),
            "no-class-body" => Loc.T("在头文件中找不到类体", "the class body was not found in the header"),
            "no-field" => Loc.T("光标不在类的成员变量声明上", "the caret is not on a member variable declaration"),
            "bitfield" => Loc.T("位域只能生成 bool 访问器", "bit-fields only get bool accessors"),
            "no-header" => Loc.T("找不到类的头文件", "the class header was not found"),
            _ => Loc.T($"无法安全完成（{code}），未做修改", $"cannot be done safely ({code}); nothing was changed"),
        });
    }
}
