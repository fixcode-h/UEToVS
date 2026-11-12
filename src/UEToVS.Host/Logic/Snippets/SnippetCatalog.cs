using System;
using System.Collections.Generic;
using System.Linq;
using UEToVS.Core.Localization;

namespace UEToVS.Host.Logic.Snippets
{
    /// <summary>
    /// A live template. <see cref="Body"/> uses <c>$Name$</c> / <c>$Name=default$</c> fields (same name = linked copies),
    /// <c>$END$</c> for the final caret, <c>\t</c> for one indent level and <c>\n</c> for line breaks; defaults may use the
    /// variables <c>{FILE}</c> (file name without extension) and <c>{CLASS}</c> (file name without a UE prefix).
    /// </summary>
    public sealed class SnippetTemplate
    {
        public SnippetTemplate(string shortcut, string titleZh, string titleEn, string body)
        {
            Shortcut = shortcut;
            TitleZh = titleZh;
            TitleEn = titleEn;
            Body = body;
        }

        public string Shortcut { get; }
        public string TitleZh { get; }
        public string TitleEn { get; }
        public string Body { get; }
        public string Title => Loc.T(TitleZh, TitleEn);

        /// <summary>The body as one line for the completion description ("UPROPERTY(EditAnywhere, …) int32 Value;").</summary>
        public string Preview => SnippetExpander.Expand(this, string.Empty, "    ", "\n", null).Text;

        public override string ToString() => Shortcut;
    }

    /// <summary>Built-in UE live templates (UCLASS, USTRUCT, UENUM, UPROPERTY, UFUNCTION, UINTERFACE, UE_LOG, check, delegates …). Pure.</summary>
    public static class SnippetCatalog
    {
        public static IReadOnlyList<SnippetTemplate> BuiltIn { get; } = new List<SnippetTemplate>
        {
            new SnippetTemplate("uclass", "UCLASS 类", "UCLASS class",
                "UCLASS($Specifiers=$)\nclass $Name=A{CLASS}$ : public $Base=AActor$\n{\n\tGENERATED_BODY()\n\npublic:\n\t$Name$();\n$END$\n};"),
            new SnippetTemplate("ustruct", "USTRUCT 结构体", "USTRUCT struct",
                "USTRUCT($Specifiers=BlueprintType$)\nstruct $Name=F{CLASS}$\n{\n\tGENERATED_BODY()\n\n\t$END$\n};"),
            new SnippetTemplate("uenum", "UENUM 枚举", "UENUM enum",
                "UENUM($Specifiers=BlueprintType$)\nenum class $Name=E{CLASS}$ : uint8\n{\n\t$Value=None$ UMETA(DisplayName = \"$Value$\"),\n\t$END$\n};"),
            new SnippetTemplate("uinterface", "UINTERFACE 接口", "UINTERFACE interface",
                "UINTERFACE($Specifiers=MinimalAPI, Blueprintable$)\nclass U$Name={CLASS}$ : public UInterface\n{\n\tGENERATED_BODY()\n};\n\nclass I$Name$\n{\n\tGENERATED_BODY()\n\npublic:\n\t$END$\n};"),
            new SnippetTemplate("uprop", "UPROPERTY 属性", "UPROPERTY property",
                "UPROPERTY($Specifiers=EditAnywhere, BlueprintReadWrite$, Category = \"$Category=Default$\")\n$Type=int32$ $Name=Value$;$END$"),
            new SnippetTemplate("upropr", "复制属性（ReplicatedUsing）", "Replicated property (ReplicatedUsing)",
                "UPROPERTY(ReplicatedUsing = OnRep_$Name=Value$)\n$Type=int32$ $Name$;\n\nUFUNCTION()\nvoid OnRep_$Name$();$END$"),
            new SnippetTemplate("ufunc", "UFUNCTION 函数", "UFUNCTION function",
                "UFUNCTION($Specifiers=BlueprintCallable$, Category = \"$Category=Default$\")\n$Type=void$ $Name=DoSomething$($Params=$);$END$"),
            new SnippetTemplate("ufuncn", "BlueprintNativeEvent 函数", "BlueprintNativeEvent function",
                "UFUNCTION(BlueprintNativeEvent, Category = \"$Category=Default$\")\n$Type=void$ $Name=OnSomething$($Params=$);\n$Type$ $Name$_Implementation($Params$);$END$"),
            new SnippetTemplate("userver", "Server RPC", "Server RPC",
                "UFUNCTION(Server, $Reliability=Reliable$)\nvoid Server$Name=DoSomething$($Params=$);$END$"),
            new SnippetTemplate("uelog", "UE_LOG 日志", "UE_LOG",
                "UE_LOG($Category=LogTemp$, $Verbosity=Log$, TEXT(\"$Message=$\")$Args=$);$END$"),
            new SnippetTemplate("uelogf", "UE_LOG 带格式参数", "UE_LOG with arguments",
                "UE_LOG($Category=LogTemp$, $Verbosity=Log$, TEXT(\"$Message=Value: %s$\"), $Args=*Value$);$END$"),
            new SnippetTemplate("logcat", "声明日志分类", "Declare log category",
                "DECLARE_LOG_CATEGORY_EXTERN($Name=LogMyGame$, $Verbosity=Log$, All);$END$"),
            new SnippetTemplate("logdef", "定义日志分类", "Define log category",
                "DEFINE_LOG_CATEGORY($Name=LogMyGame$);$END$"),
            new SnippetTemplate("checkf", "checkf 断言", "checkf assertion",
                "checkf($Condition=$, TEXT(\"$Message=$\"));$END$"),
            new SnippetTemplate("uecheck", "check 断言", "check assertion",
                "check($Condition=$);$END$"),
            new SnippetTemplate("ensuremsg", "ensureMsgf", "ensureMsgf",
                "if (!ensureMsgf($Condition=$, TEXT(\"$Message=$\")))\n{\n\treturn$END$;\n}"),
            new SnippetTemplate("dmdel", "动态多播委托", "Dynamic multicast delegate",
                "DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam($Name=FOnSomething$, $ParamType=int32$, $ParamName=Value$);$END$"),
            new SnippetTemplate("ddel", "动态单播委托", "Dynamic delegate",
                "DECLARE_DYNAMIC_DELEGATE_OneParam($Name=FOnSomething$, $ParamType=int32$, $ParamName=Value$);$END$"),
            new SnippetTemplate("mdel", "多播委托", "Multicast delegate",
                "DECLARE_MULTICAST_DELEGATE_OneParam($Name=FOnSomething$, $ParamType=int32$);$END$"),
            new SnippetTemplate("del", "单播委托", "Delegate",
                "DECLARE_DELEGATE_OneParam($Name=FOnSomething$, $ParamType=int32$);$END$"),
            new SnippetTemplate("cds", "CreateDefaultSubobject", "CreateDefaultSubobject",
                "$Member=Component$ = CreateDefaultSubobject<$Type=USceneComponent$>(TEXT(\"$Member$\"));$END$"),
            new SnippetTemplate("adddyn", "AddDynamic 绑定", "AddDynamic binding",
                "$Delegate=OnSomething$.AddDynamic(this, &ThisClass::$Handler=HandleSomething$);$END$"),
            new SnippetTemplate("gbody", "GENERATED_BODY", "GENERATED_BODY", "GENERATED_BODY()\n$END$"),
            new SnippetTemplate("superbp", "BeginPlay 覆盖", "BeginPlay override",
                "void $Class={FILE}$::BeginPlay()\n{\n\tSuper::BeginPlay();\n\t$END$\n}"),
        };

        /// <summary>Templates whose shortcut starts with <paramref name="prefix"/> (case-insensitive).</summary>
        public static IEnumerable<SnippetTemplate> Matching(string prefix) =>
            BuiltIn.Where(t => string.IsNullOrEmpty(prefix) || t.Shortcut.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        public static SnippetTemplate Find(string shortcut) =>
            BuiltIn.FirstOrDefault(t => string.Equals(t.Shortcut, shortcut, StringComparison.Ordinal));

        /// <summary>
        /// The abbreviation before the caret when it may be expanded: an identifier that is the first thing on the line and ends at
        /// the caret (nothing but white space before it), not in a comment. Returns its start column or -1.
        /// </summary>
        public static int ShortcutAtLineStart(string line, int caret, out string word)
        {
            word = null;
            if (line == null || caret <= 0 || caret > line.Length) return -1;
            int s = caret;
            while (s > 0 && (char.IsLetterOrDigit(line[s - 1]) || line[s - 1] == '_')) s--;
            if (s == caret) return -1;
            for (int i = 0; i < s; i++)
                if (!char.IsWhiteSpace(line[i])) return -1;
            if (caret < line.Length && (char.IsLetterOrDigit(line[caret]) || line[caret] == '_')) return -1;
            word = line.Substring(s, caret - s);
            return s;
        }
    }
}
