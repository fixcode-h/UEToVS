using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.Unreal;

namespace UEToVS.Mcp.Tools
{
    /// <summary>v0.7.3 find_blueprint_usages (data: the engine's offline Blueprint index, <see cref="Data.BlueprintIndexReader"/>).</summary>
    public sealed partial class ToolCatalog
    {
        public const string BlueprintSource = "blueprint-index";

        private const string NoBlueprintIndexNote =
            "No Blueprint index yet: it is written by the UEToVS engine (.vs/UEToVS/Blueprints/blueprints.jsonl.gz) a few seconds after " +
            "indexing finishes in Visual Studio. Open the solution in VS once, then try again.";

        private const int MaxBlueprintUsers = 1000;

        private const string UnreadableBlueprintIndexNote =
            "The Blueprint index file could not be read (the engine may be rewriting it right now, or it is from another UEToVS version). " +
            "Try again in a few seconds; if it keeps failing, let Visual Studio re-index the solution.";

        private ToolDefinition BlueprintUsagesTool() => Define(
            "find_blueprint_usages", "Blueprint users and derived Blueprints",
            ToolGuides.Description("find_blueprint_usages"),
            Schema(
                "\"class\":{\"type\":\"string\",\"description\":\"C++ class, with or without its prefix: AMyCharacter or MyCharacter.\"}," +
                "\"member\":{\"type\":\"string\",\"description\":\"Function or property of that class (UFUNCTION / UPROPERTY name). Omit for derived Blueprints and class users.\"}," +
                PagingProps, "class"),
            BlueprintUsagesAsync);

        private Task<ToolOutput> BlueprintUsagesAsync(ToolArgs a, CancellationToken ct) =>
            Task.Run(() => BlueprintUsages(a, ct), ct); // 大工程要把索引文件读一遍（几秒），不占住调用线程

        private ToolOutput BlueprintUsages(ToolArgs a, CancellationToken ct)
        {
            string cls = a.String("class", required: true, maxLength: 256).Trim();
            string member = a.String("member", maxLength: 256)?.Trim();
            var (limit, offset) = Paging(a);
            int colon = cls.LastIndexOf("::", System.StringComparison.Ordinal);
            if (member == null && colon > 0)
            {
                member = cls.Substring(colon + 2);
                cls = cls.Substring(0, colon);
            }
            string ue = BlueprintIndex.UeClassName(cls);
            var scan = _index.Blueprints.Query(ue, string.IsNullOrEmpty(member) ? null : member, MaxBlueprintUsers, ct);
            string note = scan == null ? NoBlueprintIndexNote : !scan.Ok ? UnreadableBlueprintIndexNote : null;
            var items = new List<JsonNode>();
            if (scan != null && scan.Ok)
            {
                if (string.IsNullOrEmpty(member))
                {
                    items.AddRange(scan.Derived.Select(d => (JsonNode)DerivedJson(d)));
                    foreach (var e in scan.Users) items.Add(UsageJson(e, "usesClass"));
                }
                else
                {
                    foreach (var e in scan.Users) items.Add(UsageJson(e, "usesMember"));
                    if (items.Count == 0)
                        note = "No Blueprint or level mentions '" + member + "' together with class '" + ue + "'. Matches are by name in the asset's name table; " +
                               "a member only used from C++ or from engine content does not show up.";
                }
                if (scan.UsersCapped)
                    note = "Only " + MaxBlueprintUsers + " users are listed; more Blueprints match. Pass 'member' or a more specific class to narrow it.";
            }
            var (page, more) = Slice(items, offset, limit);
            var payload = ResultJson.Page(BlueprintSource, scan != null && scan.Ok, page, offset, more, note);
            payload.Insert(0, "class", ue);
            if (!string.IsNullOrEmpty(member)) payload.Insert(1, "member", member);
            return new ToolOutput { Payload = payload, ResultCount = page.Count, Source = BlueprintSource };
        }

        private static JsonObject AssetJson(BlueprintEntry e)
        {
            var o = new JsonObject { ["name"] = e.Name };
            if (e.ObjectPath != null) o["objectPath"] = e.ObjectPath;
            o["file"] = e.FilePath;
            return o;
        }

        private static JsonObject UsageJson(BlueprintEntry e, string relation)
        {
            var o = AssetJson(e);
            o["kind"] = e.IsMap ? "level" : "blueprint";
            o["relation"] = relation;
            if (e.ParentClass != null) o["parent"] = UassetReader.ClassName(e.ParentClass);
            return o;
        }

        private static JsonObject DerivedJson(DerivedBlueprint d)
        {
            var o = AssetJson(d.Entry);
            o["kind"] = "blueprint";
            o["relation"] = "derives";
            o["parent"] = UassetReader.ClassName(d.Entry.ParentClass);
            if (d.Children.Count > 0) o["children"] = new JsonArray(d.Children.Select(c => (JsonNode)DerivedJson(c)).ToArray());
            return o;
        }
    }
}
