using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;

namespace OfficeSignal
{
    public sealed class UpdateInfo
    {
        public string Version
        {
            get; internal set;
        }
        public string Url
        {
            get; internal set;
        }
        public string Sha256
        {
            get; internal set;
        }
        public long Size
        {
            get; internal set;
        }
        public string Notes
        {
            get; internal set;
        }
        public bool IsNewer
        {
            get
            {
                return OnlineUpdate.CompareVersions(Version, OnlineUpdate.CurrentVersion.ToString()) > 0;
            }
        }
        internal string BaseUrl
        {
            get; set;
        }
    }

    public sealed class StagedUpdate
    {
        public string PlanPath
        {
            get; internal set;
        }
        public string StageDirectory
        {
            get; internal set;
        }
        public string InstallDirectory
        {
            get; internal set;
        }
    }

    public static class OnlineUpdate
    {
        private const long MaximumPackageBytes = 50L * 1024 * 1024;
        private const long MaximumExtractedBytes = 100L * 1024 * 1024;
        private const string ApplicationFile = "邮件提醒助手.exe";
        private const string HelperFile = "MailReminderUpdater.exe";
        private static readonly string[] PackageFiles = { ApplicationFile, ApplicationFile + ".config", "team.json", "使用说明.txt" };
        private static readonly string[] ApplicationFiles = { ApplicationFile, ApplicationFile + ".config", "使用说明.txt" };
        public static string LastError
        {
            get; private set;
        }
        public static Version CurrentVersion
        {
            get
            {
                return typeof(OnlineUpdate).Assembly.GetName().Version;
            }
        }

        public static async Task<UpdateInfo> CheckAsync(string baseUrl, CancellationToken cancellationToken)
        {
            PruneCompletedStages();
            Uri service = ValidateServiceUri(baseUrl);
            using (var client = CreateClient(TimeSpan.FromSeconds(25)))
            using (var response = await client.GetAsync(new Uri(service, "api/updates/windows"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                string json;
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new MemoryStream())
                {
                    await CopyLimitedAsync(input, output, 65536, cancellationToken).ConfigureAwait(false);
                    json = Encoding.UTF8.GetString(output.ToArray());
                }
                return ParseManifest(json, service.AbsoluteUri);
            }
        }

        public static async Task<StagedUpdate> DownloadAndStageAsync(UpdateInfo info, CancellationToken cancellationToken)
        {
            ValidateInfo(info);
            string stage = Path.Combine(UpdatesRoot(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            string archivePath = Path.Combine(stage, "package.zip");
            try
            {
                using (var client = CreateClient(TimeSpan.FromMinutes(8)))
                using (var response = await client.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != info.Size)
                        throw new InvalidDataException("下载文件大小与更新清单不一致。");
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        long received = await CopyLimitedAsync(input, output, info.Size, cancellationToken).ConfigureAwait(false);
                        if (received != info.Size)
                            throw new InvalidDataException("更新包下载不完整。");
                        output.Flush(true);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                string payload = Path.Combine(stage, "payload");
                VerifyAndExtractPackage(archivePath, payload, info.Sha256, info.Size, info.Version);
                string original = CurrentExecutable();
                var plan = new InstallPlan
                {
                    Schema = 1,
                    Version = info.Version,
                    StageDirectory = stage,
                    InstallDirectory = Path.GetDirectoryName(original),
                    PackageSha256 = info.Sha256.ToUpperInvariant(),
                    PackageSize = info.Size,
                    ParentProcessId = Process.GetCurrentProcess().Id,
                    ParentStartUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                    ParentExecutable = original,
                    Nonce = Guid.NewGuid().ToString("N"),
                    ExpiresUtcTicks = DateTime.UtcNow.AddHours(12).Ticks
                };
                string planPath = Path.Combine(stage, "plan.json");
                WriteJson(planPath, plan);
                return new StagedUpdate { PlanPath = planPath, StageDirectory = stage, InstallDirectory = plan.InstallDirectory };
            }
            catch
            {
                try
                {
                    DeleteOwnedStage(stage);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }

        public static void LaunchInstaller(StagedUpdate staged)
        {
            if (staged == null)
                throw new ArgumentNullException("staged");
            var plan = ReadPlan(staged.PlanPath);
            ValidatePlanLocations(plan, staged.PlanPath);
            if (!SamePath(plan.ParentExecutable, CurrentExecutable()) || plan.ParentProcessId != Process.GetCurrentProcess().Id)
                throw new InvalidDataException("更新计划不属于当前程序。");
            string helper = Path.Combine(plan.StageDirectory, HelperFile);
            File.Copy(plan.ParentExecutable, helper, false);
            string currentConfig = plan.ParentExecutable + ".config";
            if (File.Exists(currentConfig))
                File.Copy(currentConfig, helper + ".config", false);
            plan.HelperSha256 = FileHash(helper);
            WriteJson(staged.PlanPath, plan);
            string readyPath = Path.Combine(plan.StageDirectory, "helper-ready.json");
            using (var process = Process.Start(new ProcessStartInfo
            {
                FileName = helper,
                Arguments = "--apply-update " + QuoteArgument(staged.PlanPath),
                WorkingDirectory = plan.StageDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }))
            {
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(8))
                {
                    if (File.Exists(readyPath))
                    {
                        var ready = ReadJson<ReadySignal>(readyPath);
                        if (ready.Nonce == plan.Nonce && ready.HelperProcessId == process.Id)
                            return;
                        throw new InvalidDataException("更新助手的启动确认无效。");
                    }
                    if (process.HasExited)
                        throw new InvalidOperationException("更新助手未能启动，请重新检查更新。");
                    Thread.Sleep(50);
                }
                File.WriteAllText(Path.Combine(plan.StageDirectory, "cancel"), "cancel", Encoding.ASCII);
                throw new TimeoutException("更新助手启动超时，当前程序继续运行。请稍后重试。");
            }
        }

        public static int ApplyStagedPlan(string planPath)
        {
            LastError = null;
            InstallPlan plan = null;
            bool parentExited = false;
            bool locationsValidated = false;
            try
            {
                plan = ReadPlan(planPath);
                ValidatePlanLocations(plan, planPath);
                locationsValidated = true;
                string helper = Path.Combine(plan.StageDirectory, HelperFile);
                if (!SamePath(CurrentExecutable(), helper) || !HashEquals(FileHash(helper), plan.HelperSha256))
                    throw new InvalidDataException("更新助手身份校验失败。");
                using (var parent = Process.GetProcessById(plan.ParentProcessId))
                {
                    if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartUtcTicks || !SamePath(parent.MainModule.FileName, plan.ParentExecutable))
                        throw new InvalidDataException("原程序进程已变化，更新已取消。");
                    WriteJson(Path.Combine(plan.StageDirectory, "helper-ready.json"), new ReadySignal { Nonce = plan.Nonce, HelperProcessId = Process.GetCurrentProcess().Id });
                    var timeout = Stopwatch.StartNew();
                    while (!parent.WaitForExit(100))
                    {
                        if (File.Exists(Path.Combine(plan.StageDirectory, "cancel")))
                            throw new OperationCanceledException("原程序取消了本次更新。");
                        if (timeout.Elapsed > TimeSpan.FromSeconds(35))
                            throw new TimeoutException("等待原程序退出超时，本次没有替换程序文件。");
                    }
                    parentExited = true;
                }
                if (File.Exists(Path.Combine(plan.StageDirectory, "cancel")))
                    throw new OperationCanceledException("本次更新已取消。");
                string verified = Path.Combine(plan.StageDirectory, "verified");
                VerifyAndExtractPackage(Path.Combine(plan.StageDirectory, "package.zip"), verified, plan.PackageSha256, plan.PackageSize, plan.Version);
                string backup;
                using (AcquireInstallLock(plan.InstallDirectory))
                {
                    backup = InstallApplicationFilesCore(verified, plan.InstallDirectory, plan.Version);
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = Path.Combine(plan.InstallDirectory, ApplicationFile),
                            WorkingDirectory = plan.InstallDirectory,
                            UseShellExecute = false
                        });
                    }
                    catch
                    {
                        RestoreBackup(backup, plan.InstallDirectory);
                        throw;
                    }
                }
                try
                {
                    WriteJson(Path.Combine(plan.StageDirectory, "result.json"), new
                    {
                        success = true,
                        version = plan.Version,
                        backup = backup
                    });
                    CleanupStagePayload(plan.StageDirectory);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return 0;
            }
            catch (Exception exception)
            {
                LastError = "在线更新未完成。\r\n" + exception.Message;
                if (plan != null && locationsValidated)
                {
                    try
                    {
                        WriteJson(Path.Combine(plan.StageDirectory, "result.json"), new
                        {
                            success = false,
                            message = exception.Message
                        });
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (parentExited && File.Exists(plan.ParentExecutable))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo { FileName = plan.ParentExecutable, WorkingDirectory = plan.InstallDirectory, UseShellExecute = false });
                        }
                        catch (Exception restartError) { LastError += "\r\n请手动重新启动原程序。" + restartError.Message; }
                    }
                }
                return 1;
            }
        }

        internal static int CompareVersions(string candidate, string installed)
        {
            return NormalizedVersion(candidate).CompareTo(NormalizedVersion(installed));
        }

        internal static UpdateInfo ParseManifest(string json, string baseUrl)
        {
            var values = new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<Dictionary<string, object>>(json);
            if (values == null)
                throw new InvalidDataException("更新清单为空。");
            var info = new UpdateInfo
            {
                Version = ReadString(values, "version"),
                Url = ReadString(values, "url"),
                Sha256 = ReadString(values, "sha256"),
                Size = Convert.ToInt64(values.ContainsKey("size") ? values["size"] : 0),
                Notes = values.ContainsKey("notes") ? Convert.ToString(values["notes"]) : "",
                BaseUrl = ValidateServiceUri(baseUrl).AbsoluteUri
            };
            ValidateInfo(info);
            return info;
        }

        internal static void VerifyAndExtractPackage(string archivePath, string payloadDirectory, string expectedHash, long expectedSize, string version)
        {
            if (Directory.Exists(payloadDirectory))
                throw new InvalidDataException("更新解压目录已存在，请重新下载更新。");
            if (expectedSize <= 0 || expectedSize > MaximumPackageBytes || !ValidHash(expectedHash))
                throw new InvalidDataException("更新包校验信息无效。");
            NormalizedVersion(version);
            using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != expectedSize || !HashEquals(StreamHash(input), expectedHash))
                    throw new InvalidDataException("更新包 SHA256 或大小校验失败。");
                input.Position = 0;
                using (var archive = new ZipArchive(input, ZipArchiveMode.Read, true))
                {
                    var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                    string root = null;
                    long totalBytes = 0;
                    foreach (var entry in archive.Entries)
                    {
                        string entryRoot;
                        string leaf = ValidateEntryName(entry.FullName, out entryRoot);
                        if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                            throw new InvalidDataException("更新包包含符号链接。");
                        if (leaf == null)
                            continue;
                        if (root == null)
                            root = entryRoot;
                        else if (root != entryRoot)
                            throw new InvalidDataException("更新包目录结构不一致。");
                        if (!PackageFiles.Contains(leaf, StringComparer.OrdinalIgnoreCase) || entries.ContainsKey(leaf))
                            throw new InvalidDataException("更新包包含未知或重复文件。");
                        totalBytes = checked(totalBytes + entry.Length);
                        if (entry.Length < 0 || entry.Length > MaximumPackageBytes || totalBytes > MaximumExtractedBytes)
                            throw new InvalidDataException("更新包解压大小超出限制。");
                        entries.Add(leaf, entry);
                    }
                    if (entries.Count < ApplicationFiles.Length || entries.Count > PackageFiles.Length || ApplicationFiles.Any(name => !entries.ContainsKey(name)))
                        throw new InvalidDataException("更新包缺少必要文件。");
                    Directory.CreateDirectory(payloadDirectory);
                    foreach (string name in entries.Keys)
                    {
                        using (var source = entries[name].Open())
                        using (var destination = new FileStream(Path.Combine(payloadDirectory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            CopyLimited(source, destination, entries[name].Length);
                            destination.Flush(true);
                        }
                    }
                }
            }
            Version binaryVersion = AssemblyName.GetAssemblyName(Path.Combine(payloadDirectory, ApplicationFile)).Version;
            if (NormalizedVersion(version) != NormalizedVersion(binaryVersion.ToString()))
                throw new InvalidDataException("更新包内程序版本与清单不一致。");
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = XmlReader.Create(Path.Combine(payloadDirectory, ApplicationFile + ".config"), settings))
            {
                var configuration = new XmlDocument { XmlResolver = null };
                configuration.Load(reader);
                if (configuration.DocumentElement == null || configuration.DocumentElement.Name != "configuration")
                    throw new InvalidDataException("更新包的运行配置文件无效。");
            }
        }

        internal static string InstallApplicationFiles(string payloadDirectory, string installDirectory, string version)
        {
            using (AcquireInstallLock(installDirectory))
                return InstallApplicationFilesCore(payloadDirectory, installDirectory, version);
        }

        private static FileStream AcquireInstallLock(string installDirectory)
        {
            EnsurePlainDirectory(installDirectory);
            return new FileStream(Path.Combine(installDirectory, ".mail-reminder-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static string InstallApplicationFilesCore(string payloadDirectory, string installDirectory, string version)
        {
            EnsurePlainDirectory(installDirectory);
            ValidateInstalledTeam(installDirectory);
            foreach (string name in ApplicationFiles)
                EnsurePlainFile(Path.Combine(payloadDirectory, name));
            string backupRoot = Path.Combine(installDirectory, ".mail-reminder-rollback");
            Directory.CreateDirectory(backupRoot);
            EnsurePlainDirectory(backupRoot);
            string backup = Path.Combine(backupRoot, DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            var record = new BackupRecord { Version = version, Files = new List<BackupFile>() };
            foreach (string name in ApplicationFiles)
            {
                string target = Path.Combine(installDirectory, name);
                bool existed = File.Exists(target);
                if (existed)
                {
                    EnsurePlainFile(target);
                    File.Copy(target, Path.Combine(backup, name), false);
                }
                record.Files.Add(new BackupFile { Name = name, Existed = existed });
            }
            WriteJson(Path.Combine(backup, "backup.json"), record);
            var changed = new List<BackupFile>();
            try
            {
                foreach (BackupFile file in record.Files)
                {
                    ReplaceFrom(Path.Combine(payloadDirectory, file.Name), Path.Combine(installDirectory, file.Name));
                    changed.Add(file);
                }
            }
            catch (Exception installError)
            {
                try
                {
                    RestoreFiles(backup, installDirectory, changed);
                }
                catch (Exception rollbackError) { throw new IOException("部分文件恢复失败，回滚资料保留在 " + backup, new AggregateException(installError, rollbackError)); }
                throw new IOException("更新替换失败，已恢复此前替换的文件。" + installError.Message, installError);
            }
            PruneBackups(backupRoot, backup);
            return backup;
        }

        private static void RestoreBackup(string backup, string installDirectory)
        {
            var record = ReadJson<BackupRecord>(Path.Combine(backup, "backup.json"));
            RestoreFiles(backup, installDirectory, record.Files);
        }

        private static void RestoreFiles(string backup, string installDirectory, IEnumerable<BackupFile> files)
        {
            foreach (var file in files.Reverse())
            {
                if (!ApplicationFiles.Contains(file.Name, StringComparer.Ordinal))
                    throw new InvalidDataException("回滚记录包含未知文件。");
                string target = Path.Combine(installDirectory, file.Name);
                if (file.Existed)
                    ReplaceFrom(Path.Combine(backup, file.Name), target);
                else if (File.Exists(target))
                    File.Delete(target);
            }
        }

        private static void ReplaceFrom(string source, string target)
        {
            string temporary = target + ".update-" + Guid.NewGuid().ToString("N");
            try
            {
                File.Copy(source, temporary, false);
                if (File.Exists(target))
                    File.Replace(temporary, target, null);
                else
                    File.Move(temporary, target);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static string ValidateEntryName(string name, out string root)
        {
            root = "";
            if (string.IsNullOrEmpty(name) || name.StartsWith("/", StringComparison.Ordinal) || name.StartsWith("\\", StringComparison.Ordinal) || name.Any(char.IsControl) || name.Contains(":"))
                throw new InvalidDataException("更新包文件路径无效。");
            string normalized = name.Replace('\\', '/');
            bool directory = normalized.EndsWith("/", StringComparison.Ordinal);
            string[] parts = normalized.TrimEnd('/').Split('/');
            if (parts.Length > 2 || parts.Any(part => part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal)))
                throw new InvalidDataException("更新包存在越界或嵌套文件路径。");
            if (directory)
            {
                if (parts.Length != 1)
                    throw new InvalidDataException("更新包包含额外目录。");
                return null;
            }
            if (parts.Length == 2)
                root = parts[0];
            return parts[parts.Length - 1];
        }

        private static void ValidateInfo(UpdateInfo info)
        {
            if (info == null)
                throw new ArgumentNullException("info");
            NormalizedVersion(info.Version);
            Uri service = ValidateServiceUri(info.BaseUrl);
            Uri download;
            if (!Uri.TryCreate(info.Url, UriKind.Absolute, out download) || download.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(download.UserInfo) || !string.IsNullOrEmpty(download.Fragment) ||
                !string.Equals(download.Host, service.Host, StringComparison.OrdinalIgnoreCase) || download.Port != service.Port ||
                !download.AbsolutePath.StartsWith(new Uri(service, "download/").AbsolutePath, StringComparison.Ordinal) || !download.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包必须来自同一 FEIAN HTTPS 下载地址。");
            if (!ValidHash(info.Sha256) || info.Size <= 0 || info.Size > MaximumPackageBytes || (info.Notes ?? "").Length > 8192)
                throw new InvalidDataException("更新清单的大小或校验值无效。");
        }

        private static Uri ValidateServiceUri(string baseUrl)
        {
            Uri service;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out service) || service.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(service.UserInfo) || !string.IsNullOrEmpty(service.Query) || !string.IsNullOrEmpty(service.Fragment) ||
                !(string.Equals(service.Host, "feian.online", StringComparison.OrdinalIgnoreCase) || service.Host.EndsWith(".feian.online", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("更新服务必须是 FEIAN 的 HTTPS 地址。");
            return new Uri(service.AbsoluteUri.TrimEnd('/') + "/");
        }

        private static void ValidatePlanLocations(InstallPlan plan, string planPath)
        {
            if (plan == null || plan.Schema != 1 || plan.ExpiresUtcTicks < DateTime.UtcNow.Ticks || !Guid.TryParseExact(plan.Nonce, "N", out _))
                throw new InvalidDataException("更新计划已失效。");
            NormalizedVersion(plan.Version);
            string root = UpdatesRoot();
            string stage = Path.GetFullPath(plan.StageDirectory);
            if (!SamePath(Path.GetDirectoryName(stage), root) || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _) || !SamePath(planPath, Path.Combine(stage, "plan.json")))
                throw new InvalidDataException("更新暂存目录边界不正确。");
            EnsurePlainDirectory(stage);
            string install = Path.GetFullPath(plan.InstallDirectory);
            EnsurePlainDirectory(install);
            ValidateInstalledTeam(install);
            if (!SamePath(Path.GetDirectoryName(plan.ParentExecutable), install) || !string.Equals(Path.GetFileName(plan.ParentExecutable), ApplicationFile, StringComparison.OrdinalIgnoreCase) ||
                IsInside(install, stage) || IsInside(stage, install) || !ValidHash(plan.PackageSha256) || plan.PackageSize <= 0 || plan.PackageSize > MaximumPackageBytes || plan.ParentProcessId <= 0)
                throw new InvalidDataException("更新安装位置或校验信息无效。");
        }

        private static void EnsurePlainDirectory(string path)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            if (!directory.Exists)
                throw new DirectoryNotFoundException("目录不存在。" + path);
            for (var current = directory; current != null; current = current.Parent)
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("更新目录不能通过符号链接或目录联接定位。");
        }
        private static void EnsurePlainFile(string path)
        {
            EnsurePlainDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新文件缺失或包含链接。" + Path.GetFileName(path));
        }
        private static void PruneBackups(string root, string current)
        {
            foreach (var directory in new DirectoryInfo(root).GetDirectories().OrderByDescending(item => item.CreationTimeUtc).Skip(2))
            {
                if (SamePath(directory.FullName, current) || !File.Exists(Path.Combine(directory.FullName, "backup.json")))
                    continue;
                try
                {
                    DeletePlainDirectory(directory.FullName, root);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidDataException) { }
            }
        }
        private static void CleanupStagePayload(string stage)
        {
            foreach (string name in new[] { "payload", "verified" })
            {
                string path = Path.Combine(stage, name);
                if (Directory.Exists(path))
                    DeletePlainDirectory(path, stage);
            }
            string package = Path.Combine(stage, "package.zip");
            if (File.Exists(package))
                File.Delete(package);
        }
        private static void DeleteOwnedStage(string stage)
        {
            if (Directory.Exists(stage))
                DeletePlainDirectory(stage, UpdatesRoot());
        }
        private static void PruneCompletedStages()
        {
            string root = UpdatesRoot();
            if (!Directory.Exists(root))
                return;
            foreach (string directory in Directory.GetDirectories(root))
            {
                Guid identifier;
                string resultPath = Path.Combine(directory, "result.json");
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out identifier) || !File.Exists(resultPath))
                    continue;
                try
                {
                    var result = ReadJson<Dictionary<string, object>>(resultPath);
                    object succeeded;
                    if (result != null && result.TryGetValue("success", out succeeded) && succeeded is bool && (bool)succeeded)
                    {
                        string readyPath = Path.Combine(directory, "helper-ready.json");
                        if (File.Exists(readyPath))
                        {
                            var ready = ReadJson<ReadySignal>(readyPath);
                            try
                            {
                                using (var helper = Process.GetProcessById(ready.HelperProcessId))
                                    if (!helper.HasExited && SamePath(helper.MainModule.FileName, Path.Combine(directory, HelperFile)))
                                        continue;
                            }
                            catch (ArgumentException) { }
                            catch (System.ComponentModel.Win32Exception) { continue; }
                        }
                        DeleteOwnedStage(directory);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (InvalidDataException) { }
                catch (ArgumentException) { }
            }
        }
        private static void ValidateInstalledTeam(string installDirectory)
        {
            string path = Path.Combine(installDirectory, "team.json");
            EnsurePlainFile(path);
            var team = ReadJson<Dictionary<string, object>>(path);
            object roomKey;
            object port;
            object broadcast;
            object name;
            IPAddress address;
            if (team == null || !team.TryGetValue("RoomKey", out roomKey) || !(roomKey is string) || Convert.FromBase64String((string)roomKey).Length != 32 ||
                !team.TryGetValue("RoomName", out name) || string.IsNullOrWhiteSpace(name as string) ||
                !team.TryGetValue("Port", out port) || Convert.ToInt32(port) < 1024 || Convert.ToInt32(port) > 65535 ||
                !team.TryGetValue("BroadcastAddress", out broadcast) || !IPAddress.TryParse(broadcast as string, out address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidDataException("安装目录缺少有效的本地团队配置，请完整解压办公室分发包后再更新。");
        }
        private static void DeletePlainDirectory(string directory, string boundary)
        {
            if (!IsInside(directory, boundary) || SamePath(directory, boundary))
                throw new InvalidDataException("清理目录超出更新边界。");
            EnsurePlainDirectory(directory);
            if (new DirectoryInfo(directory).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(item => (item.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException("清理目录包含链接。");
            Directory.Delete(directory, true);
        }
        private static InstallPlan ReadPlan(string path)
        {
            return ReadJson<InstallPlan>(path);
        }
        private static T ReadJson<T>(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 65536)
                throw new InvalidDataException("更新计划或记录大小无效。");
            return new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        }
        private static void WriteJson(string path, object value)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
        private static string ReadString(Dictionary<string, object> values, string key)
        {
            object value;
            if (!values.TryGetValue(key, out value) || !(value is string))
                throw new InvalidDataException("更新清单缺少 " + key);
            return (string)value;
        }
        private static Version NormalizedVersion(string text)
        {
            Version version;
            if (!Version.TryParse(text, out version) || version.Major < 0 || version.Minor < 0)
                throw new InvalidDataException("更新版本号无效。");
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }
        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var client = new HttpClient(handler) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MailReminder-Updater/1.3");
            return client;
        }
        private static async Task<long> CopyLimitedAsync(Stream input, Stream output, long maximum, CancellationToken token)
        {
            long count = 0;
            var buffer = new byte[65536];
            int length;
            while ((length = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
            {
                count += length;
                if (count > maximum)
                    throw new InvalidDataException("下载内容超出允许大小。");
                await output.WriteAsync(buffer, 0, length, token).ConfigureAwait(false);
            }
            return count;
        }
        private static void CopyLimited(Stream input, Stream output, long expected)
        {
            long count = 0;
            var buffer = new byte[65536];
            int length;
            while ((length = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                count += length;
                if (count > expected)
                    throw new InvalidDataException("解压内容超过文件标记长度。");
                output.Write(buffer, 0, length);
            }
            if (count != expected)
                throw new InvalidDataException("解压文件不完整。");
        }
        private static bool ValidHash(string hash)
        {
            return hash != null && hash.Length == 64 && hash.All(Uri.IsHexDigit);
        }
        private static bool HashEquals(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
        private static string StreamHash(Stream stream)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }
        internal static string FileHash(string path)
        {
            using (var input = File.OpenRead(path))
                return StreamHash(input);
        }
        private static string CurrentExecutable()
        {
            return Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
        }
        private static string UpdatesRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfficeSignal", "Updates");
        }
        private static bool SamePath(string first, string second)
        {
            return string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        private static bool IsInside(string path, string boundary)
        {
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(boundary).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        private static string QuoteArgument(string text)
        {
            return "\"" + text.Replace("\"", "\\\"") + "\"";
        }

        private sealed class InstallPlan
        {
            public InstallPlan()
            {
            }
            public int Schema
            {
                get; set;
            }
            public string Version
            {
                get; set;
            }
            public string StageDirectory
            {
                get; set;
            }
            public string InstallDirectory
            {
                get; set;
            }
            public string PackageSha256
            {
                get; set;
            }
            public long PackageSize
            {
                get; set;
            }
            public int ParentProcessId
            {
                get; set;
            }
            public long ParentStartUtcTicks
            {
                get; set;
            }
            public string ParentExecutable
            {
                get; set;
            }
            public string HelperSha256
            {
                get; set;
            }
            public string Nonce
            {
                get; set;
            }
            public long ExpiresUtcTicks
            {
                get; set;
            }
        }
        private sealed class ReadySignal
        {
            public ReadySignal()
            {
            }
            public string Nonce
            {
                get; set;
            }
            public int HelperProcessId
            {
                get; set;
            }
        }
        private sealed class BackupRecord
        {
            public BackupRecord()
            {
            }
            public string Version
            {
                get; set;
            }
            public List<BackupFile> Files
            {
                get; set;
            }
        }
        private sealed class BackupFile
        {
            public BackupFile()
            {
            }
            public string Name
            {
                get; set;
            }
            public bool Existed
            {
                get; set;
            }
        }
    }
}
