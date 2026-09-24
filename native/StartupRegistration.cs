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
        public sealed class Snapshot
        {
            internal string legacy, taskXml, keyPath, taskName;
        }
        public static Snapshot Capture(string keyPath = RunKey, string taskName = null)
        {
            return new Snapshot { legacy = ReadCommand(keyPath), taskXml = ScheduledStartup.ReadXml(taskName), keyPath = keyPath, taskName = taskName };
        }
        public static void Restore(Snapshot previous)
        {
            ScheduledStartup.WriteXml(previous.taskXml, previous.taskName);
            using (var key = Registry.CurrentUser.CreateSubKey(previous.keyPath))
                if (previous.legacy == null) key.DeleteValue(ValueName, false); else key.SetValue(ValueName, previous.legacy, RegistryValueKind.String);
        }
        public static bool IsEnabled(string executable, string keyPath = RunKey, string taskName = null)
        {
            try { if (ScheduledStartup.Matches(ScheduledStartup.ReadXml(taskName), executable, true)) return true; }
            catch (Exception e) { if (!(e.GetBaseException() is System.Runtime.InteropServices.COMException)) throw; }
            return String.Equals(ReadCommand(keyPath), Command(executable), StringComparison.OrdinalIgnoreCase);
        }
        public static string ReadCommand(string keyPath = RunKey)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, false)) return key == null ? null : key.GetValue(ValueName) as string;
        }
        public static void SetEnabled(bool enabled, string executable, string keyPath = RunKey, string taskName = null)
        {
            string command = Command(executable);
            var previous = Capture(keyPath, taskName);
            try
            {
                if (enabled) ScheduledStartup.WriteXml(ScheduledStartup.Definition(executable), taskName);
                else if (ScheduledStartup.Matches(previous.taskXml, executable, false)) ScheduledStartup.WriteXml(null, taskName);
                using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                    if (enabled || String.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(ValueName, false);
            }
            catch
            {
                Restore(previous); throw;
            }
        }
    }
}
