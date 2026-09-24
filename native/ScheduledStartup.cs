using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Xml;

namespace SuperBrain
{
    public static class ScheduledStartup
    {
        public const string Schema = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        public static string UserSid { get { using (var user = WindowsIdentity.GetCurrent()) return user.User.Value; } }
        public static string TaskName { get { return "SuperBrainLite-" + UserSid; } }
        public static string Name(string name)
        {
            if (name == null || name == TaskName) return TaskName;
            string prefix = TaskName + "-Test-"; Guid id;
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring(prefix.Length), "N", out id)) throw new ArgumentException("无效的启动任务名称。");
            return name;
        }
        static object Call(object target, string name, params object[] args) { return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.OptionalParamBinding, null, target, args); }
        static object Get(object target, string name) { return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null); }
        static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        static bool Missing(Exception error) { int code = error.GetBaseException().HResult; return code == unchecked((int)0x80070002) || code == unchecked((int)0x80070003); }
        static object Task(object folder, string name)
        {
            try { return Call(folder, "GetTask", Name(name)); }
            catch (Exception e) { if (Missing(e)) return null; throw; }
        }
        static T WithFolder<T>(Func<object, T> action)
        {
            object service = null, folder = null;
            try
            {
                service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                Call(service, "Connect", null, null, null, null); folder = Call(service, "GetFolder", "\\");
                return action(folder);
            }
            finally { Release(folder); Release(service); }
        }
        public static string ReadXml(string name = null)
        {
            return WithFolder(delegate(object folder) { object task = Task(folder, name); try { return task == null ? null : (string)Get(task, "Xml"); } finally { Release(task); } });
        }
        public static void WriteXml(string xml, string name = null)
        {
            WithFolder(delegate(object folder)
            {
                if (String.IsNullOrEmpty(xml))
                {
                    object existing = Task(folder, name); if (existing == null) return false;
                    Release(existing); Call(folder, "DeleteTask", Name(name), 0); return true;
                }
                // Interactive token, current user, least privilege; no stored Windows password.
                object task = Call(folder, "RegisterTask", Name(name), xml, 6, UserSid, null, 3, null);
                Release(task); return true;
            });
        }
        public static string Definition(string executable)
        {
            string full = Path.GetFullPath(executable), user = SecurityElement.Escape(UserSid);
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.2\" xmlns=\"" + Schema + "\">" +
                "<RegistrationInfo><Description>超强大脑：登录后后台待命，按快捷键呼出。</Description></RegistrationInfo>" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + user + "</UserId></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"CurrentUser\"><UserId>" + user + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>" +
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><Hidden>false</Hidden>" +
                "<RunOnlyIfIdle>false</RunOnlyIfIdle><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>7</Priority></Settings>" +
                "<Actions Context=\"CurrentUser\"><Exec><Command>" + SecurityElement.Escape(full) + "</Command><Arguments>--background</Arguments>" +
                "<WorkingDirectory>" + SecurityElement.Escape(Path.GetDirectoryName(full)) + "</WorkingDirectory></Exec></Actions></Task>";
        }
        public static bool Matches(string xml, string executable, bool requireEnabled)
        {
            if (xml == null) return false;
            var document = new XmlDocument { XmlResolver = null }; document.LoadXml(xml);
            var ns = new XmlNamespaceManager(document.NameTable); ns.AddNamespace("t", Schema);
            Func<string, string> value = delegate(string path) { var node = document.SelectSingleNode("/t:Task/" + path, ns); return node == null ? null : node.InnerText; };
            if (!CurrentUser(value("t:Principals/t:Principal/t:UserId"))) return false;
            return String.Equals(value("t:Actions/t:Exec/t:Command"), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) && value("t:Actions/t:Exec/t:Arguments") == "--background" &&
                (!requireEnabled || (value("t:Settings/t:Enabled") != "false" && value("t:Triggers/t:LogonTrigger/t:Enabled") != "false" && CurrentUser(value("t:Triggers/t:LogonTrigger/t:UserId")) && value("t:Principals/t:Principal/t:LogonType") == "InteractiveToken"));
        }
        static bool CurrentUser(string owner)
        {
            if (String.IsNullOrEmpty(owner)) return false;
            if (String.Equals(owner, UserSid, StringComparison.OrdinalIgnoreCase)) return true;
            try { return ((SecurityIdentifier)new NTAccount(owner).Translate(typeof(SecurityIdentifier))).Value == UserSid; }
            catch (IdentityNotMappedException) { return false; }
        }
    }
}
