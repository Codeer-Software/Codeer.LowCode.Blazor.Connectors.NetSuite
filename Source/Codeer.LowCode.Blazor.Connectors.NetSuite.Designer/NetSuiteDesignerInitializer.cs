using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.Designer.Extensibility;
using Codeer.LowCode.Blazor.Designer.Views.Windows;
using Codeer.LowCode.Blazor.SystemSettings;
using System.Globalization;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Designer
{
    public static class NetSuiteDesignerInitializer
    {
        const string Prefix = "Codeer.LowCode.Blazor.Connectors.NetSuite.Designer.SpecDocs.";

        static DesignerEnvironment? _environment;

        static bool IsJapanese => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";

        /// <summary>
        /// NetSuite のモジュールを設計するための AI 用の仕様ドキュメントを登録する (ai-refresh が _specs/NetSuite.md に書き出す)。
        /// デザイナのアプリの OnStartup で、base.OnStartup(e) より前に呼ぶ。
        /// これを呼ぶと、<see cref="CreateDbAccessor"/> の IDbAccessor は NetSuite の表の定義を、プロジェクトのフォルダに保存したファイルから読む
        /// (NetSuite に取りに行くのは Tools のメニューで更新したときだけ。headless の CLI でも同じ)。
        /// </summary>
        public static void Initialize(DesignerEnvironment designerEnvironment)
        {
            _environment = designerEnvironment;
            var assembly = typeof(NetSuiteDesignerInitializer).Assembly;
            foreach (var name in assembly.GetManifestResourceNames()
                         .Where(e => e.StartsWith(Prefix, StringComparison.Ordinal) && e.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(e => e, StringComparer.Ordinal))
            {
                SpecDocCatalog.RegisterEmbedded(assembly, name);
            }
        }

        /// <summary>
        /// メニュー Tools &gt; NetSuite のメタ情報を更新 を足す。デザイナのアプリの OnStartup で、base.OnStartup(e) の後に呼ぶ。
        /// </summary>
        public static void Setup(DesignerEnvironment designerEnvironment)
        {
            _environment = designerEnvironment;
            designerEnvironment.AddMainMenu(() => UpdateMetadata(designerEnvironment), "Tools",
                IsJapanese ? "NetSuite のメタ情報を更新" : "Update NetSuite Metadata");
        }

        /// <summary>
        /// デザイナ用の IDbAccessor (IDbAccessorFactory の実装で使う)。NetSuite 以外のデータソースは inner が受け持つ。
        /// </summary>
        public static IDbAccessor CreateDbAccessor(IDbAccessor inner, IEnumerable<DataSource> dataSources)
            => new NetSuiteDbAccessor(inner, NetSuiteConnection.FromDataSources(dataSources), GetProjectDirectory());

        static string? GetProjectDirectory()
        {
            if (_environment == null) return null;
            try
            {
                return _environment.CurrentFileDirectory;
            }
            catch (NullReferenceException)
            {
                //プロジェクトを開いていない
                return null;
            }
        }

        static async void UpdateMetadata(DesignerEnvironment environment)
        {
            try
            {
                var directory = GetProjectDirectory();
                if (string.IsNullOrEmpty(directory)) return;

                var connections = NetSuiteConnection.FromDataSources(environment.GetDesignerSettings().DataSources);
                if (connections.Count == 0)
                {
                    environment.ShowToast(IsJapanese ? "NetSuite のデータソースがありません。" : "There is no NetSuite data source.", false);
                    return;
                }
                foreach (var connection in connections) await NetSuiteMetadataFile.UpdateAsync(connection, directory);

                environment.RefreshDatabase();
                environment.ShowToast(IsJapanese ? "NetSuite のメタ情報を更新しました。" : "NetSuite metadata has been updated.", true);
            }
            catch (Exception e)
            {
                MessageWindow.Show(e.Message, "Error");
            }
        }
    }
}
