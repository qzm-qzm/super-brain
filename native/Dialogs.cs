using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SuperBrain
{
    public sealed partial class BrainWindow
    {
        string cachedImage;
        static Color ColorOf(string value) { return (Color)ColorConverter.ConvertFromString(value); }
        static Color Mix(Color a, Color b, double fraction) { return Color.FromRgb((byte)(a.R * (1 - fraction) + b.R * fraction), (byte)(a.G * (1 - fraction) + b.G * fraction), (byte)(a.B * (1 - fraction) + b.B * fraction)); }
        static double Luminance(Color c) { Func<byte, double> channel = delegate(byte v) { double x = v / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }; return .2126 * channel(c.R) + .7152 * channel(c.G) + .0722 * channel(c.B); }
        static double Contrast(Color a, Color b) { var x = Luminance(a); var y = Luminance(b); return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05); }
        static Color Legible(Color value, Color background, double target)
        {
            Color edge = Contrast(Colors.White, background) > Contrast(Colors.Black, background) ? Colors.White : Colors.Black;
            for (int i = 0; i <= 100; i++) { Color next = Mix(value, edge, i / 100.0); if (Contrast(next, background) >= target) return next; } return edge;
        }
        void Brush(string key, Color value) { var brush = new SolidColorBrush(value); brush.Freeze(); Resources[key] = brush; }
        void ApplyTheme()
        {
            var bg = ColorOf(config.background); bool dark = Contrast(Colors.White, bg) > Contrast(Colors.Black, bg);
            var surface = dark ? Mix(bg, Colors.Black, .22) : Mix(bg, Colors.White, .96);
            var panel = dark ? Mix(bg, Colors.Black, .18) : Mix(bg, Colors.White, .96);
            var ink = dark ? ColorOf("#EDF0F5") : ColorOf("#242830"); var accent = Legible(ColorOf(config.accent), surface, 4.5);
            Brush("Paper", bg); Brush("Ink", Legible(ink, surface, 10)); Brush("Muted", Legible(dark ? ColorOf("#AEB8C7") : ColorOf("#626B78"), surface, 4.8)); Brush("Accent", accent);
            Brush("OnAccent", Contrast(Colors.White, accent) > Contrast(Colors.Black, accent) ? Colors.White : Colors.Black);
            Brush("Surface", surface);
            Brush("SearchSurface", Mix(surface, ink, dark ? .06 : .035)); Brush("Panel", panel);
            Brush("Selected", Mix(surface, accent, dark ? .20 : .085));
            Brush("Line", Mix(surface, ink, dark ? .20 : .12)); Brush("Control", Legible(Mix(surface, ink, .4), surface, 3)); Brush("Hover", Mix(surface, accent, .045)); Brush("Danger", Legible(ColorOf("#B73342"), surface, 5));
            Brush("Favorite", Legible(ColorOf("#AD741B"), surface, 4.5)); Brush("FavoriteBorder", Mix(surface, accent, .38));
            wallpaper.Opacity = 1 - config.imageTransparency / 100.0; Topmost = config.alwaysOnTop;
            if (config.image == cachedImage) return; cachedImage = config.image;
            if (String.IsNullOrEmpty(config.image)) wallpaper.Source = null;
            else
            {
                try { var bytes = Convert.FromBase64String(config.image.Substring(config.image.IndexOf(',') + 1)); using (var stream = new MemoryStream(bytes)) { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1200; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); wallpaper.Source = bitmap; } }
                catch { wallpaper.Source = null; Notice("背景图片无法读取，可在外观设置中更换。", true); }
            }
        }
        void AppearanceChanged() { configDirty = true; ApplyTheme(); saveTimer.Stop(); saveTimer.Start(); }
        Window Dialog(string title, StackPanel body, double height = 600)
        {
            if (activeDialog != null) { activeDialog.Activate(); return null; }
            SavePending(); var window = new Window { Owner = this, Title = title, Width = 420, Height = height, MinWidth = 390, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, FontFamily = FontFamily, FontSize = 13 };
            window.UseLayoutRounding = true; TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
            window.Resources.MergedDictionaries.Add(Resources); window.SetResourceReference(Window.BackgroundProperty, "Surface"); window.SetResourceReference(Window.ForegroundProperty, "Ink");
            body.Margin = new Thickness(22, 16, 22, 18); window.Content = new ScrollViewer { Content = body };
            window.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { activityAt = DateTime.UtcNow; if (e.Key == Key.Escape) { e.Handled = true; window.Close(); } };
            window.PreviewMouseDown += delegate { activityAt = DateTime.UtcNow; };
            window.Closed += delegate { activeDialog = null; Try(SavePending); };
            activeDialog = window; return window;
        }
        void ShowAppearance()
        {
            var body = new StackPanel(); var dialog = Dialog("外观 · 超强大脑", body, 675); if (dialog == null) return;
            body.Children.Add(Text("外观", 18, false, true)); body.Children.Add(Text("背景图片单独调节，文字和按钮始终清晰。", 12, true));
            var themes = new WrapPanel { Margin = new Thickness(0, 14, 0, 4) };
            string[][] presets = { new[] { "云雾白", "#F8F9FB", "#1665D8" }, new[] { "石墨黑", "#20242C", "#AEC6FF" }, new[] { "雾蓝", "#EAF2FA", "#345F91" }, new[] { "奶油", "#FBF5E9", "#866343" }, new[] { "浅樱", "#F8EEF1", "#9B4D68" }, new[] { "鼠尾草", "#EEF3EE", "#43684D" } };
            TextBox background = null, accent = null;
            foreach (var values in presets)
            {
                var preset = values; var button = Button(preset[0], "theme-" + preset[0], delegate { config.background = preset[1]; config.accent = preset[2]; background.Text = config.background; accent.Text = config.accent; AppearanceChanged(); });
                button.Background = new SolidColorBrush(ColorOf(preset[1])); button.Foreground = new SolidColorBrush(Legible(ColorOf(preset[2]), ColorOf(preset[1]), 5)); button.Width = 104; button.Margin = new Thickness(0, 0, 8, 8); themes.Children.Add(button);
            }
            body.Children.Add(themes);
            background = Input(config.background, "background-color", "背景色", 7, delegate(string value) { if (System.Text.RegularExpressions.Regex.IsMatch(value, "^#[a-fA-F0-9]{6}$")) { config.background = value.ToUpperInvariant(); AppearanceChanged(); } });
            accent = Input(config.accent, "accent-color", "主题色", 7, delegate(string value) { if (System.Text.RegularExpressions.Regex.IsMatch(value, "^#[a-fA-F0-9]{6}$")) { config.accent = value.ToUpperInvariant(); AppearanceChanged(); } });
            Field(body, "小窗背景色，例如 #F6F8FB", background); Field(body, "主题色", accent);
            var imageActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            imageActions.Children.Add(Button("选择背景图片", "choose-image", delegate
            {
                var pick = new OpenFileDialog { Title = "选择背景图片", Filter = "图片 (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", CheckFileExists = true };
                if (pick.ShowDialog(dialog) != true) return;
                if (new FileInfo(pick.FileName).Length > 2 * 1024 * 1024) throw new Exception("请选择 2 MB 以内的图片。");
                byte[] data = File.ReadAllBytes(pick.FileName);
                using (var stream = new MemoryStream(data)) { var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand); if (frame.PixelWidth > 12000 || frame.PixelHeight > 12000) throw new Exception("图片尺寸过大，请缩小后再选择。"); }
                string type = Path.GetExtension(pick.FileName).ToLowerInvariant() == ".png" ? "png" : "jpeg"; config.image = "data:image/" + type + ";base64," + Convert.ToBase64String(data); AppearanceChanged();
            }));
            imageActions.Children.Add(Button("移除图片", "remove-image", delegate { config.image = ""; AppearanceChanged(); })); body.Children.Add(imageActions);
            body.Children.Add(Text("支持 PNG、JPG，最大 2 MB。图片随资料一起保存。", 11, true));
            var valueLabel = Text("图片透明度  " + config.imageTransparency + "%", 13); valueLabel.Margin = new Thickness(0, 16, 0, 8); body.Children.Add(valueLabel);
            var transparency = Identify(new Slider { Minimum = 0, Maximum = 100, Value = config.imageTransparency, TickFrequency = 1, IsSnapToTickEnabled = true, Height = 24 }, "image-transparency", "图片透明度");
            transparency.ValueChanged += delegate { config.imageTransparency = (int)transparency.Value; valueLabel.Text = "图片透明度  " + config.imageTransparency + "%"; AppearanceChanged(); }; body.Children.Add(transparency); body.Children.Add(Text("0% 不透明                         100% 完全透明", 11, true));
            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            controls.Children.Add(Button("恢复默认", "reset-appearance", delegate { config.background = "#F8F9FB"; config.accent = "#1665D8"; config.image = ""; config.imageTransparency = 35; background.Text = config.background; accent.Text = config.accent; transparency.Value = 35; AppearanceChanged(); })); controls.Children.Add(Button("完成", "appearance-done", delegate { SavePending(); dialog.Close(); }, true)); body.Children.Add(controls);
            dialog.ShowDialog();
        }
        void ShowSettings()
        {
            var body = new StackPanel(); var dialog = Dialog("设置 · 超强大脑", body, 690); if (dialog == null) return;
            body.Children.Add(Text("设置", 18, false, true));
            var hotkey = Identify(new TextBox { Text = config.shortcut, MaxLength = 60, IsReadOnly = true, IsReadOnlyCaretVisible = false, Cursor = Cursors.Hand }, "setting-shortcut", "点击后按键设置全局快捷键");
            Field(body, "呼出 / 收起快捷键", hotkey);
            var captureHelp = Text("点击上方输入框，然后直接按想绑定的键。", 11, true); body.Children.Add(captureHelp);
            bool startupEnabled = StartupRegistration.IsEnabled(StartupRegistration.Executable);
            var startup = Identify(new CheckBox { Content = "登录 Windows 后后台启动", IsChecked = startupEnabled }, "setting-startup", "登录 Windows 后后台启动"); body.Children.Add(startup);
            body.Children.Add(Text("开机不弹窗口，按快捷键打开；收起后释放窗口内存。", 11, true));
            var topmost = Identify(new CheckBox { Content = "窗口始终置顶", IsChecked = config.alwaysOnTop }, "setting-topmost", "窗口始终置顶"); body.Children.Add(topmost);
            var timeout = Identify(new ComboBox { ItemsSource = new[] { 1, 5, 10, 15, 30 }, SelectedItem = config.autoLockMinutes, Padding = new Thickness(8), FontSize = 14 }, "setting-timeout", "闲置锁定分钟数"); Field(body, "闲置多少分钟后锁定密码库", timeout);
            body.Children.Add(Text("收起窗口、锁屏或休眠时也会锁定。", 11, true));
            var error = Identify(Text("", 12), "settings-error", "快捷键错误提示"); error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); body.Children.Add(error);
            Action restoreShortcut = delegate
            {
                if (activeShortcut == null && handle != IntPtr.Zero)
                {
                    try { SetShortcut(config.shortcut); }
                    catch (Exception e) { error.Text = e.Message; }
                }
            };
            hotkey.GotKeyboardFocus += delegate
            {
                if (activeShortcut != null && handle != IntPtr.Zero) { Platform.UnregisterHotKey(handle, hotkeyId); activeShortcut = null; }
                captureHelp.Text = "现在按下快捷键；只按 Ctrl / Alt / Shift 时，继续按一个主键。";
                hotkey.SelectAll();
            };
            hotkey.LostKeyboardFocus += delegate { restoreShortcut(); };
            hotkey.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Tab || e.Key == Key.Escape) return;
                e.Handled = true;
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (Platform.IsModifierKey(key)) { captureHelp.Text = "继续按一个主键，例如 Q 或 F9。"; return; }
                if (e.IsRepeat) return;
                try
                {
                    string captured = Platform.CapturedShortcut(key, Keyboard.Modifiers);
                    hotkey.Text = captured; error.Text = "";
                    captureHelp.Text = "已录入 " + captured + "，点击“保存设置”后生效。";
                }
                catch (Exception ex) { error.Text = ex.Message; }
            };
            dialog.Closed += delegate { restoreShortcut(); };
            body.Children.Add(Button("保存设置", "save-settings", delegate
            {
                try
                {
                    var next = JsonFile.Clone(config); next.shortcut = hotkey.Text.Trim(); next.alwaysOnTop = topmost.IsChecked == true; next.autoLockMinutes = (int)timeout.SelectedItem; next.Validate(); string oldShortcut = config.shortcut; bool changed = !String.Equals(next.shortcut, oldShortcut, StringComparison.OrdinalIgnoreCase);
                    if (changed) SetShortcut(next.shortcut);
                    bool changeStartup = startup.IsChecked == true || startupEnabled;
                    var previousStartup = changeStartup ? StartupRegistration.Capture() : null;
                    try
                    {
                        if (changeStartup) StartupRegistration.SetEnabled(startup.IsChecked == true, StartupRegistration.Executable);
                        store.SaveConfig(next);
                    }
                    catch
                    {
                        if (changeStartup) StartupRegistration.Restore(previousStartup);
                        if (changed) SetShortcut(oldShortcut); throw;
                    }
                    config = next; configDirty = false; ApplyTheme(); if (tray != null) tray.Text = "超强大脑 · " + config.shortcut; dialog.Close(); Render();
                }
                catch (Exception e) { error.Text = e.Message; }
            }, true));
            var heading = Text("资料与备份", 14, false, true); heading.Margin = new Thickness(0, 24, 0, 6); body.Children.Add(heading); body.Children.Add(Text("资料在同目录 data 文件夹中。普通备忘录为明文，密码库单独加密。", 12, true));
            var data = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) }; var export = Button("导出备份", "export-backup", ExportBackup); export.Margin = new Thickness(0, 0, 6, 6); data.Children.Add(export); var import = Button("恢复备份", "import-backup", ImportBackup); import.Margin = new Thickness(0, 0, 6, 6); data.Children.Add(import); var directory = Button("打开 data 文件夹", "open-data", delegate { Platform.Open(store.DirectoryPath); }); directory.Margin = new Thickness(0, 0, 0, 6); data.Children.Add(directory); body.Children.Add(data);
            var change = Button("修改主密码", "change-master", delegate { dialog.Close(); ShowPasswordChange(); }); change.IsEnabled = vault.Unlocked; body.Children.Add(change);
            var version = Text("超强大脑 " + Platform.Version + " · 轻量版", 12, true); version.Margin = new Thickness(0, 24, 0, 4); body.Children.Add(version); body.Children.Add(Text("更新时替换运行文件，保留 data 文件夹。", 11, true));
            var bottom = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; var updates = Button("查看新版本", "updates", delegate { Platform.Open(Platform.Repository + "/releases/latest"); }); updates.Margin = new Thickness(0, 0, 6, 6); bottom.Children.Add(updates); var source = Button("源码", "repository", delegate { Platform.Open(Platform.Repository); }); source.Margin = new Thickness(0, 0, 6, 6); bottom.Children.Add(source); bottom.Children.Add(Button("退出工具", "quit", Quit)); body.Children.Add(bottom);
            dialog.ShowDialog();
        }
        void ExportBackup()
        {
            SavePending(); var picker = new SaveFileDialog { Title = "导出备份（密码库保持加密）", FileName = "SuperBrain-backup-" + DateTime.Today.ToString("yyyy-MM-dd") + ".json", Filter = "超强大脑备份|*.json", DefaultExt = ".json" };
            if (picker.ShowDialog(activeDialog ?? this) != true) return; store.Export(picker.FileName, vault); Notice("备份已导出");
        }
        void ImportBackup()
        {
            SavePending(); var picker = new OpenFileDialog { Title = "选择轻量版备份", Filter = "超强大脑备份|*.json" }; if (picker.ShowDialog(activeDialog ?? this) != true) return;
            var backup = JsonFile.Read<BackupDocument>(picker.FileName, JsonFile.BackupLimit); backup.Validate();
            if (MessageBox.Show(activeDialog ?? this, "用备份替换当前资料？当前资料会先备份到 data 文件夹。密码库仍需该备份原来的主密码解锁。", "恢复备份", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            store.Restore(backup, vault); config = JsonFile.Clone(store.Config); current = deleted = null; notesDirty = configDirty = false; secretTab = editing = false; ApplyTheme(); if (activeDialog != null) activeDialog.Close(); Render(); Notice("备份已恢复，密码库保持锁定");
        }
        void ShowPasswordChange()
        {
            if (!vault.Unlocked) throw new Exception("请先解锁密码库。");
            var body = new StackPanel(); var dialog = Dialog("修改主密码", body, 500); if (dialog == null) return;
            var old = Identify(new PasswordBox { MaxLength = 1024 }, "old-master", "当前主密码"); var next = Identify(new PasswordBox { MaxLength = 1024 }, "new-master", "新主密码"); var again = Identify(new PasswordBox { MaxLength = 1024 }, "repeat-master", "再次输入新主密码");
            Field(body, "当前主密码", old); Field(body, "新主密码", next); Field(body, "再次输入新主密码", again); body.Children.Add(Text("旧备份仍需要旧主密码。", 12, true));
            var error = Text("", 12); error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); body.Children.Add(error);
            var submit = Button("修改主密码", "save-master", delegate { }, true);
            submit.Click += async delegate
            {
                if (busy) return; if (next.Password != again.Password) { error.Text = "两次新主密码不一致。"; return; }
                string previous = old.Password, replacement = next.Password; submit.IsEnabled = false; busy = true;
                try { await Task.Run(delegate { vault.ChangePassword(previous, replacement); }); dialog.Close(); Notice("主密码已修改"); }
                catch (Exception e) { error.Text = e.Message; }
                finally { busy = false; submit.IsEnabled = true; old.Clear(); next.Clear(); again.Clear(); }
            };
            body.Children.Add(submit); dialog.Closed += delegate { old.Clear(); next.Clear(); again.Clear(); }; dialog.ShowDialog();
        }
    }
}
