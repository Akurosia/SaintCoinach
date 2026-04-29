using System;
using System.IO;
using System.Threading.Tasks;
using SaintCoinach.Imaging;
using Tharga.Console.Commands.Base;

#pragma warning disable CS1998

namespace SaintCoinach.Cmd.Commands {
    public class LoadingImageCommand : AsyncActionCommandBase {
        private const string BasePathFormat = "ui/loadingimage/-nowloading_base{0:D2}.tex";
        private const string HrPathFormat = "ui/loadingimage/-nowloading_base{0:D2}_hr1.tex";

        private readonly ARealmReversed _Realm;

        public LoadingImageCommand(ARealmReversed realm)
            : base("loadingimage", "Export loading images until the next numbered base image is missing.") {
            _Realm = realm;
        }

        public override async Task InvokeAsync(string[] paramList) {
            if (paramList.Length != 0) {
                OutputError("The loadingimage command does not accept parameters.");
                return;
            }

            var exported = 0;
            for (var i = 1; ; i++) {
                var basePath = string.Format(BasePathFormat, i);
                if (!_Realm.Packs.TryGetFile(basePath, out var baseFile))
                    break;

                exported += ExportImage(basePath, baseFile) ? 1 : 0;

                var hrPath = string.Format(HrPathFormat, i);
                if (_Realm.Packs.TryGetFile(hrPath, out var hrFile))
                    exported += ExportImage(hrPath, hrFile) ? 1 : 0;
            }

            OutputInformation($"{exported} loading images processed");
        }

        private bool ExportImage(string path, IO.File file) {
            ImageFile imgFile = null;
            if (path.EndsWith(".atex", StringComparison.OrdinalIgnoreCase) && file is IO.FileDefault defaultFile)
                imgFile = new ImageFile(defaultFile);
            else if (file is ImageFile imageFile)
                imgFile = imageFile;

            if (imgFile == null) {
                OutputError($"{path} is not an image.");
                return false;
            }

            var img = imgFile.GetImage();
            var target = new FileInfo(Path.Combine(_Realm.GameVersion, file.Path));
            if (!target.Directory.Exists)
                target.Directory.Create();

            var webpPath = target.FullName.Substring(0, target.FullName.Length - target.Extension.Length) + ImageExportHelper.WebpExtension;
            ImageExportHelper.SaveAsDropperWebp(img, webpPath);
            OutputInformation(path);
            return true;
        }
    }
}
