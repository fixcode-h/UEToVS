using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Editing;
using UEToVS.Host.Logic.Localization;
using UEToVS.UI.Controls;
using UEToVS.UI.Patterns;

namespace UEToVS.Host.Editor
{
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(EngineReadOnlyMargin.MarginName)]
    [MarginContainer(PredefinedMarginNames.Top)]
    [Order(Before = NavigationBarMargin.MarginName)]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class EngineReadOnlyMarginProvider : IWpfTextViewMarginProvider
    {
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost wpfTextViewHost, IWpfTextViewMargin marginContainer) =>
            new EngineReadOnlyMargin(wpfTextViewHost.TextView);
    }

    /// <summary>
    /// v0.9.4 编辑器顶部的“引擎代码只读”提示条（UEToVS.UI <see cref="EditorNoticeBar"/>），在导航栏上面。文件锁着时显示，
    /// 同时给这个视图设 ViewProhibitUserInput；解锁后收起并恢复输入。不是引擎文件时一直收起，开销只是打开和获得焦点时的一次路径比较。
    /// </summary>
    internal sealed class EngineReadOnlyMargin : IWpfTextViewMargin
    {
        public const string MarginName = "UEToVS.EngineReadOnly";

        private static readonly Logger L = Log.For("EngineReadOnly");

        private readonly IWpfTextView _view;
        private readonly EngineReadOnlyState _state;
        private readonly EditorNoticeBar _bar = new EditorNoticeBar { Visibility = Visibility.Collapsed };
        private bool _prohibitSetByUs;
        private bool _disposed;

        public EngineReadOnlyMargin(IWpfTextView view)
        {
            _view = view;
            _state = EngineReadOnlyState.For(view.TextBuffer);
            _bar.Set(() => EditorFeatureStrings.EngineReadOnlyMessage, () => EditorFeatureStrings.EngineReadOnlyDetail);
            _bar.AddAction(() => EditorFeatureStrings.EngineReadOnlyEnable, () => EngineReadOnlyFeature.Unlock(_state, "bar"), ButtonKind.Primary,
                           () => EditorFeatureStrings.EngineReadOnlyEnableTip, "Ctrl+Shift+Alt+U, E");
            _bar.AddAction(() => EditorFeatureStrings.EngineReadOnlySettings, () => Safe.RunAsync(L, "engineReadOnly.openSettings", EngineReadOnlyFeature.OpenSettingsAsync),
                           ButtonKind.Ghost, () => EditorFeatureStrings.EngineReadOnlySettingsTip);
            _state.Changed += OnStateChanged;
            _state.FlashRequested += OnFlash;
            _view.GotAggregateFocus += OnFocus;
            EngineReadOnlyFeature.Reevaluate += OnReevaluate;
            Safe.Execute(L, "engineReadOnly.open", () =>
            {
                _state.Evaluate();
                Apply();
            });
        }

        public FrameworkElement VisualElement => _bar;

        public double MarginSize => _bar.ActualHeight;

        public bool Enabled => !_disposed;

        public ITextViewMargin GetTextViewMargin(string marginName) =>
            string.Equals(marginName, MarginName, StringComparison.OrdinalIgnoreCase) ? this : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _state.Changed -= OnStateChanged;
            _state.FlashRequested -= OnFlash;
            _view.GotAggregateFocus -= OnFocus;
            EngineReadOnlyFeature.Reevaluate -= OnReevaluate;
        }

        // 游戏项目里引擎根要等代码引擎起来才知道：获得焦点时再判断一次（已经判断过的结果不变时什么也不做）。
        private void OnFocus(object sender, EventArgs e) => Safe.Execute(L, "engineReadOnly.focus", _state.Evaluate);

        private void OnReevaluate(object sender, EventArgs e) => Safe.Execute(L, "engineReadOnly.reevaluate.view", () =>
        {
            if (!_disposed && !_view.IsClosed) _state.Evaluate();
        });

        private void OnStateChanged(object sender, EventArgs e) => Safe.Execute(L, "engineReadOnly.apply", Apply);

        private void OnFlash(object sender, EventArgs e) => Safe.Execute(L, "engineReadOnly.flash", () =>
        {
            if (_view.HasAggregateFocus) _bar.Flash();
        });

        private void Apply()
        {
            if (_disposed || _view.IsClosed) return;
            bool locked = _state.Locked;
            var want = locked ? Visibility.Visible : Visibility.Collapsed;
            if (_bar.Visibility != want) _bar.Visibility = want;

            // 只撤销自己加的禁止输入，别的功能（例如 VS 的只读预览）设的不动
            var options = _view.Options;
            bool prohibited = options.GetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId);
            if (locked && !prohibited)
            {
                options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, true);
                _prohibitSetByUs = true;
            }
            else if (!locked && prohibited && _prohibitSetByUs)
            {
                options.SetOptionValue(DefaultTextViewOptions.ViewProhibitUserInputId, false);
                _prohibitSetByUs = false;
            }
        }
    }

    /// <summary>
    /// 锁着的引擎文件里挡住编辑命令：放在命令链最前面，打字、回车、删除、粘贴、剪切、撤销、注释、格式化等直接吞掉，提示条闪一下，
    /// 状态栏说明怎么解锁。ViewProhibitUserInput 已经挡住编辑器自己的编辑，这里再挡语言服务（C++ 自动补括号、格式化等）直接改缓冲区的路径，
    /// 也给用户一个“为什么打不了字”的反馈。没锁时只多一次布尔判断。
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class EngineReadOnlyFilterProvider : IVsTextViewCreationListener
    {
        private static readonly Logger L = Log.For("EngineReadOnly");

        [Import]
        internal IVsEditorAdaptersFactoryService Adapters { get; set; }

        public void VsTextViewCreated(IVsTextView textViewAdapter) => Safe.Execute(L, "engineReadOnly.filter.attach", () =>
        {
            var view = Adapters?.GetWpfTextView(textViewAdapter);
            if (view == null) return;
            var filter = new EngineReadOnlyCommandFilter(EngineReadOnlyState.For(view.TextBuffer));
            if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out IOleCommandTarget next))) filter.Next = next;
            else L.Warn("engineReadOnly.filter.attachFailed", "AddCommandFilter failed");
        });
    }

    internal sealed class EngineReadOnlyCommandFilter : IOleCommandTarget
    {
        private static readonly Guid Std97 = VSConstants.GUID_VSStandardCommandSet97;
        private static readonly Guid Std2K = VSConstants.VSStd2K;

        private static readonly HashSet<uint> Std2KEdits = new HashSet<uint>
        {
            (uint)VSConstants.VSStd2KCmdID.TYPECHAR,
            (uint)VSConstants.VSStd2KCmdID.BACKSPACE,
            (uint)VSConstants.VSStd2KCmdID.RETURN,
            (uint)VSConstants.VSStd2KCmdID.TAB,
            (uint)VSConstants.VSStd2KCmdID.BACKTAB,
            (uint)VSConstants.VSStd2KCmdID.DELETE,
            (uint)VSConstants.VSStd2KCmdID.DELETEWORDLEFT,
            (uint)VSConstants.VSStd2KCmdID.DELETEWORDRIGHT,
            (uint)VSConstants.VSStd2KCmdID.DELETETOBOL,
            (uint)VSConstants.VSStd2KCmdID.DELETETOEOL,
            (uint)VSConstants.VSStd2KCmdID.DELETEBLANKLINES,
            (uint)VSConstants.VSStd2KCmdID.DELETEWHITESPACE,
            (uint)VSConstants.VSStd2KCmdID.DELETELINE,
            (uint)VSConstants.VSStd2KCmdID.CUTLINE,
            (uint)VSConstants.VSStd2KCmdID.OPENLINEABOVE,
            (uint)VSConstants.VSStd2KCmdID.OPENLINEBELOW,
            (uint)VSConstants.VSStd2KCmdID.INDENT,
            (uint)VSConstants.VSStd2KCmdID.UNINDENT,
            (uint)VSConstants.VSStd2KCmdID.TRANSPOSECHAR,
            (uint)VSConstants.VSStd2KCmdID.TRANSPOSEWORD,
            (uint)VSConstants.VSStd2KCmdID.TRANSPOSELINE,
            (uint)VSConstants.VSStd2KCmdID.COMMENT_BLOCK,
            (uint)VSConstants.VSStd2KCmdID.UNCOMMENT_BLOCK,
            (uint)VSConstants.VSStd2KCmdID.COMMENTBLOCK,
            (uint)VSConstants.VSStd2KCmdID.UNCOMMENTBLOCK,
            (uint)VSConstants.VSStd2KCmdID.FORMATDOCUMENT,
            (uint)VSConstants.VSStd2KCmdID.FORMATSELECTION,
            (uint)VSConstants.VSStd2KCmdID.INSERTSNIPPET,
            (uint)VSConstants.VSStd2KCmdID.SURROUNDWITH,
        };

        private static readonly HashSet<uint> Std97Edits = new HashSet<uint>
        {
            (uint)VSConstants.VSStd97CmdID.Paste,
            (uint)VSConstants.VSStd97CmdID.Cut,
            (uint)VSConstants.VSStd97CmdID.Delete,
            (uint)VSConstants.VSStd97CmdID.Undo,
            (uint)VSConstants.VSStd97CmdID.Redo,
            (uint)VSConstants.VSStd97CmdID.MultiLevelUndo,
            (uint)VSConstants.VSStd97CmdID.MultiLevelRedo,
        };

        private readonly EngineReadOnlyState _state;

        public EngineReadOnlyCommandFilter(EngineReadOnlyState state) => _state = state;

        public IOleCommandTarget Next { get; set; }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
            Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (_state.Locked && IsEdit(ref pguidCmdGroup, nCmdID))
            {
                string command = (pguidCmdGroup == Std2K ? "std2k." : "std97.") + nCmdID;
                Safe.Execute(Log.For("EngineReadOnly"), "engineReadOnly.blocked", () =>
                {
                    _state.Flash();
                    EngineReadOnlyFeature.NoteBlockedEdit(_state, command);
                });
                return VSConstants.S_OK;
            }
            return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut) ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        private static bool IsEdit(ref Guid group, uint id) =>
            (group == Std2K && Std2KEdits.Contains(id)) || (group == Std97 && Std97Edits.Contains(id));
    }
}
