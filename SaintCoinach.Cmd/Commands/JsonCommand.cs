using SaintCoinach.Ex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Tharga.Console;
using Tharga.Console.Commands;
using Tharga.Console.Commands.Base;

#pragma warning disable CS1998

namespace SaintCoinach.Cmd.Commands {

    public class JsonCommand : AsyncActionCommandBase {
        private ARealmReversed _Realm;
        public string GetLanguageCode(string lang) {
            return lang switch {
                "English" => "en",
                "German" => "de",
                "French" => "fr",
                "Japanese" => "ja",
                _ => "en" // fallback
            };
        }
        public JsonCommand(ARealmReversed realm)
            : base("json", "Export all data (default), or only specific data files seperated by spaces, as JSON-files.") {
            _Realm = realm;
        }

        public override async Task InvokeAsync(string[] paramList) {
            const string JsonFileFormat = "json/{0}{1}.json";

            IEnumerable<string> filesToExport;

            if (paramList.Length == 0)
                filesToExport = _Realm.GameData.AvailableSheets;
            else
                filesToExport = paramList.Select(_ => _Realm.GameData.FixName(_));

            var successCount = 0;
            var failCount = 0;
            var code = "." + GetLanguageCode(_Realm.GameData.ActiveLanguage.ToString());

            foreach (var name in filesToExport) {
                try {
                    var sheet = _Realm.GameData.GetSheet(name);
                    var target = new FileInfo(Path.Combine(_Realm.GameVersion, string.Format(JsonFileFormat, name, "")));
                    if (sheet.Header.AvailableLanguages.ToList().Count > 1) {
                        target = new FileInfo(Path.Combine(_Realm.GameVersion, string.Format(JsonFileFormat, name, code)));
                    }

                    if (!target.Directory.Exists)
                        target.Directory.Create();
                    ExdHelper.SaveAsJson(sheet, _Realm.GameData.ActiveLanguage, target.FullName, false);

                    ++successCount;
                }
                catch (Exception e) {
                    OutputError($"Export of {name} failed: {e.Message}");
                    ++failCount;
                }
            }
            OutputInformation($"{successCount} files exported, {failCount} failed");
        }
    }
}
