using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness
{
    /// <summary>一時フォルダのデザインのプロジェクト (メタ情報のファイルの置き場)。Dispose で消す。</summary>
    public sealed class TestProject : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "NetSuiteTest", Guid.NewGuid().ToString("N"));

        public TestProject() => System.IO.Directory.CreateDirectory(Directory);

        public NetSuiteMetadataFile MetadataFile => NetSuiteMetadataFile.FromProject(Directory);

        /// <summary>メタ情報のファイルのパス (プロジェクトのサーバー専用ファイル)。</summary>
        public string MetadataPath(string dataSourceName = TestConnections.DataSourceName)
            => Path.Combine(Directory, "ServerFiles", NetSuiteMetadataFile.GetFileName(dataSourceName));

        /// <summary>
        /// 偽物の定義 (レコード種別 → JSON Schema) からメタ情報のファイルを書く。書くための要求は別の偽物に送る (テストの偽物の記録に混ぜない)。
        /// </summary>
        public async Task WriteAsync(IReadOnlyDictionary<string, string> schemas)
        {
            using var fake = new FakeNetSuite();
            fake.Schemas.Clear();
            foreach (var (name, schema) in schemas) fake.Schemas[name] = schema;
            await NetSuiteMetadataFile.UpdateAsync(TestConnections.Create(fake), Directory);
        }

        public void Dispose()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
