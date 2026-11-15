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
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>
    /// 明細 (サブリスト) と取引のレコードを、モック + 本物のコネクター + 本体の ModuleDataIO で確かめる。
    /// 受注 (salesorder) の明細 item を、本体の列モードの一覧 (Items → 行モジュール OrderLine) で見せる。期待値は mock-data.json の初期の行。
    /// </summary>
    public class MockSublistTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp() => _h = await StartAsync();

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        internal static Task<MockHarness> StartAsync()
            => MockHarness.StartAsync(null, e => e.Records.AddRange(new[] { "item", "salesorder" }), AddModules);

        //受注 (Order) と、明細の行モジュール (OrderLine: ルートの JsonField + ノードに結線したフィールド)
        internal static void AddModules(DesignData d)
        {
            var modules = (IEditableModuleDesign)d.Modules;
            var order = new ModuleDesign { Name = "Order", DataSourceName = TestConnections.DataSourceName, DbTable = "salesorder", CanCreate = true, CanUpdate = true, CanDelete = true };
            order.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            order.Fields.Add(new LinkFieldDesign
            {
                Name = "Customer",
                DbColumn = "entity",
                SearchCondition = new SearchCondition("Customer"),
                ValueVariable = "Id.Value",
                DisplayTextVariable = "CompanyName.Value",
            });
            order.Fields.Add(new TextFieldDesign { Name = "Customer.CompanyName", DbColumn = "companyname" });
            order.Fields.Add(new DateFieldDesign { Name = "TranDate", DbColumn = "trandate" });
            order.Fields.Add(new TextFieldDesign { Name = "Memo", DbColumn = "memo" });
            order.Fields.Add(new ListFieldDesign
            {
                Name = "Items",
                DbColumn = "item",
                SearchCondition = new SearchCondition("OrderLine") { LimitCount = null },
                CanCreate = true,
                CanUpdate = true,
                CanDelete = true,
            });
            modules.Add(order);

            var line = new ModuleDesign { Name = "OrderLine" };
            var json = new JsonFieldDesign { Name = "Json" };
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "item", NodeType = JsonNodeType.Text });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "item_refName", NodeType = JsonNodeType.Text });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "quantity", NodeType = JsonNodeType.Number });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "rate", NodeType = JsonNodeType.Number });
            json.JsonFieldSettings.JsonNodes.Add(new JsonFieldNodeSettings { Key = "expectedShipDate", NodeType = JsonNodeType.Date });
            line.Fields.Add(json);
            line.Fields.Add(new TextFieldDesign { Name = "Item", DbColumn = "item" });
            line.Fields.Add(new TextFieldDesign { Name = "ItemName", DbColumn = "item_refName" });
            line.Fields.Add(new NumberFieldDesign { Name = "Quantity", DbColumn = "quantity" });
            line.Fields.Add(new NumberFieldDesign { Name = "Rate", DbColumn = "rate" });
            line.Fields.Add(new DateFieldDesign { Name = "ShipDate", DbColumn = "expectedShipDate" });
            modules.Add(line);
        }

        static FieldValueMatchCondition Match(string variable, MatchComparison comparison, MultiTypeValue value)
            => new() { SearchTargetVariable = variable, Comparison = comparison, Value = value };

        async Task<ModuleData> ReadOrderAsync(string id)
            => (await _h.IO.GetListAsync(new SearchCondition("Order") { Condition = Match("Id.Value", MatchComparison.Equal, new StringValue { Value = id }) }, 0)).Items.Single();

        static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
            => data.Fields.TryGetValue(name, out var value) ? (T)value : null;

        static List<ModuleData> Lines(ModuleData order) => Field<ListFieldData>(order, "Items")!.Children;

        static ModuleData NewLine(string item, decimal quantity, decimal? rate = null, DateOnly? shipDate = null)
        {
            var row = new ModuleData { Name = "OrderLine" };
            row.Fields["Item"] = new TextFieldData { Value = item };
            row.Fields["Quantity"] = new NumberFieldData { Value = quantity };
            row.Fields["Rate"] = new NumberFieldData { Value = rate };
            row.Fields["ShipDate"] = new DateFieldData { Value = shipDate };
            return row;
        }

        Task<List<ModuleSubmitResult>> SubmitAsync(ModuleSubmitData data) => _h.IO.SubmitWithTransactionAsync(new() { data });

        //更新: 読んだ受注の一覧 (と、変える項目) を送る
        async Task<ModuleSubmitResult> UpdateAsync(string id, ListFieldData items, Action<ModuleData>? edit = null)
        {
            var update = new ModuleData { Name = "Order" };
            update.Fields["Id"] = new IdFieldData { Value = id };
            update.Fields["Items"] = items;
            edit?.Invoke(update);
            _h.Recorder.Clear();
            return (await SubmitAsync(new ModuleSubmitData { ModuleName = "Order", Id = id, Update = new() { update } })).Single();
        }

        /// <summary>モックの REST の読み出し (コネクターを通さない)。</summary>
        async Task<JsonObject> GetRestRecordAsync(string type, string id)
        {
            using var client = _h.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/services/rest/record/v1/{type}/{id}?expandSubResources=true");
            request.Headers.Authorization = new("Bearer", await MockServerTest.GetTokenAsync(client));
            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }

        async Task<List<JsonObject>> RestLinesAsync(string id)
            => (await GetRestRecordAsync("salesOrder", id))["item"]!["items"]!.AsArray().Select(e => e!.AsObject()).ToList();

        List<(HttpMethod Method, string Path, string Body, string Query)> Patches => _h.Recorder.Requests.Where(e => e.Method == HttpMethod.Patch).ToList();

        // ---------------------------------------------------------------- 定義

        [Test]
        public async Task 明細はREST名の小文字の列で行の項目はrefの先から読む()
        {
            var table = await _h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "salesorder");
            var column = table!.Columns.Single(e => e.Name == "item");
            Assert.That(column.NetTypeFullName, Is.EqualTo(typeof(string).FullName));
            Assert.That(column.RawDbTypeName, Is.EqualTo("CLOB"));

            var definition = await _h.Connection.GetRecordDefinitionAsync("salesorder");
            var sublist = definition.FindByColumn("item")!;
            Assert.That(sublist.Kind, Is.EqualTo(NetSuiteFieldKind.Sublist));
            Assert.That(sublist.SublistFields.ToDictionary(e => e.RestName, e => e.Kind), Is.EquivalentTo(new Dictionary<string, NetSuiteFieldKind>
            {
                ["line"] = NetSuiteFieldKind.Integer,
                ["item"] = NetSuiteFieldKind.Reference,
                ["quantity"] = NetSuiteFieldKind.Number,
                ["rate"] = NetSuiteFieldKind.Number,
                ["amount"] = NetSuiteFieldKind.Number,
                ["description"] = NetSuiteFieldKind.String,
                ["expectedShipDate"] = NetSuiteFieldKind.Date,
            }));
            Assert.That(sublist.FindSublistField("item")!.IsNullable, Is.False);
            //$ref の先 (…Collection と …Element) を同じ metadata-catalog から読んだ
            var metadata = _h.Recorder.Requests.Where(e => e.Path.Contains("/metadata-catalog/")).Select(e => e.Path.Split('/').Last()).ToList();
            Assert.That(metadata, Is.SupersetOf(new[] { "salesorder", "salesOrder-itemCollection", "salesOrder-itemElement" }));
        }

        // ---------------------------------------------------------------- 読み出し

        [Test]
        public async Task 詳細の明細は行の値と参照のidと表示名と日付を読みlineを温存する()
        {
            var order = await ReadOrderAsync("1");

            Assert.That(Field<LinkFieldData>(order, "Customer")!.Value, Is.EqualTo("1"));
            Assert.That(Field<LinkFieldData>(order, "Customer")!.DisplayText, Is.EqualTo("Alpha Trading"));
            Assert.That(Field<DateFieldData>(order, "TranDate")!.Value, Is.EqualTo(new DateOnly(2026, 4, 1)));
            var lines = Lines(order);
            Assert.That(lines, Has.Count.EqualTo(2));
            Assert.That(Field<TextFieldData>(lines[0], "Item")!.Value, Is.EqualTo("1"));
            Assert.That(Field<TextFieldData>(lines[0], "ItemName")!.Value, Is.EqualTo("WIDGET-S"));
            Assert.That(Field<NumberFieldData>(lines[0], "Quantity")!.Value, Is.EqualTo(10m));
            Assert.That(Field<NumberFieldData>(lines[0], "Rate")!.Value, Is.EqualTo(120m));
            Assert.That(Field<DateFieldData>(lines[0], "ShipDate")!.Value, Is.EqualTo(new DateOnly(2026, 4, 10)));
            Assert.That(Field<TextFieldData>(lines[1], "Item")!.Value, Is.EqualTo("4"));
            Assert.That(Field<DateFieldData>(lines[1], "ShipDate")?.Value, Is.Null);

            //行の JSON: キーは REST の項目名・参照は id の文字列と _refName・line はそのまま・links は入らない
            var json = JsonNode.Parse(Field<JsonFieldData>(lines[0], "Json")!.Value!)!.AsObject();
            Assert.That(json["line"]!.GetValue<long>(), Is.EqualTo(1));
            Assert.That(json["item"]!.GetValue<string>(), Is.EqualTo("1"));
            Assert.That(json["item_refName"]!.GetValue<string>(), Is.EqualTo("WIDGET-S"));
            Assert.That(json["amount"]!.GetValue<decimal>(), Is.EqualTo(1200m));
            Assert.That(json["expectedShipDate"]!.GetValue<string>(), Is.EqualTo("2026-04-10"));
            Assert.That(json.ContainsKey("links"), Is.False);
            Assert.That(JsonNode.Parse(Field<JsonFieldData>(lines[1], "Json")!.Value!)!["line"]!.GetValue<long>(), Is.EqualTo(2));

            //明細は SuiteQL ではなく、レコードの GET (expandSubResources) で読む
            var gets = _h.Recorder.Requests.Where(e => e.Method == HttpMethod.Get && e.Path.StartsWith("/services/rest/record/v1/salesorder/")).ToList();
            Assert.That(gets.Select(e => (e.Path, e.Query)), Is.EqualTo(new[] { ("/services/rest/record/v1/salesorder/1", "?expandSubResources=true") }));
            var suiteQl = _h.Recorder.Requests.Single(e => e.Path.EndsWith("/suiteql"));
            Assert.That(suiteQl.Body, Does.Not.Contain("salesorder.item "));
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 一覧で明細の列を読むと行ごとにGETが出て_読まない一覧では出ない()
        {
            //SelectFields を指定しない一覧 = 本体は全部の列を select する (明細の列も)
            var all = await _h.IO.GetListAsync(new SearchCondition("Order"), 0);
            Assert.That(all.Items, Has.Count.EqualTo(4));
            Assert.That(all.Items.Select(e => Lines(e).Count), Is.EqualTo(new[] { 1, 3, 1, 2 }));
            var gets = _h.Recorder.Requests.Where(e => e.Method == HttpMethod.Get && e.Path.StartsWith("/services/rest/record/v1/salesorder/")).ToList();
            Assert.That(gets.Select(e => e.Path.Split('/').Last()), Is.EquivalentTo(new[] { "1", "2", "3", "4" }));

            //一覧のレイアウトの列だけ (SelectFields) なら明細の列は select されず、GET も出ない
            _h.Recorder.Clear();
            var list = await _h.IO.GetListAsync(new SearchCondition("Order") { SelectFields = new() { "Id", "Memo" }, LimitCount = 10 }, 0);
            Assert.That(list.Items, Has.Count.EqualTo(4));
            Assert.That(_h.Recorder.Requests.Where(e => e.Method == HttpMethod.Get && e.Path.StartsWith("/services/rest/record/v1/salesorder/")), Is.Empty);
        }

        [Test]
        public async Task 明細の列だけを読むときもidを足して読み結果には返さない()
        {
            var rows = await _h.Accessor.QueryAsync(TestConnections.DataSourceName, "select \"salesorder\".\"item\" as \"items\" from \"salesorder\" where \"salesorder\".\"id\" = :p0",
                new Dictionary<string, ParamAndRawDbTypeName> { [":p0"] = new() { Value = "2" } });

            Assert.That(rows.Single().Keys, Is.EqualTo(new[] { "items" }));
            var array = JsonNode.Parse((string)rows.Single()["items"])!.AsArray();
            Assert.That(array.Select(e => e!["item"]!.GetValue<string>()), Is.EqualTo(new[] { "2" }));
            var suiteQl = _h.Recorder.Requests.Single(e => e.Path.EndsWith("/suiteql"));
            Assert.That(JsonNode.Parse(suiteQl.Body)!["q"]!.GetValue<string>(),
                Is.EqualTo("select salesorder.id as nsxid from (select * from transaction where type = 'SalesOrd') salesorder where salesorder.id = ? order by salesorder.id"));
        }

        [TestCase("select \"salesorder\".\"id\" as \"a\" from \"salesorder\" where \"salesorder\".\"item\" like :p0", TestName = "条件")]
        [TestCase("select \"salesorder\".\"id\" as \"a\" from \"salesorder\" order by \"salesorder\".\"item\"", TestName = "並び")]
        [TestCase("select max(\"salesorder\".\"item\") as \"a\" from \"salesorder\"", TestName = "集計")]
        [TestCase("select \"c\".\"id\" as \"a\", \"o\".\"item\" as \"b\" from \"customer\" \"c\" left join \"salesorder\" \"o\" on \"o\".\"entity\" = \"c\".\"id\"", TestName = "結合先の表")]
        public void 明細の列は主表の列として読むだけ(string sql)
        {
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.QueryAsync(TestConnections.DataSourceName, sql,
                new Dictionary<string, ParamAndRawDbTypeName> { [":p0"] = new() { Value = "%x%" } }));
            Assert.That(ex!.Message, Does.Contain("sublist column 'item'"));
        }

        // ---------------------------------------------------------------- 新規

        [Test]
        public async Task 新規は明細をPOSTに同梱し定義に無いキーとlineと空の値は送らない()
        {
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Order" };
            add.Fields["Id"] = tempId;
            add.Fields["Customer"] = new LinkFieldData { Value = "5" };
            add.Fields["TranDate"] = new DateFieldData { Value = new DateOnly(2026, 6, 1) };
            add.Fields["Memo"] = new TextFieldData { Value = "New order" };
            var items = new ListFieldData();
            items.Children.Add(NewLine("2", 3, 350, new DateOnly(2026, 6, 10)));
            var second = NewLine("4", 1.5m);
            //外から来た未知のキー・表示名・line は送らない
            second.Fields["Json"] = new JsonFieldData { Value = """{"line":7,"item_refName":"x","custcol_unknown":"y"}""" };
            items.Children.Add(second);
            add.Fields["Items"] = items;

            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "Order", Id = tempId.Value!, Add = new() { add } })).Single();

            Assert.That(result.ExceptionMessage, Is.Empty);
            //取引の id は取引のレコード同士で通し
            Assert.That(result.DestinationId, Is.EqualTo("5"));
            var post = _h.Recorder.Requests.Single(e => e.Method == HttpMethod.Post && e.Path == "/services/rest/record/v1/salesorder");
            Assert.That(JsonNode.Parse(post.Body)!["item"]!.ToJsonString(),
                Is.EqualTo("""{"items":[{"item":{"id":"2"},"quantity":3,"rate":350,"expectedShipDate":"2026-06-10"},{"item":{"id":"4"},"quantity":1.5}]}"""));

            var lines = await RestLinesAsync("5");
            Assert.That(lines.Select(e => e["line"]!.GetValue<long>()), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(lines[0]["item"]!["id"]!.GetValue<string>(), Is.EqualTo("2"));
            Assert.That(lines[0]["expectedShipDate"]!.GetValue<string>(), Is.EqualTo("2026-06-10"));
            Assert.That(lines[1]["quantity"]!.GetValue<decimal>(), Is.EqualTo(1.5m));

            //読み直すと同じ行
            var read = Lines(await ReadOrderAsync("5"));
            Assert.That(read.Select(e => Field<TextFieldData>(e, "ItemName")!.Value), Is.EqualTo(new[] { "WIDGET-L", "CABLE-1M" }));
        }

        [Test]
        public async Task 明細なしの新規は保存の結果のエラーになり送らない明細は項目ごと省く()
        {
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Order" };
            add.Fields["Id"] = tempId;
            add.Fields["Customer"] = new LinkFieldData { Value = "5" };
            add.Fields["TranDate"] = new DateFieldData { Value = new DateOnly(2026, 6, 1) };
            add.Fields["Items"] = new ListFieldData();

            var result = (await SubmitAsync(new ModuleSubmitData { ModuleName = "Order", Id = tempId.Value!, Add = new() { add } })).Single();

            Assert.That(result.ExceptionMessage, Does.Contain("at least one line item"));
            var post = _h.Recorder.Requests.Single(e => e.Method == HttpMethod.Post && e.Path == "/services/rest/record/v1/salesorder");
            Assert.That(JsonNode.Parse(post.Body)!.AsObject().ContainsKey("item"), Is.False);
            Assert.That((await _h.IO.GetListAsync(new SearchCondition("Order") { SelectFields = new() { "Id" } }, 0)).Items, Has.Count.EqualTo(4));
        }

        // ---------------------------------------------------------------- 更新

        [Test]
        public async Task 行の変更はlineと変わった項目だけをreplaceなしで送る()
        {
            var order = await ReadOrderAsync("1");
            var items = Field<ListFieldData>(order, "Items")!;
            Field<NumberFieldData>(items.Children[0], "Quantity")!.Value = 11;
            Field<DateFieldData>(items.Children[0], "ShipDate")!.Value = null;

            var result = await UpdateAsync("1", items);

            Assert.That(result.ExceptionMessage, Is.Empty);
            var patch = Patches.Single();
            Assert.That(patch.Query, Is.Empty);
            Assert.That(patch.Body, Is.EqualTo("""{"item":{"items":[{"line":1,"quantity":11,"expectedShipDate":null}]}}"""));
            var lines = await RestLinesAsync("1");
            Assert.That(lines.Select(e => e["line"]!.GetValue<long>()), Is.EqualTo(new long[] { 1, 2 }));
            Assert.That(lines[0]["quantity"]!.GetValue<decimal>(), Is.EqualTo(11m));
            Assert.That(lines[0].ContainsKey("expectedShipDate"), Is.False);
            Assert.That(lines[0]["rate"]!.GetValue<decimal>(), Is.EqualTo(120m));
            Assert.That(lines[1]["quantity"]!.GetValue<decimal>(), Is.EqualTo(5m));
        }

        [Test]
        public async Task 行の追加はlineの無い行だけを送る()
        {
            var order = await ReadOrderAsync("2");
            var items = Field<ListFieldData>(order, "Items")!;
            items.Children.Add(NewLine("3", 2, 900));

            var result = await UpdateAsync("2", items);

            Assert.That(result.ExceptionMessage, Is.Empty);
            var patch = Patches.Single();
            Assert.That(patch.Query, Is.Empty);
            Assert.That(patch.Body, Is.EqualTo("""{"item":{"items":[{"item":{"id":"3"},"quantity":2,"rate":900}]}}"""));
            var lines = await RestLinesAsync("2");
            Assert.That(lines.Select(e => (e["line"]!.GetValue<long>(), e["item"]!["id"]!.GetValue<string>())), Is.EqualTo(new[] { (1L, "2"), (2L, "3") }));
        }

        [Test]
        public async Task 行の削除はreplaceにして残す行を並べ_変わっていない行はlineだけ()
        {
            var order = await ReadOrderAsync("3");
            var items = Field<ListFieldData>(order, "Items")!;
            items.Children.RemoveAt(0);
            Field<NumberFieldData>(items.Children[1], "Quantity")!.Value = 6;
            items.Children.Add(NewLine("2", 1));

            var result = await UpdateAsync("3", items);

            Assert.That(result.ExceptionMessage, Is.Empty);
            var patch = Patches.Single();
            Assert.That(patch.Query, Is.EqualTo("?replace=item"));
            Assert.That(patch.Body, Is.EqualTo("""{"item":{"items":[{"line":2},{"line":3,"quantity":6},{"item":{"id":"2"},"quantity":1}]}}"""));
            //消した line の番号は使い回さない
            var lines = await RestLinesAsync("3");
            Assert.That(lines.Select(e => (e["line"]!.GetValue<long>(), e["item"]!["id"]!.GetValue<string>())), Is.EqualTo(new[] { (2L, "1"), (3L, "4"), (4L, "2") }));
            Assert.That(lines[1]["quantity"]!.GetValue<decimal>(), Is.EqualTo(6m));
        }

        [Test]
        public async Task 明細を全部消すとreplaceで空にしようとして必須の明細のエラーになる()
        {
            var order = await ReadOrderAsync("2");
            var items = Field<ListFieldData>(order, "Items")!;
            items.Children.Clear();

            var result = await UpdateAsync("2", items);

            Assert.That(result.ExceptionMessage, Does.Contain("at least one line item"));
            var patch = Patches.Single();
            Assert.That((patch.Query, patch.Body), Is.EqualTo(("?replace=item", """{"item":{"items":[]}}""")));
            Assert.That(await RestLinesAsync("2"), Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 明細に差が無ければ送らない_ほかの項目があればそれだけを1回のPATCHで送る()
        {
            var order = await ReadOrderAsync("1");
            var items = Field<ListFieldData>(order, "Items")!;

            var result = await UpdateAsync("1", items);
            Assert.That(result.ExceptionMessage, Is.Empty);
            Assert.That(Patches, Is.Empty);
            //今の明細を読んで比べた
            Assert.That(_h.Recorder.Requests.Count(e => e.Method == HttpMethod.Get && e.Path == "/services/rest/record/v1/salesorder/1"), Is.EqualTo(1));

            result = await UpdateAsync("1", items, e => e.Fields["Memo"] = new TextFieldData { Value = "Changed" });
            Assert.That(result.ExceptionMessage, Is.Empty);
            Assert.That(Patches.Single().Body, Is.EqualTo("""{"memo":"Changed"}"""));
        }

        [Test]
        public async Task 本体の項目と明細は1回のPATCHにまとめる()
        {
            var order = await ReadOrderAsync("4");
            var items = Field<ListFieldData>(order, "Items")!;
            Field<NumberFieldData>(items.Children[0], "Rate")!.Value = 13.5m;

            var result = await UpdateAsync("4", items, e => e.Fields["Memo"] = new TextFieldData { Value = "Spare parts (repriced)" });

            Assert.That(result.ExceptionMessage, Is.Empty);
            var patch = Patches.Single();
            Assert.That(patch.Path, Is.EqualTo("/services/rest/record/v1/salesorder/4"));
            Assert.That(patch.Body, Is.EqualTo("""{"memo":"Spare parts (repriced)","item":{"items":[{"line":1,"rate":13.5}]}}"""));
            var rest = await GetRestRecordAsync("salesOrder", "4");
            Assert.That(rest["memo"]!.GetValue<string>(), Is.EqualTo("Spare parts (repriced)"));
            Assert.That(rest["item"]!["items"]![0]!["rate"]!.GetValue<decimal>(), Is.EqualTo(13.5m));
        }

        [Test]
        public async Task 今の明細に無いlineの行と同じlineの2つ目は新しい行として送る()
        {
            var order = await ReadOrderAsync("2");
            var items = Field<ListFieldData>(order, "Items")!;
            var copy = NewLine("1", 1);
            copy.Fields["Json"] = new JsonFieldData { Value = """{"line":1}""" };
            items.Children.Add(copy);
            var gone = NewLine("4", 2);
            gone.Fields["Json"] = new JsonFieldData { Value = """{"line":99}""" };
            items.Children.Add(gone);

            var result = await UpdateAsync("2", items);

            Assert.That(result.ExceptionMessage, Is.Empty);
            Assert.That(Patches.Single().Body, Is.EqualTo("""{"item":{"items":[{"item":{"id":"1"},"quantity":1},{"item":{"id":"4"},"quantity":2}]}}"""));
            Assert.That(await RestLinesAsync("2"), Has.Count.EqualTo(3));
        }

        [Test]
        public async Task 時差の無い日時はUTCとしてZを付けて送り同じ瞬間なら変わっていない()
        {
            var sublist = new NetSuiteFieldDefinition
            {
                RestName = "time",
                Column = "time",
                Kind = NetSuiteFieldKind.Sublist,
                SublistFields =
                {
                    new NetSuiteFieldDefinition { RestName = "line", Column = "line", Kind = NetSuiteFieldKind.Integer },
                    new NetSuiteFieldDefinition { RestName = "at", Column = "at", Kind = NetSuiteFieldKind.DateTime },
                    new NetSuiteFieldDefinition { RestName = "who", Column = "who", Kind = NetSuiteFieldKind.Reference },
                },
            };
            var insert = NetSuiteSublistJson.CreateInsertItems(sublist, NetSuiteSublistJson.ParseColumnValue(sublist, """[{"at":"2026-11-14T13:45:00","who":"3","who_refName":"A"}]"""));
            Assert.That(insert.ToJsonString(), Is.EqualTo("""{"items":[{"at":"2026-11-14T13:45:00Z","who":{"id":"3"}}]}"""));

            using var current = System.Text.Json.JsonDocument.Parse("""{"time":{"items":[{"line":1,"at":"2026-11-14T13:45:00Z","who":{"id":"3","refName":"A"}}]}}""");
            var same = NetSuiteSublistJson.CreateUpdateItems(sublist, NetSuiteSublistJson.ParseColumnValue(sublist, """[{"line":1,"at":"2026-11-14T22:45:00+09:00","who":"3"}]"""), current.RootElement);
            Assert.That(same.Items, Is.Null);
            var changed = NetSuiteSublistJson.CreateUpdateItems(sublist, NetSuiteSublistJson.ParseColumnValue(sublist, """[{"line":"1","at":"2026-11-14T14:00:00","who":"4"}]"""), current.RootElement);
            //行の line は文字で来ても整数にそろえて送る
            Assert.That(changed.Items!.ToJsonString(), Is.EqualTo("""{"items":[{"line":1,"at":"2026-11-14T14:00:00Z","who":{"id":"4"}}]}"""));
            Assert.That(changed.Replace, Is.False);
        }

        // ---------------------------------------------------------------- 取引の一覧・検索・集計

        [Test]
        public async Task 取引の一覧と検索と並びとページングはtransactionの表で読む()
        {
            var condition = new SearchCondition("Order") { SelectFields = new() { "Id", "TranDate", "Memo", "Customer", "Customer.CompanyName" }, LimitCount = 2 };
            condition.Condition = Match("Customer.CompanyName.Value", MatchComparison.Like, new StringValue { Value = "a" });
            condition.SortConditions.Add(new SortCondition { Variable = "TranDate.Value", IsDescending = true });

            var first = await _h.IO.GetListAsync(condition, 0);
            var second = await _h.IO.GetListAsync(condition, 1);

            //Alpha Trading (1)・Beta Foods (2)・Gamma Systems (3) の受注は全部。日付の新しい順
            Assert.That(first.TotalCount, Is.EqualTo(4));
            Assert.That(first.Items.Select(e => Field<IdFieldData>(e, "Id")!.Value), Is.EqualTo(new[] { "4", "3" }));
            Assert.That(second.Items.Select(e => Field<IdFieldData>(e, "Id")!.Value), Is.EqualTo(new[] { "2", "1" }));
            Assert.That(first.Items.Select(e => Field<LinkFieldData>(e, "Customer")!.DisplayText), Is.EqualTo(new[] { "Alpha Trading", "Gamma Systems" }));

            var queries = _h.Recorder.Requests.Where(e => e.Path.EndsWith("/suiteql")).Select(e => JsonNode.Parse(e.Body)!["q"]!.GetValue<string>()).ToList();
            Assert.That(queries, Has.All.Contains("from (select * from transaction where type = 'SalesOrd') salesorder"));
            Assert.That(queries, Has.None.Contains("from salesorder"));
        }

        [Test]
        public async Task 取引の集計もtransactionの表で読む()
        {
            var condition = new AggregateCondition("Order")
            {
                Measures = { new AggregateMeasure { Function = AggregateFunction.Count }, new AggregateMeasure { Function = AggregateFunction.CountDistinct, Variable = "Customer.Value" } },
            };
            condition.Condition = Match("TranDate.Value", MatchComparison.GreaterThanOrEqual, new DateOnlyValue { Value = new DateOnly(2026, 4, 15) });

            var result = await _h.IO.AggregateAsync(condition);

            var row = result.Rows.Single();
            Assert.That(row.Value(0), Is.EqualTo(3m));
            Assert.That(row.Value(1), Is.EqualTo(3m));
        }

        [Test]
        public async Task 取引の削除と取引でないレコードは今までどおり()
        {
            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Order", Delete = new() { new ModuleDeleteInfo { ModuleName = "Order", Id = "2" } } });
            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            Assert.That(_h.Recorder.Requests.Any(e => e.Method == HttpMethod.Delete && e.Path == "/services/rest/record/v1/salesorder/2"), Is.True);
            Assert.That((await _h.IO.GetListAsync(new SearchCondition("Order") { SelectFields = new() { "Id" } }, 0)).Items, Has.Count.EqualTo(3));

            //customer は transaction を使わない
            _h.Recorder.Clear();
            var customers = await _h.IO.GetListAsync(new SearchCondition("Customer"), 0);
            Assert.That(customers.Items, Has.Count.EqualTo(MockHarness.Rows("customer").Count));
            var q = JsonNode.Parse(_h.Recorder.Requests.Single(e => e.Path.EndsWith("/suiteql")).Body)!["q"]!.GetValue<string>();
            Assert.That(q, Does.Contain("from customer").And.Not.Contain("transaction"));
        }

        // ---------------------------------------------------------------- モック単体 (コネクターを通さない HTTP)

        async Task<(HttpStatusCode Status, JsonObject? Body)> SendRestAsync(HttpMethod method, string path, string? body = null, MockHarness? h = null)
        {
            h ??= _h;
            using var client = h.CreateClient();
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new("Bearer", await MockServerTest.GetTokenAsync(client));
            request.Headers.TryAddWithoutValidation("Prefer", "transient");
            request.Headers.TryAddWithoutValidation("Accept", "application/schema+json");
            if (body != null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text)!.AsObject());
        }

        static string ErrorDetail(JsonObject? body) => body!["o:errorDetails"]![0]!["detail"]!.GetValue<string>();

        [Test]
        public async Task モックのメタデータは明細をrefで書き先の定義も読める()
        {
            var root = (await SendRestAsync(HttpMethod.Get, "/services/rest/record/v1/metadata-catalog/salesOrder")).Body!;
            Assert.That(root["properties"]!["item"]!["$ref"]!.GetValue<string>(), Does.EndWith("/services/rest/record/v1/metadata-catalog/salesOrder-itemCollection"));

            var (status, collection) = await SendRestAsync(HttpMethod.Get, "/services/rest/record/v1/metadata-catalog/salesOrder-itemCollection");
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(collection!["properties"]!["items"]!["items"]!["$ref"]!.GetValue<string>(), Does.EndWith("/salesOrder-itemElement"));
            var element = (await SendRestAsync(HttpMethod.Get, "/services/rest/record/v1/metadata-catalog/salesOrder-itemElement")).Body!;
            Assert.That(element["properties"]!.AsObject().Select(e => e.Key), Is.SupersetOf(new[] { "line", "item", "quantity", "expectedShipDate" }));
            Assert.That(element["required"]!.AsArray().Select(e => e!.GetValue<string>()), Does.Contain("item"));

            Assert.That((await SendRestAsync(HttpMethod.Get, "/services/rest/record/v1/metadata-catalog/salesOrder-noneCollection")).Status, Is.EqualTo(HttpStatusCode.NotFound));
        }

        [Test]
        public async Task モックは新規でlineを受けず必須の明細が空なら400()
        {
            var withLine = await SendRestAsync(HttpMethod.Post, "/services/rest/record/v1/salesOrder",
                """{"entity":{"id":"1"},"tranDate":"2026-06-01","item":{"items":[{"line":1,"item":{"id":"1"}}]}}""");
            Assert.That(withLine.Status, Is.EqualTo(HttpStatusCode.BadRequest));

            var empty = await SendRestAsync(HttpMethod.Post, "/services/rest/record/v1/salesOrder", """{"entity":{"id":"1"},"tranDate":"2026-06-01","item":{"items":[]}}""");
            Assert.That(empty.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(ErrorDetail(empty.Body), Does.Contain("at least one line item"));

            var noItem = await SendRestAsync(HttpMethod.Post, "/services/rest/record/v1/salesOrder", """{"entity":{"id":"1"},"tranDate":"2026-06-01","item":{"items":[{"quantity":1}]}}""");
            Assert.That(noItem.Status, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task モックのPATCHはlineで突き合わせ_無いlineは400で何も変えない()
        {
            var mismatch = await SendRestAsync(HttpMethod.Patch, "/services/rest/record/v1/salesOrder/1",
                """{"memo":"x","item":{"items":[{"line":1,"quantity":99},{"line":9,"quantity":1}]}}""");
            Assert.That(mismatch.Status, Is.EqualTo(HttpStatusCode.BadRequest));
            var record = await GetRestRecordAsync("salesOrder", "1");
            Assert.That(record["memo"]!.GetValue<string>(), Is.EqualTo("First order"));
            Assert.That(record["item"]!["items"]![0]!["quantity"]!.GetValue<decimal>(), Is.EqualTo(10m));

            Assert.That((await SendRestAsync(HttpMethod.Patch, "/services/rest/record/v1/salesOrder/1?replace=nosuch", "{}")).Status, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task モックのreplaceは送らなかった行を消しlineの番号は使い回さない()
        {
            var replaced = await SendRestAsync(HttpMethod.Patch, "/services/rest/record/v1/salesOrder/1?replace=item", """{"item":{"items":[{"line":2}]}}""");
            Assert.That(replaced.Status, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That((await RestLinesAsync("1")).Select(e => e["line"]!.GetValue<long>()), Is.EqualTo(new long[] { 2 }));

            //replace なしなら送らなかった行は残る。足した行は 3 (消した 1 は使わない)
            var added = await SendRestAsync(HttpMethod.Patch, "/services/rest/record/v1/salesOrder/1", """{"item":{"items":[{"item":{"id":"5"},"quantity":1}]}}""");
            Assert.That(added.Status, Is.EqualTo(HttpStatusCode.NoContent));
            var lines = await RestLinesAsync("1");
            Assert.That(lines.Select(e => e["line"]!.GetValue<long>()), Is.EqualTo(new long[] { 2, 3 }));
            //参照は id と refName。行の links も付く
            Assert.That(lines[1]["item"]!.ToJsonString(), Is.EqualTo("""{"id":"5","refName":"OLD-PART"}"""));
        }

        [Test]
        public async Task モックのGETはexpandSubResourcesのときだけ明細の行を返す()
        {
            var plain = (await SendRestAsync(HttpMethod.Get, "/services/rest/record/v1/salesOrder/2")).Body!;
            Assert.That(plain["item"]!.AsObject().ContainsKey("items"), Is.False);
            Assert.That(plain["item"]!["links"], Is.Not.Null);

            var expanded = await GetRestRecordAsync("salesOrder", "2");
            var line = expanded["item"]!["items"]!.AsArray().Single()!.AsObject();
            Assert.That(line["line"]!.GetValue<long>(), Is.EqualTo(1));
            Assert.That(line["item"]!["id"]!.GetValue<string>(), Is.EqualTo("2"));
        }

        [Test]
        public async Task モックの取引のidは取引の種類をまたいで重ならずtransactionの表はtypeを持つ()
        {
            var data = JsonNode.Parse(File.ReadAllText(MockHarness.DataPath))!;
            var invoice = JsonNode.Parse(data["records"]!["salesOrder"]!.ToJsonString())!.AsObject();
            invoice["transactionType"] = "CustInvc";
            invoice["rows"] = JsonNode.Parse("""[{"id":10,"entity":{"id":"2"},"tranDate":"2026-05-01","memo":"Invoice","item":{"items":[{"item":{"id":"1"},"quantity":1}]}}]""");
            data["records"]!.AsObject()["invoice"] = invoice;
            await using var h = await MockHarness.StartAsync(global::NetSuite.Mock.MockNetSuite.FromJson(data.ToJsonString()));

            var created = await SendRestAsync(HttpMethod.Post, "/services/rest/record/v1/salesOrder", """{"entity":{"id":"1"},"tranDate":"2026-06-01","item":{"items":[{"item":{"id":"1"}}]}}""", h);
            Assert.That(created.Status, Is.EqualTo(HttpStatusCode.NoContent));
            var createdInvoice = await SendRestAsync(HttpMethod.Post, "/services/rest/record/v1/invoice", """{"entity":{"id":"1"},"tranDate":"2026-06-01","item":{"items":[{"item":{"id":"1"}}]}}""", h);
            Assert.That(createdInvoice.Status, Is.EqualTo(HttpStatusCode.NoContent));

            var (status, body) = await SendRestAsync(HttpMethod.Post, "/services/rest/query/v1/suiteql?limit=100&offset=0", """{"q":"select id, type from transaction order by id"}""", h);
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            var rows = body!["items"]!.AsArray().Select(e => (e!["id"]!.ToString(), e["type"]!.GetValue<string>())).ToList();
            Assert.That(rows, Is.EqualTo(new[]
            {
                ("1", "SalesOrd"), ("2", "SalesOrd"), ("3", "SalesOrd"), ("4", "SalesOrd"), ("10", "CustInvc"), ("11", "SalesOrd"), ("12", "CustInvc"),
            }));

            //同じ id を別の取引の種類に書いた初期データは読めない
            invoice["rows"]![0]!["id"] = 1;
            Assert.Throws(Is.InstanceOf<Exception>(), () => global::NetSuite.Mock.MockNetSuite.FromJson(data.ToJsonString()));
        }
    }
}
