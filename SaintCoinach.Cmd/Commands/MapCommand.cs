using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Tharga.Console.Commands.Base;

#pragma warning disable CS1998

namespace SaintCoinach.Cmd.Commands {
    public class MapCommand : AsyncActionCommandBase {
        private ARealmReversed _Realm;

        public MapCommand(ARealmReversed realm)
            : base("maps", "Export all map images as WebP.") {
            _Realm = realm;
        }

        public override async Task InvokeAsync(string[] paramList) {
            var rawFileNames = false;

            if (paramList.Length != 0) {
                var parameters = paramList;
                rawFileNames = parameters.Contains("raw");
                if (parameters.Any(p => p != "raw" && p != "webp" && p != "png")) {
                    OutputError($"Invalid map format {string.Join(", ", paramList)}");
                    return;
                }
            }

            var c = 0;
            var allMaps = _Realm.GameData.GetSheet<SaintCoinach.Xiv.Map>()
                .Where(m => m.PlaceName != null)
                        //.Where(m => {
                        //    var id = m.Id?.ToString() ?? string.Empty;
                        //    return id.StartsWith("r2d", StringComparison.OrdinalIgnoreCase)
                        //|| id.StartsWith("s1b", StringComparison.OrdinalIgnoreCase);
                        //})
                ;

            var fileSet = new Dictionary<string, int>();
            foreach (var map in allMaps) {
                var img = map.MediumImage;
                if (img == null)
                    continue;

                var outPathSb = new StringBuilder("ui/map/");
                if(rawFileNames) {
                    outPathSb.AppendFormat("{0}/{1}", map.Id.ToString().Split('/')[0], map.Id.ToString().Replace("/", "."));
                    outPathSb.Append(ImageExportHelper.WebpExtension);
                } else {
                    var mapId = map.Id?.ToString() ?? string.Empty;
                    var mapFolder = GetMapIdFolder(mapId);
                    var territoryName = GetTerritoryFilePrefix(map.TerritoryType?.Name?.ToString() ?? string.Empty);
                    if (string.IsNullOrWhiteSpace(territoryName))
                        territoryName = ToPathSafeString(mapId.Replace("/", "_"));
                    if (!string.IsNullOrWhiteSpace(mapFolder))
                        outPathSb.AppendFormat("{0}/", mapFolder);
                    
                    if (!string.IsNullOrEmpty(territoryName)) {
                        outPathSb.AppendFormat("{0} - ", territoryName);
                    }
                    outPathSb.AppendFormat("{0}", ToPathSafeString(map.PlaceName.Name.ToString()));
                    if (map.LocationPlaceName != null && map.LocationPlaceName.Key != 0 && !map.LocationPlaceName.Name.IsEmpty)
                        outPathSb.AppendFormat(" - {0}", ToPathSafeString(map.LocationPlaceName.Name.ToString()));
                    var mapKey = StripMapMarkers(outPathSb.ToString());
                    fileSet.TryGetValue(mapKey, out int mapIndex);
                    if (mapIndex > 0) {
                        outPathSb.AppendFormat(" - {0}", mapIndex);
                    }
                    fileSet[mapKey] = mapIndex + 1;
                    outPathSb.Append(ImageExportHelper.WebpExtension);
                }

                var finalRelativePath = StripMapMarkers(outPathSb.ToString());
                var outFile = new FileInfo(Path.Combine(_Realm.GameVersion, finalRelativePath));
                if (!outFile.Directory.Exists)
                    outFile.Directory.Create();
                
                ImageExportHelper.SaveAsDropperWebp(img, outFile.FullName);
                ++c;
            }
            OutputInformation($"{c} maps saved");
        }

        static string ToPathSafeString(string input, char invalidReplacement = '_') {
            var cleaned = StripMapMarkers(input);
            var sb = new StringBuilder(cleaned);
            var invalid = Path.GetInvalidFileNameChars();
            foreach (var c in invalid)
                sb.Replace(c, invalidReplacement);
            return sb.ToString();
        }

        static string StripMapMarkers(string input) {
            return Regex.Replace(
                input ?? string.Empty,
                @"_{1,2}(?:Emphasis|Soft-Hypen|SoftHypen)_",
                "",
                RegexOptions.CultureInvariant);
        }

        static string GetMapIdFolder(string mapId) {
            var firstSegment = (mapId ?? string.Empty).Split('/')[0];
            if (string.IsNullOrWhiteSpace(firstSegment))
                return string.Empty;
            return firstSegment.Length <= 3 ? firstSegment : firstSegment.Substring(0, 3);
        }

        static string GetTerritoryFilePrefix(string territoryName) {
            var value = (territoryName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            return ToPathSafeString(value);
        }

    }
}
