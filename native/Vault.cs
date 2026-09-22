using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SuperBrain
{
    public sealed class VaultEnvelope
    {
        public string format { get; set; }
        public int version { get; set; }
        public string kdf { get; set; }
        public int iterations { get; set; }
        public string salt { get; set; }
        public string iv { get; set; }
        public string ciphertext { get; set; }
        public string mac { get; set; }
        public VaultEnvelope() { format = "super-brain.lite.vault"; version = 2; kdf = "PBKDF2-HMAC-SHA256"; iterations = Crypto.Iterations; }
        public void Validate()
        {
            if (format != "super-brain.lite.vault" || version != 2 || kdf != "PBKDF2-HMAC-SHA256" || iterations != Crypto.Iterations) throw new Exception("密码库版本或加密参数不受支持。");
            Decode(salt, 32); Decode(iv, 16); Decode(mac, 32); var data = Decode(ciphertext, 0);
            if (data.Length == 0 || data.Length % 16 != 0) throw new Exception("密码库内容损坏。");
        }
        static byte[] Decode(string value, int length)
        {
            if (value == null || value.Length > JsonFile.Limit) throw new Exception("密码库内容损坏。");
            byte[] data;
            try { data = Convert.FromBase64String(value); } catch { throw new Exception("密码库内容损坏。"); }
            if ((length != 0 && data.Length != length) || Convert.ToBase64String(data) != value) throw new Exception("密码库内容损坏。");
            return data;
        }
    }

    public static class Crypto
    {
        public const int Iterations = 600000;
        static readonly byte[] Domain = Encoding.ASCII.GetBytes("SuperBrainLite:2:PBKDF2-SHA256:600000:AES256CBC-HMACSHA256:");
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, string implementation, uint flags);
        [DllImport("bcrypt.dll")] static extern int BCryptDeriveKeyPBKDF2(IntPtr algorithm, byte[] password, int passwordLength, byte[] salt, int saltLength, ulong iterations, byte[] output, int outputLength, uint flags);
        [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);
        public static byte[] Random(int length) { var bytes = new byte[length]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return bytes; }
        public static void CheckPassword(string password, bool creating)
        {
            if (String.IsNullOrEmpty(password) || password.Length > 1024) throw new Exception("请输入主密码。");
            if (creating && password.Length < 12) throw new Exception("主密码至少需要 12 个字符，可以使用较长的中文短句。");
        }
        public static byte[] Derive(string password, byte[] salt, int iterations = Iterations)
        {
            CheckPassword(password, false); IntPtr algorithm = IntPtr.Zero; byte[] text = Encoding.UTF8.GetBytes(password), key = new byte[64];
            try
            {
                if (BCryptOpenAlgorithmProvider(out algorithm, "SHA256", null, 8) != 0 || BCryptDeriveKeyPBKDF2(algorithm, text, text.Length, salt, salt.Length, (ulong)iterations, key, key.Length, 0) != 0) throw new Exception("Windows 加密服务无法派生密钥。");
                return key;
            }
            catch { Array.Clear(key, 0, key.Length); throw; }
            finally { Array.Clear(text, 0, text.Length); if (algorithm != IntPtr.Zero) BCryptCloseAlgorithmProvider(algorithm, 0); }
        }
        static byte[] Join(params byte[][] parts) { using (var stream = new MemoryStream()) { foreach (var part in parts) stream.Write(part, 0, part.Length); return stream.ToArray(); } }
        static byte[] Authenticate(byte[] key, byte[] salt, byte[] iv, byte[] cipher)
        {
            var half = key.Skip(32).Take(32).ToArray();
            try { using (var hmac = new HMACSHA256(half)) return hmac.ComputeHash(Join(Domain, salt, iv, cipher)); }
            finally { Array.Clear(half, 0, half.Length); }
        }
        public static VaultEnvelope Encrypt(List<Record> items, byte[] key, byte[] salt)
        {
            Record.ValidateList(items); var iv = Random(16); var plain = Encoding.UTF8.GetBytes(JsonFile.Encode(items)); var half = key.Take(32).ToArray();
            try
            {
                byte[] encrypted;
                using (var aes = Aes.Create()) { aes.Key = half; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; using (var transform = aes.CreateEncryptor()) encrypted = transform.TransformFinalBlock(plain, 0, plain.Length); }
                return new VaultEnvelope { salt = Convert.ToBase64String(salt), iv = Convert.ToBase64String(iv), ciphertext = Convert.ToBase64String(encrypted), mac = Convert.ToBase64String(Authenticate(key, salt, iv, encrypted)) };
            }
            finally { Array.Clear(plain, 0, plain.Length); Array.Clear(half, 0, half.Length); }
        }
        public static List<Record> Decrypt(VaultEnvelope envelope, byte[] key)
        {
            envelope.Validate(); var salt = Convert.FromBase64String(envelope.salt); var iv = Convert.FromBase64String(envelope.iv); var encrypted = Convert.FromBase64String(envelope.ciphertext);
            var expected = Authenticate(key, salt, iv, encrypted); var actual = Convert.FromBase64String(envelope.mac); int different = 0;
            for (int i = 0; i < expected.Length; i++) different |= expected[i] ^ actual[i];
            if (different != 0) throw new Exception("主密码不正确，或密码库已损坏。");
            byte[] plain = null, half = key.Take(32).ToArray();
            try
            {
                using (var aes = Aes.Create()) { aes.Key = half; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7; using (var transform = aes.CreateDecryptor()) plain = transform.TransformFinalBlock(encrypted, 0, encrypted.Length); }
                var list = JsonFile.Decode<List<Record>>(Encoding.UTF8.GetString(plain)); Record.ValidateList(list); return list;
            }
            catch { throw new Exception("主密码不正确，或密码库已损坏。"); }
            finally { if (plain != null) Array.Clear(plain, 0, plain.Length); Array.Clear(half, 0, half.Length); }
        }
        public static string GeneratePassword()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%&*-_";
            var text = new StringBuilder(); using (var rng = RandomNumberGenerator.Create()) { var value = new byte[1]; while (text.Length < 24) { rng.GetBytes(value); if (value[0] < 256 - 256 % chars.Length) text.Append(chars[value[0] % chars.Length]); } } return text.ToString();
        }
    }

    public sealed class Vault : IDisposable
    {
        readonly object gate = new object();
        public readonly string FileName;
        VaultEnvelope envelope, persisted;
        byte[] key;
        List<Record> items = new List<Record>();
        long epoch;
        bool busy;
        public Vault(string directory) { FileName = Path.Combine(directory, "vault.enc.json"); envelope = JsonFile.Read<VaultEnvelope>(FileName); if (envelope != null) envelope.Validate(); persisted = envelope; }
        public bool Exists { get { lock (gate) return envelope != null; } }
        public bool Unlocked { get { lock (gate) return key != null; } }
        public void Lock() { lock (gate) { epoch++; if (key != null) Array.Clear(key, 0, key.Length); key = null; items.Clear(); } }
        public List<Record> List() { lock (gate) { RequireUnlocked(); return JsonFile.Clone(items); } }
        void RequireUnlocked() { if (key == null) throw new Exception("密码库已锁定，请先解锁。"); }
        void RequireIdle() { if (busy) throw new Exception("密码库正在处理，请稍后重试。"); }
        public void Setup(string password) { Open(password, true); }
        public void Unlock(string password) { Open(password, false); }
        void Open(string password, bool create)
        {
            Crypto.CheckPassword(password, create); long generation; VaultEnvelope record;
            lock (gate) { RequireIdle(); if (create == (envelope != null)) throw new Exception(create ? "密码库已创建。" : "请先创建密码库。"); busy = true; generation = epoch; record = envelope; }
            byte[] derived = null;
            try
            {
                var salt = create ? Crypto.Random(32) : Convert.FromBase64String(record.salt); derived = Crypto.Derive(password, salt);
                var list = create ? new List<Record>() : Crypto.Decrypt(record, derived);
                lock (gate)
                {
                    if (epoch != generation) throw new Exception("解锁已取消，请重新打开密码库。");
                    if (create) { record = Crypto.Encrypt(list, derived, salt); JsonFile.Write(FileName, record); envelope = persisted = record; }
                    if (key != null) Array.Clear(key, 0, key.Length); key = (byte[])derived.Clone(); items = list;
                }
            }
            finally { if (derived != null) Array.Clear(derived, 0, derived.Length); lock (gate) busy = false; }
        }
        public void Save(Record item)
        {
            item.Validate(); lock (gate)
            {
                RequireIdle(); RequireUnlocked(); var list = JsonFile.Clone(items); var index = list.FindIndex(r => r.id == item.id);
                if (index < 0) list.Insert(0, JsonFile.Clone(item)); else list[index] = JsonFile.Clone(item);
                var next = Crypto.Encrypt(list, key, Convert.FromBase64String(envelope.salt)); items = list; envelope = next; Flush();
            }
        }
        public void Delete(string id)
        {
            lock (gate) { RequireIdle(); RequireUnlocked(); var list = items.Where(r => r.id != id).ToList(); envelope = Crypto.Encrypt(list, key, Convert.FromBase64String(envelope.salt)); items = list; Flush(); }
        }
        public void Flush() { lock (gate) { if (envelope != persisted && envelope != null) { JsonFile.Write(FileName, envelope); persisted = envelope; } } }
        public VaultEnvelope Backup() { lock (gate) { Flush(); return persisted == null ? null : JsonFile.Clone(persisted); } }
        public void ChangePassword(string oldPassword, string nextPassword)
        {
            Crypto.CheckPassword(nextPassword, true); long generation; VaultEnvelope record;
            lock (gate) { RequireIdle(); RequireUnlocked(); busy = true; generation = epoch; record = envelope; }
            byte[] previous = null, next = null;
            try
            {
                previous = Crypto.Derive(oldPassword, Convert.FromBase64String(record.salt)); var list = Crypto.Decrypt(record, previous); var salt = Crypto.Random(32); next = Crypto.Derive(nextPassword, salt); var updated = Crypto.Encrypt(list, next, salt);
                lock (gate) { if (generation != epoch) throw new Exception("密码库已锁定，请重新解锁后修改。"); JsonFile.Write(FileName, updated); envelope = persisted = updated; Array.Clear(key, 0, key.Length); key = (byte[])next.Clone(); items = list; }
            }
            finally { if (previous != null) Array.Clear(previous, 0, previous.Length); if (next != null) Array.Clear(next, 0, next.Length); lock (gate) busy = false; }
        }
        public void Restore(VaultEnvelope value)
        {
            if (value != null) value.Validate(); lock (gate)
            {
                RequireIdle(); Lock();
                if (value != null) JsonFile.Write(FileName, value);
                else if (File.Exists(FileName)) { File.Copy(FileName, FileName + ".bak", true); File.Delete(FileName); }
                envelope = persisted = value == null ? null : JsonFile.Clone(value);
            }
        }
        public void Dispose() { Lock(); }
    }
}
