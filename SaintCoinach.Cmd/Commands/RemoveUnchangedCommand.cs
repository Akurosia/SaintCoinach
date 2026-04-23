using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

            var candidates = newFiles
                .Where(pair => oldFiles.ContainsKey(pair.Key))
                .Select(pair => new CompareCandidate(pair.Key, oldFiles[pair.Key], pair.Value))
                .ToList();

            OutputInformation($"[RUF] {candidates.Count} files exist in both versions");

            var deletedPaths = new ConcurrentBag<string>();
            var errors = new ConcurrentBag<string>();
            var options = new ParallelOptions {
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 8))
            };

            Parallel.ForEach(candidates, options, candidate => {
                try {
                    var oldInfo = new FileInfo(candidate.OldPath);
                    var newInfo = new FileInfo(candidate.NewPath);
                    if (!oldInfo.Exists || !newInfo.Exists || oldInfo.Length != newInfo.Length)
                        return;

                    if (!FilesEqual(candidate.OldPath, candidate.NewPath))
                        return;

                    File.Delete(candidate.NewPath);
                    deletedPaths.Add(candidate.NewPath);
                }
                catch (Exception e) {
                    errors.Add($"[ERR] Failed comparing {candidate.RelativePath}: {e.Message}");
                }
            });

            foreach (var path in deletedPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                OutputInformation($"[RUF] Removed duplicate: {path}");
            foreach (var error in errors.OrderBy(error => error, StringComparer.OrdinalIgnoreCase))
                OutputError(error);

            return deletedPaths.Count;
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

        private static bool FilesEqual(string firstPath, string secondPath) {
            const int bufferSize = 1024 * 1024;

            using var first = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
            using var second = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);

            var firstBuffer = new byte[bufferSize];
            var secondBuffer = new byte[bufferSize];

            while (true) {
                var firstRead = first.Read(firstBuffer, 0, firstBuffer.Length);
                var secondRead = second.Read(secondBuffer, 0, secondBuffer.Length);

                if (firstRead != secondRead)
                    return false;
                if (firstRead == 0)
                    return true;
                if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead)))
                    return false;
            }
        }

        private static string ResolvePath(string path) {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }

        private class CompareCandidate {
            public CompareCandidate(string relativePath, string oldPath, string newPath) {
                RelativePath = relativePath;
                OldPath = oldPath;
                NewPath = newPath;
            }

            public string RelativePath { get; }
            public string OldPath { get; }
            public string NewPath { get; }
        }
    }
}
