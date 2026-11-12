using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Core.Localization;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Patterns;
using UEToVS.UI.Perf;

namespace UEToVS.UI.Gallery
{
    /// <summary>Fake symbol used by the gallery.</summary>
    public sealed class SampleSymbol
    {
        public string Name;
        public string Container;
        public SymbolKind Kind;
        public BadgeKind Badge;
        public string File;
        public int Line;
        public bool IsHeader;
        public int GroupCount;
        public bool NonProject;
    }

    public sealed class SamplePresenter : IItemPresenter<SampleSymbol>, ISelectablePresenter<SampleSymbol>
    {
        public Func<SampleSymbol, IReadOnlyList<TextRange>> Ranges { get; set; }

        public void Present(SampleSymbol s, RowData row)
        {
            if (s.IsHeader)
            {
                row.IsGroupHeader = true;
                row.Primary = s.Name;
                row.GroupCount = s.GroupCount;
                return;
            }
            row.Glyph = s.Kind;
            row.Primary = s.Name;
            row.PrimaryRanges = Ranges?.Invoke(s);
            row.Secondary = s.Container;
            row.Right = s.File + ":" + s.Line;
            row.IsDimmed = s.NonProject;
            if (s.Badge != BadgeKind.Neutral) row.AddBadge(Strings.Badge(s.Badge), s.Badge);
        }

        public bool IsSelectable(SampleSymbol item) => !item.IsHeader;
    }

    public static class SampleData
    {
        private static readonly string[] Prefixes = { "A", "U", "F", "E", "I", "S" };
        private static readonly string[] Stems =
        {
            "Character", "Actor", "PlayerController", "GameMode", "Pawn", "Widget", "AbilitySystemComponent",
            "MovementComponent", "AnimInstance", "HUD", "GameState", "Projectile", "Weapon", "Inventory",
            "Interactable", "DamageType", "Subsystem", "AssetManager", "SaveGame", "BehaviorTree",
        };
        private static readonly string[] Members = { "BeginPlay", "Tick", "OnHit", "ApplyDamage", "Health", "MaxSpeed", "bIsJumping", "SpawnActor", "GetOwner", "SetupInputComponent" };
        private static readonly string[] Modules = { "Engine", "CoreUObject", "GameplayAbilities", "UMG", "MyGame", "AIModule" };

        public static List<SampleSymbol> Symbols(int count)
        {
            var list = new List<SampleSymbol>(count);
            var rnd = new Random(42);
            for (int i = 0; i < count; i++)
            {
                string p = Prefixes[i % Prefixes.Length];
                string stem = Stems[(i / Prefixes.Length) % Stems.Length];
                int variant = i / (Prefixes.Length * Stems.Length);
                bool member = i % 3 == 0;
                SymbolKind kind;
                BadgeKind badge;
                string name;
                if (member)
                {
                    name = Members[i % Members.Length] + (variant > 0 ? variant.ToString() : "");
                    kind = name.StartsWith("b") || name == "Health" || name == "MaxSpeed" ? SymbolKind.Property : SymbolKind.Method;
                    badge = kind == SymbolKind.Property ? BadgeKind.UProperty : BadgeKind.UFunction;
                }
                else
                {
                    name = p + stem + (variant > 0 ? variant.ToString() : "");
                    kind = p == "F" ? SymbolKind.Struct : p == "E" ? SymbolKind.Enum : p == "I" ? SymbolKind.Interface : SymbolKind.Class;
                    badge = p == "F" ? BadgeKind.UStruct : p == "E" ? BadgeKind.UEnum : p == "I" ? BadgeKind.UInterface : p == "S" ? BadgeKind.Neutral : BadgeKind.UClass;
                }
                string module = Modules[rnd.Next(Modules.Length)];
                list.Add(new SampleSymbol
                {
                    Name = name,
                    Container = member ? p + stem : module,
                    Kind = kind,
                    Badge = badge,
                    File = stem + ".h",
                    Line = 20 + rnd.Next(800),
                    NonProject = module != "MyGame" && i % 7 == 0,
                });
            }
            return list;
        }

        /// <summary>Same data grouped by kind with header rows (VirtualList group headers).</summary>
        public static List<SampleSymbol> Grouped(List<SampleSymbol> items)
        {
            var result = new List<SampleSymbol>(items.Count + 8);
            foreach (var g in items.GroupBy(s => s.Kind).OrderBy(g => g.Key))
            {
                var arr = g.ToList();
                result.Add(new SampleSymbol { IsHeader = true, Name = g.Key.ToString(), GroupCount = arr.Count });
                result.AddRange(arr);
            }
            return result;
        }

        public static string FakeSource(SampleSymbol s, int lines = 400)
        {
            var sb = new StringBuilder();
            for (int i = 1; i <= lines; i++)
            {
                if (i == s.Line) sb.Append("\tvoid ").Append(s.Name).Append("(float DeltaSeconds) override;\n");
                else if (i % 17 == 0) sb.Append("UCLASS(BlueprintType)\n");
                else if (i % 5 == 0) sb.Append("\t// line ").Append(i).Append('\n');
                else sb.Append("\tUPROPERTY(EditAnywhere) float Value").Append(i).Append(" = 0.f;\n");
            }
            return sb.ToString();
        }

        public static PerformanceViewData Perf(int tick, Random rnd, List<double> cpu, List<double> mem)
        {
            cpu.Add(Math.Max(0, Math.Min(100, 18 + 12 * Math.Sin(tick / 9.0) + rnd.NextDouble() * 10)));
            mem.Add(780e6 + 60e6 * Math.Sin(tick / 40.0) + rnd.NextDouble() * 10e6);
            if (cpu.Count > 600) cpu.RemoveAt(0);
            if (mem.Count > 600) mem.RemoveAt(0);
            double progress = (tick % 120) / 120.0;
            return new PerformanceViewData
            {
                EngineMemoryBytes = mem[mem.Count - 1],
                EngineMemoryBudgetBytes = 1024e6,
                HostMemoryBytes = 96e6 + rnd.NextDouble() * 4e6,
                HostMemoryBudgetBytes = 150e6,
                CpuPercent = cpu[cpu.Count - 1],
                IndexState = Loc.T("索引中", "Indexing"),
                IndexProgress = progress,
                IndexDetail = Loc.F("{0} / 3,120 文件", "{0} / 3,120 files", (int)(progress * 3120)),
                CpuHistory = cpu.ToArray(),
                MemoryHistory = mem.ToArray(),
                Active = new[]
                {
                    new ActiveWorkView { Name = Loc.T("解析头文件", "Parse headers"), Lane = "background", Done = (long)(progress * 3120), Total = 3120, ElapsedMs = tick * 1000 % 120000 },
                    new ActiveWorkView { Name = Loc.T("合并增量段", "Merge delta segments"), Lane = "background", Done = 3, Total = 0, ElapsedMs = 840 },
                },
                Phases = new[]
                {
                    new PhaseView { Name = Loc.T("列举文件", "List files"), DurationMs = 412, CpuMs = 380, Items = 41230, Detail = "dirs=812" },
                    new PhaseView { Name = Loc.T("加载缓存", "Load cache"), DurationMs = 1290, CpuMs = 900, Items = 38100, Detail = "cacheHits=38100" },
                    new PhaseView { Name = Loc.T("解析", "Parse"), DurationMs = 15400 + tick * 10, CpuMs = 88000, Items = (long)(progress * 3120), Running = true, Detail = "threads=8" },
                    new PhaseView { Name = Loc.T("合并", "Merge"), DurationMs = 0, CpuMs = 0, Items = 0 },
                },
                MemoryParts = new[]
                {
                    new MemoryPartView { Name = Loc.T("字符串池", "String pool"), ManagedBytes = 64_000_000, MappedBytes = 120_000_000, DiskBytes = 118_000_000 },
                    new MemoryPartView { Name = Loc.T("符号", "Symbols"), ManagedBytes = 32_000_000, MappedBytes = 210_000_000, DiskBytes = 205_000_000 },
                    new MemoryPartView { Name = Loc.T("引用", "Occurrences"), ManagedBytes = 12_000_000, MappedBytes = 330_000_000, DiskBytes = 325_000_000 },
                    new MemoryPartView { Name = Loc.T("增量段", "Delta"), ManagedBytes = 48_000_000, MappedBytes = 0, DiskBytes = 9_000_000 },
                },
                Operations = new[]
                {
                    new OperationView { Name = "search.symbols", Count = 1200 + tick, Errors = 0, P50Ms = 6.2, P95Ms = 18, MaxMs = 64, BudgetMs = 30 },
                    new OperationView { Name = "search.text", Count = 88, Errors = 1, P50Ms = 41, P95Ms = 160 + rnd.Next(40), MaxMs = 420, BudgetMs = 150 },
                    new OperationView { Name = "file.symbols", Count = 640, Errors = 0, P50Ms = 2.1, P95Ms = 7.5, MaxMs = 31, BudgetMs = 20 },
                    new OperationView { Name = "goto.definition", Count = 311, Errors = 2, P50Ms = 9, P95Ms = 24, MaxMs = 120, BudgetMs = 50 },
                    new OperationView { Name = "parse.file", Count = 9100, Errors = 12, P50Ms = 3.3, P95Ms = 22, MaxMs = 380, BudgetMs = 0 },
                },
                BackgroundPaused = false,
            };
        }
    }

    /// <summary>In-memory palette source over sample symbols (debounced, cancellable, streams partial results).</summary>
    public sealed class SamplePaletteSource : ICommandPaletteSource<SampleSymbol>
    {
        private readonly SamplePresenter _presenter = new SamplePresenter();
        private readonly Dictionary<SampleSymbol, IReadOnlyList<TextRange>> _ranges = new Dictionary<SampleSymbol, IReadOnlyList<TextRange>>();

        public SamplePaletteSource(Func<IReadOnlyList<SampleSymbol>> data)
        {
            Data = data;
            _presenter.Ranges = s => _ranges.TryGetValue(s, out var r) ? r : null;
            Tabs = new[]
            {
                new PaletteTab("all", () => Loc.T("全部", "All")),
                new PaletteTab("types", () => Loc.T("类型", "Types")) { Placeholder = () => Loc.T("输入类型名", "Type name") },
                new PaletteTab("symbols", () => Loc.T("符号", "Symbols")),
                new PaletteTab("files", () => Loc.T("文件", "Files")),
                new PaletteTab("actions", () => Loc.T("操作", "Actions")) { SupportsPreview = false },
                new PaletteTab("text", () => Loc.T("文本", "Text"), 200),
            };
            Filters = new[] { new PaletteFilter("nonProject", () => Loc.T("包含非项目项", "Include non-project items")) { Shortcut = "Alt+N" } };
        }

        public Func<IReadOnlyList<SampleSymbol>> Data { get; }
        public IReadOnlyList<PaletteTab> Tabs { get; }
        public IReadOnlyList<PaletteFilter> Filters { get; }
        public IItemPresenter<SampleSymbol> Presenter => _presenter;

        public Action<SampleSymbol, InvokeKind> Invoked { get; set; }

        public async Task<PaletteResult<SampleSymbol>> QueryAsync(PaletteQuery q, IProgress<PaletteResult<SampleSymbol>> partial, CancellationToken ct)
        {
            var data = Data() ?? Array.Empty<SampleSymbol>();
            string text = q.Text ?? string.Empty;
            bool nonProject = q.IsFilterOn("nonProject");
            string tab = q.TabId;
            var matches = await Task.Run(() =>
            {
                var res = new List<(SampleSymbol S, List<TextRange> R)>();
                foreach (var s in data)
                {
                    ct.ThrowIfCancellationRequested();
                    if (s.IsHeader || (s.NonProject && !nonProject)) continue;
                    bool isType = s.Kind == SymbolKind.Class || s.Kind == SymbolKind.Struct || s.Kind == SymbolKind.Enum || s.Kind == SymbolKind.Interface;
                    if (tab == "types" && !isType) continue;
                    if (tab == "symbols" && isType) continue;
                    if (tab == "files" || tab == "actions") continue;
                    if (!HighlightRanges.TryMatchSubsequence(s.Name, text, out var r)) continue;
                    res.Add((s, r));
                    if (res.Count >= 5000) break;
                }
                return res;
            }, ct);
            if (tab == "text") await Task.Delay(120, ct); // pretend full-text search is slower
            lock (_ranges)
            {
                _ranges.Clear();
                foreach (var m in matches) _ranges[m.S] = m.R;
            }
            var items = matches.Select(m => m.S).ToList();
            if (items.Count > 200) partial?.Report(new PaletteResult<SampleSymbol> { Items = items.Take(200).ToList(), IsComplete = false });
            return new PaletteResult<SampleSymbol>
            {
                Items = items,
                IsComplete = items.Count < 5000,
                TabCounts = new Dictionary<string, int> { [tab ?? "all"] = items.Count },
            };
        }

        public CodePreviewRequest GetPreview(SampleSymbol item) => item == null ? null : new CodePreviewRequest
        {
            FilePath = "C:/Project/Source/" + item.File,
            Line = item.Line,
            Column = 6,
            Length = item.Name.Length,
            Text = SampleData.FakeSource(item, Math.Max(item.Line + 40, 200)),
        };

        public bool Invoke(SampleSymbol item, InvokeKind kind, PaletteQuery query, IReadOnlyList<SampleSymbol> all)
        {
            Invoked?.Invoke(item, kind);
            return true;
        }
    }
}
