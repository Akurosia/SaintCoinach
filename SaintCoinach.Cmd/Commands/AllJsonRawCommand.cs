using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using SaintCoinach.Ex;
using Tharga.Console.Commands.Base;

#pragma warning disable CS1998

namespace SaintCoinach.Cmd.Commands {
    public class AllJsonRawCommand : AsyncActionCommandBase {
        private ARealmReversed _Realm;

        /// <summary>
        /// Setup the command
        /// </summary>
        /// <param name="realm"></param>
        public AllJsonRawCommand(ARealmReversed realm)
            : base("allrawjson", "Export all data (default), or only specific data files, as JSON-files; including all languages. No post-processing is applied to values.")
        {
            _Realm = realm;
        }

        /// <summary>
        /// Obtain game sheets from the game data
        /// </summary>
        /// <param name="paramList"></param>
        /// <returns></returns>
        public override async Task InvokeAsync(string[] paramList) {
            var versionPath = _Realm.GameVersion;
            if (paramList?.Contains("/UseDefinitionVersion") ?? false)
                versionPath = _Realm.DefinitionVersion;

            const string CsvFileFormat = "raw-json-all/{0}{1}.json";

            IEnumerable<string> filesToExport;

            // Gather files to export, may be split by params.
            if (paramList.Length == 0)
                filesToExport = _Realm.GameData.AvailableSheets;
            else
                filesToExport = paramList.Select(_ => _Realm.GameData.FixName(_));

            // Action counts
            var successCount = 0;
            var failCount = 0;
            var currentCount = 0;
            var total = filesToExport.Count();

            // Process game files.
            foreach (var name in filesToExport)
            {
                currentCount++;
                if (name.StartsWith("content/") || name.StartsWith("custom/") || name.StartsWith("cut_scene/") || name.StartsWith("dungeon/") || name.StartsWith("guild_order/") || name.StartsWith("leve/") || name.StartsWith("opening/") || name.StartsWith("quest/") || name.StartsWith("raid/") || name.StartsWith("shop/") || name.StartsWith("story/") || name.StartsWith("system/") || name.StartsWith("transport/") || name.StartsWith("warp/")) {continue;}
                var sheet = _Realm.GameData.GetSheet(name);

                // Loop through all available languages
                foreach (var lang in sheet.Header.AvailableLanguages)
                {
                    var code = lang.GetCode();
                    if (code == "chs" || code == "ko") { continue; }
                    if (code == "chs" || code == "ko" || code == "tc") { continue; }
                    if (code.Length > 0)
                        code = "." + code;

                    var target = new FileInfo(Path.Combine(versionPath, string.Format(CsvFileFormat, name, code)));

                    try
                    {
                        if (!target.Directory.Exists)
                            target.Directory.Create();

                        // Save
                        OutputInformation($"[{currentCount}/{total}] Processing: {name} - Language: {lang.GetSuffix()}");
                        ExdHelper.SaveAsJson(sheet, lang, target.FullName, true);
                        ++successCount;
                    }
                    catch (Exception e)
                    {
                        OutputError($"Export of {name} failed: {e.Message}");
                        try { if (target.Exists) { target.Delete(); } } catch { }
                        ++failCount;
                    }
                }
            }
            OutputInformation($"{successCount} files exported, {failCount} failed");
        }
    }
}
