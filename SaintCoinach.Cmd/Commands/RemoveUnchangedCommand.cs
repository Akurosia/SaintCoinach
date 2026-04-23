using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Tharga.Console.Commands.Base;

#pragma warning disable CS1998

namespace SaintCoinach.Cmd.Commands {
    public class RemoveUnchangedCommand : AsyncActionCommandBase {
        private const string DefaultComparePath = @"..\..\Versions\latest";
        private static readonly HashSet<string> ExcludedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "done_by.script"
        };

        private readonly ARealmReversed _Realm;

        public RemoveUnchangedCommand(ARealmReversed realm)
            : base("removeunchanged", "Remove files from the current version that are unchanged from a compare folder.") {
            _Realm = realm;
        }

        public override async Task InvokeAsync(string[] paramList) {
            if (paramList.Length > 1) {
                OutputError("Usage: removeunchanged [compare-folder]");
                return;
            }

            var oldVersion = paramList.Length == 1 ? paramList[0] : DefaultComparePath;
            var oldDir = ResolvePath(oldVersion);
            var newDir = Path.Combine(AppContext.BaseDirectory, _Realm.GameVersion);
            OutputInformation($"[RUF] Set Game_Location to {AppContext.BaseDirectory}");
            OutputInformation($"[RUF] Start comparing Version '{_Realm.GameVersion}' to '{oldVersion}'");

            if (!Directory.Exists(oldDir) || !Directory.Exists(newDir)) {
                OutputError("[ERR] One of the directories does not exist.");
                OutputError($"[ERR] Compare folder: {oldDir}");
                OutputError($"[ERR] Current folder: {newDir}");
                return;
            }

            var deleted = RemoveDuplicates(oldDir, newDir);
            var deletedFolders = RemoveEmptyFolders(newDir);
            OutputInformation($"[RUF] Done. Deleted {deleted} duplicate files.");
            OutputInformation($"[RUF] Deleted {deletedFolders} empty folders.");
        }

        private int RemoveDuplicates(string oldDir, string newDir) {
            OutputInformation($"[RUF] Indexing source version: {oldDir}");
            var oldFiles = GetAllFiles(oldDir);
            OutputInformation($"[RUF] Indexing target version: {newDir}");
            var newFiles = GetAllFiles(newDir);

            OutputInformation($"[RUF] Comparing {newFiles.Count} files");

            var deleted = 0;
            foreach (var pair in newFiles) {
                if (!oldFiles.TryGetValue(pair.Key, out var oldPath))
                    continue;

                var newPath = pair.Value;
                try {
                    var oldInfo = new FileInfo(oldPath);
                    var newInfo = new FileInfo(newPath);
                    if (!oldInfo.Exists || !newInfo.Exists || oldInfo.Length != newInfo.Length)
                        continue;

                    if (!Sha1Equals(oldPath, newPath))
                        continue;

                    File.Delete(newPath);
                    OutputInformation($"[RUF] Removed duplicate: {newPath}");
                    deleted++;
                }
                catch (Exception e) {
                    OutputError($"[ERR] Failed comparing {pair.Key}: {e.Message}");
                }
            }

            return deleted;
        }

        private int RemoveEmptyFolders(string path) {
            var deleted = 0;
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length)) {
                try {
                    if (Directory.EnumerateFileSystemEntries(dir).Any())
                        continue;

                    Directory.Delete(dir);
                    OutputInformation($"[RUF] Deleted empty folder: {dir}");
                    deleted++;
                }
                catch (Exception e) {
                    OutputError($"[ERR] Could not delete folder {dir}: {e.Message}");
                }
            }
            return deleted;
        }

        private static Dictionary<string, string> GetAllFiles(string baseDir) {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories)) {
                if (ExcludedFiles.Contains(Path.GetFileName(path)))
                    continue;

                var rel = Path.GetRelativePath(baseDir, path);
                files[rel] = path;
            }
            return files;
        }

        private static bool Sha1Equals(string firstPath, string secondPath) {
            using var first = File.OpenRead(firstPath);
            using var second = File.OpenRead(secondPath);
            using var sha1 = SHA1.Create();

            var firstHash = sha1.ComputeHash(first);
            var secondHash = sha1.ComputeHash(second);
            return firstHash.SequenceEqual(secondHash);
        }

        private static string ResolvePath(string path) {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }

    }
}
