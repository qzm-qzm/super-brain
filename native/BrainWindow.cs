using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace SuperBrain
{
    public sealed partial class BrainWindow : Window
    {
        readonly LocalStore store;
        readonly Vault vault;
        Preferences config;
        readonly uint showMessage;
        readonly Grid content = new Grid();
        readonly Image wallpaper = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        readonly TextBlock status = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock shortcutHint = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly DispatcherTimer idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        Forms.NotifyIcon tray;
        HwndSource source;
        IntPtr handle;
        string activeShortcut, query = "", clipboardDigest;
        int hotkeyId = 51;
        bool secretTab, editing, rendering, notesDirty, configDirty, exiting, busy;
        Record current, deleted;
        bool deletedSecret;
        DateTime deletedAt, activityAt = DateTime.UtcNow;
        StackPanel rows;
        Button notesTab, passwordsTab, retry;
        Window activeDialog;

        public BrainWindow(string directory, uint showMessage)
        {
            this.showMessage = showMessage; store = new LocalStore(directory); vault = new Vault(directory); config = JsonFile.Clone(store.Config);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SuperBrain.Theme.xaml")) Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
            Title = "超强大脑"; Width = 480; Height = 720; MinWidth = 400; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"); FontSize = 13; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize;
            SetResourceReference(BackgroundProperty, "Paper"); SetResourceReference(ForegroundProperty, "Ink");
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(5), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12) });
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SuperBrain.icon.png")) Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BuildShell(); ApplyTheme(); Render();
            saveTimer.Tick += delegate { saveTimer.Stop(); Try(SavePending); };
            idleTimer.Tick += delegate { if (vault.Unlocked && DateTime.UtcNow - activityAt > TimeSpan.FromMinutes(config.autoLockMinutes)) LockVault(); };
            clipboardTimer.Tick += delegate { ClearClipboard(); };
            PreviewKeyDown += OnKeyDown; PreviewMouseDown += delegate { activityAt = DateTime.UtcNow; };
            SourceInitialized += delegate { handle = new WindowInteropHelper(this).Handle; source = HwndSource.FromHwnd(handle); source.AddHook(Message); Platform.RoundCorners(handle); try { SetShortcut(config.shortcut); } catch (Exception e) { Notice(e.Message); } };
            Loaded += delegate { SetupTray(); idleTimer.Start(); Try(delegate { store.SaveConfig(config); }); };
            Closing += OnClosing;
            StateChanged += delegate { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; HideToTray(); } };
            SystemEvents.SessionSwitch += SessionSwitch; SystemEvents.PowerModeChanged += PowerChanged;
        }
        void BuildShell()
        {
            var root = new Grid(); root.Children.Add(wallpaper);
            var layout = new Grid { Margin = new Thickness(22, 18, 22, 12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
            header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 1 && !HasButtonAncestor(e.OriginalSource as DependencyObject)) try { DragMove(); } catch (InvalidOperationException) { } };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; DockPanel.SetDock(actions, Dock.Right);
            actions.Children.Add(IconButton("\uE790", "外观设置", "appearance", ShowAppearance));
            actions.Children.Add(IconButton("\uE72E", "锁定密码库", "lock-vault", LockVault));
            actions.Children.Add(IconButton("\uE711", "收起到托盘", "hide-window", HideToTray)); header.Children.Add(actions);
            var brand = new StackPanel(); brand.Children.Add(Text("超强大脑", 23, false, true)); brand.Children.Add(Text("随手记，随时找。", 12, true)); header.Children.Add(brand); layout.Children.Add(header);
            Grid.SetRow(content, 1); layout.Children.Add(content);
            var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
            var settings = Button("设置", "settings", ShowSettings); settings.Padding = new Thickness(8, 5, 8, 5); DockPanel.SetDock(settings, Dock.Right); footer.Children.Add(settings);
            shortcutHint.Margin = new Thickness(8, 0, 8, 0); shortcutHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(shortcutHint, Dock.Right); footer.Children.Add(shortcutHint);
            retry = Button("重试保存", "retry-save", SavePending); retry.Visibility = Visibility.Collapsed; DockPanel.SetDock(retry, Dock.Right); footer.Children.Add(retry);
            status.TextTrimming = TextTrimming.CharacterEllipsis; status.TextWrapping = TextWrapping.NoWrap; status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); footer.Children.Add(status);
            var footBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = footer }; footBorder.SetResourceReference(Border.BorderBrushProperty, "Line"); Grid.SetRow(footBorder, 2); layout.Children.Add(footBorder);
            root.Children.Add(layout); Content = root;
        }
        static bool HasButtonAncestor(DependencyObject source) { while (source != null) { if (source is Button) return true; source = VisualTreeHelper.GetParent(source); } return false; }
        public static T Identify<T>(T control, string id, string name) where T : FrameworkElement { AutomationProperties.SetAutomationId(control, id); AutomationProperties.SetName(control, name); return control; }
        TextBlock Text(string value, double size = 13, bool muted = false, bool strong = false)
        {
            var text = new TextBlock { Text = value, FontSize = size, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 3, 0, 3) };
            text.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Muted" : "Ink"); return text;
        }
        Button Button(string caption, string id, Action action, bool primary = false)
        {
            var button = Identify(new Button { Content = caption }, id, caption); if (primary) button.SetResourceReference(StyleProperty, "Primary");
            button.Click += delegate { Try(action); }; return button;
        }
        Button IconButton(string glyph, string name, string id, Action action)
        {
            var button = Button(glyph, id, action); AutomationProperties.SetName(button, name); button.ToolTip = name; button.FontFamily = new FontFamily("Segoe MDL2 Assets"); button.FontSize = 17; button.Width = 36; button.Padding = new Thickness(5); return button;
        }
        void Try(Action action) { try { action(); } catch (Exception e) { Notice(e.Message, true); } }
        void Notice(string message, bool error = false) { status.Text = message; status.ToolTip = message; status.SetResourceReference(TextBlock.ForegroundProperty, error ? "Danger" : "Muted"); if (error) retry.Visibility = Visibility.Visible; }
        void Changed(bool isSecret)
        {
            if (rendering || current == null) return; activityAt = DateTime.UtcNow; current.updatedAt = activityAt.ToString("o");
            if (isSecret) Try(delegate { vault.Save(current); Notice("已加密保存到本地"); retry.Visibility = Visibility.Collapsed; });
            else { notesDirty = true; Notice("正在保存…"); saveTimer.Stop(); saveTimer.Start(); }
        }
        void SavePending()
        {
            saveTimer.Stop(); if (notesDirty) { store.SaveNotes(); notesDirty = false; } if (configDirty) { store.SaveConfig(config); configDirty = false; } vault.Flush(); retry.Visibility = Visibility.Collapsed;
            Notice(editing ? (secretTab ? "已加密保存到本地" : "已保存到本地") : "资料保存在同目录 data 文件夹");
        }
        void Render()
        {
            rendering = true; content.Children.Clear(); rows = null;
            if (editing && current != null) RenderEditor(); else RenderList();
            shortcutHint.Text = editing ? "Esc 返回" : config.shortcut + " 收起"; rendering = false;
        }
        void RenderList()
        {
            var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
            notesTab = Button("备忘录  " + store.Notes.Count, "notes-tab", delegate { SwitchTab(false); }); passwordsTab = Button("账号密码", "vault-tab", delegate { SwitchTab(true); });
            notesTab.FontWeight = !secretTab ? FontWeights.SemiBold : FontWeights.Normal; passwordsTab.FontWeight = secretTab ? FontWeights.SemiBold : FontWeights.Normal;
            (secretTab ? passwordsTab : notesTab).SetResourceReference(Control.BorderBrushProperty, "Accent"); tabs.Children.Add(notesTab); tabs.Children.Add(passwordsTab); layout.Children.Add(tabs);
            if (secretTab && !vault.Unlocked) { var locked = UnlockForm(); Grid.SetRow(locked, 1); Grid.SetRowSpan(locked, 2); layout.Children.Add(locked); Notice("密码库已锁定"); }
            else
            {
                var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) }; var add = Button("＋ 新建", "new-item", NewRecord, true); add.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(add, Dock.Right); toolbar.Children.Add(add);
                var search = Identify(new TextBox { Text = query, ToolTip = secretTab ? "搜索账号名称或用户名" : "搜索标题与内容" }, "search", "搜索记录");
                var searchHost = new Grid(); var placeholder = Text(secretTab ? "搜索账号或用户名…" : "搜索备忘录…", 13, true); placeholder.IsHitTestVisible = false; placeholder.VerticalAlignment = VerticalAlignment.Center; placeholder.Margin = new Thickness(12, 0, 8, 0); placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                search.TextChanged += delegate { query = search.Text; placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateRows(); }; searchHost.Children.Add(search); searchHost.Children.Add(placeholder); toolbar.Children.Add(searchHost); Grid.SetRow(toolbar, 1); layout.Children.Add(toolbar);
                rows = new StackPanel(); var scroll = new ScrollViewer { Content = rows }; Grid.SetRow(scroll, 2); layout.Children.Add(scroll); UpdateRows();
            }
            content.Children.Add(layout);
        }
        List<Record> Records() { return secretTab ? vault.List() : store.Notes; }
        void UpdateRows()
        {
            if (rows == null) return; rows.Children.Clear(); var records = Records();
            var filtered = records.Where(r => (r.title + " " + (secretTab ? r.username : r.body)).IndexOf(query.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0).OrderByDescending(r => r.pinned).ThenByDescending(r => r.updatedAt).ToList();
            string group = null;
            foreach (var record in filtered)
            {
                string next = record.pinned ? "置顶" : "最近"; if (next != group && query.Length == 0) { var caption = Text(next, 12, true); caption.Margin = new Thickness(10, 12, 0, 8); rows.Children.Add(caption); group = next; }
                var item = record; var row = new Grid(); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var titleLine = new DockPanel(); var date = Text(DateLabel(item.updatedAt), 11, true); date.Margin = new Thickness(10, 3, 0, 3); DockPanel.SetDock(date, Dock.Right); titleLine.Children.Add(date);
                var title = Text(String.IsNullOrEmpty(item.title) ? "未命名" : item.title, 15, false, true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; titleLine.Children.Add(title); row.Children.Add(titleLine);
                string preview = secretTab ? item.username : item.body.Replace("\r", " ").Replace("\n", " "); var summary = Text(String.IsNullOrEmpty(preview) ? "写一点什么…" : preview, 12, true); summary.TextWrapping = TextWrapping.NoWrap; summary.TextTrimming = TextTrimming.CharacterEllipsis; Grid.SetRow(summary, 1); row.Children.Add(summary);
                var open = Button("打开记录", "record-" + item.id, delegate { OpenRecord(item.id); }); AutomationProperties.SetName(open, "打开 " + title.Text); open.Content = row; open.HorizontalContentAlignment = HorizontalAlignment.Stretch; open.Padding = new Thickness(12, 13, 12, 13); open.Margin = new Thickness(0, 0, 0, 3); rows.Children.Add(open);
            }
            if (filtered.Count == 0) { var empty = new StackPanel { Margin = new Thickness(12, 55, 12, 20) }; empty.Children.Add(Text(query.Length > 0 ? "没有找到相关内容" : secretTab ? "添加你的第一个账号" : "把重要的事，先记下来。", 19, false, true)); empty.Children.Add(Text(query.Length > 0 ? "换个关键词试试。" : "一个想法，一件小事，都可以放在这里。", 13, true)); rows.Children.Add(empty); }
            Notice(records.Count + (secretTab ? " 条账号 · 已解锁" : " 条备忘录"));
        }
        static string DateLabel(string value) { var date = DateTime.Parse(value).ToLocalTime(); return date.Date == DateTime.Today ? "今天" : date.ToString("M月d日"); }
        void SwitchTab(bool secret) { SavePending(); secretTab = secret; editing = false; current = null; query = ""; Render(); }
        void NewRecord()
        {
            SavePending(); current = new Record(); if (secretTab) vault.Save(current); else { if (store.Notes.Count >= 5000) throw new Exception("最多保存 5000 条备忘录。"); store.Notes.Insert(0, current); notesDirty = true; SavePending(); }
            editing = true; Render();
        }
        void OpenRecord(string id) { SavePending(); current = Records().First(r => r.id == id); editing = true; Render(); }
        void Back() { SavePending(); current = null; editing = false; Render(); }
        void RenderEditor()
        {
            var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
            var actions = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(tools, Dock.Right);
            tools.Children.Add(IconButton("\uE718", current.pinned ? "取消置顶" : "置顶", "pin", delegate { current.pinned = !current.pinned; Changed(secretTab); SavePending(); Render(); }));
            tools.Children.Add(IconButton("\uE74D", "删除这一条", "delete", DeleteRecord)); actions.Children.Add(tools); actions.Children.Add(Button("‹  返回列表", "back", Back)); layout.Children.Add(actions);
            var fields = new StackPanel(); var title = Input(current.title, "edit-title", "标题", 120, delegate(string value) { current.title = value; Changed(secretTab); }); title.FontSize = 22; title.FontWeight = FontWeights.SemiBold; title.Background = Brushes.Transparent; title.BorderThickness = new Thickness(0); fields.Children.Add(title);
            fields.Children.Add(Text(secretTab ? "账号和备注加密保存 · 收起后锁定" : "本地备忘录 · 自动保存", 11, true));
            if (secretTab)
            {
                Field(fields, "账号 / 用户名", Input(current.username, "edit-username", "账号 / 用户名", 2048, delegate(string value) { current.username = value; Changed(true); }));
                var passwordRow = new DockPanel(); var reveal = Button("显示", "reveal-password", delegate { }); DockPanel.SetDock(reveal, Dock.Right); passwordRow.Children.Add(reveal);
                var copy = IconButton("\uE8C8", "复制密码", "copy-password", delegate { Copy(current.password); }); DockPanel.SetDock(copy, Dock.Right); passwordRow.Children.Add(copy);
                var passwordHost = new Grid(); var masked = Identify(new PasswordBox { Password = current.password, MaxLength = 4096 }, "edit-password", "密码"); var plain = Input(current.password, "edit-password-visible", "密码明文", 4096, delegate { }); plain.Visibility = Visibility.Collapsed; bool synchronizing = false;
                masked.PasswordChanged += delegate { if (synchronizing || rendering) return; synchronizing = true; plain.Text = masked.Password; current.password = masked.Password; Changed(true); synchronizing = false; };
                plain.TextChanged += delegate { if (synchronizing || rendering) return; synchronizing = true; masked.Password = plain.Text; current.password = plain.Text; Changed(true); synchronizing = false; };
                reveal.Click += delegate { bool show = plain.Visibility != Visibility.Visible; plain.Visibility = show ? Visibility.Visible : Visibility.Collapsed; masked.Visibility = show ? Visibility.Collapsed : Visibility.Visible; reveal.Content = show ? "隐藏" : "显示"; };
                passwordHost.Children.Add(masked); passwordHost.Children.Add(plain); passwordRow.Children.Add(passwordHost); Field(fields, "密码", passwordRow);
                var extra = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                extra.Children.Add(Button("复制账号", "copy-username", delegate { Copy(current.username); })); extra.Children.Add(Button("生成随机密码", "generate-password", delegate { masked.Password = Crypto.GeneratePassword(); })); fields.Children.Add(extra);
                Field(fields, "网站（可选）", Input(current.url, "edit-url", "网站", 2048, delegate(string value) { current.url = value; Changed(true); }));
            }
            var body = Input(current.body, "edit-body", secretTab ? "私密备注" : "正文", 100000, delegate(string value) { current.body = value; Changed(secretTab); }); body.AcceptsReturn = true; body.TextWrapping = TextWrapping.Wrap; body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; body.MinHeight = secretTab ? 100 : 300; Field(fields, secretTab ? "私密备注" : "正文", body);
            var scroll = new ScrollViewer { Content = fields }; Grid.SetRow(scroll, 1); layout.Children.Add(scroll); content.Children.Add(layout); Notice("已保存到本地");
        }
        TextBox Input(string value, string id, string name, int maximum, Action<string> changed)
        {
            var field = Identify(new TextBox { Text = value, MaxLength = maximum }, id, name); field.TextChanged += delegate { if (!rendering) changed(field.Text); }; return field;
        }
        void Field(StackPanel parent, string caption, FrameworkElement input) { var label = Text(caption, 12, true); label.Margin = new Thickness(0, 14, 0, 6); parent.Children.Add(label); parent.Children.Add(input); }
        void DeleteRecord()
        {
            SavePending(); deleted = JsonFile.Clone(current); deletedSecret = secretTab; deletedAt = DateTime.UtcNow;
            if (secretTab) vault.Delete(current.id); else { store.Notes.Remove(current); notesDirty = true; SavePending(); }
            current = null; editing = false; Render(); Notice("已删除 · 10 秒内按 Ctrl+Z 撤销");
        }
        void UndoDelete()
        {
            if (deleted == null || DateTime.UtcNow - deletedAt > TimeSpan.FromSeconds(10)) return;
            if (deletedSecret) vault.Save(deleted); else { store.Notes.Insert(0, deleted); notesDirty = true; SavePending(); }
            deleted = null; if (!editing) Render();
        }
        FrameworkElement UnlockForm()
        {
            bool create = !vault.Exists; var form = new StackPanel { Margin = new Thickness(18, 24, 18, 12) };
            form.Children.Add(Text(create ? "为密码库设置主密码" : "解锁你的密码库", 21, false, true)); form.Children.Add(Text("账号、密码和私密备注仅在本地加密保存。", 12, true));
            var password = Identify(new PasswordBox { MaxLength = 1024 }, "master-password", "主密码"); Field(form, "主密码", password);
            PasswordBox confirm = null; CheckBox acknowledge = null;
            if (create)
            {
                confirm = Identify(new PasswordBox { MaxLength = 1024 }, "confirm-password", "再次输入主密码"); Field(form, "再次输入主密码", confirm);
                var warning = Text("至少 12 个字符，可用较长的中文短句。主密码无法找回，请另行妥善保管。", 12, true); warning.Margin = new Thickness(0, 14, 0, 6); form.Children.Add(warning);
                acknowledge = Identify(new CheckBox { Content = "我已了解忘记主密码将无法解密" }, "master-acknowledge", "了解主密码无法找回"); form.Children.Add(acknowledge);
            }
            var error = Text("", 12); error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); form.Children.Add(error);
            var submit = Button(create ? "创建密码库" : "解锁", "unlock", delegate { }, true); submit.Margin = new Thickness(0, 12, 0, 10); submit.IsDefault = true;
            submit.Click += async delegate
            {
                if (busy) return; string pass = password.Password;
                if (create && (confirm.Password != pass || acknowledge.IsChecked != true)) { error.Text = "请确认两次主密码一致，并勾选提示。"; return; }
                busy = true; submit.IsEnabled = false; submit.Content = "正在处理…"; error.Text = "";
                try { await Task.Run(delegate { if (create) vault.Setup(pass); else vault.Unlock(pass); }); password.Clear(); if (confirm != null) confirm.Clear(); activityAt = DateTime.UtcNow; if (vault.Unlocked) { editing = false; Render(); } }
                catch (Exception e) { password.Clear(); error.Text = e.Message; }
                finally { busy = false; submit.IsEnabled = true; submit.Content = create ? "创建密码库" : "解锁"; }
            };
            form.Children.Add(submit); return new ScrollViewer { Content = form };
        }
        void LockVault()
        {
            vault.Lock(); current = secretTab ? null : current; deleted = null; ClearClipboard();
            if (activeDialog != null) activeDialog.Close();
            if (secretTab) { editing = false; Render(); } Notice("密码库已锁定"); Try(delegate { vault.Flush(); });
        }
        void HideToTray() { SavePending(); LockVault(); Hide(); }
        void Reveal() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); activityAt = DateTime.UtcNow; }
        void Toggle() { if (IsVisible && IsActive) Try(HideToTray); else Reveal(); }
        void Quit() { SavePending(); LockVault(); exiting = true; Close(); }
        void OnClosing(object sender, CancelEventArgs e)
        {
            if (!exiting) { e.Cancel = true; Try(HideToTray); return; }
            idleTimer.Stop(); saveTimer.Stop(); ClearClipboard(); vault.Dispose();
            if (handle != IntPtr.Zero) Platform.UnregisterHotKey(handle, hotkeyId); if (source != null) source.RemoveHook(Message);
            if (tray != null) { tray.Visible = false; tray.Dispose(); } SystemEvents.SessionSwitch -= SessionSwitch; SystemEvents.PowerModeChanged -= PowerChanged;
        }
        void OnKeyDown(object sender, KeyEventArgs e)
        {
            activityAt = DateTime.UtcNow;
            if (e.Key == Key.Escape && activeDialog == null) { e.Handled = true; Try(editing ? (Action)Back : HideToTray); }
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Try(SavePending); }
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control && !editing) { e.Handled = true; Try(UndoDelete); }
        }
        void SessionSwitch(object sender, SessionSwitchEventArgs e) { if (e.Reason == SessionSwitchReason.SessionLock) Dispatcher.BeginInvoke((Action)LockVault); }
        void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Suspend) Dispatcher.BeginInvoke((Action)LockVault); }
        IntPtr Message(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x312 && wParam.ToInt32() == hotkeyId) { handled = true; Toggle(); }
            if ((uint)message == showMessage) { handled = true; Reveal(); } return IntPtr.Zero;
        }
        void SetShortcut(string shortcut)
        {
            if (String.Equals(activeShortcut, shortcut, StringComparison.OrdinalIgnoreCase)) return;
            uint modifiers, key; Platform.Shortcut(shortcut, out modifiers, out key); int nextId = hotkeyId == 51 ? 52 : 51;
            if (!Platform.RegisterHotKey(handle, nextId, modifiers, key)) throw new Exception("快捷键 " + shortcut + " 已被占用，可在设置中更换。");
            if (activeShortcut != null) Platform.UnregisterHotKey(handle, hotkeyId); hotkeyId = nextId; activeShortcut = shortcut; shortcutHint.Text = shortcut + " 收起";
        }
        void SetupTray()
        {
            tray = new Forms.NotifyIcon { Text = "超强大脑 · " + config.shortcut, Visible = true };
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SuperBrain.icon.ico")) using (var icon = new System.Drawing.Icon(stream)) tray.Icon = (System.Drawing.Icon)icon.Clone();
            var menu = new Forms.ContextMenuStrip(); menu.Items.Add("打开超强大脑", null, delegate { Dispatcher.Invoke((Action)Reveal); }); menu.Items.Add("锁定密码库", null, delegate { Dispatcher.Invoke((Action)LockVault); }); menu.Items.Add("退出", null, delegate { Dispatcher.Invoke((Action)delegate { Try(Quit); }); });
            tray.ContextMenuStrip = menu; tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) Toggle(); };
        }
        static string Digest(string value) { using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value))); }
        void Copy(string value) { Clipboard.SetText(value); clipboardDigest = Digest(value); clipboardTimer.Stop(); clipboardTimer.Start(); Notice("已复制，30 秒后清除本次复制内容"); }
        void ClearClipboard() { clipboardTimer.Stop(); try { if (clipboardDigest != null && Clipboard.ContainsText() && Digest(Clipboard.GetText()) == clipboardDigest) Clipboard.Clear(); } catch { } clipboardDigest = null; }
    }
}
