using NetSuite.Mock;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Match;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>
    /// メタ情報のファイル (NetSuiteMetadataFile) の書き出し (UpdateAsync) と、それを読む NetSuiteDbAccessor (プロジェクト / 配られたデザイン) をモックで確かめる。
    /// </summary>
    public class NetSuiteMetadataFileTest
    {
        string _directory = null!;
        readonly List<MockHarness> _harnesses = new();

        //モックの全部のレコード (取引の明細の表を含む)
        static readonly string[] AllTables = { "customer", "customrecord_sample", "employee", "item", "salesorder", "salesorder_item", "subsidiary" };

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

        /// <param name="metadataFile">null ならモックから書いたハーネスのプロジェクトのファイル。</param>
        async Task<MockHarness> StartAsync(MockNetSuite? mock = null, Action<NetSuiteConnectionSettings>? edit = null, NetSuiteMetadataFile? metadataFile = null)
        {
            var h = await MockHarness.StartAsync(mock, edit, null, metadataFile);
            _harnesses.Add(h);
            return h;
        }

        //アクセサの接続とは別の接続 (要求をハーネスの記録に入れない)
        static NetSuiteConnection Writer(MockHarness h, HttpMessageHandler? handler = null)
            => new(h.Connection.Settings, handler ?? h.Handler) { Delay = _ => Task.CompletedTask };

        static string Serialize(IEnumerable<DbTableDefinition?> tables) => JsonSerializer.Serialize(tables);

        NetSuiteMetadataFile ProjectFile => NetSuiteMetadataFile.FromProject(_directory);

        string FilePath => Path.Combine(_directory, "ServerFiles", NetSuiteMetadataFile.GetFileName(TestConnections.DataSourceName));

        static string[] Names(NetSuiteMetadata metadata) => metadata.Records.Select(e => e.Name).ToArray();

        // ---------------------------------------------------------------- ファイルの名前と置き場

        [TestCase("NS", "netsuite.NS.metadata.json")]
        [TestCase("a-b_c.d", "netsuite.a-b_c.d.metadata.json")]
        [TestCase("NS:a/b*c?<d>|\"e f\\g", "netsuite.NS_a_b_c__d___e_f_g.metadata.json")]
        [TestCase("本番", "netsuite.本番.metadata.json")]
        public void ファイル名はデータソース名の文字と数字とアンダースコアとハイフンとピリオド以外をアンダースコアにしたもの(string dataSourceName, string expected)
        {
            //OS (ファイル名に使えない文字) に依らない決まり。文字は英字に限らない (日本語なども残す)
            Assert.That(NetSuiteMetadataFile.GetFileName(dataSourceName), Is.EqualTo(expected));
        }

        [Test]
        public async Task UpdateAsyncでプロジェクトのサーバー専用ファイルができ中身は全レコードの名前順で接続の情報と鍵を含まない()
        {
            var h = await StartAsync();

            await NetSuiteMetadataFile.UpdateAsync(Writer(h), _directory);

            Assert.That(File.Exists(FilePath), Is.True);
            //プロジェクトの直下には置かない
            Assert.That(Directory.GetFiles(_directory), Is.Empty);
            var text = File.ReadAllText(FilePath);
            var root = JsonNode.Parse(text)!.AsObject();
            Assert.That(root.Select(e => e.Key), Is.EqualTo(new[] { "Records" }));
            var records = root["Records"]!.AsArray();
            Assert.That(records.Select(e => e!["Name"]!.GetValue<string>()), Is.EqualTo(AllTables));

            //項目は名前・種類 (文字列)・必須。読み取り専用と選択肢は、あるときだけ
            var customer = records[0]!["Fields"]!.AsArray().Select(e => e!.AsObject()).ToList();
            Assert.That(customer.All(e => e.Select(p => p.Key).Take(3).SequenceEqual(new[] { "Name", "Kind", "Required" })), Is.True);
            Assert.That(customer.Where(e => e.Count > 3).Select(e => e["Name"]!.GetValue<string>() + ":" + string.Join(",", e.Skip(3).Select(p => p.Key))),
                Is.EquivalentTo(new[] { "emailPreference:Choices", "dateCreated:ReadOnly" }));
            Assert.That(customer.All(e => e["Kind"]!.GetValueKind() == JsonValueKind.String), Is.True);
            Assert.That(customer.Single(e => e["Name"]!.GetValue<string>() == "companyName")["Required"]!.GetValue<bool>(), Is.True);
            Assert.That(customer.Single(e => e["Name"]!.GetValue<string>() == "email")["Required"]!.GetValue<bool>(), Is.False);

            //接続の情報・鍵は入らない
            var settings = h.Connection.Settings;
            foreach (var secret in new[] { settings.ClientId, settings.CertificateId, settings.ServiceUrl, "PRIVATE KEY", "MII" })
                Assert.That(text, Does.Not.Contain(secret));
        }

        [Test]
        public async Task 表名はレコード種別の一覧の名前を小文字にしたもの()
        {
            //モックのレコード種別の一覧は salesOrder (大文字を含む)
            var h = await StartAsync();
            var catalog = JsonNode.Parse(h.Mock.GetMetadataCatalog("").Body!)!["items"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()).ToList();
            Assert.That(catalog, Does.Contain("salesOrder"));

            Assert.That(Names(h.Project.MetadataFile.Load(TestConnections.DataSourceName)), Does.Contain("salesorder").And.Not.Contain("salesOrder"));
        }

        [Test]
        public async Task 対応表に無い取引は取り込まない()
        {
            //取引 (transactionType あり) だが、コネクターの取引の対応表に無いレコード
            var data = JsonNode.Parse(File.ReadAllText(MockHarness.DataPath))!;
            data["records"]!.AsObject()["customtransaction_x"] = JsonNode.Parse("""
                { "transactionType": "CuTrX", "fields": { "memo": "string" }, "rows": [ { "id": 900, "memo": "x" } ] }
                """);
            var h = await StartAsync(MockNetSuite.FromJson(data.ToJsonString()));

            var names = Names(h.Project.MetadataFile.Load(TestConnections.DataSourceName));
            Assert.That(names, Does.Not.Contain("customtransaction_x"));
            Assert.That(names, Is.EqualTo(AllTables));
        }

        [Test]
        public async Task 明細の表は対応表の取引の品目の明細だけで_ほかの明細は表にしない()
        {
            //受注にもう 1 つ明細 (shipGroup) がある NetSuite
            var data = JsonNode.Parse(File.ReadAllText(MockHarness.DataPath))!;
            data["records"]!["salesOrder"]!["sublists"]!.AsObject()["shipGroup"] = JsonNode.Parse("""
                { "fields": { "shipMethod": { "type": "string", "title": "Ship Method" } } }
                """);
            var h = await StartAsync(MockNetSuite.FromJson(data.ToJsonString()));

            var names = Names(h.Project.MetadataFile.Load(TestConnections.DataSourceName));
            Assert.That(names, Is.EqualTo(AllTables));
            //明細そのものは受注の列として読み書きできる
            var salesOrder = h.Project.MetadataFile.Load(TestConnections.DataSourceName).Records.Single(e => e.Name == "salesorder");
            Assert.That(salesOrder.FindByColumn("shipgroup")!.Kind, Is.EqualTo(NetSuiteFieldKind.Sublist));
        }

        [Test]
        public async Task 前の版が書いた余計な明細の表はファイルから読まない()
        {
            var writer = await StartAsync();
            await NetSuiteMetadataFile.UpdateAsync(Writer(writer), _directory);
            //前の版は取引の明細を全部表にしていた (salesorder_shipgroup)
            var root = JsonNode.Parse(File.ReadAllText(FilePath))!;
            var extra = root["Records"]!.AsArray().Single(e => e!["Name"]!.GetValue<string>() == "salesorder_item")!.DeepClone();
            extra["Name"] = "salesorder_shipgroup";
            extra["Sublist"] = "shipGroup";
            root["Records"]!.AsArray().Add(extra);
            File.WriteAllText(FilePath, root.ToJsonString());

            var names = Names(ProjectFile.Load(TestConnections.DataSourceName));

            Assert.That(names, Is.EqualTo(AllTables));
        }

        [TestCase("salesorder", "SalesOrd", "item")]
        [TestCase("itemfulfillment", "ItemShip", "item")]
        [TestCase("transferorder", "TrnfrOrd", "item")]
        [TestCase("workorder", "WorkOrd", null)]
        [TestCase("purchaseorder", "PurchOrd", null)]
        [TestCase("journalentry", "Journal", null)]
        [TestCase("customer", null, null)]
        public void 取引の種別と明細の表にする明細(string recordType, string? transactionType, string? lineSublist)
        {
            Assert.That(SuiteQlTranslator.GetTransactionType(recordType), Is.EqualTo(transactionType));
            Assert.That(SuiteQlTranslator.GetLineSublist(recordType), Is.EqualTo(lineSublist));
        }

        [Test]
        public async Task Example_Designのメタ情報のファイルはモックから書き出したものと同じ()
        {
            var h = await StartAsync();
            var written = JsonNode.Parse(File.ReadAllText(h.Project.MetadataPath()))!;

            var example = Path.Combine(FindSourceDirectory(), "Example", "Design", "ServerFiles", "netsuite.NetSuite.metadata.json");
            Assert.That(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(example)), written), Is.True,
                "Example/Design の netsuite.NetSuite.metadata.json をモックから書き出し直す");
        }

        static string FindSourceDirectory()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Example", "Design"))) return dir.FullName;
            }
            throw new DirectoryNotFoundException("Source/Example/Design was not found above the test output folder.");
        }

        // ---------------------------------------------------------------- プロジェクトのファイルから読む (デザイナ)

        [Test]
        public async Task ファイルから答えるときはNetSuiteに要求を送らない_トークンも取らない()
        {
            var h = await StartAsync();
            var tokens = h.Mock.IssuedTokenCount;

            var tables = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);
            var customer = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customer");

            Assert.That(tables!.Select(e => e.Name), Is.EqualTo(AllTables));
            Assert.That(customer!.Name, Is.EqualTo("customer"));
            Assert.That(h.Recorder.Requests, Is.Empty);
            Assert.That(h.Mock.IssuedTokenCount, Is.EqualTo(tokens));
        }

        [Test]
        public async Task ファイルが無ければ表は0件で1件で求めるとメタ情報の更新を案内するエラー_要求は送らない()
        {
            var h = await StartAsync(metadataFile: ProjectFile);

            Assert.That(await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName), Is.Empty);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customer"));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.NotFound));
            Assert.That(ex.Message, Does.Contain("customer").And.Contain("Update the NetSuite metadata (Tools menu of the designer)"));
            //一覧の読み出しも同じ (SuiteQL を送らない)
            Assert.ThrowsAsync<NetSuiteException>(() => h.IO.GetListAsync(new SearchCondition("Customer"), 0));
            Assert.That(h.Recorder.Requests, Is.Empty);
        }

        [Test]
        public async Task ファイルに無いレコードは読み書きせず要求も送らない()
        {
            //customrecord_sample の無いモックで書いたファイル
            var data = JsonNode.Parse(File.ReadAllText(MockHarness.DataPath))!;
            data["records"]!.AsObject().Remove("customrecord_sample");
            var writer = await StartAsync(MockNetSuite.FromJson(data.ToJsonString()));
            await NetSuiteMetadataFile.UpdateAsync(Writer(writer), _directory);
            var h = await StartAsync(metadataFile: ProjectFile);

            Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customrecord_sample"));
            Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.QueryAsync(TestConnections.DataSourceName, "select \"customrecord_sample\".\"id\" as \"a\" from \"customrecord_sample\"", new()));
            var args = new Dictionary<string, object?> { [":p0"] = "x" };
            Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.InsertAsync(TestConnections.DataSourceName,
                "BEGIN INSERT INTO \"customrecord_sample\"(\"name\") VALUES (:p0) RETURNING \"id\" INTO :returning_id; END;", args));
            Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.ExecuteAsync(TestConnections.DataSourceName, "delete from \"customrecord_sample\" where \"id\"=:pid0",
                new Dictionary<string, object?> { [":pid0"] = "1" }));
            Assert.That(h.Recorder.Requests, Is.Empty);
        }

        [Test]
        public async Task 項目が増えたNetSuiteでファイルを更新し直すと同じアクセサが新しい中身を読む()
        {
            var writer = await StartAsync();
            await NetSuiteMetadataFile.UpdateAsync(Writer(writer), _directory);
            var h = await StartAsync(metadataFile: ProjectFile);
            var before = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customer");
            Assert.That(before!.Columns.Select(e => e.Name), Does.Not.Contain("custentity_rank"));

            //NetSuite に項目が増えた (別のモックで同じデータソース名)
            var data = JsonNode.Parse(File.ReadAllText(MockHarness.DataPath))!;
            data["records"]!["customer"]!["fields"]!.AsObject()["custentity_rank"] = JsonNode.Parse("""{ "type": "integer", "title": "Rank" }""");
            var grown = await StartAsync(MockNetSuite.FromJson(data.ToJsonString()));
            await NetSuiteMetadataFile.UpdateAsync(Writer(grown), _directory);

            Assert.That((await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customer"))!.Columns.Select(e => e.Name), Does.Contain("custentity_rank"));
            Assert.That((await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName))!.Single(e => e.Name == "customer").Columns.Select(e => e.Name),
                Does.Contain("custentity_rank"));
        }

        [Test]
        public async Task 中身が同じなら解釈し直さない()
        {
            var writer = await StartAsync();
            await NetSuiteMetadataFile.UpdateAsync(Writer(writer), _directory);
            var first = ProjectFile.Load(TestConnections.DataSourceName);
            //最終更新時刻だけ変えても同じもの
            File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow.AddMinutes(5));
            Assert.That(NetSuiteMetadataFile.FromProject(_directory).Load(TestConnections.DataSourceName), Is.SameAs(first));
        }

        [Test]
        public async Task 更新の途中で取得に失敗しても前のファイルが残り読める_一時ファイルも残らない()
        {
            var h = await StartAsync();
            await NetSuiteMetadataFile.UpdateAsync(Writer(h), _directory);
            var before = File.ReadAllText(FilePath);

            //subsidiary の定義の取得がサーバー側の失敗 (取り込みを飛ばす失敗ではない) になる
            var failing = new FailingHandler(h.Handler, request => request.RequestUri!.AbsolutePath.EndsWith("/metadata-catalog/subsidiary"));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => NetSuiteMetadataFile.UpdateAsync(Writer(h, failing), _directory));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));

            Assert.That(File.ReadAllText(FilePath), Is.EqualTo(before));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(FilePath)!), Is.EqualTo(new[] { FilePath }));
            Assert.That(Names(ProjectFile.Load(TestConnections.DataSourceName)), Is.EqualTo(AllTables));
        }

        [Test]
        public async Task 同じファイルへ並行して更新しても壊れず中身はどちらか一方()
        {
            var full = await StartAsync();
            var data = JsonNode.Parse(File.ReadAllText(MockHarness.DataPath))!;
            data["records"]!.AsObject().Remove("customrecord_sample");
            var fewer = await StartAsync(MockNetSuite.FromJson(data.ToJsonString()));

            await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => NetSuiteMetadataFile.UpdateAsync(Writer(i % 2 == 0 ? full : fewer), _directory))));

            Assert.That(Directory.GetFiles(Path.GetDirectoryName(FilePath)!), Is.EqualTo(new[] { FilePath }));
            var names = JsonNode.Parse(File.ReadAllText(FilePath))!["Records"]!.AsArray().Select(e => e!["Name"]!.GetValue<string>()).ToArray();
            Assert.That(names, Is.EqualTo(AllTables).Or.EqualTo(AllTables.Where(e => e != "customrecord_sample").ToArray()));
            Assert.That(Names(ProjectFile.Load(TestConnections.DataSourceName)), Is.EqualTo(names));
        }

        [Test]
        public async Task 壊れたJSONのファイルはNetSuiteException()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, "{ \"Records\": [ { \"Name\": ");
            var h = await StartAsync(metadataFile: ProjectFile);

            var ex = Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName));

            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.NotSupported));
            Assert.That(ex.Message, Does.Contain(Path.GetFileName(FilePath)));
            Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customer"));
        }

        [TestCase("""{"Records":[null]}""", "", TestName = "Recordsの要素がnull")]
        [TestCase("""{"Records":[{"Name":"customer","Fields":null}]}""", "customer()", TestName = "Fieldsがnull")]
        [TestCase("""{"Records":[{"Name":"customer","Fields":[null,{"Name":"companyName","Kind":"String","Required":true},{"Name":"item","Kind":"Sublist","Required":false,"Fields":[null,{"Name":"line","Kind":"Integer","Required":false}]}]}]}""",
            "customer(companyname,item)", TestName = "Fieldsの要素がnull")]
        public async Task JSONとして正しいが形の違うファイルのnullは無いものとして読む(string json, string expected)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
            var h = await StartAsync(metadataFile: ProjectFile);

            var tables = await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);

            Assert.That(string.Join(";", tables!.Select(t => $"{t.Name}({string.Join(",", t.Columns.Select(c => c.Name))})")), Is.EqualTo(expected));
            if (expected.Contains("item"))
            {
                //明細の行の項目の null も同じ
                var item = ProjectFile.Load(TestConnections.DataSourceName).Find("customer")!.FindByColumn("item")!;
                Assert.That(item.SublistFields.Select(e => e.RestName), Is.EqualTo(new[] { "line" }));
            }
            Assert.That(h.Recorder.Requests, Is.Empty);
        }

        [Test]
        public async Task フォルダの指定が空なら今のフォルダとして読み例外にならない()
        {
            //今のフォルダに何があっても当たらないデータソース名 (今のフォルダにはファイルを置かない)
            var name = "NS" + Guid.NewGuid().ToString("N");
            foreach (var (file, guide) in new[]
            {
                (NetSuiteMetadataFile.FromProject(""), "Update the NetSuite metadata (Tools menu of the designer)"),
                (NetSuiteMetadataFile.FromDeployedDesign(""), "deploy the design again"),
            })
            {
                var h = await StartAsync(edit: e => e.Name = name, metadataFile: file);

                Assert.That(await h.Accessor.GetCustomTableDefinitionsAsync(name), Is.Empty);
                var ex = Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(name, "customer"));
                Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.NotFound));
                Assert.That(ex.Message, Does.Contain("customer").And.Contain(guide));
                Assert.That(h.Recorder.Requests, Is.Empty);
            }
        }

        [Test]
        public async Task ファイル名に使えない文字を含むデータソース名でも保存して読める()
        {
            const string name = "NS:a/b*c?<d>|\"e";
            var writer = await StartAsync(edit: e => e.Name = name);
            await NetSuiteMetadataFile.UpdateAsync(Writer(writer), _directory);

            var files = Directory.GetFiles(Path.Combine(_directory, "ServerFiles"));
            Assert.That(files.Select(Path.GetFileName), Is.EqualTo(new[] { NetSuiteMetadataFile.GetFileName(name) }));
            var h = await StartAsync(edit: e => e.Name = name, metadataFile: ProjectFile);
            Assert.That((await h.Accessor.GetCustomTableDefinitionsAsync(name))!.Select(e => e.Name), Is.EqualTo(AllTables));
        }

        // ---------------------------------------------------------------- 配られたデザイン (App.zip) から読む (Web アプリ)

        //プロジェクトのフォルダを App.zip にして配置先に置く (置き換えて更新日時を決める)
        string Deploy(DateTime lastWriteTimeUtc)
        {
            var deploy = Path.Combine(_directory, "..", Path.GetFileName(_directory) + "-deploy");
            Directory.CreateDirectory(deploy);
            var temporary = Path.Combine(deploy, "App.zip.tmp");
            if (File.Exists(temporary)) File.Delete(temporary);
            ZipFile.CreateFromDirectory(_directory, temporary);
            var zip = Path.Combine(deploy, "App.zip");
            File.Move(temporary, zip, true);
            File.SetLastWriteTimeUtc(zip, lastWriteTimeUtc);
            return Path.GetFullPath(deploy);
        }

        async Task<MockHarness> StartDeployedAsync(string deploy)
        {
            var h = await StartAsync(metadataFile: NetSuiteMetadataFile.FromDeployedDesign(deploy));
            _deployDirectories.Add(deploy);
            return h;
        }

        readonly List<string> _deployDirectories = new();

        [TearDown]
        public void DeleteDeployDirectories()
        {
            foreach (var directory in _deployDirectories.Where(Directory.Exists)) Directory.Delete(directory, true);
            _deployDirectories.Clear();
        }

        [Test]
        public async Task 配られたデザインのファイルから読み実行時に定義を取りに行かない()
        {
            var writer = await StartAsync();
            await NetSuiteMetadataFile.UpdateAsync(Writer(writer), _directory);
            var h = await StartDeployedAsync(Deploy(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));

            var list = await h.IO.GetListAsync(new SearchCondition("Customer")
            {
                Condition = new FieldValueMatchCondition { SearchTargetVariable = "Id.Value", Comparison = MatchComparison.Equal, Value = new StringValue { Value = "1" } },
            }, 0);

            Assert.That(((TextFieldData)list.Items.Single().Fields["CompanyName"]).Value, Is.EqualTo(MockHarness.Rows("customer")[0].Text("companyName")));
            Assert.That(h.Recorder.Requests.Where(e => e.Path.Contains("/metadata-catalog")), Is.Empty);
        }

        [Test]
        public async Task 配られたデザインに無いレコードは配り直しを案内するエラーで要求を送らない()
        {
            File.WriteAllText(Path.Combine(_directory, "app.clprj"), "{}");
            var h = await StartDeployedAsync(Deploy(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));

            var ex = Assert.ThrowsAsync<NetSuiteException>(() => h.IO.GetListAsync(new SearchCondition("Customer"), 0));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.NotFound));
            Assert.That(ex.Message, Does.Contain("deploy the design again"));
            Assert.That(h.Recorder.Requests, Is.Empty);
        }

        [Test]
        public async Task AppZipが無ければ表は無い()
        {
            var h = await StartAsync(metadataFile: NetSuiteMetadataFile.FromDeployedDesign(_directory));
            Assert.That(await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName), Is.Empty);
            Assert.That(h.Recorder.Requests, Is.Empty);
        }

        [Test]
        public async Task AppZipが差し替わると読み直し_同じなら開き直さない()
        {
            var data = JsonNode.Parse(File.ReadAllText(MockHarness.DataPath))!;
            data["records"]!.AsObject().Remove("customrecord_sample");
            var fewer = await StartAsync(MockNetSuite.FromJson(data.ToJsonString()));
            await NetSuiteMetadataFile.UpdateAsync(Writer(fewer), _directory);
            var deploy = Deploy(new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc));
            _deployDirectories.Add(deploy);
            var file = NetSuiteMetadataFile.FromDeployedDesign(deploy);
            var first = file.Load(TestConnections.DataSourceName);
            Assert.That(Names(first), Does.Not.Contain("customrecord_sample"));

            //同じ App.zip のままなら同じもの (別に作った出所でも)
            Assert.That(NetSuiteMetadataFile.FromDeployedDesign(deploy).Load(TestConnections.DataSourceName), Is.SameAs(first));

            var full = await StartAsync();
            await NetSuiteMetadataFile.UpdateAsync(Writer(full), _directory);
            Deploy(new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc));
            Assert.That(Names(file.Load(TestConnections.DataSourceName)), Is.EqualTo(AllTables));
        }

        // ---------------------------------------------------------------- 読み書きは NetSuite へ

        [Test]
        public async Task ファイルから定義を答える設定でも一覧の読み出しと保存はNetSuiteへ送る()
        {
            var h = await StartAsync();

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

            //内側の DB には行かない・定義は取りに行かない
            Assert.That(h.Inner.DataSourceCalls, Is.Empty);
            Assert.That(h.Recorder.Requests.Where(e => e.Path.Contains("/metadata-catalog")), Is.Empty);
        }

        // ---------------------------------------------------------------- 明細

        static IEnumerable<(string, NetSuiteFieldKind, bool)> Describe(NetSuiteFieldDefinition field)
            => field.SublistFields.Select(e => (e.RestName, e.Kind, e.IsNullable));

        [Test]
        public async Task 明細の行の項目はファイルに保存して読み戻せる_明細でない項目はFieldsを書かない()
        {
            var h = await StartAsync();

            var records = JsonNode.Parse(File.ReadAllText(h.Project.MetadataPath()))!["Records"]!.AsArray();
            var salesOrder = records.Single(e => e!["Name"]!.GetValue<string>() == "salesorder")!["Fields"]!.AsArray().Select(e => e!.AsObject()).ToList();
            var item = salesOrder.Single(e => e["Name"]!.GetValue<string>() == "item");
            Assert.That(item["Kind"]!.GetValue<string>(), Is.EqualTo("Sublist"));
            Assert.That(item["Fields"]!.AsArray().Select(e => e!["Name"]!.GetValue<string>()), Does.Contain("line").And.Contain("expectedShipDate"));
            Assert.That(salesOrder.Where(e => e != item).All(e => !e.ContainsKey("Fields")), Is.True);

            var expected = await Writer(h).FetchRecordDefinitionAsync("salesOrder", "salesorder");
            //添付の仮想の列はファイルに書かず、読むときに取引へ足す
            expected.AddAttachmentsField();
            var actual = h.Project.MetadataFile.Load(TestConnections.DataSourceName).Find("salesorder")!;
            Assert.That(Describe(actual.FindByColumn("item")!), Is.EqualTo(Describe(expected.FindByColumn("item")!)));
            Assert.That(Serialize(new[] { actual.ToTableDefinition() }), Is.EqualTo(Serialize(new[] { expected.ToTableDefinition() })));
        }

        [Test]
        public async Task ファイルの定義で明細を読み書きできFieldsの無いファイルでは明細は列にならない()
        {
            var h = await MockHarness.StartAsync(null, null, MockSublistTest.AddModules);
            _harnesses.Add(h);

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

            //明細の項目 (Kind = Sublist) の無いファイル: 明細を知らないので列にならず、ほかの項目は今までどおり
            var root = JsonNode.Parse(File.ReadAllText(h.Project.MetadataPath()))!;
            foreach (var record in root["Records"]!.AsArray())
            {
                var fields = record!["Fields"]!.AsArray();
                foreach (var field in fields.Where(e => e!["Kind"]!.GetValue<string>() == "Sublist").ToList()) fields.Remove(field);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, root.ToJsonString());
            var old = await StartAsync(metadataFile: ProjectFile);
            var table = await old.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "salesorder");
            Assert.That(table!.Columns.Select(e => e.Name), Does.Contain("memo").And.Not.Contain("item"));
        }

        /// <summary>条件に合う要求にサーバー側の失敗 (500) を返す。</summary>
        sealed class FailingHandler : DelegatingHandler
        {
            readonly Func<HttpRequestMessage, bool> _match;

            public FailingHandler(HttpMessageHandler inner, Func<HttpRequestMessage, bool> match) : base(inner) => _match = match;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => _match(request) ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)) : base.SendAsync(request, cancellationToken);
        }
    }
}
