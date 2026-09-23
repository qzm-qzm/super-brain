using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Forms = System.Windows.Forms;

namespace SuperBrain
{
    // The idle process loads WinForms for its message loop/tray, never WPF or note/vault data.
    // The same executable runs a short-lived WPF child only while the user needs the window.
    public sealed class BackgroundHost : Forms.ApplicationContext
    {
        const int HotkeyId = 61;
        const uint ChildExited = 0x8001;
        readonly string directory;
        readonly uint showMessage, exitHostMessage, closeWindowMessage, lockMessage, readyMessage;
        readonly Listener listener;
        readonly Forms.NotifyIcon tray;
        Process child;
        bool quitting, registered, disposed;
        string shortcut = "F8";

        public BackgroundHost(string directory, uint showMessage, bool openImmediately)
        {
            this.directory = directory; this.showMessage = showMessage;
            exitHostMessage = Platform.RegisterWindowMessage("SuperBrainLite.ExitHost." + Platform.Identity(directory));
            closeWindowMessage = Platform.RegisterWindowMessage("SuperBrainLite.CloseWindow." + Platform.Identity(directory));
            lockMessage = Platform.RegisterWindowMessage("SuperBrainLite.Lock." + Platform.Identity(directory));
            readyMessage = Platform.RegisterWindowMessage("SuperBrainLite.Ready." + Platform.Identity(directory));
            listener = new Listener(HandleMessage, "SuperBrain.Background." + Platform.Identity(directory));
            tray = new Forms.NotifyIcon { Text = "超强大脑 · 后台待命" };
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SuperBrain.icon.ico"))
            using (var icon = new System.Drawing.Icon(stream)) tray.Icon = (System.Drawing.Icon)icon.Clone();
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开超强大脑", null, delegate { OpenWindow(); });
            menu.Items.Add("锁定密码库", null, delegate { Platform.PostMessage(new IntPtr(0xffff), lockMessage, IntPtr.Zero, IntPtr.Zero); });
            menu.Items.Add("退出", null, delegate { RequestQuit(); });
            tray.ContextMenuStrip = menu; tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button == Forms.MouseButtons.Left) OpenWindow(); };
            tray.Visible = true; RegisterShortcut(); if (openImmediately) OpenWindow();
        }
        void Report(string message) { tray.ShowBalloonTip(6000, "超强大脑", message, Forms.ToolTipIcon.Warning); }
        void RegisterShortcut()
        {
            if (quitting || disposed) return;
            try
            {
                // Read only preferences. Do not load notes, decrypt the vault, or decode the wallpaper.
                var preferences = JsonFile.Read<Preferences>(Path.Combine(directory, "config.json")) ?? new Preferences();
                preferences.Validate(); shortcut = preferences.shortcut;
                uint modifiers, key; Platform.Shortcut(shortcut, out modifiers, out key);
                registered = Platform.RegisterHotKey(listener.Handle, HotkeyId, modifiers, key);
                tray.Text = "超强大脑 · " + shortcut + " 呼出";
                if (!registered) Report("快捷键 " + shortcut + " 已被占用。单击托盘图标打开，再到设置中更换快捷键。");
            }
            catch (Exception e) { Report("后台启动遇到问题：" + e.Message + " 可单击托盘图标打开工具。"); }
        }
        void ReleaseShortcut() { if (registered) { Platform.UnregisterHotKey(listener.Handle, HotkeyId); registered = false; } }
        void OpenWindow()
        {
            if (quitting || disposed) return;
            if (child != null)
            {
                if (!child.HasExited) { Platform.PostMessage(new IntPtr(0xffff), showMessage, new IntPtr(1), IntPtr.Zero); return; }
                FinishChild();
            }
            ReleaseShortcut();
            try
            {
                string executable = StartupRegistration.Executable;
                string quotedDirectory = "\"" + directory + (directory.EndsWith("\\") ? "\\" : "") + "\"";
                var next = new Process { StartInfo = new ProcessStartInfo(executable, "--hosted-window --data-dir " + quotedDirectory) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable) }, EnableRaisingEvents = true };
                next.Exited += delegate { if (!disposed) Platform.PostMessage(listener.Handle, ChildExited, IntPtr.Zero, IntPtr.Zero); };
                child = next;
                if (!child.Start()) throw new Exception("窗口进程未启动。");
                tray.Text = "超强大脑 · 窗口已打开";
            }
            catch (Exception e) { if (child != null) child.Dispose(); child = null; RegisterShortcut(); Report("无法打开窗口：" + e.Message); }
        }
        void FinishChild()
        {
            if (child == null || !child.HasExited) return;
            child.Dispose(); child = null;
            if (quitting) ExitThread(); else RegisterShortcut();
        }
        void RequestQuit()
        {
            if (quitting) return; quitting = true; ReleaseShortcut();
            if (child == null || child.HasExited) { ExitThread(); return; }
            // The window flushes accepted edits and locks the vault before it exits.
            Platform.PostMessage(new IntPtr(0xffff), closeWindowMessage, IntPtr.Zero, IntPtr.Zero);
        }
        void HandleMessage(ref Forms.Message message)
        {
            if (message.Msg == 0x312 && message.WParam.ToInt32() == HotkeyId) OpenWindow();
            else if ((uint)message.Msg == showMessage && message.WParam == IntPtr.Zero) OpenWindow();
            else if ((uint)message.Msg == ChildExited) FinishChild();
            else if ((uint)message.Msg == exitHostMessage) RequestQuit();
            // A tray quit can arrive before the child's HWND exists. Repeat once it is ready.
            else if ((uint)message.Msg == readyMessage && quitting && child != null) Platform.PostMessage(new IntPtr(0xffff), closeWindowMessage, IntPtr.Zero, IntPtr.Zero);
        }
        protected override void Dispose(bool disposing)
        {
            if (!disposed)
            {
                disposed = true; ReleaseShortcut(); tray.Visible = false;
                if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose();
                if (tray.Icon != null) tray.Icon.Dispose(); tray.Dispose(); listener.DestroyHandle();
                if (child != null) child.Dispose();
            }
            base.Dispose(disposing);
        }
        delegate void MessageHandler(ref Forms.Message message);
        sealed class Listener : Forms.NativeWindow
        {
            readonly MessageHandler receive;
            public Listener(MessageHandler receive, string name) { this.receive = receive; CreateHandle(new Forms.CreateParams { Caption = name, ExStyle = 0x80 }); }
            protected override void WndProc(ref Forms.Message message) { receive(ref message); base.WndProc(ref message); }
        }
    }
}
