using System;
using System.IO;
using System.Linq;
using System.Threading;
using UEToVS.Contracts;
using UEToVS.Engine.Indexing;
using UEToVS.Engine.IntelliSense;
using UEToVS.Engine.Syntax;
using UEToVS.Engine.Unreal;
using Xunit;

namespace UEToVS.Engine.Tests
{
    /// <summary>
    /// A file restored at VS startup is classified while the index is still loading: library calls and types come back
    /// unresolved. When the index phase changes the editor asks again for the same document version, and the answer must be
    /// recomputed against the new index instead of being served from the per-document line cache.
    /// </summary>
    public sealed class ClassifyIndexReadyTests : IDisposable
    {
        private const string Header = "typedef struct lua_State lua_State;\nint lua_getglobal(lua_State* L, const char* name);\n";
        private const string Source = "#include \"lua.h\"\nvoid GetPackagePath(lua_State* L)\n{\n    lua_getglobal(L, \"UnLua\");\n}\n";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "uetovs-classify-ready", Guid.NewGuid().ToString("N"));
        private readonly SegmentedIndexBackend _backend = new SegmentedIndexBackend();
        private readonly FileTable _files = new FileTable();
        private long _phase;

        public ClassifyIndexReadyTests()
        {
            Directory.CreateDirectory(_root);
            _backend.TryLoad(Path.Combine(_root, ".index"), "classify-ready");
        }

        private string Add(string name, string text, bool index)
        {
            string path = Path.Combine(_root, name);
            File.WriteAllText(path, text);
            var listed = new ListedFile { Path = path, Name = name, Module = "UnLua", Origin = ItemOrigin.Project, Kind = name.EndsWith(".h") ? SourceFileKind.Header : SourceFileKind.Source };
            _files.Upsert(listed);
            if (index)
            {
                var ctx = new FileContext { Path = path, Module = listed.Module, Origin = listed.Origin, Kind = listed.Kind };
                _backend.UpdateParsed(path, new SyntaxFileParser().Parse(ctx, File.ReadAllBytes(path), CancellationToken.None));
            }
            return path;
        }

        [Fact]
        public void IndexPhaseChange_RecomputesCachedLines()
        {
            string cpp = Add("UnLuaLib.cpp", Source, index: true);
            var service = new IntelliSenseService(_backend, _files, indexStamp: () => Interlocked.Read(ref _phase));
            var doc = new CaretRequest { FilePath = cpp, Text = Source, TextVersion = 2 };
            var request = new ClassifyRequest { Document = doc, StartLine = 0, EndLine = 10 };

            // index still loading: the Lua header is not in it yet
            var before = service.Classify(request, CancellationToken.None);
            Assert.DoesNotContain(before.Spans, s => s.Line == 3 && s.Column == 4 && s.Kind == SemanticKind.Function);

            Add("lua.h", Header, index: true);
            Interlocked.Increment(ref _phase); // index phase changed (e.g. Parsing → Ready)

            var after = service.Classify(request, CancellationToken.None);
            Assert.Contains(after.Spans, s => s.Line == 3 && s.Column == 4 && s.Kind == SemanticKind.Function);
            Assert.Contains(after.Spans, s => s.Line == 1 && s.Column == 20 && (s.Kind == SemanticKind.Typedef || s.Kind == SemanticKind.Struct));
        }

        [Fact]
        public void SameIndexPhase_ServesTheLineCache()
        {
            string cpp = Add("UnLuaLib.cpp", Source, index: true);
            Add("lua.h", Header, index: true);
            var service = new IntelliSenseService(_backend, _files, indexStamp: () => Interlocked.Read(ref _phase));
            var request = new ClassifyRequest { Document = new CaretRequest { FilePath = cpp, Text = Source, TextVersion = 2 }, StartLine = 0, EndLine = 10 };
            service.Classify(request, CancellationToken.None);
            long misses = service.Classifier.LineMisses;
            service.Classify(request, CancellationToken.None);
            Assert.Equal(misses, service.Classifier.LineMisses);
        }

        public void Dispose()
        {
            _backend.Dispose();
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
