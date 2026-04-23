using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SaintCoinach.Ex;
using SaintCoinach.Imaging;
using SaintCoinach.Xiv;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Tharga.Console.Commands.Base;

#pragma warning disable CS1998

namespace SaintCoinach.Cmd.Commands {
    public class SpriteCommand : AsyncActionCommandBase {
        private const int MaxImageSide = 300;
        private static readonly string[] Categories = { "uld", "icon" };
        private static readonly string[] TextColumns = { "Name", "Singular", "Title", "PlaceName", "AethernetName", "Text" };
        private static readonly Ex.Language[] TextLanguages = { Ex.Language.German, Ex.Language.English };

        private readonly ARealmReversed _Realm;

        public SpriteCommand(ARealmReversed realm)
            : base("sprite", "Generate sprite overview JSON, sprite sheets, CSS, icon text links, and webpages.") {
            _Realm = realm;
        }

        public override async Task InvokeAsync(string[] paramList) {
            if (paramList.Length > 2) {
                OutputError("Usage: sprite [input-dir] [output-dir]");
                return;
            }

            var sourceRoot = ResolvePath(paramList.Length >= 1 ? paramList[0] : Path.Combine(_Realm.GameVersion, "ui"));
            var outputRoot = ResolvePath(paramList.Length >= 2 ? paramList[1] : Path.Combine(_Realm.GameVersion, "sprite"));
            Directory.CreateDirectory(Path.Combine(outputRoot, "css"));
            Directory.CreateDirectory(Path.Combine(outputRoot, "png"));
            Directory.CreateDirectory(Path.Combine(outputRoot, "html"));

            OutputInformation($"Sprite source: {sourceRoot}");
            OutputInformation($"Sprite output: {outputRoot}");

            var icons = BuildIconGroups(sourceRoot);
            WriteJson(Path.Combine(outputRoot, "icons.json"), icons, true);
            OutputInformation($"Wrote icons.json with {icons.Count} groups");

            var entries = BuildSprites(sourceRoot, outputRoot, icons);
            OutputInformation($"Generated {entries.Count} sprite entries");

            var links = BuildIconTextLinks(icons);
            WriteJson(Path.Combine(outputRoot, "icon_game_text.json"), links, false);
            OutputInformation($"Wrote icon_game_text.json with {links.Count} icon entries");

            SpriteHtmlCommand.GenerateHtml(outputRoot, message => OutputInformation(message));
            OutputInformation("Sprite export complete");
        }

        private Dictionary<string, IconGroup> BuildIconGroups(string sourceRoot) {
            var result = new Dictionary<string, IconGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in Categories) {
                var categoryPath = Path.Combine(sourceRoot, category);
                if (!Directory.Exists(categoryPath))
                    continue;

                foreach (var folder in Directory.EnumerateDirectories(categoryPath).OrderBy(p => p)) {
                    var groupName = Path.GetFileName(folder);
                    var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                        .Where(IsImage)
                        .Where(p => p.IndexOf("_ow_", StringComparison.OrdinalIgnoreCase) < 0)
                        .Select(Path.GetFileName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var group = new IconGroup();
                    foreach (var file in files) {
                        if (file.IndexOf("_hr1", StringComparison.OrdinalIgnoreCase) >= 0) {
                            if (files.Contains(file.Replace("_hr1", "", StringComparison.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                                AddUnique(group.Both, file);
                            else
                                AddUnique(group.HrOnly, file);
                        } else {
                            var hr = AddHr(file);
                            if (files.Contains(hr, StringComparer.OrdinalIgnoreCase))
                                AddUnique(group.Both, hr);
                            else
                                AddUnique(group.NoHr, file);
                        }
                    }

                    result[groupName] = group;
                }
            }
            return result;
        }

        private List<SpriteEntry> BuildSprites(string sourceRoot, string outputRoot, Dictionary<string, IconGroup> icons) {
            var jobs = new List<SpriteBuildJob>();
            foreach (var category in Categories) {
                var categoryPath = Path.Combine(sourceRoot, category);
                if (!Directory.Exists(categoryPath))
                    continue;

                foreach (var folder in Directory.EnumerateDirectories(categoryPath).OrderBy(p => p)) {
                    var groupName = Path.GetFileName(folder);
                    if (!icons.TryGetValue(groupName, out var group))
                        continue;
                    jobs.Add(new SpriteBuildJob(category, folder, groupName, group, false));

                    var hq = Path.Combine(folder, "hq");
                    if (Directory.Exists(hq))
                        jobs.Add(new SpriteBuildJob(category, hq, groupName, group, true));
                }
            }

            var entries = new ConcurrentBag<SpriteEntry>();
            var options = new ParallelOptions {
                MaxDegreeOfParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, 4))
            };

            Parallel.ForEach(jobs, options, job => {
                foreach (var entry in BuildSpriteSet(outputRoot, job.Category, job.Folder, job.GroupName, job.Group, job.Hq))
                    entries.Add(entry);
            });

            return entries
                .OrderBy(entry => entry.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.IconId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<SpriteEntry> BuildSpriteSet(string outputRoot, string category, string folder, string groupName, IconGroup group, bool hq) {
            var images = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(IsImage)
                .Where(p => Include(Path.GetFileName(p), group))
                .Select(LoadSpriteImage)
                .Where(i => i != null)
                .OrderBy(i => i.Path)
                .ToList();
            var result = new List<SpriteEntry>();

            foreach (var set in images.GroupBy(i => $"{i.OriginalHeight}x{i.OriginalWidth}")) {
                var list = set.ToList();
                var split = Math.Max(1, (int)Math.Floor(Math.Sqrt(list.Count)));
                var cellW = list.Max(i => i.Image.Width);
                var cellH = list.Max(i => i.Image.Height);
                var prefix = hq && category != "uld" ? $"sprite_{groupName}_hq_{set.Key}" : $"sprite_{groupName}_{set.Key}";
                var master = hq && category != "uld" ? $"sprite-icon-{groupName}-hq-{set.Key}" : $"sprite-icon-{groupName}-{set.Key}";
                var cssPrefix = $"sprite-icon-{groupName}";
                var css = new StringBuilder();
                css.AppendLine($".{master} {{");
                css.AppendLine($"  background-image: url('../png/{prefix}.png');");
                css.AppendLine("}");

                using var sheet = new Image<Rgba32>(cellW * split, cellH * (int)Math.Ceiling(list.Count / (double)split));
                var x = 0;
                var y = 0;
                for (var i = 0; i < list.Count; i++) {
                    var img = list[i];
                    sheet.Mutate(c => c.DrawImage(img.Image, new Point(x, y), 1f));
                    var cls = CssClass(cssPrefix + "-" + Path.GetFileNameWithoutExtension(img.Path).Replace("_hr1", "", StringComparison.OrdinalIgnoreCase));
                    css.AppendLine($".{cls} {{");
                    css.AppendLine($"  background-position: -{x}px -{y}px;");
                    css.AppendLine($"  width: {img.Image.Width}px;");
                    css.AppendLine($"  height: {img.Image.Height}px;");
                    css.AppendLine("}");

                    result.Add(new SpriteEntry {
                        Group = groupName,
                        Category = category,
                        Name = Path.GetFileNameWithoutExtension(img.Path).Replace("_hr1", "", StringComparison.OrdinalIgnoreCase),
                        IconId = ExtractIconId(Path.GetFileName(img.Path)),
                        MasterClass = master,
                        SpriteClass = cls,
                        CssPath = $"css/{prefix}.css",
                        Width = img.Image.Width,
                        Height = img.Image.Height
                    });

                    x += cellW;
                    if ((i + 1) % split == 0) {
                        x = 0;
                        y += cellH;
                    }
                }

                sheet.Save(Path.Combine(outputRoot, "png", prefix + ".png"), new PngEncoder());
                File.WriteAllText(Path.Combine(outputRoot, "css", prefix + ".css"), css.ToString(), Encoding.UTF8);
                foreach (var img in list)
                    img.Image.Dispose();
            }
            return result;
        }

        private Dictionary<string, List<IconTextLink>> BuildIconTextLinks(Dictionary<string, IconGroup> icons) {
            var wanted = new HashSet<string>(icons.Values.SelectMany(g => g.Both.Concat(g.HrOnly).Concat(g.NoHr)).Select(ExtractIconId).Where(v => v.Length > 0));
            var result = new Dictionary<string, List<IconTextLink>>();
            var seen = new HashSet<string>();
            var oldLanguage = _Realm.GameData.ActiveLanguage;

            try {
                foreach (var sheetName in _Realm.GameData.AvailableSheets.OrderBy(n => n)) {
                    var def = _Realm.GameData.Definition.SheetDefinitions.FirstOrDefault(d => d.Name == sheetName);
                    if (def == null)
                        continue;
                    var columns = def.GetAllColumnNames().ToList();
                    var media = columns.Where(c => c.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0 || c.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    var text = TextColumns.Where(columns.Contains).ToList();
                    if (media.Count == 0 || text.Count == 0)
                        continue;

                    foreach (var language in TextLanguages) {
                        _Realm.GameData.ActiveLanguage = language;
                        IXivSheet<XivRow> sheet;
                        try { sheet = _Realm.GameData.GetSheet(sheetName); }
                        catch { continue; }

                        foreach (var row in sheet) {
                            var rowTexts = text.Select(c => Read(row, c)?.ToString()?.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
                            if (rowTexts.Count == 0)
                                continue;
                            foreach (var mediaColumn in media) {
                                var icon = FormatIconId(ReadRaw(row, mediaColumn));
                                if (icon.Length == 0 || icon == "000000" || !wanted.Contains(icon))
                                    continue;
                                foreach (var rowText in rowTexts) {
                                    var link = new IconTextLink { Source = sheetName, Field = mediaColumn, Language = language.GetCode(), Text = rowText, RowId = row.Key.ToString(CultureInfo.InvariantCulture) };
                                    var key = $"{icon}|{link.Source}|{link.Field}|{link.Language}|{link.Text}|{link.RowId}";
                                    if (!seen.Add(key))
                                        continue;
                                    if (!result.TryGetValue(icon, out var list))
                                        result[icon] = list = new List<IconTextLink>();
                                    list.Add(link);
                                }
                            }
                        }
                    }
                }
            }
            finally {
                _Realm.GameData.ActiveLanguage = oldLanguage;
            }

            result["000405"] = TextLanguages.Select(l => new IconTextLink { Source = "Action", Field = "Icon", Language = l.GetCode(), Text = "Vita", RowId = "120" }).ToList();
            return result;
        }

        private static SpriteImage LoadSpriteImage(string path) {
            try {
                var image = Image.Load<Rgba32>(path);
                var originalW = image.Width;
                var originalH = image.Height;
                var max = Math.Max(image.Width, image.Height);
                if (max > MaxImageSide) {
                    var scale = MaxImageSide / (double)max;
                    image.Mutate(c => c.Resize(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale))));
                }
                return new SpriteImage { Path = path, Image = image, OriginalWidth = originalW, OriginalHeight = originalH };
            }
            catch {
                return null;
            }
        }

        private static bool Include(string filename, IconGroup group) {
            if (filename.IndexOf("_hr1", StringComparison.OrdinalIgnoreCase) >= 0)
                return group.HrOnly.Contains(filename, StringComparer.OrdinalIgnoreCase) || group.Both.Contains(filename, StringComparer.OrdinalIgnoreCase);
            return group.NoHr.Contains(filename, StringComparer.OrdinalIgnoreCase);
        }

        private static object Read(XivRow row, string column) {
            try { return row[column]; }
            catch { return null; }
        }

        private static object ReadRaw(XivRow row, string column) {
            try { return row.GetRaw(column); }
            catch { return null; }
        }

        private static string FormatIconId(object value) {
            if (value is ImageFile image)
                return ExtractIconId(Path.GetFileName(image.Path));
            if (value is IXivRow row)
                return row.Key.ToString("D6", CultureInfo.InvariantCulture);
            return int.TryParse(value?.ToString(), out var i) && i > 0 ? i.ToString("D6", CultureInfo.InvariantCulture) : "";
        }

        private static string ExtractIconId(string filename) {
            var clean = Path.GetFileNameWithoutExtension(filename ?? "").Replace("_hr1", "", StringComparison.OrdinalIgnoreCase).Replace(".tex", "", StringComparison.OrdinalIgnoreCase);
            return Regex.IsMatch(clean, "^[0-9]+$") ? int.Parse(clean, CultureInfo.InvariantCulture).ToString("D6", CultureInfo.InvariantCulture) : clean;
        }

        private static bool IsImage(string path) {
            var ext = Path.GetExtension(path);
            return ext.Equals(".png", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private static string AddHr(string filename) {
            var ext = Path.GetExtension(filename);
            return filename.Substring(0, filename.Length - ext.Length) + "_hr1" + ext;
        }

        private static void AddUnique(List<string> list, string value) {
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase))
                list.Add(value);
        }

        private static string CssClass(string value) {
            return Regex.Replace(value.Replace(".tex", "", StringComparison.OrdinalIgnoreCase), "[^A-Za-z0-9_-]", "-");
        }

        private static void WriteJson(string path, object value, bool indented) {
            File.WriteAllText(path, JsonConvert.SerializeObject(value, indented ? Formatting.Indented : Formatting.None), Encoding.UTF8);
        }

        private static string ResolvePath(string path) {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }

        private static string H(string value) => WebUtility.HtmlEncode(value ?? "");
        private static string A(string value) => H(value).Replace("\"", "&quot;");

        private class IconGroup {
            [JsonProperty("both")] public List<string> Both { get; set; } = new List<string>();
            [JsonProperty("hronly")] public List<string> HrOnly { get; set; } = new List<string>();
            [JsonProperty("nohr")] public List<string> NoHr { get; set; } = new List<string>();
        }

        private class SpriteImage {
            public string Path { get; set; }
            public Image<Rgba32> Image { get; set; }
            public int OriginalWidth { get; set; }
            public int OriginalHeight { get; set; }
        }

        private class SpriteEntry {
            public string Group { get; set; }
            public string Category { get; set; }
            public string Name { get; set; }
            public string IconId { get; set; }
            public string MasterClass { get; set; }
            public string SpriteClass { get; set; }
            public string CssPath { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
        }

        private class SpriteBuildJob {
            public SpriteBuildJob(string category, string folder, string groupName, IconGroup group, bool hq) {
                Category = category;
                Folder = folder;
                GroupName = groupName;
                Group = group;
                Hq = hq;
            }

            public string Category { get; }
            public string Folder { get; }
            public string GroupName { get; }
            public IconGroup Group { get; }
            public bool Hq { get; }
        }

        private class IconTextLink {
            [JsonProperty("source")] public string Source { get; set; }
            [JsonProperty("field")] public string Field { get; set; }
            [JsonProperty("language")] public string Language { get; set; }
            [JsonProperty("text")] public string Text { get; set; }
            [JsonProperty("row_id")] public string RowId { get; set; }
        }
    }
}
