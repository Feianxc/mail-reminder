using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace OfficeSignal
{
    public static class UpdateTests
    {
        private const string ApplicationName = "邮件提醒助手.exe";
        private static int passed;
        private static string root;
        private static readonly string BinaryVersion = typeof(OnlineUpdate).Assembly.GetName().Version.ToString();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static int Main(string[] args)
        {
            try
            {
                string build = args.Length == 0 ? Path.Combine(Environment.CurrentDirectory, ".build") : Path.GetFullPath(args[0]);
                root = Path.Combine(build, "updater-fixtures", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                VerifyVersionAndManifest();
                VerifyPackagesAndInstallation();
                VerifyRejectedPackages();
                VerifyRollback();
                VerifyInvalidPlanCannotWrite();
                Console.WriteLine("RESULT pass " + passed + " updater fixture checks. Running application replacement and Internet download not run.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("RESULT fail updater " + exception);
                return 1;
            }
        }

        private static void VerifyVersionAndManifest()
        {
            Check(OnlineUpdate.CompareVersions("1.3", "1.3.0.0") == 0, "version comparison normalizes missing build components");
            Check(OnlineUpdate.CompareVersions("1.10.0", "1.9.9") > 0, "version comparison uses numeric components");
            Check(OnlineUpdate.CompareVersions("1.3.0.1", "1.3.0") > 0, "version comparison preserves revision updates");
            Reject(() => OnlineUpdate.CompareVersions("1.3-preview", "1.2.0"), "invalid update version rejected");
            var manifest = new Dictionary<string, object> {
                { "version", "99.0.0" }, { "url", "https://feian.online/mail-reminder/download/邮件提醒助手-99.0.0.zip" },
                { "sha256", new string('A', 64) }, { "size", 1000 }, { "notes", "中文更新说明" }
            };
            var info = OnlineUpdate.ParseManifest(Json.Serialize(manifest), "https://feian.online/mail-reminder/");
            Check(info.Version == "99.0.0" && info.IsNewer && info.Notes == "中文更新说明", "FEIAN manifest and Chinese notes read correctly");
            foreach (string url in new[] {
                "http://feian.online/mail-reminder/download/update.zip",
                "https://other.example/mail-reminder/download/update.zip",
                "https://feian.online:444/mail-reminder/download/update.zip",
                "https://feian.online/elsewhere/update.zip",
                "https://name:secret@feian.online/mail-reminder/download/update.zip"
            })
            {
                manifest["url"] = url;
                Reject(() => OnlineUpdate.ParseManifest(Json.Serialize(manifest), "https://feian.online/mail-reminder/"), "untrusted update URL rejected " + url.Split('@').Last());
            }
            manifest["url"] = "https://feian.online/mail-reminder/download/update.zip";
            manifest["size"] = 51L * 1024 * 1024;
            Reject(() => OnlineUpdate.ParseManifest(Json.Serialize(manifest), "https://feian.online/mail-reminder/"), "oversize package rejected before download");
        }

        private static void VerifyPackagesAndInstallation()
        {
            foreach (bool includeTeam in new[] { false, true })
            {
                string archive = Package("valid-" + includeTeam, includeTeam);
                string payload = Path.Combine(root, "payload-" + includeTeam);
                OnlineUpdate.VerifyAndExtractPackage(archive, payload, OnlineUpdate.FileHash(archive), new FileInfo(archive).Length, BinaryVersion);
                Check(File.Exists(Path.Combine(payload, ApplicationName)) && File.Exists(Path.Combine(payload, "team.json")) == includeTeam,
                    includeTeam ? "four-file private package extracts safely" : "three-file public update package extracts safely");
                string installation = NewInstall("安装目录 带空格 " + includeTeam, 3);
                string previousTeam = File.ReadAllText(Path.Combine(installation, "team.json"));
                File.WriteAllText(Path.Combine(installation, "用户资料.txt"), "原始资料", Encoding.UTF8);
                string backup = OnlineUpdate.InstallApplicationFiles(payload, installation, BinaryVersion);
                Check(OnlineUpdate.FileHash(Path.Combine(installation, ApplicationName)) == OnlineUpdate.FileHash(Path.Combine(payload, ApplicationName)), "verified binary installs into a Chinese path with spaces");
                Check(File.ReadAllText(Path.Combine(installation, "team.json")) == previousTeam, includeTeam ? "package team replacement never overwrites local room key" : "public update preserves local room key");
                Check(File.ReadAllText(Path.Combine(installation, "用户资料.txt")) == "原始资料", "unrelated existing documents survive update");
                Check(File.Exists(Path.Combine(backup, ApplicationName)), "previous application remains in rollback directory");
            }
            string complete = Package("backup-shapes", false);
            string files = Path.Combine(root, "payload-shapes");
            OnlineUpdate.VerifyAndExtractPackage(complete, files, OnlineUpdate.FileHash(complete), new FileInfo(complete).Length, BinaryVersion);
            foreach (int existingCount in new[] { 0, 1, 3 })
            {
                string installation = NewInstall("prior-" + existingCount, existingCount);
                string backup = OnlineUpdate.InstallApplicationFiles(files, installation, BinaryVersion);
                var record = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(backup, "backup.json")));
                var entries = record["Files"] as ArrayList;
                int found = entries == null ? -1 : entries.Cast<Dictionary<string, object>>().Count(item => (bool)item["Existed"]);
                Check(entries != null && entries.Count == 3 && found == existingCount, "rollback collection shape is an array with 0/1/N existing files: " + existingCount);
            }
            string missingTeam = Path.Combine(root, "missing-local-team");
            Directory.CreateDirectory(missingTeam);
            Reject(() => OnlineUpdate.InstallApplicationFiles(files, missingTeam, BinaryVersion), "updater requires an existing local team configuration");
        }

        private static void VerifyRejectedPackages()
        {
            string valid = Package("integrity", false);
            Reject(() => OnlineUpdate.VerifyAndExtractPackage(valid, Path.Combine(root, "bad-hash"), new string('0', 64), new FileInfo(valid).Length, BinaryVersion), "wrong SHA256 rejected");
            Reject(() => OnlineUpdate.VerifyAndExtractPackage(valid, Path.Combine(root, "bad-size"), OnlineUpdate.FileHash(valid), new FileInfo(valid).Length + 1, BinaryVersion), "wrong package size rejected");
            Reject(() => OnlineUpdate.VerifyAndExtractPackage(valid, Path.Combine(root, "bad-version"), OnlineUpdate.FileHash(valid), new FileInfo(valid).Length, "99.0.0"), "binary metadata version must match the manifest");
            int count = 0;
            foreach (string extra in new[] { "../outside.exe", "MailReminder/../../outside.exe", "C:/outside.exe", "MailReminder/another.exe", "MailReminder/邮件提醒助手.EXE", "MailReminder/nested/file.txt", "MailReminder/hidden.txt:stream" })
            {
                string archive = Package("unsafe-" + count, false, extra);
                string destination = Path.Combine(root, "unsafe-out-" + count++);
                Reject(() => OnlineUpdate.VerifyAndExtractPackage(archive, destination, OnlineUpdate.FileHash(archive), new FileInfo(archive).Length, BinaryVersion), "unsafe or duplicate ZIP entry rejected " + extra);
            }
            string linkArchive = Package("link", false, "MailReminder/link", true);
            Reject(() => OnlineUpdate.VerifyAndExtractPackage(linkArchive, Path.Combine(root, "link-out"), OnlineUpdate.FileHash(linkArchive), new FileInfo(linkArchive).Length, BinaryVersion), "ZIP symbolic link rejected");
            Check(!File.Exists(Path.Combine(root, "outside.exe")), "rejected archive cannot create an escaped executable");
        }

        private static void VerifyRollback()
        {
            string archive = Package("rollback", false);
            string payload = Path.Combine(root, "rollback-payload");
            OnlineUpdate.VerifyAndExtractPackage(archive, payload, OnlineUpdate.FileHash(archive), new FileInfo(archive).Length, BinaryVersion);
            string installation = NewInstall("回滚 测试", 3);
            string originalBinary = File.ReadAllText(Path.Combine(installation, ApplicationName));
            string originalConfig = File.ReadAllText(Path.Combine(installation, ApplicationName + ".config"));
            string originalTeam = File.ReadAllText(Path.Combine(installation, "team.json"));
            using (var locked = new FileStream(Path.Combine(installation, ApplicationName + ".config"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Reject(() => OnlineUpdate.InstallApplicationFiles(payload, installation, BinaryVersion), "locked second file makes installation fail clearly");
            }
            Check(File.ReadAllText(Path.Combine(installation, ApplicationName)) == originalBinary, "first replaced binary rolls back after a later replacement fails");
            Check(File.ReadAllText(Path.Combine(installation, ApplicationName + ".config")) == originalConfig, "locked configuration keeps its original bytes");
            Check(File.ReadAllText(Path.Combine(installation, "team.json")) == originalTeam, "failed installation preserves local team configuration");
            Check(!Directory.GetFiles(installation, "*.update-*").Any(), "failed installation removes only its temporary replacement files");
            OnlineUpdate.InstallApplicationFiles(payload, installation, BinaryVersion);
            Check(OnlineUpdate.FileHash(Path.Combine(installation, ApplicationName)) == OnlineUpdate.FileHash(Path.Combine(payload, ApplicationName)), "failed update can be retried successfully after the lock clears");
            OnlineUpdate.InstallApplicationFiles(payload, installation, BinaryVersion);
            Check(Directory.GetDirectories(Path.Combine(installation, ".mail-reminder-rollback")).Length <= 2, "rollback retention stays bounded after repeated updates");
            string beforeConcurrent = OnlineUpdate.FileHash(Path.Combine(installation, ApplicationName));
            using (var installationLock = new FileStream(Path.Combine(installation, ".mail-reminder-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Reject(() => OnlineUpdate.InstallApplicationFiles(payload, installation, BinaryVersion), "concurrent installer is rejected by the per-installation file lock");
            Check(OnlineUpdate.FileHash(Path.Combine(installation, ApplicationName)) == beforeConcurrent, "concurrent attempt leaves installed files unchanged");
        }

        private static void VerifyInvalidPlanCannotWrite()
        {
            string outside = Path.Combine(root, "arbitrary-plan-target");
            Directory.CreateDirectory(outside);
            string plan = Path.Combine(root, "invalid-plan.json");
            File.WriteAllText(plan, Json.Serialize(new
            {
                Schema = 1,
                Version = "1.3.0",
                StageDirectory = outside,
                InstallDirectory = outside,
                Nonce = Guid.NewGuid().ToString("N"),
                ExpiresUtcTicks = DateTime.UtcNow.AddHours(1).Ticks
            }), Encoding.UTF8);
            Check(OnlineUpdate.ApplyStagedPlan(plan) == 1 && !string.IsNullOrWhiteSpace(OnlineUpdate.LastError), "untrusted helper plan returns a clear failure");
            Check(!File.Exists(Path.Combine(outside, "result.json")), "invalid helper plan cannot write to a supplied arbitrary directory");
        }

        private static string NewInstall(string label, int existingCount)
        {
            string directory = Path.Combine(root, label);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "team.json"), Json.Serialize(new
            {
                RoomName = "原始办公室",
                RoomKey = Convert.ToBase64String(Enumerable.Repeat((byte)13, 32).ToArray()),
                BroadcastAddress = "192.168.20.255",
                Port = 45820
            }), Encoding.UTF8);
            string[] names = { ApplicationName, ApplicationName + ".config", "使用说明.txt" };
            for (int i = 0; i < existingCount; i++)
                File.WriteAllText(Path.Combine(directory, names[i]), "old " + names[i], Encoding.UTF8);
            return directory;
        }

        private static string Package(string label, bool includeTeam, string extra = null, bool symlink = false)
        {
            string path = Path.Combine(root, label + ".zip");
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var executable = archive.CreateEntry("MailReminder/" + ApplicationName);
                using (var input = File.OpenRead(typeof(OnlineUpdate).Assembly.Location))
                using (var output = executable.Open())
                    input.CopyTo(output);
                TextEntry(archive, "MailReminder/" + ApplicationName + ".config", "<?xml version=\"1.0\"?><configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.7.2\"/></startup></configuration>");
                TextEntry(archive, "MailReminder/使用说明.txt", "更新后的使用说明");
                if (includeTeam)
                    TextEntry(archive, "MailReminder/team.json", "{\"RoomKey\":\"malicious replacement\"}");
                if (extra != null)
                {
                    var entry = TextEntry(archive, extra, "untrusted");
                    if (symlink)
                        entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                }
            }
            return path;
        }
        private static ZipArchiveEntry TextEntry(ZipArchive archive, string name, string text)
        {
            var entry = archive.CreateEntry(name);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                writer.Write(text);
            return entry;
        }
        private static void Check(bool condition, string label)
        {
            if (!condition)
                throw new Exception(label);
            passed++;
            Console.WriteLine("pass " + label);
        }
        private static void Reject(Action action, string label)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                if (exception is IOException || exception is InvalidDataException || exception is ArgumentException || exception is System.Xml.XmlException)
                {
                    Check(true, label);
                    return;
                }
                throw;
            }
            throw new Exception("Expected rejection: " + label);
        }
    }
}
