using System;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using UEToVS.Core.Diagnostics;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.ProjectFiles;
using UEToVS.Host.Navigation;
using UEToVS.Host.Shaders;

namespace UEToVS.Host.Editor
{
    /// <summary>
    /// v0.10：着色器（.usf / .ush）和 UE 配置文件（.ini / .uproject / .uplugin）里的 Ctrl+左键 = 转到定义。C++ 的 Ctrl+点击（带悬停下划线）
    /// 是 <see cref="CtrlClickController"/>；这里只做点击：按下时记住位置，同一处松开就把光标放过去，执行 Edit.GoToDefinition，由各自的
    /// F12 命令过滤器（ShaderIncludeFilter / ProjectFileNavigationFilter）处理。设置“Ctrl+点击跳转”关闭时不接管。
    /// </summary>
    [Export(typeof(IMouseProcessorProvider))]
    [Name("UEToVS.FileCtrlClick")]
    [Order(Before = "WordSelection")]
    [Order(Before = "GoToDefinition")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    internal sealed class FileCtrlClickProvider : IMouseProcessorProvider
    {
        public IMouseProcessor GetAssociatedProcessor(IWpfTextView wpfTextView)
        {
            string path = VsEditor.FilePathOf(wpfTextView.TextBuffer);
            bool shader = ShaderWorkspace.IsShaderFile(path);
            if (!shader && !ProjectFileLogic.IsIni(path) && !ProjectFileLogic.IsDescriptor(path)) return null;
            return new FileCtrlClick(wpfTextView, shader);
        }
    }

    internal sealed class FileCtrlClick : MouseProcessorBase
    {
        private static readonly Logger L = Log.For("CtrlClick");
        private readonly IWpfTextView _view;
        private readonly bool _shader;
        private SnapshotPoint? _down;
        private bool _vsSuppressed;

        public FileCtrlClick(IWpfTextView view, bool shader)
        {
            _view = view;
            _shader = shader;
        }

        private bool Enabled => NavigationContext.IsUnrealSolution && NavigationContext.Settings.CtrlClickNavigation && (!_shader || ShaderWorkspace.Enabled);

        private static bool CtrlOnly => (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == ModifierKeys.Control;

        private SnapshotPoint? PointAt(MouseEventArgs e)
        {
            if (_view.IsClosed || _view.InLayout || _view.TextViewLines == null) return null;
            Point pos = e.GetPosition(_view.VisualElement);
            var line = _view.TextViewLines.GetTextViewLineContainingYCoordinate(pos.Y + _view.ViewportTop);
            return line?.GetBufferPositionFromXCoordinate(pos.X + _view.ViewportLeft);
        }

        /// <summary>VS 自带的 Ctrl+点击（code 类内容）会和这里各跳一次：本视图里关掉它（只改这个视图的选项）。</summary>
        private void SuppressVsClick()
        {
            if (_vsSuppressed) return;
            _vsSuppressed = true;
            try
            {
                _view.Options.SetOptionValue(DefaultWpfViewOptions.ClickGoToDefEnabledId, false);
            }
            catch (Exception ex)
            {
                L.Debug("fileCtrlClick.vsOption.failed", ex.Message);
            }
        }

        public override void PreprocessMouseLeftButtonDown(MouseButtonEventArgs e) => Safe.Execute(L, "fileCtrlClick.down", () =>
        {
            _down = null;
            if (!CtrlOnly || e.ClickCount != 1 || !Enabled) return;
            SuppressVsClick();
            var p = PointAt(e);
            if (p == null) return;
            _down = p;
            e.Handled = true;
        });

        public override void PreprocessMouseLeftButtonUp(MouseButtonEventArgs e) => Safe.Execute(L, "fileCtrlClick.up", () =>
        {
            var down = _down;
            _down = null;
            if (down == null) return;
            var p = PointAt(e);
            if (p == null || p.Value.Snapshot != down.Value.Snapshot || Math.Abs(p.Value.Position - down.Value.Position) > 1) return;
            e.Handled = true;
            _view.Selection.Clear();
            _view.Caret.MoveTo(p.Value);
            L.Info("fileCtrlClick.navigate", null, ("shader", _shader), ("line", p.Value.GetContainingLine().LineNumber));
            ThreadHelper.ThrowIfNotOnUIThread();
            VsEditor.TryExecute("Edit.GoToDefinition");
        });
    }
}
