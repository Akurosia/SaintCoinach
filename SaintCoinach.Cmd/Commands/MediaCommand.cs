using System;
using System.Threading.Tasks;
using Tharga.Console.Commands.Base;
using Tharga.Console.Entities;

namespace SaintCoinach.Cmd.Commands {
    public class MediaCommand : AsyncActionCommandBase {
        private readonly ARealmReversed _Realm;

        public MediaCommand(ARealmReversed realm)
            : base("media", "Export UI icons, HD UI icons, maps, and BGM files in parallel.") {
            _Realm = realm;
        }

        public override async Task InvokeAsync(string[] paramList) {
            if (paramList.Length != 0) {
                OutputError("The media command does not accept parameters.");
                return;
            }

            var tasks = new[] {
                RunCommandAsync("ui", new UiCommand(_Realm)),
                RunCommandAsync("uiHD", new HDUiCommand(_Realm)),
                RunCommandAsync("maps", new MapCommand(_Realm)),
                RunCommandAsync("bgm", new BgmCommand(_Realm)),
                RunCommandAsync("uld", new BgmCommand(_Realm)),
                RunCommandAsync("loadingimage", new BgmCommand(_Realm))
            };

            await Task.WhenAll(tasks);
            OutputInformation("Media export complete");
        }

        private async Task RunCommandAsync(string name, AsyncActionCommandBase command) {
            try {
                OutputInformation($"Starting {name}");
                command.WriteEvent += ForwardCommandOutput;
                await Task.Run(() => command.InvokeAsync(Array.Empty<string>()));
                OutputInformation($"Finished {name}");
            }
            catch (Exception e) {
                OutputError($"{name} export failed.");
                OutputError(e, true);
            }
            finally {
                command.WriteEvent -= ForwardCommandOutput;
            }
        }

        private void ForwardCommandOutput(object sender, WriteEventArgs e) {
            Output(e.Message, e.OutputLevel);
        }
    }
}
