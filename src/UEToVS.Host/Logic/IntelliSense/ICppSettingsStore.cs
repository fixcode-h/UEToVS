using System.Collections.Generic;

namespace UEToVS.Host.Logic.IntelliSense
{
    /// <summary>
    /// Access to VS's C/C++ text-editor options. The VS implementation (DteCppSettingsStore) goes through
    /// DTE.Properties["TextEditor", page] on the UI thread; tests use an in-memory fake. Implementations never throw.
    /// </summary>
    public interface ICppSettingsStore
    {
        /// <summary>All property names on a page, or null when the page cannot be enumerated.</summary>
        IReadOnlyList<string> GetPropertyNames(string page);

        /// <summary>Reads a property; false when it does not exist or cannot be read. Name matching is case-insensitive.</summary>
        bool TryRead(string page, string name, out object value, out string error);

        /// <summary>Writes a property; false (with an error text) when it fails.</summary>
        bool TryWrite(string page, string name, object value, out string error);
    }
}
