using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

[assembly: AssemblyTitle("超强大脑")]
[assembly: AssemblyDescription("超强大脑轻量版 · 双击即用")]
[assembly: AssemblyCompany("qzm-qzm")]
[assembly: AssemblyProduct("超强大脑")]
[assembly: AssemblyVersion("0.2.1.0")]
[assembly: AssemblyFileVersion("0.2.1.0")]

namespace SuperBrain
{
    public static class Platform
    {
        public const string Version = "0.2.1";
        public const string Repository = "https://github.com/qzm-qzm/super-brain";
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        public static void RoundCorners(IntPtr window) { try { int value = 2; DwmSetWindowAttribute(window, 33, ref value, sizeof(int)); } catch (DllNotFoundException) { } }
        public static string Identity(string directory)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant()))).Replace("-", "").Substring(0, 24);
        }
        public static void Shortcut(string text, out uint modifiers, out uint key)
        {
            var validation = new Preferences { shortcut = text }; validation.Validate();
            string[] parts = text.Split('+'); modifiers = 0x4000;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                uint flag = 0; switch (parts[i].ToLowerInvariant()) { case "alt": flag = 1; break; case "ctrl": case "control": flag = 2; break; case "shift": flag = 4; break; case "win": flag = 8; break; }
                if ((modifiers & flag) != 0) throw new Exception("快捷键不能重复使用同一个修饰键。"); modifiers |= flag;
            }
            string last = parts[parts.Length - 1].ToUpperInvariant();
            if ((modifiers & 1) != 0 && last == "F4") throw new Exception("Alt+F4 是系统关闭快捷键，不能用于呼出小窗。");
            if (last.StartsWith("F") && last.Length > 1) key = (uint)(111 + Int32.Parse(last.Substring(1)));
            else if (last == "SPACE") key = 32; else key = last[0];
        }
        public static bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;
        }
        public static string CapturedShortcut(Key key, ModifierKeys modifiers)
        {
            string main;
            if (key >= Key.F1 && key <= Key.F24) main = "F" + ((int)key - (int)Key.F1 + 1);
            else if (key >= Key.A && key <= Key.Z) main = key.ToString().ToUpperInvariant();
            else if (key >= Key.D0 && key <= Key.D9) main = ((char)('0' + (int)key - (int)Key.D0)).ToString();
            else if (key == Key.Space) main = "Space";
            else throw new Exception("请选择 F1–F24，或按住 Ctrl、Alt、Shift 等再按字母、数字或空格。");
            if (modifiers == ModifierKeys.None && (key < Key.F1 || key > Key.F24)) throw new Exception("单键请使用 F1–F24；字母和数字需要配合 Ctrl、Alt 等键。");
            string shortcut = ((modifiers & ModifierKeys.Control) != 0 ? "Ctrl+" : "") +
                              ((modifiers & ModifierKeys.Alt) != 0 ? "Alt+" : "") +
                              ((modifiers & ModifierKeys.Shift) != 0 ? "Shift+" : "") +
                              ((modifiers & ModifierKeys.Windows) != 0 ? "Win+" : "") + main;
            uint parsedModifiers, parsedKey;
            Shortcut(shortcut, out parsedModifiers, out parsedKey);
            return shortcut;
        }
        public static void Open(string target) { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--data-dir") directory = Path.GetFullPath(args[++i]);
            bool acquired;
            using (var mutex = new Mutex(true, "Local\\SuperBrainLite-" + Platform.Identity(directory), out acquired))
            {
                uint showMessage = Platform.RegisterWindowMessage("SuperBrainLite.Show." + Platform.Identity(directory));
                if (!acquired) { Platform.PostMessage(new IntPtr(0xffff), showMessage, IntPtr.Zero, IntPtr.Zero); return 0; }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    var window = new BrainWindow(directory, showMessage); app.Run(window); return 0;
                }
                catch (Exception e) { MessageBox.Show("无法打开超强大脑：\n" + e.Message + "\n\n请把文件放在可写的文件夹中，例如 D:\\super-brain。已有资料不会自动清空。", "超强大脑", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
