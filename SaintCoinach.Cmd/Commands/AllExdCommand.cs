using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Tharga.Toolkit.Console;
using Tharga.Toolkit.Console.Command;
using Tharga.Toolkit.Console.Command.Base;

using SaintCoinach;
using SaintCoinach.Ex;
using SaintCoinach.Xiv;

#pragma warning disable CS1998

namespace SaintCoinach.Cmd.Commands {
    public class AllExdCommand : ActionCommandBase {
        private ARealmReversed _Realm;

        public AllExdCommand(ARealmReversed realm)
            : base("allexd", "Export all data (default), or only specific data files, seperated by spaces; including all languages.") {
            _Realm = realm;
        }

        public override async Task<bool> InvokeAsync(string paramList) {
            const string CsvFileFormat = "exd-all/{0}{1}.csv";

            IEnumerable<string> filesToExport;

            if (string.IsNullOrWhiteSpace(paramList))
                filesToExport = _Realm.GameData.AvailableSheets;
            else
                filesToExport = paramList.Split(' ').Select(_ => _Realm.GameData.FixName(_));

            var successCount = 0;
            var failCount = 0;
            var oldLang = _Realm.GameData.ActiveLanguage;
            foreach (var name in filesToExport) {
                if (name.StartsWith("content/") || name.StartsWith("custom/") || name.StartsWith("cut_scene/") || name.StartsWith("dungeon/") || name.StartsWith("guild_order/") || name.StartsWith("leve/") || name.StartsWith("opening/") || name.StartsWith("quest/") || name.StartsWith("raid/") || name.StartsWith("shop/") || name.StartsWith("story/") || name.StartsWith("system/") || name.StartsWith("transport/") || name.StartsWith("warp/")) {continue;}
                var sheet = _Realm.GameData.GetSheet(name);
                foreach(var lang in sheet.Header.AvailableLanguages) {
                    var code = lang.GetCode();
                    if (code == "chs" || code == "ko") { continue; }
                    _Realm.GameData.ActiveLanguage = oldLang;
                    if (lang != Language.None) {
                        _Realm.GameData.ActiveLanguage = lang;
                    }
                    if (code.Length > 0)
                        code = "." + code;
                    var target = new FileInfo(Path.Combine(_Realm.GameVersion, string.Format(CsvFileFormat, name, code)));
                    try {

                        if (!target.Directory.Exists)
                            target.Directory.Create();

                        ExdHelper.SaveAsCsv(sheet, lang, target.FullName, false);

                        ++successCount;
                    } catch (Exception e) {
                        OutputError("Export of {0} failed: {1}", name, e.Message);
                        try { if (target.Exists) { target.Delete(); } } catch { }
                        ++failCount;
                    }
                }
                
            }
            OutputInformation("{0} files exported, {1} failed", successCount, failCount);

            return true;
        }
    }
}
