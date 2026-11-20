using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest.Harness
{
    /// <summary>
    /// テスト 1 つ分の一時フォルダ: 空のデザインプロジェクト (NetSuite のデータソースだけ) と、その場で作った鍵。
    /// </summary>
    public sealed class DesignProject : IDisposable
    {
        public const string DataSourceName = "NetSuite";

        public string Root { get; }
        public string Directory { get; }
        public string ProjectFile => Path.Combine(Directory, "app.clprj");
        //メタ情報のファイルはプロジェクトのサーバー専用ファイル
        public string MetadataFile => Path.Combine(Directory, "ServerFiles", $"netsuite.{DataSourceName}.metadata.json");
        public string FolderName => Path.GetFileName(Directory);

        public DesignProject(string serviceUrl)
        {
            Root = Path.Combine(Path.GetTempPath(), "NetSuiteDesignerTest", Guid.NewGuid().ToString("N"));
            Directory = Path.Combine(Root, "App");
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var folder in new[] { "Modules", "PageFrames", "Enums", "Resources" })
                System.IO.Directory.CreateDirectory(Path.Combine(Directory, folder));

            //コネクターが JWT の署名に使う鍵 (モックは署名を確かめない)。プロジェクトの外に置く
            var keyPath = Path.Combine(Root, "test-key.pem");
            using (var rsa = RSA.Create(2048)) File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

            File.WriteAllText(ProjectFile, "{\n  \"OmitDefaultValues\": true\n}\n");
            WriteJson("designer.settings.json", new JsonObject
            {
                ["DataSources"] = new JsonArray(new JsonObject
                {
                    ["Name"] = DataSourceName,
                    ["DataSourceType"] = "Oracle",
                }),
                ["FileStorageNames"] = new JsonArray(),
            });
            WriteJson("designer.settings.Development.json", new JsonObject
            {
                ["ConnectionStrings"] = new JsonObject
                {
                    [DataSourceName] = $"Provider=NetSuite;ServiceUrl={serviceUrl};ClientId=mock-client;CertificateId=mock-cert;PrivateKeyPath={keyPath}",
                },
            });
        }

        void WriteJson(string name, JsonNode node)
            => File.WriteAllText(Path.Combine(Directory, name), node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        /// <summary>前回開いていたタブの復元を止める。</summary>
        public void CleanIdeState()
        {
            var ide = Path.Combine(Directory, ".ide");
            if (System.IO.Directory.Exists(ide)) System.IO.Directory.Delete(ide, true);
        }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Root)) System.IO.Directory.Delete(Root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
