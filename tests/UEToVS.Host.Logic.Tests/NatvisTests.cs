using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace UEToVS.Host.Logic.Tests
{
    public sealed class NatvisTests
    {
        private static readonly XNamespace Ns = "http://schemas.microsoft.com/vstudio/debugger/natvis/2010";

        /// <summary>Types the engine's Unreal.natvis already visualizes: never redefined here.</summary>
        private static readonly string[] EngineTypes =
        {
            "TArray<*,*>", "FString", "FName", "FText", "TMap<*,*,*,*>", "TSet<*,*,*>", "UObjectBase", "UObject", "TObjectPtr<*>",
            "TWeakObjectPtr<*,*>", "TSharedPtr<*,*>", "TSharedRef<*,*>", "TUniquePtr<*,*>", "FVector", "FRotator", "FGuid",
        };

        [Fact]
        public void Natvis_IsValidAndDoesNotRepeatEngineTypes()
        {
            var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "UEToVS.natvis"));
            Assert.Equal(Ns + "AutoVisualizer", doc.Root.Name);
            var names = doc.Root.Elements(Ns + "Type").Select(t => (string)t.Attribute("Name")).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
            Assert.Contains("FGameplayTag", names);
            Assert.Contains("FSoftObjectPath", names);
            foreach (var e in EngineTypes) Assert.DoesNotContain(e, names);
            foreach (var t in doc.Root.Elements(Ns + "Type")) Assert.NotEmpty(t.Elements(Ns + "DisplayString"));
        }

        [Fact]
        public void Natvis_IsAVsixAsset()
        {
            var manifest = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "source.extension.vsixmanifest"));
            Assert.Contains("Type=\"NativeVisualizer\"", manifest);
            Assert.Contains("UEToVS.natvis", manifest);
        }
    }
}
