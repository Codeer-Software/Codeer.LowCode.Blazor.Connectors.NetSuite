using Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest.Drivers;
using Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest.Harness;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest
{
    /// <summary>
    /// デザイナを起動して、NetSuite の設定を利用者と同じ流れで通す:
    /// プロジェクトを開く → Tools > NetSuite のメタ情報を更新 → レコードがテーブルとして出る → テーブルからモジュールを作る。
    /// 相手はモック (このプロセスの中で Kestrel に立てる)。
    /// </summary>
    [NonParallelizable]
    public class NetSuiteDesignerTest
    {
        const string DataSource = DesignProject.DataSourceName;
        static readonly string[] UpdateMenu = { "NetSuite のメタ情報を更新", "Update NetSuite Metadata" };
        static readonly string[] UpdatedMessage = { "NetSuite のメタ情報を更新しました。", "NetSuite metadata has been updated." };
        static readonly TimeSpan LaunchLimit = TimeSpan.FromSeconds(120);
        static readonly TimeSpan Timeout = DesignerDriver.DefaultTimeout;

        MockServer _mock = null!;
        DesignProject _project = null!;
        readonly List<DesignerDriver> _designers = new();
        readonly List<IDisposable> _disposables = new();

        [SetUp]
        public async Task SetUp()
        {
            _mock = await MockServer.StartAsync();
            _project = new DesignProject(_mock.ServiceUrl);
        }

        [TearDown]
        public async Task TearDown()
        {
            //このテストが起動したデザイナだけを止める
            foreach (var designer in _designers) designer.Dispose();
            _designers.Clear();
            foreach (var disposable in _disposables) disposable.Dispose();
            _disposables.Clear();
            await _mock.DisposeAsync();
            _project.Dispose();
        }

        DesignerDriver Launch()
        {
            _project.CleanIdeState();
            var designer = DesignerDriver.Launch(_project.ProjectFile, LaunchLimit);
            _designers.Add(designer);
            return designer;
        }

        void Close(DesignerDriver designer)
        {
            designer.Dispose();
            _designers.Remove(designer);
        }

        static void UpdateMetadata(DesignerDriver designer)
        {
            var previous = designer.CurrentToast;
            designer.ClickExtensionMenu(UpdateMenu);
            var toast = designer.WaitForNewToast(previous, Timeout);
            Assert.That(toast.IsSuccess, Is.True, toast.Message);
            Assert.That(UpdatedMessage, Does.Contain(toast.Message));
        }

        static DesignerDriver.TableInfo Table(List<DesignerDriver.TableInfo> tables, string name)
            => tables.Single(e => e.Name == name);

        static DesignerDriver.ColumnInfo Column(DesignerDriver.TableInfo table, string name)
            => table.Columns.Single(e => e.Name == name);

        [Test]
        public void OpenProject_WithoutMetadataFile_HasNoTablesAndSendsNoRequest()
        {
            var designer = Launch();
            //開いた直後の非同期の処理が落ち着くのを待つ
            Thread.Sleep(2000);

            Assert.That(designer.GetTables(DataSource), Is.Empty);
            Assert.That(_mock.RequestCount, Is.EqualTo(0), "the designer must not call NetSuite when opening a project");
            Assert.That(designer.GetOtherVisibleWindows(), Is.Empty, "no modal");
            Assert.That(designer.CurrentToast, Is.Null, "no error toast");
            Assert.That(File.Exists(_project.MetadataFile), Is.False);
            Assert.That(designer.GetExtensionMenuHeaders().Intersect(UpdateMenu), Is.Not.Empty);
        }

        [Test]
        public void UpdateMetadata_WritesFileAndShowsRecordsAsTables()
        {
            var designer = Launch();
            UpdateMetadata(designer);

            Assert.That(_mock.RequestCount, Is.GreaterThan(0));
            Assert.That(File.Exists(_project.MetadataFile), Is.True);
            using (var file = JsonDocument.Parse(File.ReadAllText(_project.MetadataFile)))
            {
                var records = file.RootElement.GetProperty("Records").EnumerateArray().Select(e => e.GetProperty("Name").GetString()).ToList();
                Assert.That(records, Is.EquivalentTo(MockServer.DefaultTables));
            }

            var tables = designer.GetTables(DataSource);
            Assert.That(tables.Select(e => e.Name), Is.EquivalentTo(MockServer.DefaultTables));

            var customer = Table(tables, "customer");
            Assert.That(customer.Columns.Select(e => e.Name), Is.EquivalentTo(new[]
            {
                "id", "companyname", "email", "isinactive", "creditlimit", "priority", "startdate", "lastmodifieddate", "subsidiary", "emailpreference", "datecreated",
            }));
            Assert.That(Column(customer, "id").NetTypeFullName, Is.EqualTo(typeof(string).FullName));
            Assert.That(Column(customer, "companyname").NetTypeFullName, Is.EqualTo(typeof(string).FullName));
            Assert.That(Column(customer, "isinactive").NetTypeFullName, Is.EqualTo(typeof(bool).FullName));
            Assert.That(Column(customer, "creditlimit").NetTypeFullName, Is.EqualTo(typeof(decimal).FullName));
            Assert.That(Column(customer, "priority").NetTypeFullName, Is.EqualTo(typeof(long).FullName));
            Assert.That(Column(customer, "startdate").NetTypeFullName, Is.EqualTo(typeof(DateOnly).FullName));
            Assert.That(Column(customer, "lastmodifieddate").NetTypeFullName, Is.EqualTo(typeof(DateTime).FullName));
            Assert.That(Column(customer, "subsidiary").NetTypeFullName, Is.EqualTo(typeof(string).FullName));
            //必須の項目も null 可で見せる
            Assert.That(Column(customer, "companyname").IsNullable, Is.True);

            var sample = Table(tables, "customrecord_sample");
            Assert.That(sample.Columns.Select(e => e.Name), Does.Contain("id").And.Contain("custrecord_customer").And.Contain("custrecord_recorded"));
            Assert.That(Table(tables, "subsidiary").Columns.Select(e => e.Name), Is.EquivalentTo(new[] { "id", "name", "country", "isinactive" }));

            Assert.That(designer.GetOtherVisibleWindows(), Is.Empty);
        }

        [Test]
        public void CreateModuleFromTable_FieldsFollowColumns_AndDesignCheckPasses()
        {
            var designer = Launch();
            designer.SuspendFileWatcher();
            UpdateMetadata(designer);

            designer.CreateModuleFromTable("Customer", DataSource, "customer");
            designer.CreateModuleFromTable("Sample", DataSource, "customrecord_sample");
            Assert.That(designer.GetModuleNames(), Does.Contain("Customer").And.Contain("Sample"));
            Assert.That(File.Exists(Path.Combine(_project.Directory, "Modules", "Customer.mod.json")), Is.True);

            using (var customer = designer.GetModuleDesign("Customer"))
            {
                var root = customer.RootElement;
                Assert.That(root.GetProperty("DataSourceName").GetString(), Is.EqualTo(DataSource));
                Assert.That(root.GetProperty("DbTable").GetString(), Is.EqualTo("customer"));
                var fields = Fields(root);
                Assert.That(fields.Keys, Is.EquivalentTo(new[]
                {
                    "id", "companyname", "email", "isinactive", "creditlimit", "priority", "startdate", "lastmodifieddate", "subsidiary", "emailpreference", "datecreated",
                }));
                Assert.That(fields["id"].Type, Is.EqualTo("IdFieldDesign"));
                Assert.That(fields["companyname"].Type, Is.EqualTo("TextFieldDesign"));
                Assert.That(fields["isinactive"].Type, Is.EqualTo("BooleanFieldDesign"));
                Assert.That(fields["creditlimit"].Type, Is.EqualTo("NumberFieldDesign"));
                Assert.That(fields["priority"].Type, Is.EqualTo("NumberFieldDesign"));
                Assert.That(fields["startdate"].Type, Is.EqualTo("DateFieldDesign"));
                Assert.That(fields["lastmodifieddate"].Type, Is.EqualTo("DateTimeFieldDesign"));
                Assert.That(fields["lastmodifieddate"].SaveAsUtc, Is.True, "SaveAsUtc of a dateTime field");
                //参照は相手の internal ID の文字列
                Assert.That(fields["subsidiary"].Type, Is.EqualTo("TextFieldDesign"));
                Assert.That(fields["companyname"].IsRequired, Is.False);
            }
            using (var sample = designer.GetModuleDesign("Sample"))
            {
                var fields = Fields(sample.RootElement);
                Assert.That(fields["custrecord_recorded"].Type, Is.EqualTo("DateTimeFieldDesign"));
                Assert.That(fields["custrecord_recorded"].SaveAsUtc, Is.True);
                Assert.That(fields["custrecord_customer"].Type, Is.EqualTo("TextFieldDesign"));
                Assert.That(fields["custrecord_date"].Type, Is.EqualTo("DateFieldDesign"));
            }

            designer.OpenModule("Customer");
            designer.WaitFor(() => designer.IsModuleEditorOpen("Customer"), Timeout, "module editor opened");

            Assert.That(designer.RunDesignCheck(), Is.Empty);
            Assert.That(designer.GetOtherVisibleWindows(), Is.Empty);
        }

        [Test]
        public async Task Offline_ReopenProject_UsesSavedMetadataWithoutRequests()
        {
            var designer = Launch();
            designer.SuspendFileWatcher();
            UpdateMetadata(designer);
            designer.CreateModuleFromTable("Customer", DataSource, "customer");
            Close(designer);

            //NetSuite に届かない状態 (接続を受けてすぐ切る。来た接続を数える)
            await _mock.StopAsync();
            var dead = new DeadPort(_mock.Port);
            _disposables.Add(dead);

            designer = Launch();
            Thread.Sleep(2000);
            var tables = designer.GetTables(DataSource);
            Assert.That(tables.Select(e => e.Name), Is.EquivalentTo(MockServer.DefaultTables));
            Assert.That(Table(tables, "customer").Columns.Select(e => e.Name), Does.Contain("lastmodifieddate"));

            designer.OpenModule("Customer");
            designer.WaitFor(() => designer.IsModuleEditorOpen("Customer"), Timeout, "module editor opened");
            Assert.That(designer.RunDesignCheck(), Is.Empty);

            Assert.That(dead.ConnectionCount, Is.EqualTo(0), "the designer must not call NetSuite");
            Assert.That(designer.GetOtherVisibleWindows(), Is.Empty);
            Assert.That(designer.CurrentToast, Is.Null, "no error toast");
        }

        [Test]
        public async Task UpdateMetadata_WhenNetSuiteIsDown_ShowsErrorAndKeepsFile()
        {
            var designer = Launch();
            UpdateMetadata(designer);
            var before = File.ReadAllText(_project.MetadataFile);

            await _mock.StopAsync();
            designer.ClickExtensionMenu(UpdateMenu);
            var message = designer.WaitForMessageWindowAndClose(TimeSpan.FromSeconds(60));
            TestContext.WriteLine("error message: " + message);
            Assert.That(message, Is.Not.Empty);

            Assert.That(designer.HasExited, Is.False);
            Assert.That(File.ReadAllText(_project.MetadataFile), Is.EqualTo(before));
            Assert.That(designer.GetTables(DataSource).Select(e => e.Name), Is.EquivalentTo(MockServer.DefaultTables));
            Assert.That(designer.GetOtherVisibleWindows(), Is.Empty);
        }

        [Test]
        public async Task UpdateMetadata_AfterFieldAddedInNetSuite_ShowsNewColumn()
        {
            var designer = Launch();
            UpdateMetadata(designer);
            Assert.That(Table(designer.GetTables(DataSource), "customer").Columns.Select(e => e.Name), Does.Not.Contain("custentity_rank"));

            //NetSuite 側で項目を足した (同じ URL で、データを差し替えたモックを立て直す)
            await _mock.RestartAsync(MockServer.DataWithField("customer", "custentity_rank", "integer", "Rank"));

            //更新するまではファイルの定義のまま
            Assert.That(Table(designer.GetTables(DataSource), "customer").Columns.Select(e => e.Name), Does.Not.Contain("custentity_rank"));

            UpdateMetadata(designer);
            var rank = Column(Table(designer.GetTables(DataSource), "customer"), "custentity_rank");
            Assert.That(rank.NetTypeFullName, Is.EqualTo(typeof(long).FullName));
            Assert.That(File.ReadAllText(_project.MetadataFile), Does.Contain("custentity_rank"));
        }

        sealed record FieldInfo(string Type, bool SaveAsUtc, bool IsRequired);

        //DbColumn → フィールド
        static Dictionary<string, FieldInfo> Fields(JsonElement module)
        {
            var result = new Dictionary<string, FieldInfo>();
            foreach (var field in module.GetProperty("Fields").EnumerateArray())
            {
                if (!field.TryGetProperty("DbColumn", out var dbColumn) || dbColumn.ValueKind != JsonValueKind.String) continue;
                var type = field.GetProperty("TypeFullName").GetString()!.Split('.').Last();
                var saveAsUtc = field.TryGetProperty("SaveAsUtc", out var utc) && utc.ValueKind == JsonValueKind.True;
                var isRequired = field.TryGetProperty("IsRequired", out var required) && required.ValueKind == JsonValueKind.True;
                result[dbColumn.GetString()!] = new FieldInfo(type, saveAsUtc, isRequired);
            }
            return result;
        }
    }
}
