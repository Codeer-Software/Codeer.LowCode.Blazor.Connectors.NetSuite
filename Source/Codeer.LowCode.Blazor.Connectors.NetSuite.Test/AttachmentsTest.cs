using Codeer.LowCode.Blazor.Aggregation;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>
    /// 取引のファイル (仮想の列 files): 読むと保存検索で集めたファイルの並び (JSON)、書くと base64 の要素を File Cabinet に登録して添付し、
    /// deleted の要素を外す (保存検索で読んだ、そのレコードのファイルだけ)。ダウンロードは現在の利用者が読めるレコードのそのフィールドのファイルだけ。
    /// 相手はモック (NetSuite.Mock)。受注 1 には mock-data.json でファイル 500 と 501 が付いている。
    /// </summary>
    public class AttachmentsTest
    {
        const string Folder = "100";
        const string Search = "customsearch_clb_transaction_files";
        const string UpdateSql = "update \"salesorder\" set \"files\"=:p0 where \"id\"=:pid0";

        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp() => _h = await StartAsync();

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        static async Task<MockHarness> StartAsync(bool folder = true, bool search = true)
        {
            var h = await MockHarness.StartAsync(null, s =>
            {
                if (folder) s.AttachmentFolderId = Folder;
                if (!search) return;
                s.AttachmentSavedSearchId = Search;
                s.AttachmentSearchFileColumn = "file_internalid";
                s.AttachmentSearchFileNameColumn = "file_name";
                s.AttachmentSearchFileSizeColumn = "file_documentsize";
                s.AttachmentSearchFileTypeColumn = "file_filetype";
            }, AddModules);
            //利用者のモジュールは NetSuite の従業員 (AddModules)。既定は従業員 1 (添付のフィールドの権限で読めない側)
            h.SetCurrentUser("1");
            return h;
        }

        static void AddModules(DesignData d)
        {
            MockSublistTest.AddModules(d);
            var order = d.Modules.Find("Order")!;
            order.Fields.Add(new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files" });
            //添付の列に文字のフィールドを置いたもの (ダウンロードの対象にならない)
            order.Fields.Add(new TextFieldDesign { Name = "FilesText", DbColumn = "files" });

            var modules = (IEditableModuleDesign)d.Modules;
            //行の閲覧条件 (メモが Gadgets の受注だけ読める)
            var limited = Module("OrderLimited");
            limited.Fields.Add(new TextFieldDesign { Name = "Memo", DbColumn = "memo" });
            limited.DataReadCondition = new ModuleMatchCondition
            {
                ModuleName = "OrderLimited",
                Condition = MultiMatchCondition.And(new FieldValueMatchCondition { SearchTargetVariable = "Memo.Value", Comparison = MatchComparison.Equal, Value = new StringValue { Value = "Gadgets" } }),
            };
            modules.Add(limited);
            //添付のフィールドの読み取りの権限 (利用者 = NetSuite の従業員。従業員 3 だけが読める)
            var employee = new ModuleDesign { Name = "Employee", DataSourceName = TestConnections.DataSourceName, DbTable = "employee" };
            employee.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            employee.Fields.Add(new TextFieldDesign { Name = "Email", DbColumn = "email" });
            modules.Add(employee);
            d.AppSettings.CurrentUserModuleDesignName = "Employee";
            var denied = Module("OrderFilesDenied");
            var permission = new PermissionFieldDesign { Name = "Permission1", TargetFields = { "Files" } };
            permission.ReadCondition.ModuleName = "OrderFilesDenied";
            permission.ReadCondition.Condition = new FieldValueMatchCondition { SearchTargetVariable = "CurrentUser.Email.Value", Comparison = MatchComparison.Equal, Value = new StringValue { Value = "emily.brown@example.com" } };
            denied.Fields.Add(permission);
            modules.Add(denied);
            //NetSuite でないデータソースのモジュール
            var local = new ModuleDesign { Name = "Local", DataSourceName = "Other", DbTable = "salesorder" };
            local.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            local.Fields.Add(new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files" });
            modules.Add(local);
        }

        static ModuleDesign Module(string name)
        {
            var module = new ModuleDesign { Name = name, DataSourceName = TestConnections.DataSourceName, DbTable = "salesorder", CanCreate = true, CanUpdate = true, CanDelete = true };
            module.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            module.Fields.Add(new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files" });
            return module;
        }

        //受注の作成に要る明細 1 行
        const string Line = """[{"item":"2","quantity":1}]""";

        static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        static string Add(params string[] names)
            => new JsonArray(names.Select(e => (JsonNode)new JsonObject { ["name"] = e, ["base64"] = Base64("content of " + e) }).ToArray()).ToJsonString();

        static string Detach(params string[] ids)
            => new JsonArray(ids.Select(e => (JsonNode)new JsonObject { ["id"] = e, ["name"] = "x", ["deleted"] = true }).ToArray()).ToJsonString();

        Task<int> UpdateAsync(string id, string? value)
            => _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, UpdateSql, new() { [":p0"] = value, [":pid0"] = id });

        async Task<ModuleData?> ReadAsync(string module, string id)
            => (await _h.IO.GetListAsync(new SearchCondition(module)
            {
                Condition = new FieldValueMatchCondition { SearchTargetVariable = "Id.Value", Comparison = MatchComparison.Equal, Value = new StringValue { Value = id } },
            }, 0)).Items.SingleOrDefault();

        async Task<JsonArray?> FilesAsync(string id)
        {
            //値が無い (null) フィールドは読んだデータに入らない
            var value = (await ReadAsync("Order", id))!.Fields.TryGetValue("Files", out var data) ? ((NetSuiteAttachmentsFieldData)data).Value : null;
            return value == null ? null : JsonNode.Parse(value)!.AsArray();
        }

        List<(HttpMethod Method, string Path, string Body, string Query)> Requests(string pathPart)
            => _h.Recorder.Requests.Where(e => e.Path.Contains(pathPart, StringComparison.Ordinal)).ToList();

        List<(HttpMethod Method, string Path, string Body, string Query)> SavedSearches => Requests("/query/v1/savedsearch/");
        List<(HttpMethod Method, string Path, string Body, string Query)> Uploads => _h.Recorder.Requests.Where(e => e.Method == HttpMethod.Post && e.Path.EndsWith("/document/v1/file/")).ToList();
        List<(HttpMethod Method, string Path, string Body, string Query)> Downloads => Requests("/content");

        //パスに pathPart を含む要求の times 回目 (1 から) に、response を返す (モックへ渡さない)
        void FailOn(string pathPart, Func<HttpResponseMessage> response, int times = 1)
        {
            var count = 0;
            _h.Recorder.Intercept = async (request, next) =>
            {
                if (request.RequestUri!.AbsolutePath.Contains(pathPart, StringComparison.Ordinal) && Interlocked.Increment(ref count) == times) return response();
                return await next();
            };
        }

        static HttpResponseMessage Error(HttpStatusCode code, string detail = "Error.") => FakeNetSuite.ErrorDetails(code, (detail, "USER_ERROR", ""));

        List<string> Attached(string id) => _h.Mock.GetAttachedFileIds("salesOrder", id);

        // ---------------------------------------------------------------- 定義

        [Test]
        public async Task 取引には書ける仮想の列filesがあり本体には添付の型の名前の文字の列()
        {
            var definition = _h.Project.MetadataFile.Load(TestConnections.DataSourceName).Find("salesorder")!;
            var field = definition.FindByColumn("files")!;
            Assert.That(field.Kind, Is.EqualTo(NetSuiteFieldKind.Attachments));
            var column = definition.ToTableDefinition().Columns.Single(e => e.Name == "files");
            Assert.That((column.RawDbTypeName, column.NetTypeFullName), Is.EqualTo(("netsuite_files", typeof(string).FullName)));
            //取引でないレコードと明細の表には無い
            Assert.That(_h.Project.MetadataFile.Load(TestConnections.DataSourceName).Find("customer")!.FindByColumn("files"), Is.Null);
            //メタ情報のファイルには書かない (読むときに足す)
            Assert.That(File.ReadAllText(_h.Project.MetadataPath()), Does.Not.Contain("\"Attachments\""));
            await Task.CompletedTask;
        }

        // ---------------------------------------------------------------- 読み出し

        [Test]
        public async Task 読むと保存検索のファイルの並びで無ければnull()
        {
            _h.Recorder.Clear();
            var files = await FilesAsync("1");
            Assert.That(files!.Select(e => (e!["id"]!.GetValue<string>(), e["name"]!.GetValue<string>(), e["size"]!.GetValue<long>(), e["type"]!.GetValue<string>())),
                Is.EqualTo(new[] { ("500", "見積書.txt", (long)Encoding.UTF8.GetByteCount("Quotation for sales order 1 (sample)."), "PLAINTEXT"), ("501", "spec.csv", 30L, "CSV") }));
            Assert.That(SavedSearches.Single().Path, Is.EqualTo($"/services/rest/query/v1/savedsearch/{Search}/result"));
            Assert.That(await FilesAsync("2"), Is.Null);
        }

        [Test]
        public async Task 一覧の1ページで保存検索は1回で行ごとには読まない()
        {
            _h.Mock.Attach("salesOrder", "3", "500");
            _h.Recorder.Clear();
            var items = (await _h.IO.GetListAsync(new SearchCondition("Order"), 0)).Items;
            //並びは Id の降順 (4, 3, 2, 1)
            Assert.That(items.Select(e => e.Fields.ContainsKey("Files")), Is.EqualTo(new[] { false, true, false, true }));
            Assert.That(SavedSearches, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 保存検索のページをまたいで最後まで読む()
        {
            //ほかの受注のファイルを先に並ばせる (結果は親の ID の順)
            var other = _h.Mock.AddFile("other.txt", Folder, Encoding.UTF8.GetBytes("o"));
            _h.Mock.Attach("salesOrder", "2", other);
            _h.Connection.SavedSearchPageSize = 1;
            _h.Recorder.Clear();

            var files = await FilesAsync("2");

            Assert.That(files!.Select(e => e!["id"]!.GetValue<string>()), Is.EqualTo(new[] { other }));
            Assert.That(SavedSearches.Select(e => e.Query), Is.EqualTo(new[] { "?limit=1&offset=0", "?limit=1&offset=1", "?limit=1&offset=2" }));
        }

        [Test]
        public async Task ほかのレコードのファイルは混ざらない()
        {
            var other = _h.Mock.AddFile("other.txt", Folder, Encoding.UTF8.GetBytes("o"));
            _h.Mock.Attach("salesOrder", "2", other);
            Assert.That((await FilesAsync("1"))!.Select(e => e!["id"]!.GetValue<string>()), Is.EqualTo(new[] { "500", "501" }));
            Assert.That((await FilesAsync("2"))!.Select(e => e!["id"]!.GetValue<string>()), Is.EqualTo(new[] { other }));
        }

        [Test]
        public async Task 列を読まなければ保存検索を実行しない()
        {
            _h.Recorder.Clear();
            var rows = await _h.Accessor.QueryAsync(TestConnections.DataSourceName, "select \"salesorder\".\"memo\" as \"a\" from \"salesorder\" order by \"salesorder\".\"id\"", new());
            Assert.That(rows, Has.Count.EqualTo(4));
            Assert.That(SavedSearches, Is.Empty);
        }

        [Test]
        public async Task 保存検索が無ければ読むと空で足すだけ()
        {
            await _h.DisposeAsync();
            _h = await StartAsync(search: false);
            _h.Recorder.Clear();
            Assert.That(await FilesAsync("1"), Is.Null);
            Assert.That(SavedSearches, Is.Empty);

            Assert.That(await UpdateAsync("2", Add("a.txt")), Is.EqualTo(1));
            Assert.That(Attached("2"), Has.Count.EqualTo(1));
            //外す指定は、保存検索で今のファイルを確かめられないので外さない
            Assert.That(await UpdateAsync("1", Detach("500")), Is.EqualTo(1));
            Assert.That(Attached("1"), Is.EqualTo(new[] { "500", "501" }));
            Assert.That(Requests("!detach"), Is.Empty);
        }

        [Test]
        public void 保存検索の失敗はファイル0個にしない()
        {
            FailOn("/query/v1/savedsearch/", () => Error(HttpStatusCode.BadRequest, "Invalid search."));
            var e = FindException(Assert.CatchAsync(() => FilesAsync("1")));
            Assert.That(e?.Message, Does.Contain($"saved search '{Search}'").And.Contain("Invalid search."));
        }

        [Test]
        public async Task 親の列が保存検索に無ければエラー()
        {
            await _h.DisposeAsync();
            _h = await MockHarness.StartAsync(null, s =>
            {
                s.AttachmentSavedSearchId = Search;
                s.AttachmentSearchRecordColumn = "no_such_column";
                s.AttachmentSearchFileColumn = "file_internalid";
                s.AttachmentSearchFileNameColumn = "file_name";
            }, AddModules);
            _h.SetCurrentUser("1");
            var e = FindException(Assert.CatchAsync(() => FilesAsync("1")));
            Assert.That(e?.Message, Does.Contain("'no_such_column'"));
        }

        [Test]
        public void 保存検索が続きありで行が無ければエラー()
        {
            _h.Recorder.Intercept = (request, next) => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("/savedsearch/")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"items":[],"hasMore":true,"count":0,"offset":0,"totalResults":5}""", Encoding.UTF8, "application/json") }
                : next().Result);
            var e = FindException(Assert.CatchAsync(() => FilesAsync("1")));
            Assert.That(e?.Message, Does.Contain("the next page could not be read"));
        }

        static NetSuiteException? FindException(Exception? e)
        {
            for (; e != null; e = e.InnerException)
            {
                if (e is NetSuiteException n) return n;
            }
            return null;
        }

        // ---------------------------------------------------------------- 足す

        [Test]
        public async Task 更新で足したファイルは設定のフォルダに登録して足した順に添付する()
        {
            _h.Recorder.Clear();
            Assert.That(await UpdateAsync("2", Add("a.txt", "b.txt")), Is.EqualTo(1));

            var ids = Attached("2");
            Assert.That(ids.Select(e => _h.Mock.FindFile(e)!.Name), Is.EqualTo(new[] { "a.txt", "b.txt" }));
            Assert.That(ids.Select(e => _h.Mock.FindFile(e)!.FolderId), Is.All.EqualTo(Folder));
            Assert.That(Encoding.UTF8.GetString(_h.Mock.FindFile(ids[0])!.Content), Is.EqualTo("content of a.txt"));
            //登録 → 添付 を 1 つずつ。レコードの本文は送らない (変わった項目が無い)
            Assert.That(_h.Recorder.Requests.Where(e => e.Path.Contains("/document/") || e.Path.Contains('!')).Select(e => e.Path), Is.EqualTo(new[]
            {
                "/services/rest/document/v1/file/", $"/services/rest/record/v1/salesorder/2/!attach/file/{ids[0]}",
                "/services/rest/document/v1/file/", $"/services/rest/record/v1/salesorder/2/!attach/file/{ids[1]}",
            }));
            Assert.That(_h.Recorder.Requests.Any(e => e.Method == HttpMethod.Patch), Is.False);
            //読み直すと付いている
            Assert.That((await FilesAsync("2"))!.Select(e => e!["name"]!.GetValue<string>()), Is.EqualTo(new[] { "a.txt", "b.txt" }));
        }

        [Test]
        public async Task 項目とファイルを一緒に変えると本文を送ってから添付する()
        {
            var result = await _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, "update \"salesorder\" set \"memo\"=:p0,\"files\"=:p1 where \"id\"=:pid0",
                new() { [":p0"] = "Changed", [":p1"] = Add("a.txt"), [":pid0"] = "2" });
            Assert.That(result, Is.EqualTo(1));
            var writes = _h.Recorder.Requests.Where(e => e.Method == HttpMethod.Patch || e.Path.Contains("/document/") || e.Path.Contains("!attach")).Select(e => e.Method.Method).ToList();
            Assert.That(writes, Is.EqualTo(new[] { "PATCH", "POST", "POST" }));
            Assert.That(_h.Recorder.Requests.Single(e => e.Method == HttpMethod.Patch).Body, Is.EqualTo("""{"memo":"Changed"}"""));
            Assert.That(Attached("2"), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 作成ではレコードを作ってから添付し外す指定は見ない()
        {
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Order" };
            add.Fields["Id"] = tempId;
            add.Fields["Customer"] = new LinkFieldData { Value = "5" };
            add.Fields["TranDate"] = new DateFieldData { Value = new DateOnly(2026, 6, 1) };
            add.Fields["Memo"] = new TextFieldData { Value = "With files" };
            var value = JsonNode.Parse(Add("a.txt"))!.AsArray();
            value.Add(new JsonObject { ["id"] = "500", ["name"] = "x", ["deleted"] = true });
            var items = new ListFieldData();
            var line = new ModuleData { Name = "OrderLine" };
            line.Fields["Item"] = new TextFieldData { Value = "2" };
            line.Fields["Quantity"] = new NumberFieldData { Value = 1 };
            items.Children.Add(line);
            add.Fields["Items"] = items;
            add.Fields["Files"] = new NetSuiteAttachmentsFieldData { Value = value.ToJsonString() };
            _h.Recorder.Clear();

            var result = (await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Order", Id = tempId.Value!, Add = new() { add } } })).Single();

            Assert.That(result.ExceptionMessage, Is.Empty);
            var id = result.DestinationId;
            var post = _h.Recorder.Requests.Single(e => e.Method == HttpMethod.Post && e.Path == "/services/rest/record/v1/salesorder");
            Assert.That(JsonNode.Parse(post.Body)!.AsObject().ContainsKey("files"), Is.False);
            Assert.That(Attached(id).Select(e => _h.Mock.FindFile(e)!.Name), Is.EqualTo(new[] { "a.txt" }));
            Assert.That(Requests("!detach"), Is.Empty);
            Assert.That(Attached("1"), Is.EqualTo(new[] { "500", "501" }));
        }

        [Test]
        public async Task フォルダが無ければ足すときに何も書く前にエラー()
        {
            await _h.DisposeAsync();
            _h = await StartAsync(folder: false);
            _h.Recorder.Clear();
            var e = Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, "update \"salesorder\" set \"memo\"=:p0,\"files\"=:p1 where \"id\"=:pid0",
                new() { [":p0"] = "Changed", [":p1"] = Add("a.txt"), [":pid0"] = "2" }));
            Assert.That(e!.Message, Does.Contain(nameof(NetSuiteConnectionSettings.AttachmentFolderId)));
            Assert.That(_h.Recorder.Requests.Any(r => r.Method == HttpMethod.Patch || r.Path.Contains("/document/")), Is.False);
            //外すだけならフォルダは要らない
            Assert.That(await UpdateAsync("1", Detach("500")), Is.EqualTo(1));
            Assert.That(Attached("1"), Is.EqualTo(new[] { "501" }));
        }

        [TestCase("""[{"name":"a.txt","base64":""}]""", TestName = "空のファイル")]
        [TestCase("""[{"name":"a.txt","base64":"not base64!"}]""", TestName = "base64でない")]
        [TestCase("""[{"base64":"YQ=="}]""", TestName = "名前が無い")]
        [TestCase("""[{"id":"x1","name":"a","deleted":true}]""", TestName = "外すIDが数でない")]
        [TestCase("""{"name":"a"}""", TestName = "配列でない")]
        [TestCase("""[{"name":"a\nb.txt","base64":"YQ=="}]""", TestName = "名前に改行")]
        [TestCase("""[{"name":"a\u0001.txt","base64":"YQ=="}]""", TestName = "名前に制御文字")]
        public void 形の違う値は何も書かずにエラー(string value)
        {
            _h.Recorder.Clear();
            var e = Assert.ThrowsAsync<NetSuiteException>(() => UpdateAsync("2", value));
            Assert.That(e!.Kind, Is.EqualTo(NetSuiteErrorKind.InvalidRequest));
            Assert.That(_h.Recorder.Requests.Any(r => r.Method != HttpMethod.Get && !r.Path.EndsWith("/suiteql")), Is.False);
        }

        [Test]
        public async Task 名前に引用符や円記号があっても登録でき本当の名前はattributesで送る()
        {
            _h.Recorder.Clear();
            Assert.That(await UpdateAsync("2", Add("a\"b.txt", "c\\d.txt")), Is.EqualTo(1));
            Assert.That(Attached("2").Select(e => _h.Mock.FindFile(e)!.Name), Is.EqualTo(new[] { "a\"b.txt", "c\\d.txt" }));
            //multipart の data のファイル名は置き換えた安全な名前
            Assert.That(Uploads.Select(e => e.Body), Has.Some.Contain("filename=a_b.txt").Or.Some.Contain("filename=\"a_b.txt\""));
        }

        [Test]
        public void 登録の途中のNetSuiteException以外の例外もレコードのIDつきの結果不明のエラーにする()
        {
            _h.Recorder.Intercept = (request, next) => request.RequestUri!.AbsolutePath.EndsWith("/document/v1/file/")
                ? throw new InvalidOperationException("broken")
                : next();
            var e = Assert.ThrowsAsync<NetSuiteException>(() => UpdateAsync("2", Add("a.txt", "b.txt")));
            Assert.That((e!.Kind, e.RecordId), Is.EqualTo((NetSuiteErrorKind.WriteResultUnknown, "2")));
            Assert.That(e.Message, Does.Contain("(internal ID: 2) was saved").And.Contain("whether the file 'a.txt' was registered").And.Contain("broken")
                .And.Contain("1 more file(s) to add were not sent."));
        }

        [Test]
        public void 一括の追加の1件目で添付だけ失敗しても作成済みのIDと送っていない数が分かる()
        {
            FailOn("!attach", () => Error(HttpStatusCode.BadRequest, "Attach refused."));
            var sql = "BEGIN\n  INSERT INTO \"salesorder\" (\"entity\",\"trandate\",\"item\",\"files\") VALUES (:p0_0,:p0_1,:p0_3,:p0_2);\n" +
                "  INSERT INTO \"salesorder\" (\"entity\",\"trandate\",\"item\") VALUES (:p1_0,:p1_1,:p1_3);\nEND;";
            var date = new DateOnly(2026, 6, 1);
            var e = Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, sql,
                new() { [":p0_0"] = "5", [":p0_1"] = date, [":p0_2"] = Add("a.txt"), [":p1_0"] = "5", [":p1_1"] = date, [":p0_3"] = Line, [":p1_3"] = Line }));
            Assert.That((e!.CreatedIds, e.RecordId), Is.EqualTo((new[] { "5" }, "5")));
            Assert.That(e.Message, Does.StartWith("No records of 2 were created before the error. Record 1 was created (internal ID: 5) but its files were not fully attached")
                .And.Contain("The remaining 1 records were not sent."));
        }

        [Test]
        public async Task 登録で10MBを超えると断られたら分かるエラー()
        {
            FailOn("/document/v1/file/", () => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge));
            var e = Assert.ThrowsAsync<NetSuiteException>(() => UpdateAsync("2", Add("big.bin")));
            Assert.That(e!.Message, Does.Contain("10 MB").And.Contain("big.bin"));
            Assert.That(e.RecordId, Is.EqualTo("2"));
            Assert.That(Attached("2"), Is.Empty);
            await Task.CompletedTask;
        }

        // ---------------------------------------------------------------- 外す

        [Test]
        public async Task 外すのは保存検索で読んだそのレコードのファイルだけでFileCabinetには残る()
        {
            var other = _h.Mock.AddFile("other.txt", Folder, Encoding.UTF8.GetBytes("o"));
            _h.Mock.Attach("salesOrder", "2", other);
            _h.Recorder.Clear();

            //500 は受注 1 のファイル・other は受注 2 のファイル・999 はどこにも無い
            Assert.That(await UpdateAsync("1", Detach("500", other, "999")), Is.EqualTo(1));

            Assert.That(Attached("1"), Is.EqualTo(new[] { "501" }));
            Assert.That(Attached("2"), Is.EqualTo(new[] { other }));
            Assert.That(Requests("!detach").Select(e => e.Path), Is.EqualTo(new[] { "/services/rest/record/v1/salesorder/1/!detach/file/500" }));
            Assert.That(_h.Mock.FindFile("500"), Is.Not.Null);
            Assert.That(_h.Recorder.Requests.Any(e => e.Method == HttpMethod.Delete), Is.False);
        }

        [Test]
        public async Task 足すのと外すのを一緒にすると足してから外す()
        {
            var value = JsonNode.Parse(Add("a.txt"))!.AsArray();
            value.Add(new JsonObject { ["id"] = "501", ["name"] = "spec.csv", ["deleted"] = true });
            _h.Recorder.Clear();
            Assert.That(await UpdateAsync("1", value.ToJsonString()), Is.EqualTo(1));
            var order = _h.Recorder.Requests.Where(e => e.Path.Contains('!')).Select(e => e.Path.Contains("!attach") ? "attach" : "detach");
            Assert.That(order, Is.EqualTo(new[] { "attach", "detach" }));
            Assert.That(Attached("1").Select(e => _h.Mock.FindFile(e)!.Name), Is.EqualTo(new[] { "見積書.txt", "a.txt" }));
        }

        [Test]
        public async Task 無いレコードには何もしない()
        {
            _h.Recorder.Clear();
            Assert.That(await UpdateAsync("99", Add("a.txt")), Is.EqualTo(0));
            Assert.That(Uploads, Is.Empty);
        }

        [Test]
        public async Task 親のレコードを消してもFileCabinetのファイルは消さない()
        {
            var count = _h.Mock.FileCount;
            Assert.That(await _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, "delete from \"salesorder\" where \"id\"=:pid0", new() { [":pid0"] = "1" }), Is.EqualTo(1));
            Assert.That(_h.Mock.FileCount, Is.EqualTo(count));
            Assert.That(_h.Recorder.Requests.Any(e => e.Path.Contains("/document/") || e.Path.Contains('!')), Is.False);
        }

        // ---------------------------------------------------------------- 途中の失敗 (やり直さない・どこまで済んだかを返す)

        [Test]
        public void 二つ目の登録が失敗したらレコードのIDと添付済みのファイルが分かる()
        {
            FailOn("/document/v1/file/", () => Error(HttpStatusCode.BadRequest, "Upload refused."), times: 2);
            var e = Assert.ThrowsAsync<NetSuiteException>(() => UpdateAsync("2", Add("a.txt", "b.txt", "c.txt")));
            var first = Attached("2").Single();
            Assert.That(e!.Message, Does.Contain("(internal ID: 2) was saved").And.Contain("'b.txt' could not be registered").And.Contain("Upload refused.")
                .And.Contain($"Files attached before the error: {first}.").And.Contain("1 more file(s) to add were not sent."));
            Assert.That((e.RecordId, e.UnattachedFileIds), Is.EqualTo(("2", Array.Empty<string>())));
            //やり直さない
            Assert.That(Uploads, Has.Count.EqualTo(2));
        }

        [Test]
        public void 添付が失敗したら登録したファイルのIDが分かりファイルは消さない()
        {
            FailOn("!attach", () => Error(HttpStatusCode.BadRequest, "Attach refused."));
            var count = _h.Mock.FileCount;
            var e = Assert.ThrowsAsync<NetSuiteException>(() => UpdateAsync("2", Add("a.txt")));
            Assert.That(_h.Mock.FileCount, Is.EqualTo(count + 1));
            Assert.That(e!.RecordId, Is.EqualTo("2"));
            Assert.That(e.UnattachedFileIds, Has.Count.EqualTo(1));
            Assert.That(_h.Mock.FindFile(e.UnattachedFileIds[0])!.Name, Is.EqualTo("a.txt"));
            Assert.That(e.Message, Does.Contain($"(file ID: {e.UnattachedFileIds[0]}) but could not be attached"));
            Assert.That(Attached("2"), Is.Empty);
            Assert.That(Requests("!attach"), Has.Count.EqualTo(1));
        }

        [Test]
        public void 添付の応答を受け取れなければ結果が分からないと言いやり直さない()
        {
            var count = 0;
            _h.Recorder.Intercept = async (request, next) =>
            {
                var response = await next();
                if (request.RequestUri!.AbsolutePath.Contains("!attach") && Interlocked.Increment(ref count) == 1) throw new HttpRequestException("connection reset");
                return response;
            };
            var e = Assert.ThrowsAsync<NetSuiteException>(() => UpdateAsync("2", Add("a.txt")));
            Assert.That(e!.Message, Does.Contain("whether it was attached is unknown"));
            Assert.That(Requests("!attach"), Has.Count.EqualTo(1));
            //実際には付いている (モックは処理した)
            Assert.That(Attached("2"), Is.EqualTo(e.UnattachedFileIds));
        }

        [Test]
        public void 外すのが失敗したら外していない数が分かる()
        {
            FailOn("!detach", () => Error(HttpStatusCode.BadRequest, "Detach refused."));
            var e = Assert.ThrowsAsync<NetSuiteException>(() => UpdateAsync("1", Detach("500", "501")));
            Assert.That(e!.Message, Does.Contain("the file 500 could not be detached").And.Contain("1 file(s) to detach were not detached."));
            Assert.That(e.RecordId, Is.EqualTo("1"));
            Assert.That(Attached("1"), Is.EqualTo(new[] { "500", "501" }));
        }

        [Test]
        public void 作成の後の添付が失敗したらレコードのIDが分かる()
        {
            FailOn("!attach", () => Error(HttpStatusCode.BadRequest, "Attach refused."));
            var e = Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.InsertAsync(TestConnections.DataSourceName,
                "BEGIN INSERT INTO \"salesorder\"(\"entity\",\"trandate\",\"item\",\"files\") VALUES (:p0,:p1,:p3,:p2) RETURNING \"id\" INTO :returning_id; END;",
                new() { [":p0"] = "5", [":p1"] = new DateOnly(2026, 6, 1), [":p2"] = Add("a.txt"), [":p3"] = Line }));
            Assert.That(e!.RecordId, Is.EqualTo("5"));
            Assert.That(e.Message, Does.Contain("(internal ID: 5) was saved"));
        }

        [Test]
        public void 一括の追加で添付が失敗したら作成済みのIDにそのレコードも入る()
        {
            FailOn("!attach", () => Error(HttpStatusCode.BadRequest, "Attach refused."), times: 2);
            var sql = "BEGIN\n  INSERT INTO \"salesorder\" (\"entity\",\"trandate\",\"item\",\"files\") VALUES (:p0_0,:p0_1,:p0_3,:p0_2);\n" +
                "  INSERT INTO \"salesorder\" (\"entity\",\"trandate\",\"item\",\"files\") VALUES (:p1_0,:p1_1,:p1_3,:p1_2);\n  INSERT INTO \"salesorder\" (\"entity\",\"trandate\",\"item\") VALUES (:p2_0,:p2_1,:p2_3);\nEND;";
            var date = new DateOnly(2026, 6, 1);
            var e = Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, sql,
                new() { [":p0_0"] = "5", [":p0_1"] = date, [":p0_2"] = Add("a.txt"), [":p1_0"] = "5", [":p1_1"] = date, [":p1_2"] = Add("b.txt"), [":p2_0"] = "5", [":p2_1"] = date,
                    [":p0_3"] = Line, [":p1_3"] = Line, [":p2_3"] = Line }));
            Assert.That(e!.CreatedIds, Is.EqualTo(new[] { "5", "6" }));
            Assert.That(e.Message, Does.Contain("Record 2 was created (internal ID: 6) but its files were not fully attached").And.Contain("The remaining 1 records were not sent."));
        }

        // ---------------------------------------------------------------- 条件・並び・集計には使えない

        [TestCase("select \"salesorder\".\"id\" as \"a\" from \"salesorder\" where \"salesorder\".\"files\" like :p0", TestName = "条件")]
        [TestCase("select \"salesorder\".\"id\" as \"a\" from \"salesorder\" order by \"salesorder\".\"files\"", TestName = "並び")]
        [TestCase("select max(\"salesorder\".\"files\") as \"a\" from \"salesorder\"", TestName = "集計")]
        [TestCase("select \"c\".\"id\" as \"a\", \"o\".\"files\" as \"b\" from \"customer\" \"c\" left join \"salesorder\" \"o\" on \"o\".\"entity\" = \"c\".\"id\"", TestName = "結合先の表")]
        public void ファイルの列は主表の列として読むだけ(string sql)
        {
            _h.Recorder.Clear();
            var e = Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.QueryAsync(TestConnections.DataSourceName, sql,
                new Dictionary<string, ParamAndRawDbTypeName> { [":p0"] = new() { Value = "%x%" } }));
            Assert.That(e!.Message, Does.Contain("attachments column 'files'"));
            Assert.That(SavedSearches, Is.Empty);
        }

        [Test]
        public void モジュールの集計でも使えない()
        {
            var condition = new AggregateCondition { ModuleName = "Order" };
            condition.Measures.Add(new AggregateMeasure { Function = AggregateFunction.Max, Variable = "FilesText.Value" });
            var e = FindException(Assert.CatchAsync(() => _h.IO.AggregateAsync(condition)));
            Assert.That(e?.Message, Does.Contain("'files'"));
        }

        // ---------------------------------------------------------------- ダウンロード

        Task<NetSuiteAttachmentFile?> DownloadAsync(string? module = "Order", string? id = "1", string? field = "Files", string? fileId = "500")
            => _h.Accessor.GetAttachmentAsync(_h.IO, _h.Design, module, id, field, fileId);

        [Test]
        public async Task ダウンロードは保存検索の名前と中身を返す()
        {
            var file = await DownloadAsync();
            Assert.That(file!.FileName, Is.EqualTo("見積書.txt"));
            Assert.That(Encoding.UTF8.GetString(file.Content), Is.EqualTo("Quotation for sales order 1 (sample)."));
            Assert.That(Downloads.Single().Path, Is.EqualTo("/services/rest/document/v1/file/500/content"));
        }

        [Test]
        public async Task 足したファイルを読み直せばダウンロードできる()
        {
            await UpdateAsync("2", JsonNode.Parse("""[{"name":"b.bin","base64":"AAEC/w=="}]""")!.ToJsonString());
            var id = Attached("2").Single();
            var file = await DownloadAsync(id: "2", fileId: id);
            Assert.That((file!.FileName, file.Content), Is.EqualTo(("b.bin", new byte[] { 0, 1, 2, 255 })));
        }

        [TestCase("Order", "1", "Files", "501", true, Description = "そのレコードのファイル")]
        [TestCase("Order", "2", "Files", "500", false, Description = "ほかのレコードのファイル")]
        [TestCase("Order", "1", "Files", "999", false, Description = "どこにも無いファイル")]
        [TestCase("Order", "1", "FilesText", "500", false, Description = "添付のフィールドでない")]
        [TestCase("Order", "1", "Memo", "500", false, Description = "添付の列でない")]
        [TestCase("Order", "1", "Nothing", "500", false, Description = "フィールドが無い")]
        [TestCase("Nothing", "1", "Files", "500", false, Description = "モジュールが無い")]
        [TestCase("Order", "99", "Files", "500", false, Description = "レコードが無い")]
        [TestCase("Order", "", "Files", "500", false, Description = "Id が空")]
        [TestCase("Order", "1", "Files", "", false, Description = "ファイルの ID が空")]
        [TestCase("Order", "1", "Files", "500\n", false, Description = "ファイルの ID に制御文字")]
        [TestCase("Order", "1", "Files", "../500", false, Description = "ファイルの ID が数でない")]
        [TestCase(null, null, null, null, false, Description = "何も無い")]
        public async Task 対象が違えば見つからず中身を読みに行かない(string? module, string? id, string? field, string? fileId, bool found)
        {
            var file = await DownloadAsync(module, id, field, fileId);
            Assert.That(file != null, Is.EqualTo(found));
            Assert.That(Downloads, Has.Count.EqualTo(found ? 1 : 0));
        }

        [Test]
        public async Task NetSuiteでないモジュールは見つからない()
        {
            Assert.That(await DownloadAsync(module: "Local"), Is.Null);
            Assert.That(_h.Inner.Calls, Is.Empty);
            Assert.That(Downloads, Is.Empty);
        }

        [Test]
        public async Task 行の閲覧条件で読めないレコードは見つからない()
        {
            //受注 1 は読めない・受注 3 (Gadgets) は読める
            Assert.That(await DownloadAsync(module: "OrderLimited"), Is.Null);
            Assert.That(Downloads, Is.Empty);
            _h.Mock.Attach("salesOrder", "3", "501");
            Assert.That((await DownloadAsync(module: "OrderLimited", id: "3", fileId: "501"))!.FileName, Is.EqualTo("spec.csv"));
        }

        [Test]
        public async Task フィールドの権限で読めなければ見つからない()
        {
            //読めない利用者 (従業員 1)
            Assert.That(_h.CurrentUserId, Is.EqualTo("1"));
            Assert.That(await DownloadAsync(module: "OrderFilesDenied"), Is.Null);
            Assert.That(Downloads, Is.Empty);
            //対照: 同じモジュール・同じ仕掛けで、権限が通る利用者 (従業員 3) なら取れる (上の null が権限で落ちたことの裏付け)
            _h.SetCurrentUser("3");
            Assert.That((await DownloadAsync(module: "OrderFilesDenied"))!.FileName, Is.EqualTo("見積書.txt"));
            Assert.That(Downloads, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 保存検索が無ければダウンロードできない()
        {
            await _h.DisposeAsync();
            _h = await StartAsync(search: false);
            Assert.That(await DownloadAsync(), Is.Null);
            Assert.That(Downloads, Is.Empty);
        }

        [Test]
        public async Task 中身を読めなければ空のファイルにせず見つからない()
        {
            //10 MB を超えるファイル (413)・権限の無いフォルダのファイル (403)
            var big = _h.Mock.AddFile("big.bin", Folder, Array.Empty<byte>(), "MISCBINARY", 11L * 1024 * 1024);
            _h.Mock.AddFolder("200", "Restricted", restricted: true);
            var hidden = _h.Mock.AddFile("hidden.txt", "200", Encoding.UTF8.GetBytes("h"));
            _h.Mock.Attach("salesOrder", "1", big);
            _h.Mock.Attach("salesOrder", "1", hidden);

            Assert.That(await DownloadAsync(fileId: big), Is.Null);
            Assert.That(await DownloadAsync(fileId: hidden), Is.Null);
            Assert.That(Downloads, Has.Count.EqualTo(2));
        }
    }
}
