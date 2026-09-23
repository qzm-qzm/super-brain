using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SuperBrain
{
    public static class StartupRegistration
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "SuperBrainLite";
        public static string Executable { get { return Process.GetCurrentProcess().MainModule.FileName; } }
        public static string Command(string executable)
        {
            string path = Path.GetFullPath(executable);
            if (path.IndexOf('"') >= 0) throw new Exception("运行文件路径不能包含引号。");
            string command = "\"" + path + "\" --background";
            if (command.Length > 260) throw new Exception("文件路径过长，请将工具放到 D:\\super-brain 这样的较短路径。");
            return command;
        }
        public static bool IsEnabled(string executable, string keyPath = RunKey)
        {
            return String.Equals(ReadCommand(keyPath), Command(executable), StringComparison.OrdinalIgnoreCase);
        }
        public static string ReadCommand(string keyPath = RunKey)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, false)) return key == null ? null : key.GetValue(ValueName) as string;
        }
        public static void RestoreCommand(string previous, string expected, string keyPath = RunKey)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                if (!String.Equals(key.GetValue(ValueName) as string, expected, StringComparison.OrdinalIgnoreCase)) return;
                if (previous == null) key.DeleteValue(ValueName, false); else key.SetValue(ValueName, previous, RegistryValueKind.String);
            }
        }
        public static void SetEnabled(bool enabled, string executable, string keyPath = RunKey)
        {
            string command = Command(executable);
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
                else if (String.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(ValueName, false);
            }
        }
    }
}
