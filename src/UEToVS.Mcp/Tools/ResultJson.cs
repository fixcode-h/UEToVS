using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UEToVS.Contracts;

namespace UEToVS.Mcp.Tools
{
    /// <summary>
    /// Compact JSON for tool results. Conventions (also stated in the tool descriptions): line and col are 1-based;
    /// fields with default values are omitted (no "container" at global scope, "def" only when true, "origin" only
    /// when not Project); paths are absolute.
    /// </summary>
    public static class ResultJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep paths and CJK readable
        };

        public static string Serialize(JsonNode node) => node.ToJsonString(Options);

        public static JsonObject Symbol(SymbolInfo s, bool includeSignature = true)
        {
            var o = new JsonObject { ["name"] = s.Name, ["kind"] = KindName(s.Kind) };
            if (!string.IsNullOrEmpty(s.Container)) o["container"] = s.Container;
            AddLocation(o, s.Location);
            if (includeSignature && !string.IsNullOrEmpty(s.Signature)) o["sig"] = s.Signature;
            if (s.Ue != UeFlags.None) o["ue"] = UeNames(s.Ue);
            if (!string.IsNullOrEmpty(s.Specifiers)) o["spec"] = s.Specifiers;
            if (!string.IsNullOrEmpty(s.Module)) o["module"] = s.Module;
            if (s.Origin != ItemOrigin.Project) o["origin"] = OriginName(s.Origin);
            if (s.IsDefinition) o["def"] = true;
            return o;
        }

        public static void AddLocation(JsonObject o, SourceLocation loc)
        {
            if (loc == null || string.IsNullOrEmpty(loc.FilePath)) return;
            o["file"] = loc.FilePath;
            o["line"] = loc.Line + 1;
            o["col"] = loc.Column + 1;
        }

        public static string KindName(SymbolKind k) => k switch
        {
            SymbolKind.EnumValue => "enum_value",
            _ => k.ToString().ToLowerInvariant(),
        };

        public static string OriginName(ItemOrigin o) => o switch
        {
            ItemOrigin.Project => "project",
            ItemOrigin.ProjectPlugin => "project_plugin",
            ItemOrigin.Engine => "engine",
            ItemOrigin.EnginePlugin => "engine_plugin",
            ItemOrigin.ThirdParty => "third_party",
            _ => o.ToString().ToLowerInvariant(),
        };

        public static string UeNames(UeFlags f)
        {
            var parts = new List<string>();
            foreach (UeFlags v in Enum.GetValues(typeof(UeFlags)))
                if (v != UeFlags.None && (f & v) == v) parts.Add(v == UeFlags.GeneratedBody ? "GENERATED_BODY" : v.ToString().ToUpperInvariant());
            return string.Join(",", parts);
        }

        public static JsonObject File(FileItem f)
        {
            var o = new JsonObject { ["name"] = f.Name, ["path"] = f.Path };
            if (!string.IsNullOrEmpty(f.Module)) o["module"] = f.Module;
            if (f.Origin != ItemOrigin.Project) o["origin"] = OriginName(f.Origin);
            return o;
        }

        public static JsonObject Asset(AssetItem a)
        {
            var o = new JsonObject { ["name"] = a.Name, ["objectPath"] = a.ObjectPath, ["file"] = a.FilePath };
            string cls = a.ClassName;
            if (!string.IsNullOrEmpty(cls)) o["class"] = cls;
            else
            {
                string guess = AssetClassGuess.FromName(a.Name, a.FilePath);
                if (guess != null) o["classGuess"] = guess;
            }
            if (a.Origin != ItemOrigin.Project) o["origin"] = OriginName(a.Origin);
            return o;
        }

        /// <summary>Standard page envelope: {source, indexReady, count, offset, hasMore, note?, items:[...]}.</summary>
        public static JsonObject Page(string source, bool indexReady, IReadOnlyList<JsonNode> items, int offset, bool hasMore, string note = null, int? nonProjectHidden = null)
        {
            var arr = new JsonArray();
            foreach (var i in items) arr.Add(i);
            var o = new JsonObject
            {
                ["source"] = source,
                ["indexReady"] = indexReady,
                ["count"] = items.Count,
                ["offset"] = offset,
                ["hasMore"] = hasMore,
            };
            if (hasMore) o["nextOffset"] = offset + items.Count;
            if (nonProjectHidden is int n && n > 0) o["nonProjectHidden"] = n;
            if (!string.IsNullOrEmpty(note)) o["note"] = note;
            o["items"] = arr;
            return o;
        }
    }

    /// <summary>
    /// Best-effort asset class from UE naming conventions (the asset index lists files; reading class names out of
    /// .uasset headers is the Blueprint indexer's job). Reported as "classGuess" so agents know it is inferred.
    /// </summary>
    public static class AssetClassGuess
    {
        private static readonly (string Prefix, string Class)[] Prefixes =
        {
            ("BPI_", "BlueprintInterface"), ("BPFL_", "BlueprintFunctionLibrary"), ("BPML_", "BlueprintMacroLibrary"),
            ("ABP_", "AnimBlueprint"), ("WBP_", "WidgetBlueprint"), ("WB_", "WidgetBlueprint"), ("EUW_", "EditorUtilityWidgetBlueprint"),
            ("GA_", "Blueprint"), ("GE_", "Blueprint"), ("BTT_", "Blueprint"), ("BTS_", "Blueprint"), ("BTD_", "Blueprint"),
            ("BP_", "Blueprint"), ("B_", "Blueprint"),
            ("MI_", "MaterialInstanceConstant"), ("MF_", "MaterialFunction"), ("MPC_", "MaterialParameterCollection"), ("M_", "Material"),
            ("T_", "Texture2D"), ("TC_", "TextureCube"), ("RT_", "TextureRenderTarget2D"),
            ("SM_", "StaticMesh"), ("SK_", "SkeletalMesh"), ("SKEL_", "Skeleton"), ("PHYS_", "PhysicsAsset"), ("PA_", "PhysicsAsset"),
            ("AM_", "AnimMontage"), ("AS_", "AnimSequence"), ("A_", "AnimSequence"), ("BS_", "BlendSpace"), ("AO_", "AimOffsetBlendSpace"),
            ("SC_", "SoundCue"), ("SW_", "SoundWave"), ("S_", "SoundWave"), ("ATT_", "SoundAttenuation"), ("MS_", "MetaSoundSource"),
            ("NS_", "NiagaraSystem"), ("NE_", "NiagaraEmitter"), ("FX_", "NiagaraSystem"), ("P_", "ParticleSystem"), ("PS_", "ParticleSystem"),
            ("DT_", "DataTable"), ("CT_", "CurveTable"), ("DA_", "DataAsset"), ("PDA_", "PrimaryDataAsset"), ("C_", "CurveFloat"), ("Curve_", "CurveFloat"),
            ("E_", "UserDefinedEnum"), ("ST_", "UserDefinedStruct"), ("F_", "UserDefinedStruct"),
            ("BB_", "BlackboardData"), ("BT_", "BehaviorTree"), ("EQS_", "EnvQuery"),
            ("IA_", "InputAction"), ("IMC_", "InputMappingContext"), ("LS_", "LevelSequence"), ("Font_", "Font"), ("UI_", "WidgetBlueprint"),
        };

        public static string FromName(string name, string filePath = null)
        {
            if (filePath != null && filePath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)) return "World";
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var (prefix, cls) in Prefixes)
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return cls;
            return null;
        }

        /// <summary>Class filter: case-insensitive substring of the known or guessed class ("Blueprint" also matches WidgetBlueprint).</summary>
        public static bool Matches(AssetItem a, string classFilter)
        {
            if (string.IsNullOrWhiteSpace(classFilter)) return true;
            string cls = !string.IsNullOrEmpty(a.ClassName) ? a.ClassName : FromName(a.Name, a.FilePath);
            return cls != null && cls.IndexOf(classFilter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
