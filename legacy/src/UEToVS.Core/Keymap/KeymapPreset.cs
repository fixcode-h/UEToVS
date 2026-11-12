using System;
using System.Collections.Generic;

namespace UEToVS.Core.Keymap
{
    /// <summary>One shortcut in a preset. Either a VS command name or one of our own command ids.</summary>
    public sealed class KeyBindingEntry
    {
        public KeyBindingEntry(string command, string scope, string keys, string description)
        {
            Command = command;
            Scope = scope;
            Keys = keys;
            Description = description;
        }

        /// <summary>
        /// Canonical VS command name ("Edit.GoToDefinition") or "UEToVS:&lt;CommandId&gt;" for commands of this extension.
        /// </summary>
        public string Command { get; }

        /// <summary>"Global" or "Text Editor".</summary>
        public string Scope { get; }

        /// <summary>VS key syntax, e.g. "Ctrl+Shift+N" or chord "Ctrl+K, Ctrl+O".</summary>
        public string Keys { get; }

        public string Description { get; }

        public bool IsOwnCommand => Command.StartsWith(KeymapPresets.OwnPrefix, StringComparison.Ordinal);

        public string OwnCommandName => IsOwnCommand ? Command.Substring(KeymapPresets.OwnPrefix.Length) : null;

        /// <summary>DTE binding string, e.g. "Text Editor::Ctrl+B".</summary>
        public string DteBinding => Scope + "::" + Keys;
    }

    public sealed class KeymapPreset
    {
        public KeymapPreset(string id, string displayName, IReadOnlyList<KeyBindingEntry> bindings)
        {
            Id = id;
            DisplayName = displayName;
            Bindings = bindings;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public IReadOnlyList<KeyBindingEntry> Bindings { get; }
    }
}
