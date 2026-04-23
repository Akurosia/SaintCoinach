using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Tharga.Console.Commands.Base;

#pragma warning disable CS1998

namespace SaintCoinach.Cmd.Commands {
    public class SpriteHtmlCommand : AsyncActionCommandBase {
        private const string OriginalCssPath = @"N:\ff14.akurosiakamo.de\sprite_overview\sprite.css";
        private readonly ARealmReversed _Realm;

        public SpriteHtmlCommand(ARealmReversed realm)
            : base("sprite-html", "Regenerate sprite overview HTML pages from existing sprite JSON and CSS.") {
            _Realm = realm;
        }

        public override async Task InvokeAsync(string[] paramList) {
            if (paramList.Length > 1) {
                OutputError("Usage: sprite-html [sprite-output-dir]");
                return;
            }

            var outputRoot = ResolvePath(paramList.Length == 1 ? paramList[0] : Path.Combine(_Realm.GameVersion, "sprite"));
            GenerateHtml(outputRoot, message => OutputInformation(message));
        }

        public static void GenerateHtml(string outputRoot, Action<string> output = null) {
            if (!Directory.Exists(outputRoot))
                throw new DirectoryNotFoundException($"Sprite output directory not found: {outputRoot}");

            Directory.CreateDirectory(Path.Combine(outputRoot, "html"));
            WriteSpriteCss(outputRoot);

            var groups = LoadIconGroups(Path.Combine(outputRoot, "icons.json"));
            var links = LoadIconTextLinks(Path.Combine(outputRoot, "icon_game_text.json"));
            var cssFiles = Directory.Exists(Path.Combine(outputRoot, "css"))
                ? Directory.EnumerateFiles(Path.Combine(outputRoot, "css"), "*.css")
                    .Select(path => path.Substring(outputRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            if (groups.Count == 0)
                groups = cssFiles.Select(GetGroupFromCssName).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

            BuildIndex(outputRoot, groups, cssFiles, links);
            output?.Invoke($"Regenerated sprite HTML for {groups.Count} groups");
        }

        private static void BuildIndex(string outputRoot, List<string> groups, List<string> cssFiles, Dictionary<string, List<IconTextLink>> links) {
            var generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var index = new StringBuilder();
            index.AppendLine("<html>");
            index.AppendLine("<head>");
            index.AppendLine("<meta charset=\"utf-8\">");
            index.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            index.AppendLine("<title>FFXIV Sprite Overview</title>");
            index.AppendLine("<link rel=\"stylesheet\" href=\"sprite.css\">");
            index.AppendLine("</head>");
            index.AppendLine("<body class=\"overview\">");
            index.AppendLine("<div class=\"page-shell\">");
            index.AppendLine("  <header class=\"hero\">");
            index.AppendLine("    <div class=\"hero-copy\">");
            index.AppendLine("      <h1>Sprite overview</h1>");
            index.AppendLine("    </div>");
            index.AppendLine("    <div class=\"hero-tools\">");
            index.AppendLine("      <label class=\"search-shell\">");
            index.AppendLine("        <input id=\"overview_search\" type=\"search\" placeholder=\"Search\">");
            index.AppendLine("      </label>");
            index.AppendLine("    </div>");
            index.AppendLine("  </header>");
            index.AppendLine("  <main>");
            index.AppendLine("    <section class=\"overview-grid\" id=\"overview_grid\">");

            for (var i = 0; i < groups.Count; i++) {
                var group = groups[i];
                var blocks = BuildPageData(outputRoot, group, cssFiles, links);
                var title = Naming(group);
                var displayTitle = string.IsNullOrWhiteSpace(title) ? "Unnamed sprite group" : title;
                var count = blocks.Sum(block => block.Entries.Count);
                var search = string.Join(" ", new[] { group, displayTitle, CollectSearchTerms(blocks) }.Where(v => !string.IsNullOrWhiteSpace(v))).ToLowerInvariant();
                var indicator = string.IsNullOrWhiteSpace(title) ? "Raw" : "Named";

                index.AppendLine($"      <a class=\"links\" href=\"html/sprite-{H(group)}.html\" data-search=\"{A(search)}\">");
                index.AppendLine($"        <span class=\"link-id\">{H(group)}</span>");
                index.AppendLine($"        <span class=\"link-title\">{H(displayTitle)}</span>");
                index.AppendLine($"        <span class=\"link-meta\"><span>{indicator}</span><span>{count} assets</span></span>");
                index.AppendLine("      </a>");

                CreateGroupPage(outputRoot, group, blocks, groups, i);
            }

            index.AppendLine("    </section>");
            index.AppendLine("  </main>");
            index.AppendLine("  <footer class=\"site-footer\">");
            index.AppendLine("    <p>Category research and icon references are inspired by <a href=\"https://github.com/skyborn-industries/ffxiv-icons\" target=\"_blank\" rel=\"noreferrer\">Raelys and the ffxiv-icons project</a>.</p>");
            index.AppendLine("  </footer>");
            index.AppendLine("</div>");
            index.AppendLine(OverviewScript);
            index.AppendLine($"</body><dev style='display: none'>{generatedAt}</dev></html>");

            File.WriteAllText(Path.Combine(outputRoot, "index.html"), index.ToString(), Encoding.UTF8);
        }

        private static List<PageBlock> BuildPageData(string outputRoot, string group, List<string> cssFiles, Dictionary<string, List<IconTextLink>> links) {
            var blocks = new List<PageBlock>();
            foreach (var css in ReorderCssFiles(cssFiles.Where(path => CssBelongsToGroup(path, group)).ToList())) {
                var entries = ParseCssFile(Path.Combine(outputRoot, css.Replace('/', Path.DirectorySeparatorChar)), css, links);
                blocks.Add(new PageBlock { CssPath = css, Entries = entries });
            }
            return blocks;
        }

        private static void CreateGroupPage(string outputRoot, string group, List<PageBlock> blocks, List<string> groups, int index) {
            var title = Naming(group);
            if (string.IsNullOrWhiteSpace(title))
                title = "Unnamed sprite set";

            var html = new StringBuilder();
            html.AppendLine("<html>");
            html.AppendLine("  <head>");
            html.AppendLine("    <meta charset=\"utf-8\">");
            html.AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
            html.AppendLine($"    <title>Sprite {H(group)} - {H(title)}</title>");
            foreach (var css in blocks.Select(block => block.CssPath).Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
                html.AppendLine($"    <link rel=\"stylesheet\" href=\"../{H(css)}\">");
            html.AppendLine("    <link rel=\"stylesheet\" href=\"../sprite.css\">");
            html.AppendLine("  </head>");
            html.AppendLine($"  <body class=\"number_body\" data-page-title=\"{A(title.ToLowerInvariant())}\">");
            html.AppendLine("    <div class=\"page-shell\">");
            html.AppendLine("      <header class=\"hero hero-detail\">");
            html.AppendLine("        <div class=\"hero-topbar\">");
            html.AppendLine($"          <div class=\"nav-left\">{(index > 0 ? $"<a href=\"../html/sprite-{H(groups[index - 1])}.html\">&lt; Previous</a>" : "<span class=\"nav-ghost\">No previous</span>")}</div>");
            html.AppendLine("          <div class=\"nav-center\">");
            html.AppendLine("            <a href=\"../index.html\">Overview</a>");
            html.AppendLine("            <button type=\"button\" id=\"btn_grouped\" aria-pressed=\"true\">Grouped</button>");
            html.AppendLine("            <button type=\"button\" id=\"btn_sorted\" aria-pressed=\"false\">Sorted</button>");
            html.AppendLine("          </div>");
            html.AppendLine($"          <div class=\"nav-right\">{(index + 1 < groups.Count ? $"<a href=\"../html/sprite-{H(groups[index + 1])}.html\">Next &gt;</a>" : "<span class=\"nav-ghost\">No next</span>")}</div>");
            html.AppendLine("        </div>");
            html.AppendLine("        <div class=\"hero-copy\">");
            html.AppendLine($"          <h1>{H(group)}</h1>");
            html.AppendLine("        </div>");
            html.AppendLine("        <div class=\"hero-tools\">");
            html.AppendLine("          <label class=\"search-shell\">");
            html.AppendLine("            <input id=\"entry_search\" type=\"search\" placeholder=\"Search\">");
            html.AppendLine("          </label>");
            html.AppendLine("        </div>");
            html.AppendLine("      </header>");

            html.AppendLine("      <div id=\"view_grouped\">");
            foreach (var block in blocks) {
                html.AppendLine($"        <section class=\"cat_block\" data-search=\"{A(block.CssPath.ToLowerInvariant())}\">");
                html.AppendLine($"          <button type=\"button\" class=\"cat_sep cat_toggle\" aria-expanded=\"true\">{H(block.CssPath)}</button>");
                html.AppendLine("          <div class=\"entry_grid\">");
                foreach (var entry in block.Entries)
                    html.Append(RenderEntry(entry));
                html.AppendLine("          </div>");
                html.AppendLine("        </section>");
            }
            html.AppendLine("      </div>");

            var sorted = blocks.SelectMany(block => block.Entries).OrderBy(entry => entry.Number).ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            html.AppendLine("      <div id=\"view_sorted\" class=\"view-hidden\">");
            html.AppendLine("        <section class=\"cat_block\">");
            html.AppendLine("          <div class=\"cat_sep\">All assets (sorted)</div>");
            html.AppendLine("          <div class=\"entry_grid\">");
            foreach (var entry in sorted)
                html.Append(RenderEntry(entry));
            html.AppendLine("          </div>");
            html.AppendLine("        </section>");
            html.AppendLine("      </div>");

            html.AppendLine("      <footer class=\"site-footer\">");
            html.AppendLine("        <p>Sprites are organized from extracted game assets. Icon naming and broader community research builds on work by <a href=\"https://github.com/skyborn-industries/ffxiv-icons\" target=\"_blank\" rel=\"noreferrer\">Raelys and the ffxiv-icons project</a>.</p>");
            html.AppendLine("      </footer>");
            html.AppendLine("    </div>");
            html.Append(GroupScript(group));
            html.AppendLine($"  </body><dev style='display: none'>{DateTime.Now:yyyy-MM-dd HH:mm:ss}</dev>");
            html.AppendLine("</html>");

            File.WriteAllText(Path.Combine(outputRoot, "html", $"sprite-{group}.html"), html.ToString(), Encoding.UTF8);
        }

        private static string RenderEntry(SpriteEntry entry) {
            var (scale, width, height) = ScaleFitBox(entry.Width, entry.Height);
            var search = string.Join(" ", new[] { entry.IconId, entry.DisplayName, entry.GroupName }.Concat(entry.TextLinks.Select(link => link.Text)).Where(v => !string.IsNullOrWhiteSpace(v))).ToLowerInvariant();
            var hover = string.Join(Environment.NewLine, new[] { entry.DisplayName }.Concat(entry.TextLinks.Select(link => $"{link.Language.ToUpperInvariant()}: {link.Text} ({link.Source}.{link.Field}#{link.RowId})")));
            var html = new StringBuilder();
            html.AppendLine($"    <article class=\"cat_row\" data-search=\"{A(search)}\">");
            html.AppendLine($"      <div class=\"sprite-stage\" title=\"{A(hover)}\">");
            if (entry.TextLinks.Any())
                html.AppendLine("        <span class=\"info-badge\" aria-hidden=\"true\" title=\"Has source info\">i</span>");
            html.AppendLine($"        <div class=\"sprite-wrap\" style=\"width:{width}px;height:{height}px;\">");
            html.AppendLine($"          <div class=\"sprite-inner\" style=\"transform:scale({scale.ToString("0.######", CultureInfo.InvariantCulture)});\">");
            html.AppendLine($"            <div class=\"ui middle aligned fish-icon {H(entry.MasterClass)} {H(entry.SpriteClass)}\"></div>");
            html.AppendLine("          </div>");
            html.AppendLine("        </div>");
            html.AppendLine("      </div>");
            html.AppendLine("      <div class=\"cat_text\">");
            if (entry.ShowBaseLink)
                html.AppendLine($"        <a class=\"xivapilink primary\" href=\"{A(entry.BaseLink)}\">{H(entry.BaseName)}</a>");
            html.AppendLine($"        <a class=\"xivapilink\" href=\"{A(entry.AssetLink)}\">{H(entry.DisplayName)}</a>");
            html.AppendLine("      </div>");
            html.AppendLine("    </article>");
            return html.ToString();
        }

        private static List<SpriteEntry> ParseCssFile(string cssFile, string cssPath, Dictionary<string, List<IconTextLink>> links) {
            if (!File.Exists(cssFile))
                return new List<SpriteEntry>();

            var result = new List<SpriteEntry>();
            string masterClass = null;
            string selector = null;
            int? width = null;
            int? height = null;

            void Flush() {
                if (string.IsNullOrWhiteSpace(selector))
                    return;
                if (masterClass == null) {
                    masterClass = selector;
                } else if (selector != masterClass) {
                    var entry = BuildEntry(selector, masterClass, cssPath, width, height, links);
                    if (entry != null)
                        result.Add(entry);
                }
                selector = null;
                width = null;
                height = null;
            }

            foreach (var raw in File.ReadLines(cssFile)) {
                var line = raw.Trim();
                var selectorMatch = Regex.Match(line.Replace(" ", ""), @"^\.(.+)\{$");
                if (selectorMatch.Success) {
                    Flush();
                    selector = selectorMatch.Groups[1].Value;
                    continue;
                }
                if (line == "}") {
                    Flush();
                    continue;
                }
                if (selector == null)
                    continue;

                var widthMatch = Regex.Match(raw, @"^\s*width:\s*([0-9]+)px\s*;\s*$");
                if (widthMatch.Success)
                    width = int.Parse(widthMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                var heightMatch = Regex.Match(raw, @"^\s*height:\s*([0-9]+)px\s*;\s*$");
                if (heightMatch.Success)
                    height = int.Parse(heightMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            Flush();
            return result;
        }

        private static SpriteEntry BuildEntry(string selector, string masterClass, string cssPath, int? width, int? height, Dictionary<string, List<IconTextLink>> links) {
            var group = GetGroupFromCssName(cssPath);
            var prefix = $"sprite-icon-{group}-";
            var displayName = selector.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? selector.Substring(prefix.Length) : selector.Split('-').LastOrDefault() ?? selector;
            displayName = displayName.Replace(".tex", "", StringComparison.OrdinalIgnoreCase);
            var iconId = ExtractIconId(displayName);
            var baseName = displayName.Replace("_hr1", "", StringComparison.OrdinalIgnoreCase);
            var category = int.TryParse(iconId, out _) ? "icon" : "uld";
            links.TryGetValue(iconId, out var itemLinks);
            itemLinks ??= new List<IconTextLink>();

            return new SpriteEntry {
                CssPath = cssPath,
                MasterClass = masterClass,
                SpriteClass = selector,
                Width = width ?? 1,
                Height = height ?? 1,
                Number = int.TryParse(iconId, out var number) ? number : 0,
                IconId = iconId,
                DisplayName = displayName,
                BaseName = baseName,
                GroupName = Naming(iconId.Length >= 3 ? iconId.Substring(0, 3) + "000" : group),
                AssetLink = GetAssetLink(category, iconId, displayName),
                BaseLink = GetAssetLink(category, iconId, baseName),
                ShowBaseLink = !string.Equals(displayName, baseName, StringComparison.OrdinalIgnoreCase),
                TextLinks = itemLinks
            };
        }

        private static List<string> LoadIconGroups(string path) {
            if (!File.Exists(path))
                return new List<string>();
            try {
                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                return data?.Keys.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            }
            catch {
                return new List<string>();
            }
        }

        private static Dictionary<string, List<IconTextLink>> LoadIconTextLinks(string path) {
            if (!File.Exists(path))
                return new Dictionary<string, List<IconTextLink>>();
            try {
                return JsonConvert.DeserializeObject<Dictionary<string, List<IconTextLink>>>(File.ReadAllText(path, Encoding.UTF8)) ?? new Dictionary<string, List<IconTextLink>>();
            }
            catch {
                return new Dictionary<string, List<IconTextLink>>();
            }
        }

        private static void WriteSpriteCss(string outputRoot) {
            var target = Path.Combine(outputRoot, "sprite.css");
            if (File.Exists(OriginalCssPath)) {
                File.Copy(OriginalCssPath, target, true);
                return;
            }
            File.WriteAllText(target, FallbackCss, Encoding.UTF8);
        }

        private static List<string> ReorderCssFiles(List<string> files) {
            return files.OrderBy(path => {
                var token = Path.GetFileNameWithoutExtension(path).Split('_').LastOrDefault() ?? "";
                var height = token.Split('x').FirstOrDefault();
                return double.TryParse(height, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : double.MaxValue;
            }).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool CssBelongsToGroup(string css, string group) {
            var file = Path.GetFileNameWithoutExtension(css);
            return file.StartsWith($"sprite_{group}_", StringComparison.OrdinalIgnoreCase) || file.Equals($"sprite_{group}", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetGroupFromCssName(string css) {
            var file = Path.GetFileNameWithoutExtension(css);
            if (!file.StartsWith("sprite_", StringComparison.OrdinalIgnoreCase))
                return "";
            var rest = file.Substring("sprite_".Length);
            return rest.Split('_').FirstOrDefault() ?? "";
        }

        private static string CollectSearchTerms(List<PageBlock> blocks) {
            return string.Join(" ", blocks.SelectMany(block => block.Entries).SelectMany(entry => new[] { entry.IconId, entry.DisplayName, entry.GroupName }.Concat(entry.TextLinks.Select(link => link.Text))).Where(v => !string.IsNullOrWhiteSpace(v)));
        }

        private static (double Scale, int Width, int Height) ScaleFitBox(int width, int height, int cap = 160) {
            if (width <= 0 || height <= 0)
                return (1, cap, cap);
            var scale = Math.Min(Math.Min(cap / (double)width, cap / (double)height), 1);
            return (scale, Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        }

        private static string GetAssetLink(string category, string iconId, string display) {
            if (category == "uld")
                return $"https://v2.xivapi.com/api/asset?path=ui/uld/{display}.tex&format=webp";
            var group = iconId.Length >= 3 ? iconId.Substring(0, 3) + "000" : "000000";
            return $"https://v2.xivapi.com/api/asset/ui/icon/{group}/{iconId}.tex?format=webp";
        }

        private static string ExtractIconId(string value) {
            var clean = Path.GetFileNameWithoutExtension(value ?? "").Replace("_hr1", "", StringComparison.OrdinalIgnoreCase).Replace(".tex", "", StringComparison.OrdinalIgnoreCase);
            return Regex.IsMatch(clean, "^[0-9]+$") ? int.Parse(clean, CultureInfo.InvariantCulture).ToString("D6", CultureInfo.InvariantCulture) : clean;
        }

        private static string GroupScript(string group) {
            return $@"    <script>
      (function() {{
        const KEY = ""sprite_view_pref_{A(group)}"";
        const btnGrouped = document.getElementById(""btn_grouped"");
        const btnSorted = document.getElementById(""btn_sorted"");
        const viewGrouped = document.getElementById(""view_grouped"");
        const viewSorted = document.getElementById(""view_sorted"");
        const searchInput = document.getElementById(""entry_search"");
        const pageTitle = (document.body.dataset.pageTitle || """").toLowerCase();
        const toggles = Array.from(document.querySelectorAll("".cat_toggle""));
        function activeView() {{ return viewSorted.classList.contains(""view-hidden"") ? viewGrouped : viewSorted; }}
        function applySearch() {{
          const query = (searchInput.value || """").trim().toLowerCase();
          const view = activeView();
          const cards = view.querySelectorAll("".cat_row"");
          const blocks = view.querySelectorAll("".cat_block"");
          cards.forEach((card) => {{
            const haystack = [card.dataset.search || """", pageTitle].join("" "");
            card.hidden = !!query && !haystack.includes(query);
          }});
          blocks.forEach((block) => {{
            const visibleCards = block.querySelectorAll("".cat_row:not([hidden])"").length;
            block.hidden = visibleCards === 0;
            if (query && visibleCards > 0) {{
              block.classList.remove(""is-collapsed"");
              const toggle = block.querySelector("".cat_toggle"");
              if (toggle) toggle.setAttribute(""aria-expanded"", ""true"");
            }}
          }});
        }}
        function setMode(mode) {{
          const isSorted = mode === ""sorted"";
          viewGrouped.classList.toggle(""view-hidden"", isSorted);
          viewSorted.classList.toggle(""view-hidden"", !isSorted);
          btnGrouped.setAttribute(""aria-pressed"", String(!isSorted));
          btnSorted.setAttribute(""aria-pressed"", String(isSorted));
          applySearch();
          try {{ localStorage.setItem(KEY, mode); }} catch (e) {{}}
        }}
        btnGrouped.addEventListener(""click"", () => setMode(""grouped""));
        btnSorted.addEventListener(""click"", () => setMode(""sorted""));
        searchInput.addEventListener(""input"", applySearch);
        toggles.forEach((toggle) => {{
          toggle.addEventListener(""click"", () => {{
            const block = toggle.closest("".cat_block"");
            const isCollapsed = block.classList.toggle(""is-collapsed"");
            toggle.setAttribute(""aria-expanded"", String(!isCollapsed));
          }});
        }});
        try {{ if (localStorage.getItem(KEY) === ""sorted"") setMode(""sorted""); }} catch (e) {{}}
        applySearch();
      }})();
    </script>
";
        }

        private static string Naming(string value) {
            if (!int.TryParse(value, out var v))
                return value;
            if (v >= 20000 && v <= 57000 && v % 1000 == 0)
                return "items";
            if (v >= 210000 && v <= 229000 && v % 1000 == 0)
                return "status effects";
            return GroupNames.TryGetValue(v, out var name) ? name : "";
        }

        private static string H(string value) => WebUtility.HtmlEncode(value ?? "");
        private static string A(string value) => H(value).Replace("\"", "&quot;");

        private static string ResolvePath(string path) {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }

        private const string OverviewScript = @"<script>
  (function() {
    const searchInput = document.getElementById(""overview_search"");
    const cards = Array.from(document.querySelectorAll(""#overview_grid .links""));
    function applySearch() {
      const query = (searchInput.value || """").trim().toLowerCase();
      cards.forEach((card) => {
        const haystack = card.dataset.search || """";
        card.hidden = !!query && !haystack.includes(query);
      });
    }
    searchInput.addEventListener(""input"", applySearch);
    applySearch();
  })();
</script>";

        private const string FallbackCss = "body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#080808;color:#f7ecec}.page-shell{width:calc(100% - 16px);margin:0 auto;padding:8px 0 20px}.hero{position:sticky;top:0;z-index:50;margin-bottom:10px;padding:8px 10px;border:1px solid rgba(255,83,83,.18);border-radius:22px;background:#14090a}.overview-grid,.entry_grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:12px}.links,.cat_row,.cat_block{border:1px solid rgba(255,83,83,.18);border-radius:16px;background:rgba(20,10,10,.82);padding:12px}.link-id{font-size:1.4rem;font-weight:700}.link-title,.link-meta{color:#d0a8a8}.sprite-stage{position:relative;display:grid;place-items:center;padding:6px}.sprite-wrap{display:inline-block;overflow:hidden;position:relative}.sprite-inner{position:absolute;top:0;left:0;transform-origin:top left}.view-hidden,[hidden]{display:none!important}.cat_block.is-collapsed .entry_grid{display:none}.xivapilink{display:block;color:#f7ecec;overflow-wrap:anywhere}";

        private static readonly Dictionary<int, string> GroupNames = new Dictionary<int, string> {
            { 0, "actions" }, { 1000, "actions" }, { 2000, "actions" }, { 3000, "actions" },
            { 4000, "mounts (small)" }, { 5000, "traits" }, { 6000, "traits" },
            { 8000, "fashion accessories (small)" }, { 10000, "status effects" },
            { 19000, "event/fashion/mount actions" }, { 58000, "fashions (small), bardings" },
            { 59000, "mount, minions (small)" }, { 60000, "weather, map markers, market board, player markers, various icons" },
            { 61000, "event actions, markers, gods, pvp, custom deliveries, playstyles, various icons" },
            { 62000, "jobs, beast tribes, map stuff" }, { 63000, "hunts, maps" },
            { 64000, "eureka actions" }, { 65000, "currency, ocean fishing" },
            { 66000, "macro icons" }, { 68000, "mount & minion (large)" },
            { 69000, "mount footprints" }, { 72000, "blue mage, field records" },
            { 73000, "unending codex, battle dialog portraits" }, { 76000, "stickers, mahjong" },
            { 78000, "feesh" }, { 81000, "sightseeing log" }, { 82000, "trust, orchestrion, island sanctuary" },
            { 83000, "grand company seal" }, { 84000, "hunts" }, { 85000, "paintings, flashbacks" },
            { 86000, "mahjong rules, job gauges" }, { 87000, "triple triad (large)" },
            { 88000, "triple triad (small)" }, { 90000, "free company crests" },
            { 100000, "journal" }, { 111000, "guildleves" }, { 112000, "instances" },
            { 113000, "wondrous tales" }, { 114000, "new game+" }, { 120000, "flashes" },
            { 130000, "facepaint (CharaMakeCustomize)" }, { 137000, "hrothgar (CharaMakeCustomize)" },
            { 138000, "viera (CharaMakeCustomize)" }, { 139000, "hrothiera facepaint (CharaMakeCustomize)" },
            { 150000, "tutorials" }, { 151000, "tutorials" }, { 152000, "tutorials" },
            { 153000, "tutorials" }, { 154000, "tutorials" }, { 155000, "tutorials" },
            { 156000, "tutorials" }, { 180000, "golden saucer misc" },
            { 190000, "portrait backgrounds" }, { 191000, "portrait frames" },
            { 192000, "portrait decorations" }, { 193000, "plate backgrounds" },
            { 194000, "plate patterns" }, { 195000, "plate backings" }, { 196000, "plate headers" },
            { 197000, "plate portrait frames" }, { 198000, "plate frames" },
            { 199000, "plate decorations" }, { 200000, "fashion accessory items" },
            { 230000, "gpose stamps" }, { 231000, "gpose stamps" }, { 232000, "gpose stamps" },
            { 233000, "gpose stamps" }, { 234000, "gpose stamps" }, { 240000, "strategy board" },
            { 241000, "cosmic exploration" }, { 246000, "emotes" },
            { 250000, "hairstyles (CharaMakeCustomize)" }
        };

        private class PageBlock {
            public string CssPath { get; set; }
            public List<SpriteEntry> Entries { get; set; }
        }

        private class SpriteEntry {
            public string CssPath { get; set; }
            public string MasterClass { get; set; }
            public string SpriteClass { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int Number { get; set; }
            public string IconId { get; set; }
            public string DisplayName { get; set; }
            public string BaseName { get; set; }
            public string GroupName { get; set; }
            public string AssetLink { get; set; }
            public string BaseLink { get; set; }
            public bool ShowBaseLink { get; set; }
            public List<IconTextLink> TextLinks { get; set; }
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
