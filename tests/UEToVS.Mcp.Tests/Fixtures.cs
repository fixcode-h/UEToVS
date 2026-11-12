using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UEToVS.Contracts;
using UEToVS.Core.Storage;
using UEToVS.Engine;
using UEToVS.Engine.Rpc;
using UEToVS.Engine.Tests;
using UEToVS.Mcp;
using UEToVS.Mcp.Data;
using UEToVS.Mcp.Tools;
using Xunit;

namespace UEToVS.Mcp.Tests
{
    /// <summary>
    /// The synthetic UE tree of the engine tests plus a small class hierarchy and some uses, so navigation fallbacks
    /// have something to find:
    /// UObject ← AActor ← APawn ← ACharacter (engine module "Engine") ← AMyCharacter ← AMyHero (project).
    /// </summary>
    public static class Tree
    {
        public static FakeUnrealTree Create()
        {
            var t = new FakeUnrealTree();
            t.Write("UE/Engine/Source/Runtime/Engine/Engine.Build.cs",
                "public class Engine : ModuleRules { public Engine(ReadOnlyTargetRules Target) : base(Target) { } }");
            t.Write("UE/Engine/Source/Runtime/Engine/Classes/GameFramework/Actor.h",
                "#pragma once\nclass UObject {};\nUCLASS()\nclass ENGINE_API AActor : public UObject\n{\npublic:\n\tvirtual void BeginPlay();\n\tint32 Tags;\n};\n");
            t.Write("UE/Engine/Source/Runtime/Engine/Classes/GameFramework/Pawn.h",
                "#pragma once\n#include \"GameFramework/Actor.h\"\nUCLASS()\nclass ENGINE_API APawn : public AActor\n{\npublic:\n\tvoid PossessedBy();\n};\n");
            t.Write("UE/Engine/Source/Runtime/Engine/Classes/GameFramework/Character.h",
                "#pragma once\n#include \"GameFramework/Pawn.h\"\nUCLASS()\nclass ENGINE_API ACharacter : public APawn\n{\npublic:\n\tvoid Jump();\n};\n");
            t.Write("Game/Source/MyGame/Public/MyHero.h",
                "#pragma once\n#include \"MyCharacter.h\"\n\nUCLASS()\nclass MYGAME_API AMyHero : public AMyCharacter\n{\n\tGENERATED_BODY()\npublic:\n\tvoid Taunt();\n};\n");
            t.Write("Game/Source/MyGame/Private/MyHero.cpp",
                "#include \"MyHero.h\"\n\nvoid AMyHero::Taunt()\n{\n\tFire(10.f);\n\tJump();\n\tHealth = 5.f;\n}\n");
            return t;
        }

        public static EngineHost StartEngine(FakeUnrealTree t, string pipeName = null) =>
            new EngineHost(new EngineOptions { PipeName = pipeName ?? "uetovs-mcptest-" + Guid.NewGuid().ToString("N").Substring(0, 10), SolutionDir = t.ProjectRoot, MaxParallelism = 2, WatchEngine = false, NoWatch = true });

        public static Dictionary<string, JsonElement> Args(object anonymous)
        {
            string json = JsonSerializer.Serialize(anonymous);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        }
    }

    /// <summary>Index built by a real engine run, then the engine stopped: tools answer from the on-disk index only.</summary>
    public sealed class OfflineFixture : IDisposable
    {
        public OfflineFixture()
        {
            T = Tree.Create();
            var host = Tree.StartEngine(T);
            host.StartIndexing();
            host.Index.WaitForReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Index = new CodeIndex(T.ProjectRoot, DataSourceMode.Auto);
            Invoker = new ToolInvoker(new ToolCatalog(Index));
        }

        public FakeUnrealTree T { get; }
        public CodeIndex Index { get; }
        public ToolInvoker Invoker { get; }

        public async Task<JsonElement> Call(string tool, object args = null, bool expectError = false)
        {
            var r = await Invoker.InvokeAsync(tool, args == null ? new Dictionary<string, JsonElement>() : Tree.Args(args), CancellationToken.None);
            Assert.True(r.IsError == expectError, $"{tool}: isError={r.IsError} text={r.Text}");
            using var doc = JsonDocument.Parse(r.Text);
            return doc.RootElement.Clone();
        }

        public void Dispose()
        {
            Index.Dispose();
            T.Dispose();
        }
    }

    /// <summary>A running engine (in-process, real named pipe) that published .vs/UEToVS/engine.json.</summary>
    public sealed class LiveFixture : IDisposable
    {
        private readonly Task _server;

        public LiveFixture()
        {
            T = Tree.Create();
            Host = Tree.StartEngine(T);
            Host.StartIndexing();
            var pipe = new PipeServer(Host.Options.PipeName, Host);
            _server = pipe.RunAsync(Host.ShutdownToken);
            pipe.Listening.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Host.Index.WaitForReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            // what UEToVS.Engine's Program does once the pipe listens (DiscoveryPublisher)
            DiscoveryPath = new EngineDiscoveryFile
            {
                Pipe = Host.Options.PipeName,
                Pid = Environment.ProcessId,
                ProtocolVersion = ProtocolInfo.Version,
                EngineVersion = "test",
                SolutionDir = T.ProjectRoot,
                StartedUtc = DateTime.UtcNow,
            }.Write(Host.Paths);
            Index = new CodeIndex(T.ProjectRoot, DataSourceMode.Auto);
            Invoker = new ToolInvoker(new ToolCatalog(Index));
        }

        public FakeUnrealTree T { get; }
        public EngineHost Host { get; }
        public string DiscoveryPath { get; }
        public CodeIndex Index { get; }
        public ToolInvoker Invoker { get; }

        public async Task<JsonElement> Call(string tool, object args = null, bool expectError = false)
        {
            var r = await Invoker.InvokeAsync(tool, args == null ? new Dictionary<string, JsonElement>() : Tree.Args(args), CancellationToken.None);
            Assert.True(r.IsError == expectError, $"{tool}: isError={r.IsError} text={r.Text}");
            using var doc = JsonDocument.Parse(r.Text);
            return doc.RootElement.Clone();
        }

        public void Dispose()
        {
            Index.Dispose();
            Host.RequestShutdown("test-end");
            try { _server.Wait(5000); } catch { }
            Host.StopAsync(TimeSpan.FromSeconds(5)).Wait(10000);
            T.Dispose();
        }
    }

    internal static class J
    {
        public static IEnumerable<JsonElement> Items(this JsonElement e, string name = "items") => e.GetProperty(name).EnumerateArray();

        public static string Str(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public static int Int(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;

        public static bool Bool(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }
}
