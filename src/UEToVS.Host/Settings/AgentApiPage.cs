using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualStudio.Shell;
using UEToVS.Core.Diagnostics;
using UEToVS.Core.Localization;
using UEToVS.Host.Agent;
using UEToVS.Host.Diagnostics;
using UEToVS.Host.Logic.Agent;
using UEToVS.UI.Atoms;
using UEToVS.UI.Controls;
using UEToVS.UI.Logic;
using UEToVS.UI.Molecules;
using UEToVS.UI.Theming;

namespace UEToVS.Host.Settings
{
    /// <summary>
    /// Settings window "Agent / API" page (layout after AgentPanel's API page): per API a card with name, URL, key, key
    /// sending mode, concurrency limit, "测试连接" / "从接口获取", and the model list (enable for VS chat, tools, vision,
    /// context size, manual add). On top: the local gateway (port, state) and how the models reach VS chat.
    /// Edits go to a working copy; Apply/OK hands it to <see cref="AgentService.ApplyAsync"/> (saves, keys via DPAPI,
    /// restarts the gateway, rewrites VS's model file). Typed keys are kept only until then and never shown again.
    /// </summary>
    internal sealed class AgentApiPage : ISettingsCustomPage
    {
        private static readonly Logger L = Log.For("AgentApiPage");
        private readonly AgentService _service;
        private readonly StackPanel _apis = new StackPanel();
        private readonly UiText _gatewayStatus = new UiText((string)null, TextVariant.Description) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) };
        private readonly NumberStepper _port;
        private readonly Banner _howTo = new Banner(Severity.Info) { CanClose = false, Margin = new Thickness(0, 0, 0, SettingsPageLayout.GroupGap) };
        private readonly Dictionary<string, string> _typedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Fetched model ids per API (not saved; shown as chips to add).</summary>
        private readonly Dictionary<string, IReadOnlyList<string>> _fetched = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        /// <summary>Last test/fetch result per API, kept across re-renders.</summary>
        private readonly Dictionary<string, (Severity Severity, string Text)> _results = new Dictionary<string, (Severity, string)>(StringComparer.Ordinal);
        private AgentApiConfig _working;

        public AgentApiPage(AgentService service)
        {
            _service = service;
            _working = service.Snapshot();

            var content = new StackPanel { Margin = new Thickness(0, 0, 12, 16) };
            var gateway = new SettingsGroup(T("本地转发服务", "Local gateway")) { Margin = new Thickness(0, 0, 0, SettingsPageLayout.GroupGap) };
            _port = new NumberStepper(1024, 65535, 1, _working.GatewayPort) { Width = 120, AccessibleName = T("端口", "Port") };
            _port.ValueChanged += (s, e) => Safe.Execute(L, "agent.port", () => { _working.GatewayPort = (int)_port.Value; UpdateHowTo(); });
            var portRow = new SettingsRow(T("端口", "Port"), null, _port);
            portRow.Labels.Children.Add(_gatewayStatus);
            gateway.Add(portRow);
            content.Children.Add(gateway);
            content.Children.Add(_howTo);
            content.Children.Add(_apis);
            var add = new UiButton(T("添加 API", "Add API"), ButtonKind.Primary) { HorizontalAlignment = HorizontalAlignment.Left };
            add.Content = IconLabel(SettingsIcons.Plus, T("添加 API", "Add API"));
            add.Click += (s, e) => Safe.Execute(L, "agent.api.add", () =>
            {
                _working.Apis.Add(new AgentApi { Name = string.Empty, BaseUrl = string.Empty });
                Render();
            });
            content.Children.Add(add);
            var root = new UiScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Element = root;

            root.Unloaded += (s, e) => _service.StateChanged -= OnServiceStateChanged;
            root.Loaded += (s, e) => _service.StateChanged += OnServiceStateChanged;
            UpdateHowTo();
            UpdateGatewayStatus();
            Render();
        }

        public UIElement Element { get; }

        private static string T(string zh, string en) => Loc.T(zh, en);

        // ------------------------------------------------------------------ Apply / Cancel

        public void Apply()
        {
            var next = _working.Clone();
            var keys = new Dictionary<string, string>(_typedKeys, StringComparer.Ordinal);
            _typedKeys.Clear();
            Safe.RunAsync(L, "agent.apply", async () =>
            {
                await _service.ApplyAsync(next, keys);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _working = _service.Snapshot();
                Render();
                UpdateGatewayStatus();
            });
        }

        public void Cancel()
        {
            _typedKeys.Clear();
            _working = _service.Snapshot();
            _port.SetSilently(_working.GatewayPort);
            Render();
        }

        // Nothing sensible to reset to (the APIs are the user's data); the footer button leaves this page as it is.
        public void ResetToDefaults() => L.Info("agent.reset.ignored", null);

        private void OnServiceStateChanged(object sender, EventArgs e) =>
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                Safe.Execute(L, "agent.state", UpdateGatewayStatus);
            });

        // ------------------------------------------------------------------ status

        private void UpdateGatewayStatus()
        {
            string url = AgentApiConfig.GatewayUrlFor(_service.ActivePort);
            string text;
            if (_service.GatewayRunning)
                text = Loc.F("运行中：{0}，已注册 {1} 个模型到 VS，已处理 {2} 个请求", "Running at {0}; {1} models registered in VS; {2} requests served",
                             url, _service.RegisteredCount, _service.GatewayRequests);
            else if (!string.IsNullOrEmpty(_service.GatewayError))
                text = Loc.F("启动失败（{0}）：{1}。换一个端口后应用。", "Failed to start ({0}): {1}. Pick another port and apply.", url, _service.GatewayError);
            else
                text = T("未运行：还没有启用的模型。", "Not running: no model is enabled yet.");
            if (_service.PortFellBack)
                text += "\n" + T("端口 11434 已被其他程序占用（可能是本机的 Ollama），转发服务改用 47811。VS 里如需手动添加 Ollama 提供程序，地址填 http://127.0.0.1:47811。",
                                  "Port 11434 is used by another program (probably a local Ollama), so the gateway uses 47811. If you add the Ollama provider in VS by hand, use http://127.0.0.1:47811.");
            if (_service.LastVsOutcome == VsByokOutcome.Failed)
                text += "\n" + Loc.F("写入 VS 模型配置失败：{0}", "Writing VS's model configuration failed: {0}", _service.LastVsError);
            _gatewayStatus.Text = text;
        }

        private void UpdateHowTo()
        {
            string url = AgentApiConfig.GatewayUrlFor(_working.GatewayPort);
            _howTo.Message = Loc.F(
                "应用后，勾选的模型会以 Ollama 类型写入 VS 聊天的自定义模型（地址 {0}，密钥只在 UEToVS 里，不会交给 VS）。VS 在打开聊天时读取：应用后重新打开 Copilot 聊天窗口或重启 VS。如果模型选择器里还没有，在聊天的模型选择器 > 管理模型 > 添加模型提供程序 选 Ollama、地址填 {0} 添加一次。",
                "After Apply, enabled models are added to VS chat's custom models as an Ollama provider at {0} (keys stay in UEToVS). VS reads them when chat opens: reopen Copilot Chat or restart VS. If they still don't appear, add an Ollama provider with URL {0} once in the chat model picker > Manage models.",
                url);
        }

        // ------------------------------------------------------------------ API cards

        private void Render()
        {
            _apis.Children.Clear();
            if (_working.Apis.Count == 0)
            {
                _apis.Children.Add(new UiText(T("还没有 API。点“添加 API”开始。", "No API yet. Click \"Add API\" to start."), TextVariant.Secondary) { Margin = new Thickness(0, 0, 0, 8) });
                return;
            }
            foreach (var api in _working.Apis) _apis.Children.Add(BuildCard(api));
        }

        private UIElement BuildCard(AgentApi api)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, SettingsPageLayout.GroupGap) };
            string title = string.IsNullOrWhiteSpace(api.Name) ? T("新 API", "New API") : api.Name;
            var group = new SettingsGroup(title);
            block.Children.Add(group);

            var name = new TextInput { Text = api.Name ?? string.Empty, Width = 320, Placeholder = T("例如 DeepSeek、公司中转", "e.g. DeepSeek, company relay") };
            name.TextChanged += (s, e) => { api.Name = name.Text; group.Label = string.IsNullOrWhiteSpace(api.Name) ? T("新 API", "New API") : api.Name; };
            group.Add(new SettingsRow(T("名称", "Name"), T("显示在 VS 模型名后面；两个 API 有同名模型时用它区分", "Shown after the model name in VS; tells apart same-named models"), name));

            var url = new TextInput { Text = api.BaseUrl ?? string.Empty, Width = 320, Placeholder = "https://api.example.com/v1" };
            url.SetResourceReference(Control.FontFamilyProperty, Tokens.FontFamilyCode);
            var urlRow = new SettingsRow(T("地址", "Base URL"), null, url);
            var urlHint = Hint(UrlHint(api.BaseUrl));
            urlRow.Labels.Children.Add(urlHint);
            url.TextChanged += (s, e) => { api.BaseUrl = url.Text.Trim(); urlHint.Text = UrlHint(api.BaseUrl); };
            group.Add(urlRow);

            var key = new PasswordBox { Style = Styles.PasswordInput, Width = 320 };
            if (_typedKeys.TryGetValue(api.Id, out var typed)) key.Password = typed;
            bool stored = _service.HasKey(api.Id);
            var keyRow = new SettingsRow(T("密钥", "API key"), null, key);
            var keyHint = Hint(KeyHint(stored, typed));
            keyRow.Labels.Children.Add(keyHint);
            key.PasswordChanged += (s, e) => { _typedKeys[api.Id] = key.Password; keyHint.Text = KeyHint(stored, key.Password); };
            group.Add(keyRow);

            var mode = new SegmentedControl(new[] { "Bearer", "x-api-key" });
            mode.SelectedIndex = api.KeyMode == AgentKeyMode.XApiKey ? 1 : 0;
            mode.SelectionChanged += (s, e) => api.KeyMode = mode.SelectedIndex == 1 ? AgentKeyMode.XApiKey : AgentKeyMode.Bearer;
            group.Add(new SettingsRow(T("密钥的发送方式", "Send key as"), T("Authorization: Bearer 或 x-api-key 请求头", "Authorization: Bearer or the x-api-key header"), mode));

            var conc = new NumberStepper(0, 256, 1, api.MaxConcurrency) { Width = 116, AccessibleName = T("并发上限", "Concurrency limit") };
            conc.ValueChanged += (s, e) => api.MaxConcurrency = (int)conc.Value;
            group.Add(new SettingsRow(T("并发上限", "Concurrency limit"), T("同时发给这个 API 的请求数，超过的排队；0 = 不限制", "Requests in flight to this API at once; more wait. 0 = unlimited"), conc));

            // test / fetch / delete
            var test = new UiButton(T("测试连接", "Test connection"));
            var fetch = new UiButton(T("从接口获取模型", "Fetch models")) { Margin = new Thickness(8, 0, 0, 0) };
            var delete = new UiButton(T("删除这个 API", "Delete this API"), ButtonKind.Ghost) { Margin = new Thickness(8, 0, 0, 0) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(test);
            actions.Children.Add(fetch);
            actions.Children.Add(delete);
            var actionRow = new SettingsRow(T("连接", "Connection"), null, actions);
            if (_results.TryGetValue(api.Id, out var res))
            {
                var result = Hint(res.Text);
                result.SetResourceReference(TextBlock.ForegroundProperty, Tokens.Severity(res.Severity));
                actionRow.Labels.Children.Add(result);
            }
            group.Add(actionRow);
            test.Click += (s, e) => Safe.RunAsync(L, "agent.test", () => ProbeAsync(api, false, test));
            fetch.Click += (s, e) => Safe.RunAsync(L, "agent.fetch", () => ProbeAsync(api, true, fetch));
            delete.Click += (s, e) => Safe.Execute(L, "agent.api.delete", () =>
            {
                _working.Apis.Remove(api);
                _typedKeys.Remove(api.Id);
                Render();
            });

            // models
            int enabledCount = api.Models.Count(m => m.Enabled);
            var models = new SettingsGroup(Loc.F("模型 · 已启用 {0} / {1}", "Models · {0} of {1} enabled", enabledCount, api.Models.Count)) { Margin = new Thickness(0, 14, 0, 0) };
            if (api.Models.Count == 0)
                models.Add(new UiText(T("还没有模型：点“从接口获取模型”，或在下面输入模型名后回车。", "No models yet: fetch them from the API, or type a model name below and press Enter."), TextVariant.Description)
                { Margin = new Thickness(14, 14, 14, 14), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None });
            foreach (var m in api.Models) models.Add(ModelRow(api, m));
            block.Children.Add(models);

            // add box: filters the returned models live; Enter adds the only match, or the text as typed.
            var manual = new SearchInput(false) { Margin = new Thickness(0, 10, 0, 0) };
            manual.InputBox.Placeholder = T("输入模型名筛选，回车添加", "Type to filter, Enter to add");
            manual.InputBox.SetResourceReference(Control.FontFamilyProperty, Tokens.FontFamilyCode);
            manual.RightContent = new UiText(T("↵ 添加", "↵ Add"), TextVariant.Description) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            block.Children.Add(manual);

            IReadOnlyList<string> remaining = Array.Empty<string>();
            PoolView poolView = null;
            if (_fetched.TryGetValue(api.Id, out var pool))
            {
                remaining = pool.Where(id => api.Models.All(m => !string.Equals(m.Id, id, StringComparison.Ordinal))).ToList();
                if (remaining.Count > 0)
                {
                    poolView = new PoolView(this, api, remaining);
                    block.Children.Add(poolView.Root);
                }
            }
            manual.TextChanged += (s, q) => Safe.Execute(L, "agent.model.filter", () => poolView?.Filter(manual.Text));
            manual.InputBox.KeyDown += (s, e) => Safe.Execute(L, "agent.model.manual", () =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                string id = ModelFilter.ResolveEnter(remaining, manual.Text);
                if (id != null) AddModels(api, new[] { id });
            });
            return block;
        }

        private static UiText Hint(string text) =>
            new UiText(text, TextVariant.Description) { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = new Thickness(0, 2, 0, 0) };

        private static UIElement IconLabel(System.Windows.Media.Geometry icon, string text) => PathListEditor.IconText(icon, text);

        private static string UrlHint(string baseUrl)
        {
            string root = AgentApiConfig.ResolveApiRoot(baseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl)) return T("填 API 的根地址；没写版本时自动补 /v1。", "The API root; /v1 is added when no version is given.");
            return root == null ? T("不是有效的 http(s) 地址。", "Not a valid http(s) URL.") : Loc.F("实际请求：{0}/chat/completions", "Requests go to {0}/chat/completions", root);
        }

        private static string KeyHint(bool stored, string typed)
        {
            if (!string.IsNullOrEmpty(typed)) return T("应用后保存（Windows 加密，仅当前用户可读）。", "Saved on Apply (encrypted for the current Windows user).");
            return stored ? T("已保存；留空表示不修改。", "Saved; leave empty to keep it.") : T("还没有密钥。", "No key yet.");
        }

        /// <summary>
        /// One model (warm-scheme mockup): enable box, id in the code font, "工具" / "图片" toggle tags, a framed
        /// "上下文 [1M]" field and ✕ to remove; 48px tall.
        /// </summary>
        private UIElement ModelRow(AgentApi api, AgentModel m)
        {
            var row = new DockPanel { LastChildFill = true, Height = 48, Margin = new Thickness(14, 0, 10, 0) };
            var remove = new IconButton(UEToVS.UI.Theming.Ui.CrossMark, () => T("移除", "Remove"), null, 9) { Width = 28, Height = 28, Margin = new Thickness(6, 0, 0, 0) };
            remove.Click += (s, e) => Safe.Execute(L, "agent.model.remove", () => { api.Models.Remove(m); Render(); });
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var tools = TagToggle(T("工具", "Tools"), m.ToolCalling, T("支持工具调用（函数调用）", "Supports tool (function) calling"));
            tools.Click += (s, e) => m.ToolCalling = tools.IsChecked == true;
            var vision = TagToggle(T("图片", "Vision"), m.Vision, T("可以读取图片", "Accepts images"));
            vision.Click += (s, e) => m.Vision = vision.IsChecked == true;

            // Context length: "128K" below 1M tokens, "1M" / "1.5M" from there; accepts 1M, 1000K, 128 (= K).
            var ctx = new TextInput { Style = Styles.BareTextInput, Width = 46, Text = ContextSize.Format(m.ContextTokens), VerticalAlignment = VerticalAlignment.Center };
            ctx.SetResourceReference(Control.FontFamilyProperty, Tokens.FontFamilyCode);
            System.Windows.Automation.AutomationProperties.SetName(ctx, T("上下文长度", "Context length"));
            Tip.Set(ctx, T("上下文长度：可写 128K、1000K、1M、1.5M；只写数字按 K 算", "Context length: 128K, 1000K, 1M, 1.5M; a bare number means K"));
            void CommitCtx()
            {
                if (ContextSize.TryParse(ctx.Text, out int tokens)) m.ContextTokens = tokens;
                ctx.Text = ContextSize.Format(m.ContextTokens);
            }
            ctx.LostKeyboardFocus += (s, e) => Safe.Execute(L, "agent.model.context", CommitCtx);
            ctx.KeyDown += (s, e) => Safe.Execute(L, "agent.model.context", () => { if (e.Key == Key.Enter) { e.Handled = true; CommitCtx(); } });
            var ctxLine = new StackPanel { Orientation = Orientation.Horizontal };
            ctxLine.Children.Add(new UiText(T("上下文", "Context"), TextVariant.Description) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            ctxLine.Children.Add(ctx);
            var ctxBox = new Border { Child = ctxLine, Height = 28, Padding = new Thickness(8, 0, 4, 0), BorderThickness = new Thickness(1), Margin = new Thickness(10, 0, 0, 0) };
            ctxBox.SetResourceReference(Border.BackgroundProperty, Tokens.InputBackground);
            ctxBox.SetResourceReference(Border.BorderBrushProperty, Tokens.InputBorder);
            ctxBox.SetResourceReference(Border.CornerRadiusProperty, Tokens.CornerRadiusControl);
            ctxBox.MouseLeftButtonDown += (s, e) => { ctx.Focus(); };
            ctxBox.IsKeyboardFocusWithinChanged += (s, e) => ctxBox.SetResourceReference(Border.BorderBrushProperty, ctxBox.IsKeyboardFocusWithin ? Tokens.BorderFocus : Tokens.InputBorder);
            right.Children.Add(tools);
            right.Children.Add(vision);
            right.Children.Add(ctxBox);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);

            var enabled = new UiCheckBox((string)null, m.Enabled) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            System.Windows.Automation.AutomationProperties.SetName(enabled, m.Id);
            Tip.Set(enabled, T("勾选后出现在 VS 聊天的模型列表里", "Show this model in VS chat"));
            enabled.Click += (s, e) => m.Enabled = enabled.IsChecked == true;
            DockPanel.SetDock(enabled, Dock.Left);
            row.Children.Add(enabled);
            var id = new UiText(m.Id, TextVariant.Body) { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = m.Id };
            id.SetResourceReference(TextBlock.FontFamilyProperty, Tokens.FontFamilyCode);
            row.Children.Add(id);
            return row;
        }

        private static Chip TagToggle(string text, bool on, string tip)
        {
            var chip = new Chip(text, on) { Height = 22, MinHeight = 0, Padding = new Thickness(7, 0, 7, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Tip.Set(chip, tip);
            return chip;
        }

        /// <summary>Models the API returned that are not added yet: click a chip to add it; filtered by the add box.</summary>
        private sealed class PoolView
        {
            private readonly AgentApiPage _page;
            private readonly AgentApi _api;
            private readonly List<(string Id, Chip Chip)> _chips = new List<(string, Chip)>();
            private readonly UiText _head = new UiText((string)null, TextVariant.Description) { VerticalAlignment = VerticalAlignment.Center };
            private readonly UiText _none = new UiText((string)null, TextVariant.Secondary) { Visibility = Visibility.Collapsed };
            private readonly UiButton _addAll;
            private IReadOnlyList<string> _visible;

            public PoolView(AgentApiPage page, AgentApi api, IReadOnlyList<string> remaining)
            {
                _page = page;
                _api = api;
                _visible = remaining;
                var box = new Border { Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(1) };
                box.Bind(Border.BackgroundProperty, Tokens.SurfaceRaised);
                box.Bind(Border.BorderBrushProperty, Tokens.BorderSubtle);
                box.Bind(Border.CornerRadiusProperty, Tokens.CornerRadiusPopup);
                var panel = new StackPanel();
                var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
                _addAll = new UiButton(T("添加全部", "Add all"), ButtonKind.Ghost);
                _addAll.SetResourceReference(Control.ForegroundProperty, Tokens.TextLink);
                _addAll.Click += (s, e) => Safe.Execute(L, "agent.model.addAll", () => _page.AddModels(_api, _visible));
                DockPanel.SetDock(_addAll, Dock.Right);
                head.Children.Add(_addAll);
                head.Children.Add(_head);
                panel.Children.Add(head);
                var wrap = new WrapPanel();
                foreach (var id in remaining)
                {
                    var modelId = id;
                    var chip = new Chip(modelId) { Margin = new Thickness(0, 0, 6, 6), Height = 28, Padding = new Thickness(10, 0, 10, 0) };
                    chip.SetResourceReference(Control.FontFamilyProperty, Tokens.FontFamilyCode);
                    chip.Click += (s, e) => Safe.Execute(L, "agent.model.add", () => _page.AddModels(_api, new[] { modelId }));
                    wrap.Children.Add(chip);
                    _chips.Add((modelId, chip));
                }
                panel.Children.Add(wrap);
                panel.Children.Add(_none);
                box.Child = panel;
                Root = box;
                Filter(null);
            }

            public UIElement Root { get; }

            public void Filter(string query)
            {
                var visible = new List<string>();
                foreach (var (id, chip) in _chips)
                {
                    bool show = ModelFilter.Matches(id, query);
                    chip.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    if (show) visible.Add(id);
                }
                _visible = visible;
                bool filtered = !string.IsNullOrWhiteSpace(query);
                _head.Text = filtered
                    ? Loc.F("接口返回的模型 · 匹配 {0} / {1}，点一下添加", "Models from the API · {0} of {1} match; click to add", visible.Count, _chips.Count)
                    : Loc.F("接口返回的模型 · {0} 个未添加，点一下添加", "Models from the API · {0} not added; click to add", _chips.Count);
                _addAll.Content = filtered ? Loc.F("添加匹配的 {0} 个", "Add {0} matching", visible.Count) : T("添加全部", "Add all");
                _addAll.IsEnabled = visible.Count > 0;
                _none.Text = T("没有匹配的模型；回车会按输入的名字添加。", "No match; Enter adds the name as typed.");
                _none.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void AddModels(AgentApi api, IEnumerable<string> ids)
        {
            foreach (var raw in ids)
            {
                string id = raw?.Trim();
                if (string.IsNullOrEmpty(id) || api.Models.Any(m => string.Equals(m.Id, id, StringComparison.Ordinal))) continue;
                api.Models.Add(new AgentModel { Id = id, Vision = LooksVisual(id) });
            }
            Render();
        }

        /// <summary>Default for the vision box from the name (vl / vision / 4o / gemini …); the user can change it.</summary>
        private static bool LooksVisual(string id)
        {
            string s = id.ToLowerInvariant();
            return s.Contains("vision") || s.Contains("-vl") || s.Contains("vl-") || s.Contains("gpt-4o") || s.Contains("gpt-4.1") || s.Contains("gpt-5")
                || s.Contains("gemini") || s.Contains("claude") || s.Contains("qwen-vl") || s.Contains("glm-4v");
        }

        private async Task ProbeAsync(AgentApi api, bool fetch, UiButton button)
        {
            button.IsEnabled = false;
            try
            {
                _typedKeys.TryGetValue(api.Id, out var typed);
                if (string.IsNullOrEmpty(typed) && !_service.HasKey(api.Id))
                {
                    _results[api.Id] = (Severity.Warning, T("先填写密钥。", "Enter the API key first."));
                    Render();
                    return;
                }
                AgentModelsResult r;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    r = await _service.ListModelsAsync(api.Clone(), typed, cts.Token);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                L.Info(fetch ? "agent.fetch.result" : "agent.test.result", null, ("api", api.DisplayName), ("ok", r.Ok), ("status", r.StatusCode), ("models", r.Models.Count), ("ms", r.ElapsedMs));
                if (!r.Ok)
                {
                    string error = r.Error == "invalid-url" ? T("地址不是有效的 http(s) 地址。", "The base URL is not a valid http(s) URL.")
                                 : r.Error == "timeout" ? T("30 秒内没有响应。", "No response within 30 seconds.") : r.Error;
                    _results[api.Id] = (Severity.Error, Loc.F("连接失败：{0}（{1}）", "Connection failed: {0} ({1})", error, r.Url));
                }
                else
                {
                    _results[api.Id] = (Severity.Success, Loc.F("连接可用，读到 {0} 个模型（{1} ms）", "Connection OK, {0} models ({1} ms)", r.Models.Count, r.ElapsedMs));
                    if (fetch || api.Models.Count == 0) _fetched[api.Id] = r.Models;
                }
                Render();
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }
}
