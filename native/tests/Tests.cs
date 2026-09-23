using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace SuperBrain
{
    public static class Tests
    {
        const string Password = "TEST ONLY 主密码 phrase 2026";
        const string Secret = "TEST SECRET never plaintext!";
        static string root;
        static int passed;
        static Application app;
        static BrainWindow window;
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
        static int HitTest(FrameworkElement element)
        {
            Point screen = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            int packed = unchecked((((int)screen.Y & 0xffff) << 16) | ((int)screen.X & 0xffff));
            return SendMessage(new WindowInteropHelper(window).Handle, 0x84, IntPtr.Zero, new IntPtr(packed)).ToInt32();
        }
        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Throws(Action action, string message) { bool rejected = false; try { action(); } catch { rejected = true; } Assert(rejected, message); }
        static string DirectoryFor(string name) { string directory = Path.Combine(root, name); Directory.CreateDirectory(directory); return directory; }
        static Record Account(string id = "demo") { return new Record { id = id, title = "Private account", username = "test@example.invalid", password = Secret, body = "Private note" }; }
        static void Check(string title, Action run) { run(); passed++; Console.WriteLine("PASS " + title); }
        [STAThread]
        public static int Main(string[] args)
        {
            root = Path.Combine(args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-results"), "native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                CoreTests(); UiTests();
                Console.WriteLine("PASS " + passed + " checks; screenshot=" + Path.Combine(root, "preview.png"));
                File.WriteAllText(Path.Combine(root, "result.json"), JsonFile.Encode(new { passed = passed, success = true, workingSetMiB = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024 })); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); File.WriteAllText(Path.Combine(root, "failure.txt"), e.ToString()); return 1; }
            finally { if (window != null) { try { Call(window, "Quit"); } catch { window.Close(); } } if (app != null) app.Shutdown(); }
        }
        static void CoreTests()
        {
            Check("current-user startup registration quotes paths, disables and rolls back safely", delegate
            {
                string keyPath = @"Software\SuperBrainLite.Tests\" + Guid.NewGuid().ToString("N");
                string executable = Path.Combine(root, "space and 中文", "SuperBrain.exe");
                try
                {
                    Assert(!StartupRegistration.IsEnabled(executable, keyPath), "startup enabled without registration");
                    StartupRegistration.SetEnabled(true, executable, keyPath);
                    Assert(StartupRegistration.ReadCommand(keyPath) == "\"" + executable + "\" --background", "unsafe startup command");
                    Assert(StartupRegistration.IsEnabled(executable, keyPath), "startup not enabled");
                    StartupRegistration.SetEnabled(false, Path.Combine(root, "other.exe"), keyPath);
                    Assert(StartupRegistration.IsEnabled(executable, keyPath), "another copy removed registration");
                    StartupRegistration.SetEnabled(false, executable, keyPath);
                    Assert(!StartupRegistration.IsEnabled(executable, keyPath), "startup not disabled");
                    StartupRegistration.SetEnabled(true, executable, keyPath);
                    string previous = StartupRegistration.ReadCommand(keyPath), newer = Path.Combine(root, "newer.exe");
                    StartupRegistration.SetEnabled(true, newer, keyPath);
                    StartupRegistration.RestoreCommand(previous, StartupRegistration.Command(newer), keyPath);
                    Assert(StartupRegistration.IsEnabled(executable, keyPath), "rollback lost original startup path");
                }
                finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(keyPath, false); }
            });
            Check("native PBKDF2 matches .NET reference including Unicode", delegate
            {
                var salt = Encoding.UTF8.GetBytes("known-test-salt-32-byte-long-2026!"); byte[] expected;
                using (var reference = new Rfc2898DeriveBytes(Password, salt, 1000, HashAlgorithmName.SHA256)) expected = reference.GetBytes(64);
                Assert(expected.SequenceEqual(Crypto.Derive(Password, salt, 1000)), "KDF mismatch");
            });
            Check("ordinary notes/config persist beside executable, atomic backup retained", delegate
            {
                string dir = DirectoryFor("notes"); var store = new LocalStore(dir); store.SaveConfig(new Preferences { shortcut = "Ctrl+Alt+F8", background = "#20242C", imageTransparency = 37 }); store.Notes.Add(new Record { title = "普通记录", body = "Last accepted note" }); store.SaveNotes(); store.Notes[0].body = "Changed"; store.SaveNotes();
                var reopened = new LocalStore(dir); Assert(reopened.Notes[0].body == "Changed" && reopened.Config.imageTransparency == 37, "restart mismatch"); Assert(File.Exists(Path.Combine(dir, "notes.json.bak")), "missing backup");
                Assert(JsonFile.Read<NoteDocument>(Path.Combine(dir, "notes.json.bak")).notes[0].body == "Last accepted note", "wrong backup");
            });
            Check("corrupt notes are not overwritten; malformed prefs rejected", delegate
            {
                string dir = DirectoryFor("corrupt"); File.WriteAllText(Path.Combine(dir, "notes.json"), "bad JSON"); Throws(delegate { new LocalStore(dir); }, "corrupt data accepted"); Assert(File.ReadAllText(Path.Combine(dir, "notes.json")) == "bad JSON", "corrupt file replaced");
                Throws(delegate { new Preferences { imageTransparency = 101 }.Validate(); }, "invalid transparency"); Throws(delegate { new Preferences { shortcut = "Q" }.Validate(); }, "invalid hotkey");
                var oldConfig = JsonFile.Decode<Preferences>("{\"shortcut\":\"F8\"}"); oldConfig.Validate(); Assert(oldConfig.windowWidth == 424 && oldConfig.windowHeight == 634, "old configuration lost default window size");
                Throws(delegate { new Preferences { windowWidth = 100 }.Validate(); }, "invalid window size accepted");
                uint a, b; Throws(delegate { Platform.Shortcut("Ctrl+Ctrl+Q", out a, out b); }, "duplicate modifier");
                Assert(Platform.CapturedShortcut(Key.F8, ModifierKeys.None) == "F8", "single function key capture");
                Assert(Platform.CapturedShortcut(Key.F24, ModifierKeys.None) == "F24", "highest function key capture");
                Assert(Platform.CapturedShortcut(Key.Q, ModifierKeys.Control | ModifierKeys.Alt) == "Ctrl+Alt+Q", "modifier capture");
                Assert(Platform.CapturedShortcut(Key.D3, ModifierKeys.Control) == "Ctrl+3", "digit capture");
                Throws(delegate { Platform.CapturedShortcut(Key.Q, ModifierKeys.None); }, "bare letter captured");
                Throws(delegate { Platform.CapturedShortcut(Key.F, ModifierKeys.None); }, "bare F captured");
                Throws(delegate { Platform.CapturedShortcut(Key.Enter, ModifierKeys.Control); }, "unsupported key captured");
                Throws(delegate { Platform.CapturedShortcut(Key.F4, ModifierKeys.Alt); }, "system close key captured");
            });
            Check("vault encrypts all metadata; restart, wrong password and immediate lock", delegate
            {
                string dir = DirectoryFor("vault"); using (var vault = new Vault(dir))
                {
                    Throws(delegate { vault.Setup(""); }, "empty password accepted"); vault.Setup(Password); vault.Save(Account()); var text = File.ReadAllText(vault.FileName);
                    foreach (var value in new[] { Password, Secret, "test@example.invalid", "Private account", "Private note" }) Assert(!text.Contains(value), "plaintext leaked");
                    var next = Account(); next.body = "Last edit before lock"; vault.Save(next); vault.Lock(); Throws(delegate { vault.Save(Account()); }, "post-lock save accepted"); Throws(delegate { vault.List(); }, "post-lock list accepted");
                }
                using (var restarted = new Vault(dir)) { Throws(delegate { restarted.Unlock("incorrect password"); }, "wrong password accepted"); restarted.Unlock(Password); Assert(restarted.List()[0].body == "Last edit before lock", "lost edit"); }
            });
            Check("short master password survives restart; empty replacements do not change the vault", delegate
            {
                string dir = DirectoryFor("short-master");
                using (var vault = new Vault(dir))
                {
                    Throws(delegate { vault.Setup(null); }, "null password accepted");
                    Throws(delegate { vault.Setup(""); }, "empty password accepted");
                    Throws(delegate { vault.Setup(new string('x', 1025)); }, "oversized password accepted");
                    Assert(!File.Exists(vault.FileName), "invalid setup wrote a vault");
                    vault.Setup("7"); vault.Save(Account()); string original = File.ReadAllText(vault.FileName);
                    Throws(delegate { vault.ChangePassword("7", ""); }, "empty replacement accepted");
                    Assert(File.ReadAllText(vault.FileName) == original && vault.List()[0].password == Secret, "rejected replacement changed data");
                    Assert(!original.Contains(Secret), "short master password disabled encryption");
                }
                using (var reopened = new Vault(dir)) { reopened.Unlock("7"); Assert(reopened.List()[0].password == Secret, "short password could not reopen saved vault"); }
            });
            Check("MAC authenticates ciphertext, IV, salt and bounded KDF parameters", delegate
            {
                using (var vault = new Vault(DirectoryFor("tamper")))
                {
                    vault.Setup(Password); vault.Save(Account()); var record = vault.Backup(); var key = Crypto.Derive(Password, Convert.FromBase64String(record.salt));
                    foreach (var field in new[] { "ciphertext", "iv", "salt", "mac" }) { var bad = JsonFile.Clone(record); var property = typeof(VaultEnvelope).GetProperty(field); var bytes = Convert.FromBase64String((string)property.GetValue(bad)); bytes[0] ^= 1; property.SetValue(bad, Convert.ToBase64String(bytes)); Throws(delegate { Crypto.Decrypt(bad, key); }, "tampering accepted: " + field); }
                    var unreasonable = JsonFile.Clone(record); unreasonable.iterations = Int32.MaxValue; Throws(unreasonable.Validate, "unbounded KDF accepted"); Array.Clear(key, 0, key.Length);
                }
            });
            Check("password rotation and stale unlock cancellation", delegate
            {
                using (var vault = new Vault(DirectoryFor("rotation")))
                {
                    vault.Setup(Password); vault.Save(Account()); const string next = "短码"; vault.ChangePassword(Password, next); vault.Lock(); Throws(delegate { vault.Unlock(Password); }, "old password accepted"); vault.Unlock(next); Assert(vault.List()[0].password == Secret, "rotation lost data"); vault.Lock();
                    var pending = Task.Run(delegate { vault.Unlock(next); }); var timer = Stopwatch.StartNew();
                    var busyField = typeof(Vault).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic);
                    while (!(bool)busyField.GetValue(vault) && timer.ElapsedMilliseconds < 1000) Thread.Sleep(1);
                    vault.Lock(); Throws(delegate { pending.GetAwaiter().GetResult(); }, "late unlock was not cancelled"); Assert(!vault.Unlocked, "late unlock reopened vault");
                }
            });
            Check("backup/restore roundtrip; invalid backup cannot change current data", delegate
            {
                string dir = DirectoryFor("backup"); var store = new LocalStore(dir); using (var vault = new Vault(dir))
                {
                    store.SaveConfig(new Preferences { shortcut = "F9", imageTransparency = 28 }); store.Notes.Add(new Record { title = "Before" }); store.SaveNotes(); vault.Setup(Password); vault.Save(Account());
                    string file = Path.Combine(root, "backup.json"); store.Export(file, vault); Assert(!File.ReadAllText(file).Contains(Secret), "plaintext backup");
                    var backup = JsonFile.Read<BackupDocument>(file, JsonFile.BackupLimit); store.Notes[0].title = "After"; store.SaveNotes(); var account = Account(); account.password = "changed"; vault.Save(account);
                    store.Restore(backup, vault); Assert(!vault.Unlocked && store.Notes[0].title == "Before", "restore state mismatch"); vault.Unlock(Password); Assert(vault.List()[0].password == Secret, "restore password mismatch"); Assert(Directory.GetFiles(dir, "before-restore-*.json").Length == 1, "missing pre-restore backup");
                    backup.version = 999; Throws(delegate { store.Restore(backup, vault); }, "invalid restore accepted"); Assert(store.Notes[0].title == "Before", "invalid restore changed data");
                }
            });
            Check("combined backup can exceed per-file cap", delegate
            {
                string dir = DirectoryFor("large"); var store = new LocalStore(dir); using (var vault = new Vault(dir))
                {
                    for (int i = 0; i < 88; i++) store.Notes.Add(new Record { body = new string('n', 100000) }); store.SaveNotes();
                    var records = Enumerable.Range(0, 65).Select(i => new Record { body = new string('v', 100000) }).ToList(); var salt = Crypto.Random(32); var key = Crypto.Derive(Password, salt); vault.Restore(Crypto.Encrypt(records, key, salt)); Array.Clear(key, 0, key.Length);
                    string file = Path.Combine(dir, "large-backup.json"); store.Export(file, vault); Assert(new FileInfo(file).Length > JsonFile.Limit, "fixture did not exceed cap"); Assert(JsonFile.Read<BackupDocument>(file, JsonFile.BackupLimit).data.notes.Count == 88, "large backup unreadable");
                }
                // Only this test's freshly-created large fixtures are discarded.
                Assert(Path.GetDirectoryName(dir) == root, "cleanup path escaped test root"); Directory.Delete(dir, true);
            });
        }
        static void UiTests()
        {
            string uiPassword = "短码";
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); string directory = DirectoryFor("ui"); var setup = new LocalStore(directory); setup.SaveConfig(new Preferences { shortcut = "Ctrl+Alt+Shift+F24" });
            uint showMessage = Platform.RegisterWindowMessage("SuperBrainLite.Test." + Guid.NewGuid().ToString("N"));
            window = new BrainWindow(directory, showMessage) { ShowActivated = false }; window.Show(); Pump();
            Check("native title drag area and window position survive restart", delegate
            {
                Assert(WindowChrome.GetWindowChrome(window).CaptionHeight >= 44 && WindowChrome.GetWindowChrome(window).CaptionHeight <= 56, "window title cannot be dragged");
                Assert(WindowChrome.GetIsHitTestVisibleInChrome(Find<StackPanel>("window-actions")), "title buttons blocked by drag area");
                Assert(HitTest(Find<StackPanel>("window-drag-region")) == 2, "title does not hit test as draggable caption");
                Assert(HitTest(Find<Button>("appearance")) == 1, "title action cannot be clicked");
                Assert(HitTest(Find<TextBox>("search")) == 1, "search field is intercepted by window dragging");
                window.Left += 45; window.Top += 30; window.Width = 500; window.Height = 680; Pump(); Call(window, "SavePending");
                var position = new LocalStore(directory).Config;
                Assert(Math.Abs(position.windowLeft.Value - window.Left) < 2 && Math.Abs(position.windowTop.Value - window.Top) < 2, "window position was not saved");
                Assert(Math.Abs(position.windowWidth - window.ActualWidth) < 2 && Math.Abs(position.windowHeight - window.ActualHeight) < 2, "window size was not saved");
            });
            Check("real WPF note edit, native close flush and reveal message", delegate
            {
                Click("new-item"); Find<TextBox>("edit-title").Text = "随时记下灵感"; Find<TextBox>("edit-body").Text = "按 F8 呼出小窗，写完就收起来。";
                window.Close(); Pump(); Assert(!window.IsVisible, "close did not hide"); Assert(new LocalStore(directory).Notes[0].body.StartsWith("按 F8"), "native close lost final note edit");
                Platform.PostMessage(new WindowInteropHelper(window).Handle, showMessage, IntPtr.Zero, IntPtr.Zero); Wait(delegate { return window.IsVisible; }); Click("back");
            });
            Check("global shortcut registered and WM_HOTKEY toggles window", delegate
            {
                uint modifiers, key; Platform.Shortcut("Ctrl+Alt+Shift+F24", out modifiers, out key); Assert(!Platform.RegisterHotKey(new WindowInteropHelper(window).Handle, 99, modifiers, key), "hotkey not registered");
                int id = (int)typeof(BrainWindow).GetField("hotkeyId", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window); window.Activate(); Pump(); Platform.PostMessage(new WindowInteropHelper(window).Handle, 0x312, new IntPtr(id), IntPtr.Zero); Wait(delegate { return !window.IsVisible; }); Platform.PostMessage(new WindowInteropHelper(window).Handle, 0x312, new IntPtr(id), IntPtr.Zero); Wait(delegate { return window.IsVisible; });
            });
            Check("real password form, edit, hide lock, unlock and secret persistence", delegate
            {
                Click("vault-tab"); Find<PasswordBox>("master-password").Password = uiPassword; Find<PasswordBox>("confirm-password").Password = uiPassword; Find<CheckBox>("master-acknowledge").IsChecked = true; Click("unlock"); Wait(delegate { return Find<Button>("new-item", false) != null && Find<Button>("new-item").IsEnabled; });
                Click("new-item"); Find<TextBox>("edit-title").Text = "TEST account"; Find<PasswordBox>("edit-password").Password = Secret; Find<TextBox>("edit-body").Text = "Last secret edit"; Click("hide-window"); Assert(!window.IsVisible, "not hidden"); Assert(Find<PasswordBox>("edit-password", false) == null, "secret DOM survived lock");
                Platform.PostMessage(new WindowInteropHelper(window).Handle, showMessage, IntPtr.Zero, IntPtr.Zero); Wait(delegate { return window.IsVisible; }); Find<PasswordBox>("master-password").Password = uiPassword; Click("unlock"); Wait(delegate { return Find<Button>("new-item", false) != null && Find<Button>("new-item").IsEnabled; });
                var record = Descendants(window).OfType<Button>().First(b => AutomationProperties.GetAutomationId(b).StartsWith("record-")); record.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); Assert(Find<PasswordBox>("edit-password").Password == Secret && Find<TextBox>("edit-body").Text == "Last secret edit", "UI secret mismatch"); Click("back"); Click("notes-tab");
            });
            Check("real password-change dialog accepts a one-character master password", delegate
            {
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                {
                    Window dialog = app.Windows.Cast<Window>().First(w => w != window);
                    DialogControl<PasswordBox>(dialog, "old-master").Password = uiPassword;
                    DialogControl<PasswordBox>(dialog, "new-master").Password = "7";
                    DialogControl<PasswordBox>(dialog, "repeat-master").Password = "7";
                    DialogControl<Button>(dialog, "save-master").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                Call(window, "ShowPasswordChange"); uiPassword = "7";
                using (var reopened = new Vault(directory)) { reopened.Unlock(uiPassword); Assert(reopened.List()[0].password == Secret, "password change dialog lost data or rejected short password"); }
            });
            Check("pressing a key captures and registers shortcut without typing text", delegate
            {
                var open = Find<Button>("settings");
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                {
                    Window dialog = app.Windows.Cast<Window>().First(w => w != window);
                    var field = DialogControl<TextBox>(dialog, "setting-shortcut");
                    Assert(field.IsReadOnly, "shortcut still accepts typed text");
                    field.Focus(); Pump();
                    Assert((string)typeof(BrainWindow).GetField("activeShortcut", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) == null, "previous key kept intercepting capture");
                    var key = Press(field, Key.F9);
                    Assert(key.Handled && field.Text == "F9", "F9 was not captured");
                    var preview = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    preview.Render(dialog); var screenshot = new PngBitmapEncoder(); screenshot.Frames.Add(BitmapFrame.Create(preview));
                    using (var stream = File.Create(Path.Combine(root, "hotkey-preview.png"))) screenshot.Save(stream);
                    DialogControl<Button>(dialog, "save-settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Assert(new LocalStore(directory).Config.shortcut == "F9", "captured key was not saved");
                uint mods, keyCode; Platform.Shortcut("F9", out mods, out keyCode);
                Assert(!Platform.RegisterHotKey(new WindowInteropHelper(window).Handle, 99, mods, keyCode), "captured key is not active");
            });
            Check("occupied shortcut is rejected; old key is restored on cancel", delegate
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                uint mods, keyCode; Platform.Shortcut("F10", out mods, out keyCode);
                Assert(Platform.RegisterHotKey(handle, 99, mods, keyCode), "test key already occupied");
                try
                {
                    var open = Find<Button>("settings");
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                    {
                        Window dialog = app.Windows.Cast<Window>().First(w => w != window);
                        var field = DialogControl<TextBox>(dialog, "setting-shortcut"); field.Focus(); Pump(); Press(field, Key.F10);
                        Assert(field.Text == "F10", "occupied key did not display");
                        DialogControl<Button>(dialog, "save-settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                        Assert(DialogControl<TextBlock>(dialog, "settings-error").Text.Contains("已被占用"), "missing conflict explanation");
                        dialog.Close();
                    });
                    open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    Assert(new LocalStore(directory).Config.shortcut == "F9", "conflict changed saved shortcut");
                    Platform.Shortcut("F9", out mods, out keyCode);
                    Assert(!Platform.RegisterHotKey(handle, 100, mods, keyCode), "previous shortcut was not restored");
                }
                finally { Platform.UnregisterHotKey(handle, 99); }
            });
            Check("appearance changes persist and transparency is independent", delegate
            {
                var imageConfig = (Preferences)typeof(BrainWindow).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255, 255, 255, 255, 255, 20, 70, 220, 255, 210, 90, 20, 255 }, 8)));
                using (var bytes = new MemoryStream()) { png.Save(bytes); imageConfig.image = "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray()); }
                Call(window, "AppearanceChanged");
                var openAppearance = Find<Button>("appearance");
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
                {
                    Window dialog = app.Windows.Cast<Window>().First(w => w != window); var input = Descendants(dialog).OfType<TextBox>().First(t => AutomationProperties.GetAutomationId(t) == "background-color"); input.Text = "#20242C";
                    var slider = Descendants(dialog).OfType<Slider>().Single(); slider.Value = 37;
                    Descendants(dialog).OfType<Button>().First(b => AutomationProperties.GetAutomationId(b) == "appearance-done").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                openAppearance.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); var saved = new LocalStore(directory); Assert(saved.Config.image.StartsWith("data:image/png;base64,") && Descendants(window).OfType<Image>().Any(i => i.Source != null && Math.Abs(i.Opacity - .63) < .001), "background image opacity mismatch");
                Assert(saved.Config.background == "#20242C" && saved.Config.imageTransparency == 37 && saved.Config.shortcut == "F9", "appearance not persisted");
                var panel = ((SolidColorBrush)window.Resources["Panel"]).Color;
                var card = ((SolidColorBrush)Find<Button>("settings").Background).Color;
                Assert(panel.A >= 220 && card.A == 255, "wallpaper makes controls transparent");
                var photo = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                photo.Render(window); var photoPng = new PngBitmapEncoder(); photoPng.Frames.Add(BitmapFrame.Create(photo));
                using (var stream = File.Create(Path.Combine(root, "wallpaper-preview.png"))) photoPng.Save(stream);
                imageConfig.imageTransparency = 0; Call(window, "AppearanceChanged"); Pump();
                Assert(Math.Abs(Descendants(window).OfType<Image>().First(i => i.Source != null).Opacity - 1) < .001, "fully visible wallpaper setting failed");
                var fullPhoto = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                fullPhoto.Render(window); var fullPhotoPng = new PngBitmapEncoder(); fullPhotoPng.Frames.Add(BitmapFrame.Create(fullPhoto));
                using (var stream = File.Create(Path.Combine(root, "wallpaper-full-preview.png"))) fullPhotoPng.Save(stream);
                imageConfig.imageTransparency = 37; Call(window, "AppearanceChanged"); Call(window, "SavePending");
            });
            Check("UI restart reads same profile and starts vault locked", delegate
            {
                double left = window.Left, top = window.Top, width = window.Width, height = window.Height;
                Call(window, "Quit"); window = new BrainWindow(directory, showMessage) { ShowActivated = false }; window.Show(); Pump();
                Assert(Math.Abs(window.Left - left) < 2 && Math.Abs(window.Top - top) < 2 && Math.Abs(window.Width - width) < 2 && Math.Abs(window.Height - height) < 2, "window placement did not restore");
                Click("vault-tab"); Assert(Find<PasswordBox>("master-password", false) != null, "restart did not lock"); Click("notes-tab");
            });
            Check("locking the vault clears the persistent header search", delegate
            {
                Click("vault-tab"); Find<PasswordBox>("master-password").Password = uiPassword; Click("unlock"); Wait(delegate { return Find<Button>("new-item").IsEnabled; });
                Find<TextBox>("search").Text = "TEST account"; Click("lock-vault");
                Assert(Find<TextBox>("search").Text == "" && !Find<TextBox>("search").IsEnabled, "locked header leaked a private search");
                Click("notes-tab");
            });
            Check("compact layout, favorite filtering, search and new-record reset", delegate
            {
                window.Width = 400; window.Height = 560; Pump();
                var search = Find<TextBox>("search"); var newButton = Find<Button>("new-item");
                Assert(search.ActualWidth >= 100 && HitTest(search) == 1, "compact header search is unusable");
                Assert(newButton.TranslatePoint(new Point(newButton.ActualWidth, 0), window).X <= window.ActualWidth, "new button clipped at minimum width");
                var savedRecord = new LocalStore(directory).Notes[0];
                Click("favorite-" + savedRecord.id); Click("favorites");
                Assert(new LocalStore(directory).Notes[0].pinned, "favorite did not persist");
                Assert(Find<Button>("record-" + savedRecord.id, false) != null, "favorite filter lost record");
                Find<TextBox>("search").Text = "no such synthetic record";
                Assert(!Descendants(window).OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b).StartsWith("record-")), "search did not filter list");
                Click("new-item"); Find<TextBox>("edit-title").Text = "新建后仍可找到"; Click("back");
                Assert(Find<TextBox>("search").Text == "", "new item kept a stale search filter");
                Assert(Descendants(window).OfType<Button>().Count(b => AutomationProperties.GetAutomationId(b).StartsWith("record-")) == 2, "new item kept favorite filter");
                window.Width = 424; window.Height = 634; Pump();
            });
            // Produce a clean preview with synthetic records only.
            var preferences = JsonFile.Clone(new LocalStore(directory).Config); preferences.image = ""; preferences.background = "#F8F9FB"; preferences.accent = "#1665D8"; preferences.shortcut = "F8";
            typeof(BrainWindow).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, preferences); Call(window, "ApplyTheme");
            var previewStore = (LocalStore)typeof(BrainWindow).GetField("store", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window); previewStore.Notes.Clear();
            var samples = new[] {
                new Record { title = "这周要做的事", body = "整理一下桌面，把常用文件归个类。\n周五之前，记得把项目资料备份到移动硬盘。", pinned = true },
                new Record { title = "常用地址", body = "项目仓库：github.com/qzm-qzm/super-brain\n有新的想法，随时记在这里。", pinned = true },
                new Record { title = "周末采购清单", body = "咖啡豆、牛奶、鸡蛋\n还有一盆适合放在桌上的绿植。" },
                new Record { title = "突然想到的一个小点子", body = "把零碎的想法先记下来。\n不用急着整理，等有空再慢慢展开。" },
                new Record { title = "下次出门别忘了", body = "钥匙、耳机、充电宝，出门前再看一眼天气。" },
                new Record { title = "值得再读的书", body = "《设计心理学》\n从每天遇到的小问题里，找到好的设计。" }
            };
            for (int i = 0; i < samples.Length; i++) { samples[i].updatedAt = DateTime.UtcNow.AddMinutes(-i * 45).ToString("o"); previewStore.Notes.Add(samples[i]); } previewStore.SaveNotes(); Call(window, "Render"); Find<TextBox>("search").Focus();
            Pump(); var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using (var stream = File.Create(Path.Combine(root, "preview.png"))) encoder.Save(stream);
            Call(window, "Quit"); window = null;
            Check("hosted hide flushes final secret, closes window and releases its shortcut", delegate
            {
                window = new BrainWindow(directory, showMessage, true) { ShowActivated = false }; window.Show(); Pump();
                var unlocked = (Vault)typeof(BrainWindow).GetField("vault", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                Assert(!( (DispatcherTimer)typeof(BrainWindow).GetField("idleTimer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window)).IsEnabled, "locked window keeps polling");
                Click("vault-tab"); Find<PasswordBox>("master-password").Password = uiPassword; Click("unlock"); Wait(delegate { return Find<Button>("new-item").IsEnabled; });
                Click("new-item"); Find<TextBox>("edit-title").Text = "Hosted final secret"; Find<PasswordBox>("edit-password").Password = Secret; Find<TextBox>("edit-body").Text = "Accepted before process exit";
                Click("hide-window");
                Assert(!app.Windows.Cast<Window>().Contains(window) && !unlocked.Unlocked, "hosted window or unlocked vault survived hide");
                uint mods, key; Platform.Shortcut(new LocalStore(directory).Config.shortcut, out mods, out key);
                bool released = Platform.RegisterHotKey(IntPtr.Zero, 991, mods, key);
                if (released) Platform.UnregisterHotKey(IntPtr.Zero, 991);
                Assert(released, "hosted window retained the global shortcut after closing");
                using (var saved = new Vault(directory)) { saved.Unlock(uiPassword); Assert(saved.List().Any(r => r.title == "Hosted final secret" && r.password == Secret && r.body == "Accepted before process exit"), "hosted close lost accepted secret"); }
                window = null;
            });
        }
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent) { yield return parent; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child; }
        static T Find<T>(string id, bool required = true) where T : FrameworkElement { Pump(); var found = Descendants(window).OfType<T>().FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == id); if (required && found == null) throw new Exception("Missing UI element: " + id); return found; }
        static T DialogControl<T>(Window dialog, string id) where T : FrameworkElement
        {
            var found = Descendants(dialog).OfType<T>().FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == id);
            if (found == null) throw new Exception("Missing dialog control: " + id);
            return found;
        }
        static KeyEventArgs Press(TextBox input, Key key)
        {
            var source = PresentationSource.FromVisual(input);
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            input.RaiseEvent(args); Pump(); return args;
        }
        static void Click(string id) { Find<Button>(id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        static void Call(object target, string name) { typeof(BrainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null).Invoke(target, null); Pump(); }
        static void Pump() { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate { frame.Continue = false; }); Dispatcher.PushFrame(frame); }
        static void Wait(Func<bool> predicate) { var timer = Stopwatch.StartNew(); while (!predicate()) { Pump(); Thread.Sleep(10); if (timer.ElapsedMilliseconds > 15000) throw new Exception("UI wait timed out"); } Pump(); }
    }
}
