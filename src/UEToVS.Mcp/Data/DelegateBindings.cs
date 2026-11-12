using System.Collections.Generic;
using Shared = UEToVS.Engine.Navigation.DelegateBindings;

namespace UEToVS.Mcp.Data
{
    /// <summary>One place where a function is bound to a delegate, input action or timer (find_delegate_bindings result).</summary>
    public sealed class DelegateBinding
    {
        public string File { get; set; }

        /// <summary>0-based.</summary>
        public int Line { get; set; }

        /// <summary>0-based column of the bind call.</summary>
        public int Column { get; set; }

        /// <summary>AddDynamic, BindUObject, BindUFunction, BindAction, SetTimer...</summary>
        public string Method { get; set; }

        /// <summary>dynamic (UFUNCTION by member pointer), native (raw/UObject/SP...), ufunction_name (by name string), input, timer.</summary>
        public string Kind { get; set; }

        /// <summary>The delegate expression in front of the call (OnClicked, Button->OnClicked...), when on the same line.</summary>
        public string Delegate { get; set; }

        /// <summary>How the function is named there: &amp;UFoo::Bar, "Bar", GET_FUNCTION_NAME_CHECKED(UFoo, Bar).</summary>
        public string Target { get; set; }

        /// <summary>Class named in the member pointer / GET_FUNCTION_NAME_CHECKED, or null (name string).</summary>
        public string TargetClass { get; set; }

        public string Text { get; set; }
    }

    /// <summary>
    /// The binding recogniser lives in the engine (<see cref="Shared"/>, also used by the call hierarchy); this keeps the
    /// MCP tool's types.
    /// </summary>
    public static class DelegateBindings
    {
        /// <summary>Identifiers whose occurrences mark a file as binding functions by name (for the string-name scan).</summary>
        public static readonly string[] ByNameMethods = Shared.ByNameMethods;

        public static bool IsBindMethod(string name) => Shared.IsBindMethod(name);

        /// <inheritdoc cref="Shared.Match"/>
        public static DelegateBinding Match(string line, string function, IReadOnlyList<string> previousLines = null)
        {
            var b = Shared.Match(line, function, previousLines);
            if (b == null) return null;
            return new DelegateBinding
            {
                File = b.File, Line = b.Line, Column = b.Column, Method = b.Method, Kind = b.Kind, Delegate = b.Delegate, Target = b.Target,
                TargetClass = b.TargetClass, Text = b.Text,
            };
        }
    }
}
