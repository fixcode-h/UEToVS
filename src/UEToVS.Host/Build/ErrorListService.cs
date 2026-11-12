using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using UEToVS.Contracts;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Build;
using UEToVS.Host.Logic.IntelliSense;
using UEToVS.Host.Navigation;

namespace UEToVS.Host.Build
{
    /// <summary>
    /// UEToVS rows of the VS Error List (source "UEToVS"): the UE rule diagnostics the squiggle tagger computed for open files
    /// (it only checks the visible part of each file) plus, optionally, the last build's errors (VS lists most under "Build"
    /// already). An <see cref="ITableDataSource"/> fed from <see cref="ErrorListModel"/>; a new snapshot per change, posted
    /// to the sinks. Turned off by errorList.enabled. UI thread.
    /// </summary>
    internal sealed class ErrorListService : ITableDataSource, IDisposable
    {
        private static readonly Logger L = Log.For("ErrorList");
        private readonly ErrorListModel _model = new ErrorListModel();
        private readonly List<SinkManager> _sinks = new List<SinkManager>();
        private readonly Factory _factory;
        private ITableManager _manager;

        private ErrorListService()
        {
            _factory = new Factory(this);
        }

        public static ErrorListService Instance { get; private set; }

        public string SourceTypeIdentifier => StandardTableDataSources.ErrorTableDataSource;

        public string Identifier => "UEToVS.ErrorList";

        public string DisplayName => ErrorListModel.SourceName;

        public static void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Instance != null) return;
            var s = new ErrorListService();
            if (Package.GetGlobalService(typeof(SComponentModel)) is IComponentModel cm)
            {
                var provider = cm.GetService<ITableManagerProvider>();
                s._manager = provider?.GetTableManager(StandardTables.ErrorsTable);
                s._manager?.AddSource(s, StandardTableColumnDefinitions.DetailsExpander, StandardTableColumnDefinitions.ErrorSeverity,
                    StandardTableColumnDefinitions.ErrorCode, StandardTableColumnDefinitions.ErrorSource, StandardTableColumnDefinitions.BuildTool,
                    StandardTableColumnDefinitions.Text, StandardTableColumnDefinitions.DocumentName, StandardTableColumnDefinitions.Line,
                    StandardTableColumnDefinitions.Column);
            }
            if (s._manager == null) L.Warn("errorList.unavailable", "Error List table manager unavailable");
            NavigationContext.SettingsChanged += s.OnSettingsChanged;
            Instance = s;
        }

        private static bool Enabled => NavigationContext.Settings.ErrorListEnabled;

        // ---------------------------------------------------------------- inputs

        /// <summary>UE rule diagnostics of one open file (replaces its rows; null / empty removes them).</summary>
        public void SetRules(string file, IReadOnlyList<CodeDiagnostic> items)
        {
            if (string.IsNullOrEmpty(file)) return;
            List<ErrorListEntry> rows = null;
            if (items != null && items.Count > 0 && Enabled)
            {
                rows = new List<ErrorListEntry>(items.Count);
                foreach (var d in items)
                    rows.Add(new ErrorListEntry
                    {
                        File = file, Line = d.Line, Column = d.Column, Code = d.Code, Message = CodeDiagnosticText.Message(d),
                        Severity = d.Severity == CodeDiagnosticSeverity.Error ? BuildSeverity.Error
                            : d.Severity == CodeDiagnosticSeverity.Warning ? BuildSeverity.Warning : BuildSeverity.Info,
                        Origin = ErrorListOrigin.UeRules, Tool = ErrorListModel.SourceName,
                    });
            }
            if (_model.SetRules(file, rows)) Publish();
        }

        public void SetBuild(BuildErrorSet set)
        {
            if (_model.SetBuild(set)) Publish();
        }

        private void OnSettingsChanged(object sender, Options.SettingsChangedEventArgs e) => Safe.Execute(L, "errorList.settings", () =>
        {
            if (e.Old.ErrorListEnabled == e.New.ErrorListEnabled && e.Old.ErrorListIncludeBuild == e.New.ErrorListIncludeBuild) return;
            if (!e.New.ErrorListEnabled)
            {
                var build = BuildErrorService.Instance?.Current;
                _model.Clear();
                _model.SetBuild(build);
            }
            Publish();
        });

        // ---------------------------------------------------------------- table source

        private void Publish()
        {
            var s = NavigationContext.Settings;
            var rows = s.ErrorListEnabled ? _model.Rows(true, s.ErrorListIncludeBuild) : new List<ErrorListEntry>();
            _factory.Update(rows, _model.Version);
            lock (_sinks)
                foreach (var sink in _sinks) sink.Sink.FactorySnapshotChanged(_factory);
            L.Debug("errorList.published", null, ("rows", rows.Count), ("files", _model.RuleFileCount));
        }

        public IDisposable Subscribe(ITableDataSink sink)
        {
            var m = new SinkManager(this, sink);
            lock (_sinks) _sinks.Add(m);
            sink.AddFactory(_factory);
            return m;
        }

        public void Dispose()
        {
            NavigationContext.SettingsChanged -= OnSettingsChanged;
            try
            {
                _manager?.RemoveSource(this);
            }
            catch (Exception)
            {
                // shutting down
            }
            if (Instance == this) Instance = null;
        }

        private sealed class SinkManager : IDisposable
        {
            private readonly ErrorListService _owner;

            public SinkManager(ErrorListService owner, ITableDataSink sink)
            {
                _owner = owner;
                Sink = sink;
            }

            public ITableDataSink Sink { get; }

            public void Dispose()
            {
                lock (_owner._sinks) _owner._sinks.Remove(this);
            }
        }

        private sealed class Factory : ITableEntriesSnapshotFactory
        {
            private readonly ErrorListService _owner;
            private Snapshot _current = new Snapshot(new List<ErrorListEntry>(), 0);

            public Factory(ErrorListService owner) => _owner = owner;

            public int CurrentVersionNumber => _current.VersionNumber;

            public void Update(List<ErrorListEntry> rows, int version) => _current = new Snapshot(rows, version);

            public ITableEntriesSnapshot GetCurrentSnapshot() => _current;

            public ITableEntriesSnapshot GetSnapshot(int versionNumber)
            {
                var c = _current;
                return c.VersionNumber == versionNumber ? c : null;
            }

            public void Dispose()
            {
            }
        }

        private sealed class Snapshot : WpfTableEntriesSnapshotBase
        {
            private readonly List<ErrorListEntry> _rows;
            private readonly int _version;

            public Snapshot(List<ErrorListEntry> rows, int version)
            {
                _rows = rows;
                _version = version;
            }

            public override int Count => _rows.Count;

            public override int VersionNumber => _version;

            public override bool TryGetValue(int index, string keyName, out object content)
            {
                content = null;
                if (index < 0 || index >= _rows.Count) return false;
                var r = _rows[index];
                switch (keyName)
                {
                    case StandardTableKeyNames.DocumentName: content = r.File ?? string.Empty; return true;
                    case StandardTableKeyNames.Line: content = r.Line; return true;
                    case StandardTableKeyNames.Column: content = r.Column; return true;
                    case StandardTableKeyNames.Text: content = r.Message ?? string.Empty; return true;
                    case StandardTableKeyNames.FullText: content = r.Message ?? string.Empty; return true;
                    case StandardTableKeyNames.ErrorCode: content = r.Code ?? string.Empty; return true;
                    case StandardTableKeyNames.ErrorSeverity:
                        content = r.Severity == BuildSeverity.Error ? __VSERRORCATEGORY.EC_ERROR
                            : r.Severity == BuildSeverity.Warning ? __VSERRORCATEGORY.EC_WARNING : __VSERRORCATEGORY.EC_MESSAGE;
                        return true;
                    case StandardTableKeyNames.ErrorSource:
                        content = r.Origin == ErrorListOrigin.Build ? ErrorSource.Build : ErrorSource.Other;
                        return true;
                    case StandardTableKeyNames.BuildTool: content = r.Tool ?? ErrorListModel.SourceName; return true;
                    case StandardTableKeyNames.ProjectName: content = string.Empty; return true;
                    default: return false;
                }
            }
        }
    }
}
