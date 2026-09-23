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
        bool secretTab, editing, rendering, notesDirty, configDirty, exiting, busy, favoritesOnly;
        Record current, deleted;
        bool deletedSecret;
        DateTime deletedAt, activityAt = DateTime.UtcNow;
        StackPanel rows;
        Button notesTab, passwordsTab, retry, addButton, favoriteFilter, lockButton, pinWindow;
        TextBox searchInput;
        TextBlock searchPlaceholder;
        Window activeDialog;

        public BrainWindow(string directory, uint showMessage)
        {
            this.showMessage = showMessage; store = new LocalStore(directory); vault = new Vault(directory); config = JsonFile.Clone(store.Config);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SuperBrain.Theme.xaml")) Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
            Title = "超强大脑"; Width = config.windowWidth; Height = config.windowHeight; MinWidth = 400; MinHeight = 560; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (config.windowLeft.HasValue) RestoreWindowPlacement();
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"); FontSize = 13; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; UseLayoutRounding = true; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            SetResourceReference(BackgroundProperty, "Paper"); SetResourceReference(ForegroundProperty, "Ink");
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 52, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(12) });
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SuperBrain.icon.png")) Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BuildShell(); ApplyTheme(); Render();
            saveTimer.Tick += delegate { saveTimer.Stop(); Try(SavePending); };
            idleTimer.Tick += delegate { if (vault.Unlocked && DateTime.UtcNow - activityAt > TimeSpan.FromMinutes(config.autoLockMinutes)) LockVault(); };
            clipboardTimer.Tick += delegate { ClearClipboard(); };
            PreviewKeyDown += OnKeyDown; PreviewMouseDown += delegate { activityAt = DateTime.UtcNow; };
            SourceInitialized += delegate { handle = new WindowInteropHelper(this).Handle; source = HwndSource.FromHwnd(handle); source.AddHook(Message); Platform.RoundCorners(handle); try { SetShortcut(config.shortcut); } catch (Exception e) { Notice(e.Message); } };
            Loaded += delegate { WindowStartupLocation = WindowStartupLocation.Manual; LocationChanged += delegate { SaveWindowPlacement(); }; SizeChanged += delegate { SaveWindowPlacement(); }; SetupTray(); idleTimer.Start(); Try(delegate { store.SaveConfig(config); }); };
            Closing += OnClosing;
            StateChanged += delegate { if (WindowState == WindowState.Minimized) { WindowState = WindowState.Normal; HideToTray(); } };
            SystemEvents.SessionSwitch += SessionSwitch; SystemEvents.PowerModeChanged += PowerChanged;
        }
        void BuildShell()
        {
            var root = new Grid(); root.Children.Add(wallpaper);
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
            var header = new Grid { Margin = new Thickness(12, 0, 8, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var brand = Identify(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.SizeAll, ToolTip = "按住标题拖动窗口" }, "window-drag-region", "拖动窗口");
            var mark = Vector("brain", 20); mark.SetResourceReference(ForegroundProperty, "Accent"); mark.Margin = new Thickness(0, 0, 7, 0); brand.Children.Add(mark);
            var name = Text("超强大脑", 12, false, true); name.VerticalAlignment = VerticalAlignment.Center; name.TextWrapping = TextWrapping.NoWrap; brand.Children.Add(name); header.Children.Add(brand);
            searchInput = Identify(new TextBox { Text = query, Height = 28, FontSize = 11, Padding = new Thickness(27, 5, 6, 4), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "搜索记录（Ctrl+F）" }, "search", "搜索记录"); searchInput.SetResourceReference(Control.BackgroundProperty, "SearchSurface"); searchInput.SetResourceReference(Control.BorderBrushProperty, "Line");
            searchPlaceholder = Text("搜索备忘录…", 11, true); searchPlaceholder.IsHitTestVisible = false; searchPlaceholder.Margin = new Thickness(27, 0, 6, 0); searchPlaceholder.VerticalAlignment = VerticalAlignment.Center;
            var searchIcon = Vector("search", 13); searchIcon.Margin = new Thickness(8, 0, 0, 0); searchIcon.HorizontalAlignment = HorizontalAlignment.Left; searchIcon.IsHitTestVisible = false; searchIcon.SetResourceReference(ForegroundProperty, "Muted");
            var searchHost = new Grid { Margin = new Thickness(5, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center }; searchHost.Children.Add(searchInput); searchHost.Children.Add(searchPlaceholder); searchHost.Children.Add(searchIcon); Grid.SetColumn(searchHost, 1); WindowChrome.SetIsHitTestVisibleInChrome(searchHost, true); header.Children.Add(searchHost);
            searchInput.TextChanged += delegate { if (rendering) return; query = searchInput.Text; searchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateRows(); };
            var actions = Identify(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }, "window-actions", "窗口操作按钮");
            pinWindow = VectorButton("pin", "窗口置顶", "pin-window", delegate { config.alwaysOnTop = !config.alwaysOnTop; AppearanceChanged(); UpdateNavigation(); }); actions.Children.Add(pinWindow);
            actions.Children.Add(VectorButton("settings", "外观设置", "appearance", ShowAppearance));
            actions.Children.Add(VectorButton("close", "收起到托盘", "hide-window", HideToTray)); WindowChrome.SetIsHitTestVisibleInChrome(actions, true); Grid.SetColumn(actions, 2); header.Children.Add(actions);
            var headerSurface = new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }; headerSurface.SetResourceReference(Border.BackgroundProperty, "Panel"); headerSurface.SetResourceReference(Border.BorderBrushProperty, "Line"); layout.Children.Add(headerSurface);
            var navigation = new DockPanel { Margin = new Thickness(12, 8, 12, 8), LastChildFill = false };
            addButton = Button("新建", "new-item", NewRecord, true); addButton.Content = IconCaption("plus", "新建"); addButton.Height = 28; addButton.MinHeight = 28; addButton.Padding = new Thickness(8, 2, 8, 2); addButton.FontSize = 12; DockPanel.SetDock(addButton, Dock.Right); navigation.Children.Add(addButton);
            lockButton = VectorButton("lock", "锁定密码库", "lock-vault", LockVault); lockButton.Margin = new Thickness(0, 0, 6, 0); DockPanel.SetDock(lockButton, Dock.Right); navigation.Children.Add(lockButton);
            notesTab = Button("备忘录", "notes-tab", delegate { SwitchTab(false); }); notesTab.Content = IconCaption("note", "备忘录"); notesTab.Margin = new Thickness(0, 0, 4, 0); navigation.Children.Add(notesTab);
            passwordsTab = Button("密码库", "vault-tab", delegate { SwitchTab(true); }); passwordsTab.Content = IconCaption("key", "密码库"); navigation.Children.Add(passwordsTab);
            var separator = new Border { Width = 1, Height = 16, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }; separator.SetResourceReference(Border.BackgroundProperty, "Line"); navigation.Children.Add(separator);
            favoriteFilter = VectorButton("star", "只看收藏", "favorites", delegate { SavePending(); editing = false; current = null; favoritesOnly = !favoritesOnly; Render(); }); navigation.Children.Add(favoriteFilter);
            var navSurface = new Border { Child = navigation, BorderThickness = new Thickness(0, 0, 0, 1) }; navSurface.SetResourceReference(Border.BackgroundProperty, "Panel"); navSurface.SetResourceReference(Border.BorderBrushProperty, "Line"); Grid.SetRow(navSurface, 1); layout.Children.Add(navSurface);
            Grid.SetRow(content, 2); layout.Children.Add(content);
            var footer = new DockPanel { Margin = new Thickness(12, 0, 7, 0) };
            var settings = VectorButton("settings", "设置", "settings", ShowSettings); settings.Width = 24; settings.Height = settings.MinHeight = 24; settings.Margin = new Thickness(6, 0, 0, 0); DockPanel.SetDock(settings, Dock.Right); footer.Children.Add(settings);
            shortcutHint.FontSize = 10; shortcutHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); DockPanel.SetDock(shortcutHint, Dock.Right); footer.Children.Add(shortcutHint);
            retry = Button("重试", "retry-save", SavePending); retry.Visibility = Visibility.Collapsed; retry.MinHeight = 24; retry.Padding = new Thickness(4); DockPanel.SetDock(retry, Dock.Right); footer.Children.Add(retry);
            status.FontSize = 10; status.TextTrimming = TextTrimming.CharacterEllipsis; status.TextWrapping = TextWrapping.NoWrap; status.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); footer.Children.Add(status);
            var footSurface = new Border { Child = footer, BorderThickness = new Thickness(0, 1, 0, 0) }; footSurface.SetResourceReference(Border.BackgroundProperty, "Panel"); footSurface.SetResourceReference(Border.BorderBrushProperty, "Line"); Grid.SetRow(footSurface, 3); layout.Children.Add(footSurface);
            var frame = new Border { Child = layout, BorderThickness = new Thickness(1) }; frame.SetResourceReference(Border.BorderBrushProperty, "Line"); root.Children.Add(frame); Content = root;
        }
        // Vector paths are shared by the approved HTML preview and the native window.
        FrameworkElement Vector(string name, double size = 16)
        {
            string data;
            switch (name)
            {
                case "brain": data = "M12,18 V5 A3,3 0 0 0 6.4,3.5 A4,4 0 0 0 3,9 A4,4 0 0 0 3,16 A4,4 0 0 0 9,20.5 A3,3 0 0 0 12,18 M12,5 A3,3 0 0 1 17.6,3.5 A4,4 0 0 1 21,9 A4,4 0 0 1 21,16 A4,4 0 0 1 15,20.5 A3,3 0 0 1 12,18 M8,8 C6,8 5,9 5,11 M16,8 C18,8 19,9 19,11 M7,16 C9,16 10,15 10,13 M17,16 C15,16 14,15 14,13"; break;
                case "search": data = "M17.6,10.8 A6.8,6.8 0 1 1 4,10.8 A6.8,6.8 0 1 1 17.6,10.8 M16,16 L20.5,20.5"; break;
                case "note": data = "M14,3 H5 A1,1 0 0 0 4,4 V20 A1,1 0 0 0 5,21 H19 A1,1 0 0 0 20,20 V9 Z M14,3 V9 H20 M8,13 H16 M8,17 H14"; break;
                case "key": data = "M13,8 A5,5 0 1 1 3,8 A5,5 0 1 1 13,8 M11.5,11.5 L20.5,20.5 M17,16 L19,14 M14,19 L16,17"; break;
                case "pin": data = "M8,3 H16 L15,10 19,14 V16 H5 V14 L9,10 Z M12,16 V22"; break;
                case "settings": data = "M10,3 L9.3,5.2 7.3,6.1 5.2,5.6 3,9 4.5,10.7 V13.3 L3,15 5.2,18.4 7.3,17.9 9.3,18.8 10,21 H14 L14.7,18.8 16.7,17.9 18.8,18.4 21,15 19.5,13.3 V10.7 L21,9 18.8,5.6 16.7,6.1 14.7,5.2 14,3 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12"; break;
                case "close": data = "M6,6 L18,18 M18,6 L6,18"; break;
                case "star": data = "M12,3 L14.8,8.6 21,9.5 16.5,13.9 17.6,20.1 12,17.1 6.4,20.1 7.5,13.9 3,9.5 9.2,8.6 Z"; break;
                case "plus": data = "M12,5 V19 M5,12 H19"; break;
                case "back": data = "M10,5 L3,12 10,19 M3,12 H21"; break;
                case "lock": data = "M5,10 H19 V21 H5 Z M8,10 V7 A4,4 0 0 1 16,7 V10 M12,14 V17"; break;
                case "trash": data = "M3,6 H21 M9,6 V3 H15 V6 M5,6 L6,21 H18 L19,6 M10,10 V17 M14,10 V17"; break;
                default: data = "M5,12 L9,16 19,6"; break;
            }
            var drawing = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            drawing.SetBinding(System.Windows.Shapes.Path.StrokeProperty, new System.Windows.Data.Binding { Path = new PropertyPath("(0)", System.Windows.Documents.TextElement.ForegroundProperty), RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self) });
            var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(drawing);
            return new Viewbox { Child = canvas, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        }
        Button VectorButton(string icon, string name, string id, Action action)
        {
            var button = Button(name, id, action); button.Content = Vector(icon); button.ToolTip = name; button.Width = 28; button.Height = button.MinHeight = 28; button.Padding = new Thickness(5); button.SetResourceReference(StyleProperty, "IconButton"); return button;
        }
        StackPanel IconCaption(string icon, string caption)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; var symbol = Vector(icon, 15); symbol.Margin = new Thickness(0, 0, 5, 0); panel.Children.Add(symbol); panel.Children.Add(new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center }); return panel;
        }
        void UpdateNavigation()
        {
            bool locked = secretTab && !vault.Unlocked;
            notesTab.SetResourceReference(StyleProperty, secretTab ? "TextTab" : "ActiveTab"); passwordsTab.SetResourceReference(StyleProperty, secretTab ? "ActiveTab" : "TextTab");
            AutomationProperties.SetItemStatus(notesTab, secretTab ? "" : "已选中"); AutomationProperties.SetItemStatus(passwordsTab, secretTab ? "已选中" : "");
            favoriteFilter.IsEnabled = !locked; favoriteFilter.SetResourceReference(Control.BackgroundProperty, favoritesOnly ? "Selected" : "Panel"); favoriteFilter.SetResourceReference(Control.ForegroundProperty, favoritesOnly ? "Accent" : "Muted"); AutomationProperties.SetItemStatus(favoriteFilter, favoritesOnly ? "只看收藏" : "全部记录");
            addButton.IsEnabled = !locked && !busy; lockButton.Visibility = secretTab && vault.Unlocked ? Visibility.Visible : Visibility.Collapsed;
            pinWindow.SetResourceReference(Control.BackgroundProperty, config.alwaysOnTop ? "Selected" : "Panel"); pinWindow.SetResourceReference(Control.ForegroundProperty, config.alwaysOnTop ? "Accent" : "Muted"); AutomationProperties.SetItemStatus(pinWindow, config.alwaysOnTop ? "已置顶" : "未置顶");
            searchInput.IsEnabled = !locked && !editing; searchPlaceholder.Text = secretTab ? "搜索账号…" : "搜索备忘录…"; searchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (searchInput.Text != query) searchInput.Text = query;
        }
        void RestoreWindowPlacement()
        {
            double left = config.windowLeft.Value, top = config.windowTop.Value;
            double screenLeft = SystemParameters.VirtualScreenLeft, screenTop = SystemParameters.VirtualScreenTop;
            double screenRight = screenLeft + SystemParameters.VirtualScreenWidth, screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
            if (left + Width < screenLeft + 120 || left > screenRight - 120 || top + 80 < screenTop || top > screenBottom - 80)
            {
                var work = SystemParameters.WorkArea; left = work.Left + (work.Width - Width) / 2; top = work.Top + (work.Height - Height) / 2;
            }
            Left = Math.Max(screenLeft - Width + 120, Math.Min(left, screenRight - 120));
            Top = Math.Max(screenTop, Math.Min(top, screenBottom - 80)); WindowStartupLocation = WindowStartupLocation.Manual;
        }
        void SaveWindowPlacement()
        {
            if (!IsLoaded || !IsVisible || WindowState != WindowState.Normal || Double.IsNaN(Left) || Double.IsNaN(Top)) return;
            config.windowLeft = Left; config.windowTop = Top; config.windowWidth = ActualWidth; config.windowHeight = ActualHeight;
            configDirty = true; saveTimer.Stop(); saveTimer.Start();
        }
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
            var button = Button(glyph, id, action); AutomationProperties.SetName(button, name); button.ToolTip = name; button.FontFamily = new FontFamily("Segoe MDL2 Assets"); button.FontSize = 15; button.Width = 32; button.Height = 32; button.MinHeight = 32; button.Margin = new Thickness(2, 0, 2, 0); button.Padding = new Thickness(4); return button;
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
            Notice(secretTab && vault.Unlocked ? "已加密保存到本地" : "已保存到本地");
        }
        void Render()
        {
            rendering = true; content.Children.Clear(); rows = null; UpdateNavigation();
            if (editing && current != null) RenderEditor(); else RenderList();
            shortcutHint.Text = editing ? "Esc 返回" : config.shortcut + "  呼出 / 收起"; rendering = false;
        }
        void RenderList()
        {
            if (secretTab && !vault.Unlocked) { content.Children.Add(UnlockForm()); Notice("密码库已锁定"); return; }
            rows = new StackPanel { Margin = new Thickness(12, 12, 12, 2) };
            content.Children.Add(new ScrollViewer { Content = rows }); UpdateRows();
        }
        List<Record> Records() { return secretTab ? vault.List() : store.Notes; }
        void UpdateRows()
        {
            if (rows == null) return; rows.Children.Clear(); var records = Records();
            var filtered = records.Where(r => (!favoritesOnly || r.pinned) && (r.title + " " + (secretTab ? r.username : r.body)).IndexOf(query.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0).OrderByDescending(r => r.pinned).ThenByDescending(r => r.updatedAt).ToList();
            foreach (var item in filtered)
            {
                var row = new StackPanel(); var meta = new DockPanel { Margin = new Thickness(0, 0, 28, 5) };
                var date = Text(DateLabel(item.updatedAt), 10, true); date.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(date, Dock.Right); meta.Children.Add(date);
                var kind = IconCaption(secretTab ? "key" : "note", secretTab ? "账号" : "备忘录"); kind.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); TextBlock.SetFontSize(kind, 10); meta.Children.Add(kind); row.Children.Add(meta);
                var title = Text(String.IsNullOrEmpty(item.title) ? "未命名" : item.title, 13, false, true); title.Margin = new Thickness(0, 0, 0, 4); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(title);
                string preview = secretTab ? item.username + "\n••••••••••••" : item.body.Replace("\r", "");
                var summary = Text(String.IsNullOrEmpty(preview) ? "写一点什么…" : preview, 12, true); summary.Margin = new Thickness(0); summary.LineHeight = 21; summary.MaxHeight = 42; summary.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(summary);
                var open = Button("打开记录", "record-" + item.id, delegate { OpenRecord(item.id); }); open.SetResourceReference(StyleProperty, "ListRow"); AutomationProperties.SetName(open, "打开 " + title.Text); open.Content = row; open.HorizontalContentAlignment = HorizontalAlignment.Stretch; open.Padding = new Thickness(11, 10, 11, 11);
                if (item.pinned) open.SetResourceReference(Control.BorderBrushProperty, "FavoriteBorder");
                var card = new Grid { Margin = new Thickness(0, 0, 0, 10) }; card.Children.Add(open);
                var favorite = VectorButton("star", item.pinned ? "取消收藏" : "收藏", "favorite-" + item.id, delegate { ToggleFavorite(item); }); favorite.Width = favorite.Height = favorite.MinHeight = 26; favorite.HorizontalAlignment = HorizontalAlignment.Right; favorite.VerticalAlignment = VerticalAlignment.Top; favorite.Margin = new Thickness(0, 4, 5, 0); favorite.SetResourceReference(Control.ForegroundProperty, item.pinned ? "Favorite" : "Muted"); AutomationProperties.SetItemStatus(favorite, item.pinned ? "已收藏" : "未收藏"); card.Children.Add(favorite); rows.Children.Add(card);
            }
            if (filtered.Count == 0)
            {
                var empty = new StackPanel { Margin = new Thickness(14, 90, 14, 20), HorizontalAlignment = HorizontalAlignment.Center }; var symbol = Vector(query.Length > 0 ? "search" : favoritesOnly ? "star" : "note", 30); symbol.Margin = new Thickness(0, 0, 0, 16); symbol.SetResourceReference(ForegroundProperty, "Muted"); empty.Children.Add(symbol);
                var title = Text(query.Length > 0 ? "没有找到相关内容" : favoritesOnly ? "还没有收藏" : secretTab ? "添加你的第一个账号" : "记下第一条备忘录", 16, false, true); title.TextAlignment = TextAlignment.Center; empty.Children.Add(title);
                var hint = Text(query.Length > 0 ? "换个关键词试试。" : favoritesOnly ? "点击记录右上角的星标即可收藏。" : "点击右上角“新建”开始。", 12, true); hint.TextAlignment = TextAlignment.Center; empty.Children.Add(hint); rows.Children.Add(empty);
            }
            Notice(filtered.Count + (secretTab ? " 个账号" : " 条备忘录") + (favoritesOnly ? " · 收藏" : ""));
        }
        void ToggleFavorite(Record item)
        {
            SavePending(); item.pinned = !item.pinned;
            if (secretTab) vault.Save(item); else { notesDirty = true; SavePending(); }
            UpdateRows();
        }
        static string DateLabel(string value) { var date = DateTime.Parse(value).ToLocalTime(); return date.Date == DateTime.Today ? date.ToString("HH:mm") : date.Date == DateTime.Today.AddDays(-1) ? "昨天" : date.ToString("M月d日"); }
        void SwitchTab(bool secret) { SavePending(); secretTab = secret; editing = false; current = null; query = ""; favoritesOnly = false; Render(); }
        void NewRecord()
        {
            SavePending(); favoritesOnly = false; query = ""; current = new Record(); if (secretTab) vault.Save(current); else { if (store.Notes.Count >= 5000) throw new Exception("最多保存 5000 条备忘录。"); store.Notes.Insert(0, current); notesDirty = true; SavePending(); }
            editing = true; Render();
        }
        void OpenRecord(string id) { SavePending(); current = Records().First(r => r.id == id); editing = true; Render(); }
        void Back() { SavePending(); current = null; editing = false; Render(); }
        void RenderEditor()
        {
            var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
            var actions = new DockPanel { Margin = new Thickness(12, 5, 12, 5) };
            var tools = new StackPanel { Orientation = Orientation.Horizontal }; DockPanel.SetDock(tools, Dock.Right);
            tools.Children.Add(VectorButton("star", current.pinned ? "取消收藏" : "收藏", "pin", delegate { current.pinned = !current.pinned; Changed(secretTab); SavePending(); Render(); }));
            tools.Children.Add(VectorButton("trash", "删除这一条", "delete", DeleteRecord)); var done = Button("完成", "editor-done", Back, true); done.MinHeight = 28; done.Padding = new Thickness(10, 3, 10, 3); done.Margin = new Thickness(8, 0, 0, 0); tools.Children.Add(done); actions.Children.Add(tools); var back = Button("返回列表", "back", Back); back.Content = IconCaption("back", "返回列表"); back.SetResourceReference(StyleProperty, "TextTab"); back.HorizontalAlignment = HorizontalAlignment.Left; actions.Children.Add(back); var bar = new Border { Child = actions, BorderThickness = new Thickness(0, 0, 0, 1) }; bar.SetResourceReference(Border.BackgroundProperty, "Panel"); bar.SetResourceReference(Border.BorderBrushProperty, "Line"); layout.Children.Add(bar);
            var fields = new StackPanel(); var title = Input(current.title, "edit-title", "标题", 120, delegate(string value) { current.title = value; Changed(secretTab); }); title.FontSize = 19; title.Padding = new Thickness(0, 0, 0, 8); title.FontWeight = FontWeights.SemiBold; title.Background = Brushes.Transparent; title.BorderThickness = new Thickness(0); fields.Children.Add(title);
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
            var body = Input(current.body, "edit-body", secretTab ? "私密备注" : "正文", 100000, delegate(string value) { current.body = value; Changed(secretTab); }); body.AcceptsReturn = true; body.TextWrapping = TextWrapping.Wrap; body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; body.MinHeight = secretTab ? 100 : 250; if (secretTab) Field(fields, "私密备注", body); else { body.BorderThickness = new Thickness(0); body.Background = Brushes.Transparent; body.Padding = new Thickness(0, 12, 0, 0); body.FontSize = 13; fields.Children.Add(body); }
            var scroll = new ScrollViewer { Content = fields }; var paper = new Border { Child = scroll, Padding = new Thickness(17), Margin = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }; paper.SetResourceReference(Border.BackgroundProperty, "Surface"); paper.SetResourceReference(Border.BorderBrushProperty, "Line"); Grid.SetRow(paper, 1); layout.Children.Add(paper); content.Children.Add(layout); Notice(secretTab ? "已加密保存到本地" : "已保存到本地");
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
            bool create = !vault.Exists; var form = new StackPanel { Margin = new Thickness(22, 24, 22, 16) }; var symbol = Vector("lock", 29); symbol.SetResourceReference(ForegroundProperty, "Accent"); var badge = new Border { Child = symbol, Width = 58, Height = 58, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 0, 18), HorizontalAlignment = HorizontalAlignment.Center }; badge.SetResourceReference(Border.BackgroundProperty, "Selected"); form.Children.Add(badge);
            form.Children.Add(Text(create ? "为密码库设置主密码" : "解锁你的密码库", 17, false, true)); form.Children.Add(Text("账号、密码和私密备注仅在本地加密保存。", 12, true));
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
                finally { busy = false; submit.IsEnabled = true; submit.Content = create ? "创建密码库" : "解锁"; UpdateNavigation(); }
            };
            form.Children.Add(submit); var panel = new Border { Child = form, Margin = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }; panel.SetResourceReference(Border.BackgroundProperty, "Surface"); panel.SetResourceReference(Border.BorderBrushProperty, "Line"); return new ScrollViewer { Content = panel };
        }
        void LockVault()
        {
            vault.Lock(); current = secretTab ? null : current; deleted = null; ClearClipboard();
            if (activeDialog != null) activeDialog.Close();
            if (secretTab) { editing = false; query = ""; favoritesOnly = false; Render(); } Notice("密码库已锁定"); Try(delegate { vault.Flush(); });
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
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && activeDialog == null) { e.Handled = true; Try(delegate { if (editing) Back(); searchInput.Focus(); searchInput.SelectAll(); }); }
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
