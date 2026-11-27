using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RainMeadow
{
    internal sealed class DeltaPackage
    {
        internal const string Plugin = "plugins/Rain Meadow.dll";
        internal const string ManifestName = "RainMeadow-android-delta.json";
        internal const string ArchiveName = "RainMeadow-android-delta.zip";
        private const string WorkName = ".rainmeadow-delta";
        private const string JournalName = "journal.json";
        private const string ApprovedName = "approved";
        private const string OwnerName = "rainmeadow-updater";
        private static readonly Regex Hash = new Regex(@"\A[0-9a-f]{64}\z");
        private static readonly Regex Tag = new Regex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]*\z");
        private readonly Dictionary<string, string> before;
        private readonly Dictionary<string, string> after;
        private readonly string archiveHash;
        private readonly List<string> changes;
        internal string BaseTag { get; }
        internal string TargetTag { get; }

        internal DeltaPackage(JObject manifest)
        {
            if (manifest.Value<int>("schema") != 1)
                throw new InvalidDataException("Unsupported delta manifest format.");
            BaseTag = manifest.Value<string>("base_tag") ?? "";
            TargetTag = manifest.Value<string>("target_tag") ?? "";
            string revision = manifest.Value<string>("game_revision") ?? "";
            if (!ValidTag(BaseTag) || !ValidTag(TargetTag) || !ValidTag(revision) || BaseTag == TargetTag)
                throw new InvalidDataException("Invalid base or target release.");
            archiveHash = manifest.Value<string>("archive_sha256") ?? "";
            if (!ValidHash(archiveHash)) throw new InvalidDataException("Invalid delta archive hash.");
            before = ReadFiles(manifest.Value<JObject>("base_files"));
            after = ReadFiles(manifest.Value<JObject>("target_files"));
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in before.Keys.Union(after.Keys))
                if (!paths.Add(path)) throw new InvalidDataException("Case-only renames require a full install.");
            foreach (string path in paths)
                for (int slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
                    if (paths.Contains(path.Substring(0, slash)))
                        throw new InvalidDataException("File/directory changes require a full install.");
            changes = before.Keys.Union(after.Keys)
                .Where(path => !before.TryGetValue(path, out var oldSha) ||
                               !after.TryGetValue(path, out var newSha) || oldSha != newSha)
                .OrderBy(path => path == Plugin)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToList();
            if (changes.Count == 0) throw new InvalidDataException("Empty delta.");
        }

        private static Dictionary<string, string> ReadFiles(JObject? files)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (files == null) throw new InvalidDataException("Missing file hashes.");
            foreach (var property in files.Properties())
            {
                string sha = property.Value.ToString();
                if (!ValidPath(property.Name) || !ValidHash(sha))
                    throw new InvalidDataException("Invalid file in delta manifest.");
                result.Add(property.Name, sha);
            }
            if (!result.ContainsKey(Plugin) || !result.ContainsKey("modinfo.json"))
                throw new InvalidDataException("Delta lacks files required by the mod loader.");
            return result;
        }

        internal static bool ValidTag(string value) =>
            value.Length <= 128 && Tag.IsMatch(value) && !value.Contains("..") &&
            !value.EndsWith(".", StringComparison.Ordinal) && !value.EndsWith(".lock", StringComparison.Ordinal);

        internal static bool ValidHash(string value) => Hash.IsMatch(value);

        private static bool ValidPath(string name) =>
            !name.Any(character => character < 32 || "\\:*?\"<>|".Contains(character)) &&
            name.Split('/').All(part => part.Length != 0 && part[0] != '.' && part == part.TrimEnd(' ', '.'));

        internal static string Digest(Stream stream)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static string FileHash(string path)
        {
            using var stream = File.OpenRead(path);
            return Digest(stream);
        }

        private static void RejectLink(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Symbolic link in delta path.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        private static string Resolve(string root, string path)
        {
            if (!ValidPath(path)) throw new InvalidDataException("Unsafe delta path.");
            RejectLink(root);
            foreach (string part in path.Split('/'))
            {
                root = Path.Combine(root, part);
                RejectLink(root);
            }
            return root;
        }

        private static string WorkPath(string root)
        {
            RejectLink(root);
            string path = Path.Combine(root, WorkName);
            RejectLink(path);
            return path;
        }

        internal bool IsBase(string root) => Matches(root, before, after.Keys.Except(before.Keys));

        internal bool IsTarget(string root) => Matches(root, after, before.Keys.Except(after.Keys));

        private static bool Matches(string root, Dictionary<string, string> files, IEnumerable<string> absent)
        {
            foreach (var item in files)
            {
                string path = Resolve(root, item.Key);
                if (!File.Exists(path) || FileHash(path) != item.Value) return false;
            }
            foreach (string item in absent)
            {
                string path = Resolve(root, item);
                if (File.Exists(path) || Directory.Exists(path)) return false;
            }
            return true;
        }

        internal static string BeginDownload(string root)
        {
            string work = WorkPath(root);
            if (Directory.Exists(work)) throw new IOException("An update is already staged.");
            Directory.CreateDirectory(work);
            using (File.Open(Path.Combine(work, OwnerName), FileMode.CreateNew)) { }
            return Path.Combine(work, "archive");
        }

        internal void Prepare(string root, JObject manifest)
        {
            string work = WorkPath(root);
            string archivePath = Path.Combine(work, "archive");
            RejectLink(archivePath);
            if (!IsBase(root) || FileHash(archivePath) != archiveHash)
                throw new InvalidDataException("Installed base or downloaded delta failed verification.");

            using (var input = File.OpenRead(archivePath))
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
            {
                var items = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                foreach (var entry in archive.Entries)
                    items.Add(entry.FullName, entry);
                int expected = 0;
                for (int i = 0; i < changes.Count; i++)
                {
                    string name = changes[i];
                    if (after.TryGetValue(name, out string newSha))
                    {
                        expected++;
                        if (!items.TryGetValue(name, out var entry))
                            throw new InvalidDataException("Missing delta file.");
                        string staged = Path.Combine(work, "new-" + i);
                        using (var content = entry.Open())
                        using (var output = File.Open(staged, FileMode.CreateNew))
                            content.CopyTo(output);
                        if (FileHash(staged) != newSha)
                            throw new InvalidDataException("Delta file checksum failed.");
                    }
                    if (before.TryGetValue(name, out string oldSha))
                    {
                        string backup = Path.Combine(work, "old-" + i);
                        File.Copy(Resolve(root, name), backup);
                        if (FileHash(backup) != oldSha)
                            throw new InvalidDataException("Installed file changed during download.");
                    }
                }
                if (items.Count != expected) throw new InvalidDataException("Unexpected data in delta ZIP.");
            }
            File.Delete(archivePath);
            using var journal = File.Open(Path.Combine(work, JournalName), FileMode.CreateNew);
            using var writer = new System.IO.StreamWriter(journal);
            writer.Write(manifest.ToString());
            writer.Flush();
            journal.Flush(true);
        }

        internal void Apply(string root)
        {
            string work = WorkPath(root);
            if (!IsBase(root)) throw new InvalidDataException("Installed mod files changed. Use the full ZIP.");
            using (var marker = File.Open(Path.Combine(work, ApprovedName), FileMode.CreateNew)) marker.Flush(true);
            try
            {
                for (int i = 0; i < changes.Count; i++)
                {
                    string path = changes[i];
                    string target = Resolve(root, path);
                    if (after.ContainsKey(path))
                    {
                        string source = Resolve(work, "new-" + i);
                        if (FileHash(source) != after[path]) throw new InvalidDataException("Modified staged file.");
                        WriteFile(source, target);
                    }
                    else File.Delete(target);
                }
            }
            catch
            {
                Recover(root);
                throw;
            }
            File.Delete(Path.Combine(work, ApprovedName));
            try { Discard(root); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void WriteFile(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) File.Replace(source, destination, null);
            else File.Move(source, destination);
        }

        internal static void Discard(string root)
        {
            string work = WorkPath(root);
            if (!File.Exists(Resolve(work, OwnerName)) || File.Exists(Resolve(work, ApprovedName))) return;
            string retired = work + "-cleanup-" + Guid.NewGuid().ToString("N");
            Directory.Move(work, retired);
            Directory.Delete(retired, true);
        }

        internal static void Recover(string root)
        {
            string work = WorkPath(root);
            if (!File.Exists(Resolve(work, OwnerName))) return;
            string approved = Resolve(work, ApprovedName);
            if (File.Exists(approved))
            {
                string journal = Resolve(work, JournalName);
                var update = new DeltaPackage(JObject.Parse(File.ReadAllText(journal)));
                if (!update.IsTarget(root)) update.RollBack(root, work);
                File.Delete(approved);
            }
            Discard(root);
        }

        private void RollBack(string root, string work)
        {
            for (int i = 0; i < changes.Count; i++)
            {
                string name = changes[i];
                string destination = Resolve(root, name);
                if (before.TryGetValue(name, out string oldSha))
                {
                    string backup = Resolve(work, "old-" + i);
                    if (FileHash(backup) != oldSha) throw new InvalidDataException("Delta recovery backup was modified.");
                    string staged = Resolve(work, "restore-" + i);
                    File.Copy(backup, staged, true);
                    WriteFile(staged, destination);
                }
                else if (File.Exists(destination) && FileHash(destination) == after[name])
                {
                    File.Delete(destination);
                }
            }
        }
    }
}
