using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace UEToVS.Engine.Unreal
{
    /// <summary>A module entry of a .uproject / .uplugin "Modules" array.</summary>
    public sealed class ModuleDescriptor
    {
        public string Name { get; set; }

        /// <summary>Runtime, Editor, Developer, UncookedOnly, Program, ... as written in the descriptor.</summary>
        public string Type { get; set; }

        public string LoadingPhase { get; set; }
    }

    /// <summary>A "Plugins" entry: reference to a plugin with an explicit enabled state.</summary>
    public sealed class PluginReference
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
        public bool Optional { get; set; }
    }

    /// <summary>The fields of a .uproject or .uplugin that the engine process cares about.</summary>
    public sealed class UnrealDescriptor
    {
        public string FilePath { get; set; }
        public string FriendlyName { get; set; }
        public string EngineAssociation { get; set; }

        /// <summary>.uplugin only: null when the field is absent (UE then decides by plugin location).</summary>
        public bool? EnabledByDefault { get; set; }

        public bool CanContainContent { get; set; }
        public List<ModuleDescriptor> Modules { get; } = new List<ModuleDescriptor>();
        public List<PluginReference> Plugins { get; } = new List<PluginReference>();

        /// <summary>Set when the file could not be read or parsed; the descriptor is then empty.</summary>
        public string Error { get; set; }

        private static readonly JsonDocumentOptions Options = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        /// <summary>Reads a descriptor; never throws (errors are reported through <see cref="Error"/>).</summary>
        public static UnrealDescriptor Load(string path)
        {
            try
            {
                return Parse(path, File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                return new UnrealDescriptor { FilePath = path, Error = ex.GetType().Name + ": " + ex.Message };
            }
        }

        public static UnrealDescriptor Parse(string path, string json)
        {
            var d = new UnrealDescriptor { FilePath = path };
            try
            {
                using var doc = JsonDocument.Parse(json ?? string.Empty, Options);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return d;
                d.FriendlyName = GetString(root, "FriendlyName");
                d.EngineAssociation = GetString(root, "EngineAssociation");
                if (root.TryGetProperty("EnabledByDefault", out var ebd) && (ebd.ValueKind == JsonValueKind.True || ebd.ValueKind == JsonValueKind.False))
                    d.EnabledByDefault = ebd.GetBoolean();
                d.CanContainContent = GetBool(root, "CanContainContent", false);
                if (root.TryGetProperty("Modules", out var mods) && mods.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mods.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object) continue;
                        string name = GetString(m, "Name");
                        if (string.IsNullOrEmpty(name)) continue;
                        d.Modules.Add(new ModuleDescriptor { Name = name, Type = GetString(m, "Type"), LoadingPhase = GetString(m, "LoadingPhase") });
                    }
                }
                if (root.TryGetProperty("Plugins", out var plugs) && plugs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in plugs.EnumerateArray())
                    {
                        if (p.ValueKind != JsonValueKind.Object) continue;
                        string name = GetString(p, "Name");
                        if (string.IsNullOrEmpty(name)) continue;
                        d.Plugins.Add(new PluginReference { Name = name, Enabled = GetBool(p, "Enabled", false), Optional = GetBool(p, "Optional", false) });
                    }
                }
            }
            catch (Exception ex)
            {
                d.Error = ex.GetType().Name + ": " + ex.Message;
            }
            return d;
        }

        private static string GetString(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool GetBool(JsonElement e, string name, bool fallback) =>
            e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : fallback;
    }
}
