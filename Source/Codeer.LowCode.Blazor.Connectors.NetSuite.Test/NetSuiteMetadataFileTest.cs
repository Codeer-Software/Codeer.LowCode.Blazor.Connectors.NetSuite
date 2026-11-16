using NetSuite.Mock;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Match;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>
    /// メタ情報のファイル (NetSuiteMetadataFile) と、それを読む NetSuiteDbAccessor (metadataDirectory 指定) をモックで確かめる。
    /// </summary>
    public class NetSuiteMetadataFileTest
    {
        string _directory = null!;
        readonly List<MockHarness> _harnesses = new();

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(Path.GetTempPath(), "netsuite-metadata-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public async Task TearDown()
        {
            foreach (var h in _harnesses) await h.DisposeAsync();
            _harnesses.Clear();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        async Task<MockHarness> StartAsync(MockNetSuite? mock = null, Action<NetSuiteConnectionSettings>? edit = null, bool useFile = true)
        {
            var h = await MockHarness.StartAsync(mock, edit, null, useFile ? _directory : null);
            _harnesses.Add(h);
            return h;
        }

        /// <summary>ハーネスのモックから、新しい接続 (アクセサの接続とは別) でファイルを作る。アクセサの接続には要求を送らせない。</summary>
        static async Task UpdateFileAsync(MockHarness h, string directory, Action<NetSuiteConnectionSettings>? edit = null)
        {
            var settings = new NetSuiteConnectionSettings
            {
                Name = h.Connection.Settings.Name,
                ServiceUrl = h.Connection.Settings.ServiceUrl,
                ClientId = h.Connection.Settings.ClientId,
                CertificateId = h.Connection.Settings.CertificateId,
                PrivateKey = h.Connection.Settings.PrivateKey,
                Records = h.Connection.Settings.Records.ToList(),
            };
            edit?.Invoke(settings);
            await NetSuiteMetadataFile.UpdateAsync(h.CreateConnection(settings), directory);
        }

        static string Serialize(IEnumerable<DbTableDefinition?> tables) => JsonSerializer.Serialize(tables);

        string FilePath => NetSuiteMetadataFile.GetPath(_directory, TestConnections.DataSourceName);

        // ---------------------------------------------------------------- ファイル

        [Test]
        public void ファイルの場所はフォルダのnetsuite_データソース名_metadata_json()
        {
            Assert.That(NetSuiteMetadataFile.GetPath(_directory, "NS"), Is.EqualTo(Path.Combine(_directory, "netsuite.NS.metadata.json")));
        }

        [Test]
        public async Task UpdateAsyncでファイルができ中身はレコード名順で接続の情報と鍵を含まない()
        {
            var h = await StartAsync(useFile: false);

            await NetSuiteMetadataFile.UpdateAsync(h.Connection, _directory);

            Assert.That(File.Exists(FilePath), Is.True);
            var text = File.ReadAllText(FilePath);
            var root = JsonNode.Parse(text)!.AsObject();
            Assert.That(root.Select(e => e.Key), Is.EqualTo(new[] { "Records" }));
            var records = root["Records"]!.AsArray();
            Assert.That(records.Select(e => e!["Name"]!.GetValue<string>()), Is.EqualTo(new[] { "customer", "customrecord_sample", "subsidiary" }));

            //項目は名前・種類 (文字列)・必須
            var customer = records[0]!["Fields"]!.AsArray().Select(e => e!.AsObject()).ToList();
            Assert.That(customer.All(e => e.Select(p => p.Key).SequenceEqual(new[] { "Name", "Kind", "Required" })), Is.True);
            Assert.That(customer.All(e => e["Kind"]!.GetValueKind() == JsonValueKind.String), Is.True);
            var companyName = customer.Single(e => e["Name"]!.GetValue<string>() == "companyName");
            Assert.That(companyName["Required"]!.GetValue<bool>(), Is.True);
            var email = customer.Single(e => e["Name"]!.GetValue<string>() == "email");
            Assert.That(email["Required"]!.GetValue<bool>(), Is.False);

            //接続の情報・鍵は入らない
            var settings = h.Connection.Settings;
            foreach (var secret in new[] { settings.ClientId, settings.CertificateId, settings.ServiceUrl, "PRIVATE KEY", "MII" })
                Assert.That(text, Does.Not.Contain(secret));
        }

        [Test]
        public async Task 保存したファイルから読んだ表の定義はNetSuiteから直接取った定義と同じ()
        {
            var direct = await StartAsync(useFile: false);
            var fromFile = await StartAsync();
            await UpdateFileAsync(fromFile, _directory);

            var expected = await direct.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);
            var actual = await fromFile.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);

            Assert.That(actual!.Select(e => e.Name).OrderBy(e => e), Is.EqualTo(expected!.Select(e => e.Name).OrderBy(e => e)));
            Assert.That(Serialize(actual!.OrderBy(e => e.Name)), Is.EqualTo(Serialize(expected!.OrderBy(e => e.Name))));
            foreach (var name in new[] { "subsidiary", "customer", "customrecord_sample" })
            {
                Assert.That(Serialize(new[] { await fromFile.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, name) }),
                    Is.EqualTo(Serialize(new[] { await direct.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, name) })), name);
            }
        }

        [Test]
        public async Task ファイルから答えるときはNetSuiteに要求を送らない_トークンも取らない()
        {
            var h = await StartAsync();
            await UpdateFileAsync(h, _directory);
            h.Recorder.Clear();
            var tokens = h.Mock.IssuedTokenCount;

            var tables = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);
            var customer = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customer");

            Assert.That(tables!.Count, Is.EqualTo(3));
            Assert.That(customer!.Name, Is.EqualTo("customer"));
            Assert.That(h.Recorder.Requests, Is.Empty);
            Assert.That(h.Mock.IssuedTokenCount, Is.EqualTo(tokens));
        }

        [Test]
        public async Task ファイルが無ければ表は0件で要求も送らない()
        {
            var h = await StartAsync();

            var tables = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);

            Assert.That(tables, Is.Empty);
            Assert.That(h.Recorder.Requests, Is.Empty);
        }

        [Test]
        public async Task ファイルに無いレコードを1件で求めるとNetSuiteException()
        {
            var h = await StartAsync();
            await UpdateFileAsync(h, _directory, e => e.Records = new() { "subsidiary", "customer" });
            h.Recorder.Clear();

            var ex = Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customrecord_sample"));

            Assert.That(ex!.Message, Does.Contain("customrecord_sample"));
            Assert.That(h.Recorder.Requests, Is.Empty);
        }

        [Test]
        public async Task ファイルが無いときに1件で求めてもNetSuiteException()
        {
            var h = await StartAsync();

            Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customer"));
            Assert.That(h.Recorder.Requests, Is.Empty);
        }

        [Test]
        public async Task Recordsから外したレコードはファイルにあっても返さない()
        {
            var writer = await StartAsync(useFile: false);
            await NetSuiteMetadataFile.UpdateAsync(writer.Connection, _directory);
            var h = await StartAsync(edit: e => e.Records = new() { "subsidiary", "customer" });

            var tables = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);

            Assert.That(tables!.Select(e => e.Name).OrderBy(e => e), Is.EqualTo(new[] { "customer", "subsidiary" }));
            Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customrecord_sample"));
            Assert.That(h.Recorder.Requests, Is.Empty);
        }

        [Test]
        public async Task 項目が増えたNetSuiteでファイルを更新し直すと新しい中身を読む()
        {
            var h = await StartAsync();
            await UpdateFileAsync(h, _directory);
            var before = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customer");
            Assert.That(before!.Columns.Select(e => e.Name), Does.Not.Contain("custentity_rank"));

            //NetSuite に項目が増えた (別のモックで同じデータソース名)
            var data = JsonNode.Parse(File.ReadAllText(MockHarness.DataPath))!;
            data["records"]!["customer"]!["fields"]!.AsObject()["custentity_rank"] = JsonNode.Parse("""{ "type": "integer", "title": "Rank" }""");
            var grown = await StartAsync(MockNetSuite.FromJson(data.ToJsonString()), useFile: false);
            await NetSuiteMetadataFile.UpdateAsync(grown.Connection, _directory);

            var after = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customer");
            var all = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);

            Assert.That(after!.Columns.Select(e => e.Name), Does.Contain("custentity_rank"));
            Assert.That(all!.Single(e => e.Name == "customer").Columns.Select(e => e.Name), Does.Contain("custentity_rank"));
        }

        [Test]
        public async Task 更新の途中で取得に失敗しても前のファイルが残り読める_一時ファイルも残らない()
        {
            var h = await StartAsync();
            await UpdateFileAsync(h, _directory);
            var before = File.ReadAllText(FilePath);

            //2 件目以降に NetSuite に無いレコード (定義の取得が 404 で失敗する)
            Assert.ThrowsAsync<NetSuiteException>(() => UpdateFileAsync(h, _directory, e => e.Records = new() { "customer", "customrecord_missing", "subsidiary" }));

            Assert.That(File.ReadAllText(FilePath), Is.EqualTo(before));
            Assert.That(Directory.GetFiles(_directory), Is.EqualTo(new[] { FilePath }));
            var tables = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);
            Assert.That(tables!.Select(e => e.Name).OrderBy(e => e), Is.EqualTo(new[] { "customer", "customrecord_sample", "subsidiary" }));
        }

        [Test]
        public async Task 同じファイルへ並行して更新しても壊れず中身はどちらか一方()
        {
            var h = await StartAsync();
            var all = new[] { "customer", "customrecord_sample", "subsidiary" };
            var two = new[] { "customer", "subsidiary" };

            await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() =>
                UpdateFileAsync(h, _directory, e => e.Records = (i % 2 == 0 ? all : two).ToList()))));

            Assert.That(Directory.GetFiles(_directory), Is.EqualTo(new[] { FilePath }));
            var names = JsonNode.Parse(File.ReadAllText(FilePath))!["Records"]!.AsArray().Select(e => e!["Name"]!.GetValue<string>()).ToArray();
            Assert.That(names, Is.EqualTo(all).Or.EqualTo(two));
            Assert.That(NetSuiteMetadataFile.Load(_directory, TestConnections.DataSourceName)!.Select(e => e.Name), Is.EqualTo(names));
        }

        [Test]
        public async Task 更新したあとのLoadは最終更新時刻が前と同じでも新しい中身を返す()
        {
            var h = await StartAsync();
            await UpdateFileAsync(h, _directory);
            Assert.That(NetSuiteMetadataFile.Load(_directory, TestConnections.DataSourceName)!.Select(e => e.Name), Is.EqualTo(new[] { "customer", "customrecord_sample", "subsidiary" }));
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(FilePath);

            await UpdateFileAsync(h, _directory, e => e.Records = new() { "subsidiary" });
            File.SetLastWriteTimeUtc(FilePath, lastWriteTimeUtc);

            Assert.That(NetSuiteMetadataFile.Load(_directory, TestConnections.DataSourceName)!.Select(e => e.Name), Is.EqualTo(new[] { "subsidiary" }));
        }

        [Test]
        public async Task 壊れたJSONのファイルはNetSuiteException()
        {
            var h = await StartAsync();
            File.WriteAllText(FilePath, "{ \"Records\": [ { \"Name\": ");

            var ex = Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName));

            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.NotSupported));
            Assert.That(ex.Message, Does.Contain(Path.GetFileName(FilePath)));
            Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customer"));
        }

        [Test]
        public async Task ファイル名に使えない文字を含むデータソース名でも保存して読める()
        {
            const string name = "NS:a/b*c?<d>|\"e";
            var h = await StartAsync(edit: e => e.Name = name);
            await UpdateFileAsync(h, _directory);

            var path = NetSuiteMetadataFile.GetPath(_directory, name);
            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(_directory));
            Assert.That(Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()), Is.EqualTo(-1));
            Assert.That(File.Exists(path), Is.True);
            Assert.That(Directory.GetFiles(_directory), Has.Length.EqualTo(1));

            var tables = await h.Accessor.GetCustomTableDefinitionsAsync(name);
            Assert.That(tables!.Select(e => e.Name).OrderBy(e => e), Is.EqualTo(new[] { "customer", "customrecord_sample", "subsidiary" }));
        }

        // ---------------------------------------------------------------- 読み書きは今までどおり NetSuite へ

        [Test]
        public async Task ファイルから定義を答える設定でも一覧の読み出しと保存はNetSuiteへ送る()
        {
            var h = await StartAsync();
            await UpdateFileAsync(h, _directory);
            h.Recorder.Clear();

            //一覧 (QueryAsync)
            var list = await h.IO.GetListAsync(new SearchCondition("Customer")
            {
                Condition = new FieldValueMatchCondition { SearchTargetVariable = "Id.Value", Comparison = MatchComparison.Equal, Value = new StringValue { Value = "1" } },
            }, 0);
            Assert.That(list.Items.Single().Fields["CompanyName"], Is.TypeOf<TextFieldData>());
            Assert.That(((TextFieldData)list.Items.Single().Fields["CompanyName"]).Value, Is.EqualTo(MockHarness.Rows("customer")[0].Text("companyName")));
            Assert.That(h.Recorder.Requests.Any(e => e.Method == HttpMethod.Post && e.Path.EndsWith("/suiteql")), Is.True);

            //作成 (InsertAsync)
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Subsidiary" };
            add.Fields["Id"] = tempId;
            add.Fields["Name"] = new TextFieldData { Value = "Branch" };
            var added = await h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Subsidiary", Id = tempId.Value!, Add = new() { add } } });
            Assert.That(added.Single().ExceptionMessage, Is.Empty);
            Assert.That(h.Recorder.Requests.Any(e => e.Method == HttpMethod.Post && e.Path == "/services/rest/record/v1/subsidiary"), Is.True);
            var newId = added.Single().DestinationId;

            //更新
            var update = new ModuleData { Name = "Subsidiary" };
            update.Fields["Id"] = new IdFieldData { Value = newId };
            update.Fields["Name"] = new TextFieldData { Value = "Branch 2" };
            var updated = await h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Subsidiary", Id = newId, Update = new() { update } } });
            Assert.That(updated.Single().ExceptionMessage, Is.Empty);
            Assert.That(h.Recorder.Requests.Any(e => e.Method == HttpMethod.Patch && e.Path == $"/services/rest/record/v1/subsidiary/{newId}"), Is.True);

            //削除
            var deleted = await h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Subsidiary", Delete = new() { new ModuleDeleteInfo { ModuleName = "Subsidiary", Id = newId } } } });
            Assert.That(deleted.Single().ExceptionMessage, Is.Empty);
            Assert.That(h.Recorder.Requests.Any(e => e.Method == HttpMethod.Delete && e.Path == $"/services/rest/record/v1/subsidiary/{newId}"), Is.True);

            //内側の DB には行かない
            Assert.That(h.Inner.DataSourceCalls, Is.Empty);
        }

        // ---------------------------------------------------------------- 明細

        static IEnumerable<(string, NetSuiteFieldKind, bool)> Describe(NetSuiteFieldDefinition field)
            => field.SublistFields.Select(e => (e.RestName, e.Kind, e.IsNullable));

        [Test]
        public async Task 明細の行の項目はファイルに保存して読み戻せる_明細でない項目はFieldsを書かない()
        {
            var direct = await StartAsync(edit: e => e.Records.AddRange(new[] { "item", "salesorder" }), useFile: false);
            var fromFile = await StartAsync(edit: e => e.Records.AddRange(new[] { "item", "salesorder" }));
            await UpdateFileAsync(fromFile, _directory);

            var records = JsonNode.Parse(File.ReadAllText(FilePath))!["Records"]!.AsArray();
            var salesOrder = records.Single(e => e!["Name"]!.GetValue<string>() == "salesorder")!["Fields"]!.AsArray().Select(e => e!.AsObject()).ToList();
            var item = salesOrder.Single(e => e["Name"]!.GetValue<string>() == "item");
            Assert.That(item["Kind"]!.GetValue<string>(), Is.EqualTo("Sublist"));
            Assert.That(item["Fields"]!.AsArray().Select(e => e!["Name"]!.GetValue<string>()), Does.Contain("line").And.Contain("expectedShipDate"));
            Assert.That(salesOrder.Where(e => e != item).All(e => !e.ContainsKey("Fields")), Is.True);

            var expected = await direct.Connection.GetRecordDefinitionAsync("salesorder");
            var actual = NetSuiteMetadataFile.Load(_directory, TestConnections.DataSourceName)!.Single(e => e.Name == "salesorder");
            Assert.That(Describe(actual.FindByColumn("item")!), Is.EqualTo(Describe(expected.FindByColumn("item")!)));
            Assert.That(Serialize(new[] { await fromFile.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "salesorder") }),
                Is.EqualTo(Serialize(new[] { await direct.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "salesorder") })));
        }

        [Test]
        public async Task ファイルの定義でも明細を読み書きできFieldsの無い古いファイルも読める()
        {
            var h = await MockHarness.StartAsync(null, e => e.Records.AddRange(new[] { "item", "salesorder" }), MockSublistTest.AddModules, _directory);
            _harnesses.Add(h);
            await UpdateFileAsync(h, _directory);

            var order = (await h.IO.GetListAsync(new SearchCondition("Order")
            {
                Condition = new FieldValueMatchCondition { SearchTargetVariable = "Id.Value", Comparison = MatchComparison.Equal, Value = new StringValue { Value = "2" } },
            }, 0)).Items.Single();
            var items = (ListFieldData)order.Fields["Items"];
            Assert.That(items.Children, Has.Count.EqualTo(1));
            ((NumberFieldData)items.Children[0].Fields["Quantity"]).Value = 9;
            var update = new ModuleData { Name = "Order" };
            update.Fields["Id"] = new IdFieldData { Value = "2" };
            update.Fields["Items"] = items;
            h.Recorder.Clear();
            var result = await h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Order", Id = "2", Update = new() { update } } });
            Assert.That(result.Single().ExceptionMessage, Is.Empty);
            Assert.That(h.Recorder.Requests.Single(e => e.Method == HttpMethod.Patch).Body, Is.EqualTo("""{"item":{"items":[{"line":1,"quantity":9}]}}"""));

            //Fields の無い古いファイル: 明細を知らないので列にならず、ほかの項目は今までどおり
            var root = JsonNode.Parse(File.ReadAllText(FilePath))!;
            foreach (var record in root["Records"]!.AsArray())
            {
                var fields = record!["Fields"]!.AsArray();
                foreach (var field in fields.Where(e => e!["Kind"]!.GetValue<string>() == "Sublist").ToList()) fields.Remove(field);
            }
            File.WriteAllText(FilePath, root.ToJsonString());
            var old = await StartAsync(edit: e => e.Records.AddRange(new[] { "item", "salesorder" }));
            var table = await old.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "salesorder");
            Assert.That(table!.Columns.Select(e => e.Name), Does.Contain("memo").And.Not.Contain("item"));
        }
    }
}
