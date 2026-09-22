using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SuperBrain
{
    public sealed class Preferences
    {
        public string background { get; set; }
        public string accent { get; set; }
        public string image { get; set; }
        public int imageTransparency { get; set; }
        public string shortcut { get; set; }
        public int autoLockMinutes { get; set; }
        public bool alwaysOnTop { get; set; }
        public Preferences() { background = "#F6F8FB"; accent = "#48648E"; image = ""; imageTransparency = 90; shortcut = "F8"; autoLockMinutes = 5; }
        public void Validate()
        {
            if (!Regex.IsMatch(background ?? "", "^#[0-9a-fA-F]{6}$") || !Regex.IsMatch(accent ?? "", "^#[0-9a-fA-F]{6}$")) throw new Exception("颜色应为六位色值，例如 #F6F8FB。");
            if (image == null || image.Length > 2900000 || (image.Length > 0 && !Regex.IsMatch(image, @"^data:image/(png|jpeg|webp);base64,[A-Za-z0-9+/=]+$"))) throw new Exception("背景图片格式不正确或超过 2 MB。");
            if (imageTransparency < 0 || imageTransparency > 100) throw new Exception("图片透明度应为 0–100。");
            if (!(new[] { 1, 5, 10, 15, 30 }).Contains(autoLockMinutes)) throw new Exception("自动锁定时间不正确。");
            if (!Regex.IsMatch(shortcut ?? "", @"^(?:(?:Ctrl|Control|Alt|Shift|Win)\+){0,3}(?:[A-Z0-9]|F(?:[1-9]|1[0-9]|2[0-4])|Space)$", RegexOptions.IgnoreCase) || (!shortcut.Contains("+") && !Regex.IsMatch(shortcut, @"^F(?:[1-9]|1[0-9]|2[0-4])$", RegexOptions.IgnoreCase))) throw new Exception("快捷键请使用 F8，或 Ctrl+Alt+Q 这样的组合键。");
        }
    }

    public sealed class Record
    {
        public string id { get; set; }
        public string title { get; set; }
        public string body { get; set; }
        public bool pinned { get; set; }
        public string createdAt { get; set; }
        public string updatedAt { get; set; }
        public string username { get; set; }
        public string password { get; set; }
        public string url { get; set; }
        public Record() { id = Guid.NewGuid().ToString(); title = body = username = password = url = ""; createdAt = updatedAt = DateTime.UtcNow.ToString("o"); }
        public void Validate()
        {
            Limit(id, 80); Limit(title, 120); Limit(body, 100000); Limit(username, 2048); Limit(password, 4096); Limit(url, 2048);
            DateTime time;
            if (id.Length == 0 || !DateTime.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time) || !DateTime.TryParse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)) throw new Exception("记录日期或编号不正确。");
        }
        static void Limit(string value, int maximum) { if (value == null || value.Length > maximum) throw new Exception("记录内容过长或格式不正确。"); }
        public static void ValidateList(List<Record> list)
        {
            if (list == null || list.Count > 5000 || list.Any(r => r == null)) throw new Exception("记录列表不正确，最多保存 5000 条。");
            foreach (var item in list) item.Validate();
            if (list.Select(r => r.id).Distinct().Count() != list.Count) throw new Exception("存在重复记录编号。");
        }
    }

    public static class JsonFile
    {
        public const int Limit = 16 * 1024 * 1024;
        public const int BackupLimit = 2 * Limit + 4 * 1024 * 1024;
        public static string Encode(object value) { return new JavaScriptSerializer { MaxJsonLength = BackupLimit, RecursionLimit = 32 }.Serialize(value); }
        public static T Decode<T>(string value) { return new JavaScriptSerializer { MaxJsonLength = BackupLimit, RecursionLimit = 32 }.Deserialize<T>(value); }
        public static T Clone<T>(T value) { return Decode<T>(Encode(value)); }
        public static T Read<T>(string file, int maximum = Limit) where T : class
        {
            if (!File.Exists(file)) return null;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > maximum) throw new Exception("文件过大：" + Path.GetFileName(file));
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    try { var value = Decode<T>(reader.ReadToEnd()); if (value == null) throw new Exception(); return value; }
                    catch (Exception e) { throw new Exception("无法读取 " + Path.GetFileName(file) + "，原文件未被覆盖。请从备份恢复。", e); }
                }
            }
        }
        public static void Write(string file, object value, int maximum = Limit)
        {
            var bytes = Encoding.UTF8.GetBytes(Encode(value));
            if (bytes.Length > maximum) throw new Exception("数据过大，保存失败。");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(file)) File.Replace(temporary, file, file + ".bak", true); else File.Move(temporary, file);
            }
            finally { Array.Clear(bytes, 0, bytes.Length); if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    public sealed class NoteDocument
    {
        public int version { get; set; }
        public List<Record> notes { get; set; }
        public NoteDocument() { version = 2; notes = new List<Record>(); }
    }
    public sealed class BackupDocument
    {
        public string format { get; set; }
        public int version { get; set; }
        public string createdAt { get; set; }
        public Preferences config { get; set; }
        public NoteDocument data { get; set; }
        public VaultEnvelope vault { get; set; }
        public BackupDocument() { format = "super-brain.lite.backup"; version = 2; createdAt = DateTime.UtcNow.ToString("o"); }
        public void Validate()
        {
            if (format != "super-brain.lite.backup" || version != 2 || data == null || data.version != 2 || config == null) throw new Exception("请选择轻量版备份。旧版 v0.1 的加密备份请使用旧版打开。");
            config.Validate(); Record.ValidateList(data.notes); if (vault != null) vault.Validate();
        }
    }

    public sealed class LocalStore
    {
        public readonly string DirectoryPath;
        public Preferences Config { get; private set; }
        public List<Record> Notes { get; private set; }
        public LocalStore(string directory)
        {
            DirectoryPath = Path.GetFullPath(directory); Directory.CreateDirectory(DirectoryPath);
            Config = JsonFile.Read<Preferences>(FilePath("config.json")) ?? new Preferences(); Config.Validate();
            var saved = JsonFile.Read<NoteDocument>(FilePath("notes.json")) ?? new NoteDocument();
            if (saved.version != 2) throw new Exception("备忘录版本不受支持，原资料未被修改。");
            Record.ValidateList(saved.notes); Notes = saved.notes;
        }
        public string FilePath(string name) { return Path.Combine(DirectoryPath, name); }
        public void SaveConfig(Preferences config) { config.Validate(); JsonFile.Write(FilePath("config.json"), config); Config = JsonFile.Clone(config); }
        public void SaveNotes() { Record.ValidateList(Notes); JsonFile.Write(FilePath("notes.json"), new NoteDocument { notes = Notes }); }
        public BackupDocument Snapshot(Vault vault) { vault.Flush(); return new BackupDocument { config = JsonFile.Clone(Config), data = new NoteDocument { notes = JsonFile.Clone(Notes) }, vault = vault.Backup() }; }
        public void Export(string file, Vault vault) { SaveNotes(); JsonFile.Write(file, Snapshot(vault), JsonFile.BackupLimit); }
        public void Restore(BackupDocument backup, Vault vault)
        {
            backup.Validate(); var old = Snapshot(vault);
            JsonFile.Write(FilePath("before-restore-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".json"), old, JsonFile.BackupLimit);
            var next = JsonFile.Clone(backup); next.config.shortcut = Config.shortcut;
            try { vault.Restore(next.vault); SaveConfig(next.config); Notes = next.data.notes; SaveNotes(); }
            catch { vault.Restore(old.vault); SaveConfig(old.config); Notes = old.data.notes; SaveNotes(); throw; }
        }
    }
}
