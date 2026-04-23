using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using SaintCoinach.Imaging;
using SharpCompress.Archives.SevenZip;
using Tharga.Console.Commands.Base;

#pragma warning disable CS1998

namespace SaintCoinach.Cmd.Commands {
    public class UldCommand : AsyncActionCommandBase {
        private const string UldPrefix = "ui/uld/";
        private const string DefaultPathListUrl = "https://rl2.perchbird.dev/download/PathList.gz";
        private const string DefaultPathListCacheName = "PathList.gz";
        private static readonly TimeSpan PathListCacheTime = TimeSpan.FromDays(1);

        private readonly ARealmReversed _Realm;

        public UldCommand(ARealmReversed realm)
            : base("uld", "Export ULD textures from the game assets as WebP. Usage: uld [output-dir] [PathList.gz|hashlist.7z|hashlist.sqlite|paths.txt|icons.json]") {
            _Realm = realm;
        }

        public override async Task InvokeAsync(string[] paramList) {
            if (paramList.Length > 2) {
                OutputError("Usage: uld [output-dir] [PathList.gz|hashlist.7z|hashlist.sqlite|paths.txt|icons.json]");
                return;
            }

            var outputRoot = ResolvePath(paramList.Length >= 1 ? paramList[0] : Path.Combine(_Realm.GameVersion, "ui", "uld"));
            var hashSource = paramList.Length >= 2 ? ResolvePath(paramList[1]) : await GetDefaultPathListAsync();
            if (string.IsNullOrWhiteSpace(hashSource) || !File.Exists(hashSource)) {
                OutputError("ULD export needs a path/hash list so the hashed index entries can be resolved.");
                OutputError($"Could not download or find {DefaultPathListCacheName}.");
                return;
            }

            Directory.CreateDirectory(outputRoot);
            OutputInformation($"ULD output: {outputRoot}");
            OutputInformation($"ULD hash source: {hashSource}");

            var paths = LoadUldPathDatabase(hashSource);

            if (paths.FolderCount == 0) {
                OutputError("No ui/uld texture paths were found in the hash source.");
                return;
            }

            var exports = BuildExports(paths);
            OutputInformation($"Found {exports.Count} ULD texture files in game assets");

            var exported = 0;
            var missing = 0;
            var skipped = 0;
            var fallbackNames = 0;
            foreach (var export in exports) {
                try {
                    var file = export.File;
                    if (export.UsedFallbackName)
                        fallbackNames++;

                    ImageFile imgFile = null;
                    if (export.GamePath.EndsWith(".atex", StringComparison.OrdinalIgnoreCase) && file is IO.FileDefault defaultFile)
                        imgFile = new ImageFile(defaultFile);
                    else if (file is ImageFile imageFile)
                        imgFile = imageFile;

                    if (imgFile == null) {
                        skipped++;
                        continue;
                    }

                    var target = CombineOutputPath(outputRoot, export.RelativePath) + ImageExportHelper.WebpExtension;
                    var directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory))
                        Directory.CreateDirectory(directory);

                    ImageExportHelper.SaveAsDropperWebp(imgFile.GetImage(), target);
                    exported++;
                    if (exported % 250 == 0)
                        OutputInformation($"Exported {exported}/{exports.Count} ULD textures");
                }
                catch (Exception e) {
                    skipped++;
                    OutputError($"{export.GamePath}: {e.Message}");
                }
            }

            OutputInformation($"ULD export complete: {exported} exported, {missing} missing, {skipped} skipped, {fallbackNames} fallback names");
        }

        private List<UldExport> BuildExports(UldPathDatabase paths) {
            var pack = _Realm.Packs.GetPack(new IO.PackIdentifier("ui", IO.PackIdentifier.DefaultExpansion, 0));
            if (!(pack.Source is IO.IndexSource indexSource))
                throw new NotSupportedException("ULD export requires the 060000 index source.");

            var result = new List<UldExport>();
            foreach (var indexDirectory in indexSource.Index.Directories.Values.OrderBy(d => d.Key)) {
                if (!paths.TryGetFolder(indexDirectory.Key, out var folder))
                    continue;

                var directory = indexSource.GetDirectory(indexDirectory.Key);
                foreach (var indexFile in indexDirectory.Files.Values.OrderBy(f => f.FileKey)) {
                    var usedFallback = !folder.FileNames.TryGetValue(indexFile.FileKey, out var fileName);
                    if (usedFallback)
                        fileName = GetFallbackFileName(indexFile.FileKey);

                    var file = directory.GetFile(indexFile.FileKey);
                    if (file == null)
                        continue;

                    result.Add(new UldExport {
                        GamePath = folder.Path + "/" + fileName,
                        RelativePath = CombineRelativePath(folder.RelativePath, fileName),
                        File = file,
                        UsedFallbackName = usedFallback
                    });
                }
            }

            return result
                .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static UldPathDatabase LoadUldPathDatabase(string source) {
            var result = new UldPathDatabase();
            foreach (var path in LoadUldPaths(source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
                result.Add(path);
            }
            return result;
        }

        private static IEnumerable<string> LoadUldPaths(string source) {
            var extension = Path.GetExtension(source);
            if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
                return LoadUldPathsFromArchive(source);
            if (extension.Equals(".gz", StringComparison.OrdinalIgnoreCase))
                return LoadUldPathsFromGzip(source);
            if (extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase))
                return LoadUldPathsFromSqlite(source);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                return LoadUldPathsFromJson(source);
            return LoadUldPathsFromText(source);
        }

        private static IEnumerable<string> LoadUldPathsFromArchive(string source) {
            var tempPath = Path.Combine(Path.GetTempPath(), "saintcoinach-hashlist-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".sqlite");
            try {
                using (var archive = SevenZipArchive.Open(source)) {
                    var entry = archive.Entries
                        .Where(e => !e.IsDirectory)
                        .FirstOrDefault(e => IsSqliteCandidate(e.Key));
                    if (entry == null)
                        return Array.Empty<string>();

                    using var input = entry.OpenEntryStream();
                    using var stream = File.Create(tempPath);
                    input.CopyTo(stream);
                }

                return LoadUldPathsFromSqlite(tempPath).ToList();
            }
            finally {
                try {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch {
                    // Best-effort cleanup only.
                }
            }
        }

        private static IEnumerable<string> LoadUldPathsFromSqlite(string source) {
            var filenames = ReadLookupTable(source, "filenames");
            var folders = ReadLookupTable(source, "folders");
            var result = new List<string>();

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "select * from fullpaths";
            using var reader = command.ExecuteReader();
            while (reader.Read()) {
                var indexId = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (indexId != 0x060000)
                    continue;

                var folderId = Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture);
                var fileId = Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture);
                if (!folders.TryGetValue(folderId, out var folder) || !filenames.TryGetValue(fileId, out var file))
                    continue;

                var path = NormalizePath(folder + "/" + file);
                if (IsUldTexturePath(path))
                    result.Add(path);
            }

            return result;
        }

        private static Dictionary<long, string> ReadLookupTable(string source, string table) {
            var result = new Dictionary<long, string>();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"select * from {table}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                result[Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture)] = reader.GetString(1);
            return result;
        }

        private static IEnumerable<string> LoadUldPathsFromJson(string source) {
            var json = JObject.Parse(File.ReadAllText(source));
            foreach (var group in json.Properties()) {
                if (Regex.IsMatch(group.Name, "^\\d+$"))
                    continue;

                var obj = group.Value as JObject;
                if (obj == null)
                    continue;

                foreach (var listName in new[] { "both", "hronly", "nohr" }) {
                    if (!(obj[listName] is JArray files))
                        continue;

                    foreach (var fileToken in files) {
                        var file = NormalizeUldTextureName(fileToken.ToString());
                        if (!string.IsNullOrWhiteSpace(file))
                            yield return UldPrefix + group.Name.ToLowerInvariant() + "/" + file;
                    }
                }
            }
        }

        private static IEnumerable<string> LoadUldPathsFromText(string source) {
            return LoadUldPathsFromLines(File.ReadLines(source));
        }

        private static IEnumerable<string> LoadUldPathsFromGzip(string source) {
            using var file = File.OpenRead(source);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);

            string line;
            while ((line = reader.ReadLine()) != null)
                foreach (var path in LoadUldPathsFromLine(line))
                    yield return path;
        }

        private static IEnumerable<string> LoadUldPathsFromLines(IEnumerable<string> lines) {
            foreach (var rawLine in lines) {
                foreach (var path in LoadUldPathsFromLine(rawLine))
                    yield return path;
            }
        }

        private static IEnumerable<string> LoadUldPathsFromLine(string rawLine) {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                yield break;

            var comma = line.IndexOf(',');
            if (comma >= 0 && comma + 1 < line.Length)
                line = line.Substring(comma + 1).Trim();

            var path = NormalizePath(line);
            if (IsUldTexturePath(path))
                yield return path;
        }

        private static string NormalizeUldTextureName(string name) {
            var result = NormalizePath(name);
            if (result.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || result.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(0, result.LastIndexOf('.'));
            return result.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                || result.EndsWith(".atex", StringComparison.OrdinalIgnoreCase)
                ? result
                : "";
        }

        private static string NormalizePath(string path) {
            return path.Trim().Trim('"').Replace('\\', '/').ToLowerInvariant();
        }

        private static bool IsUldTexturePath(string path) {
            return path.StartsWith(UldPrefix, StringComparison.OrdinalIgnoreCase)
                && (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".atex", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSqliteCandidate(string key) {
            var extension = Path.GetExtension(key);
            return extension.Equals(".db", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase)
                || key.IndexOf("hash", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string GetFallbackFileName(uint fileKey) {
            return "~" + (fileKey & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture) + ".tex";
        }

        private static string CombineRelativePath(string folder, string fileName) {
            return string.IsNullOrWhiteSpace(folder) ? fileName : folder.Trim('/') + "/" + fileName.Trim('/');
        }

        private static string CombineOutputPath(string outputRoot, string relativePath) {
            var parts = relativePath
                .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            return parts.Length == 0 ? outputRoot : Path.Combine(new[] { outputRoot }.Concat(parts).ToArray());
        }

        private async Task<string> GetDefaultPathListAsync() {
            var path = Path.Combine(AppContext.BaseDirectory, DefaultPathListCacheName);
            if (IsFreshCache(path)) {
                OutputInformation($"Using cached {DefaultPathListCacheName}: {path}");
                return path;
            }

            OutputInformation($"Downloading {DefaultPathListCacheName} from {DefaultPathListUrl}");
            try {
                using var client = new HttpClient();
                using var response = await client.GetAsync(DefaultPathListUrl);
                response.EnsureSuccessStatusCode();

                using var input = await response.Content.ReadAsStreamAsync();
                using var output = File.Create(path);
                await input.CopyToAsync(output);

                OutputInformation($"Cached {DefaultPathListCacheName}: {path}");
                return path;
            }
            catch (Exception e) {
                if (File.Exists(path)) {
                    OutputError($"Could not refresh {DefaultPathListCacheName}, using stale cache. {e.Message}");
                    return path;
                }

                OutputError($"Could not download {DefaultPathListCacheName}. {e.Message}");
                return null;
            }
        }

        private static bool IsFreshCache(string path) {
            if (!File.Exists(path))
                return false;

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            return age >= TimeSpan.Zero && age < PathListCacheTime;
        }

        private static string ResolvePath(string path) {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }

        private class UldPathDatabase {
            private readonly Dictionary<uint, UldFolder> _Folders = new Dictionary<uint, UldFolder>();

            public int FolderCount {
                get { return _Folders.Count; }
            }

            public void Add(string path) {
                var split = path.LastIndexOf('/');
                if (split <= 0)
                    return;

                var folderPath = path.Substring(0, split);
                var fileName = path.Substring(split + 1);
                var folderKey = IO.Hash.Compute(folderPath);
                if (!_Folders.TryGetValue(folderKey, out var folder)) {
                    folder = new UldFolder(folderPath);
                    _Folders.Add(folderKey, folder);
                }

                folder.FileNames[IO.Hash.Compute(fileName)] = fileName;
            }

            public bool TryGetFolder(uint key, out UldFolder folder) {
                return _Folders.TryGetValue(key, out folder);
            }
        }

        private class UldFolder {
            public UldFolder(string path) {
                Path = path;
                RelativePath = GetRelativeFolderPath(path);
            }

            public string Path { get; }
            public string RelativePath { get; }
            public Dictionary<uint, string> FileNames { get; } = new Dictionary<uint, string>();
        }

        private class UldExport {
            public string GamePath { get; set; }
            public string RelativePath { get; set; }
            public IO.File File { get; set; }
            public bool UsedFallbackName { get; set; }
        }

        private static string GetRelativeFolderPath(string path) {
            var normalized = NormalizePath(path);
            if (normalized.Equals("ui/uld", StringComparison.OrdinalIgnoreCase))
                return "";

            if (normalized.StartsWith(UldPrefix, StringComparison.OrdinalIgnoreCase))
                return normalized.Substring(UldPrefix.Length);

            return normalized.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
