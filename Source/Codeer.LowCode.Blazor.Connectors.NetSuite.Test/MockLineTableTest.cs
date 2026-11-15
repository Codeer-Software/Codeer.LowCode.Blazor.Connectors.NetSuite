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
using Codeer.LowCode.Blazor.Utils;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>
    /// 読み取り専用の明細の表 (salesorder_item = 受注の明細 item の行を 1 行 = 1 レコードで読む) を、モック + 本物のコネクター + 本体の ModuleDataIO で確かめる。
    /// モジュール LineRow (明細の表)・Item (品目)・Order / OrderLine (受注と明細の一覧。MockSublistTest と同じ)。期待値は mock-data.json の初期の行。
    /// </summary>
    public class MockLineTableTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp() => _h = await StartAsync();

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        static Task<MockHarness> StartAsync(global::NetSuite.Mock.MockNetSuite? mock = null, string? metadataDirectory = null)
            => MockHarness.StartAsync(mock, e => e.Records.AddRange(new[] { "item", "salesorder" }), AddModules, metadataDirectory);

        static void AddModules(DesignData d)
        {
            MockSublistTest.AddModules(d);
            var modules = (IEditableModuleDesign)d.Modules;
            var ds = TestConnections.DataSourceName;

            var item = new ModuleDesign { Name = "Item", DataSourceName = ds, DbTable = "item" };
            item.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            item.Fields.Add(new TextFieldDesign { Name = "ItemId", DbColumn = "itemid" });
            item.Fields.Add(new TextFieldDesign { Name = "DisplayName", DbColumn = "displayname" });
            modules.Add(item);

            //明細の表のモジュール。Id は uniquekey、親の受注と品目はリンク
            var line = new ModuleDesign { Name = "LineRow", DataSourceName = ds, DbTable = "salesorder_item", CanCreate = true, CanUpdate = true, CanDelete = true };
            line.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "uniquekey" });
            line.Fields.Add(new LinkFieldDesign
            {
                Name = "Order",
                DbColumn = "transaction",
                SearchCondition = new SearchCondition("Order"),
                ValueVariable = "Id.Value",
                DisplayTextVariable = "Memo.Value",
            });
            line.Fields.Add(new TextFieldDesign { Name = "Order.Memo", DbColumn = "memo" });
            line.Fields.Add(new DateFieldDesign { Name = "Order.TranDate", DbColumn = "trandate" });
            line.Fields.Add(new LinkFieldDesign
            {
                Name = "Item",
                DbColumn = "item",
                SearchCondition = new SearchCondition("Item"),
                ValueVariable = "Id.Value",
                DisplayTextVariable = "ItemId.Value",
            });
            line.Fields.Add(new TextFieldDesign { Name = "Item.ItemId", DbColumn = "itemid" });
            line.Fields.Add(new NumberFieldDesign { Name = "Seq", DbColumn = "linesequencenumber" });
            line.Fields.Add(new NumberFieldDesign { Name = "Quantity", DbColumn = "quantity" });
            line.Fields.Add(new NumberFieldDesign { Name = "Rate", DbColumn = "rate" });
            line.Fields.Add(new NumberFieldDesign { Name = "Amount", DbColumn = "amount" });
            line.Fields.Add(new TextFieldDesign { Name = "Description", DbColumn = "description" });
            line.Fields.Add(new DateFieldDesign { Name = "ShipDate", DbColumn = "expectedshipdate" });
            modules.Add(line);
        }

        static FieldValueMatchCondition Match(string variable, MatchComparison comparison, MultiTypeValue value)
            => new() { SearchTargetVariable = variable, Comparison = comparison, Value = value };

        static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
            => data.Fields.TryGetValue(name, out var value) ? (T)value : null;

        static string Id(ModuleData row) => Field<IdFieldData>(row, "Id")!.Value!;

        //(受注の id, 品目の id, 数量) の並び
        static List<(string Order, string Item, decimal? Quantity)> Lines(IEnumerable<ModuleData> rows)
            => rows.Select(e => (Field<LinkFieldData>(e, "Order")!.Value!, Field<LinkFieldData>(e, "Item")!.Value!, Field<NumberFieldData>(e, "Quantity")!.Value)).ToList();

        //mock-data.json の受注の明細 (受注の id, 品目の id, 数量)。読み込んだ順 = uniquekey の順
        static List<(string Order, string Item, decimal? Quantity)> InitialLines()
            => MockHarness.Rows("salesOrder").SelectMany(order => order["item"]!["items"]!.AsArray()
                .Select(e => (order.Id(), e!["item"]!["id"]!.GetValue<string>(), e["quantity"]?.GetValue<decimal>()))).ToList();

        //uniquekey の順 (読み込んだ順) で読む。並びを指定しない一覧は本体の既定 (Id の降順)
        async Task<List<ModuleData>> ListAsync(MatchConditionBase? condition = null)
        {
            var search = new SearchCondition("LineRow");
            if (condition != null) search.Condition = condition;
            search.SortConditions.Add(new SortCondition { Variable = "Id.Value" });
            return (await _h.IO.GetListAsync(search, 0)).Items;
        }

        List<string> SuiteQlQueries => _h.Recorder.Requests.Where(e => e.Path.EndsWith("/suiteql")).Select(e => JsonNode.Parse(e.Body)!["q"]!.GetValue<string>()).ToList();

        // ---------------------------------------------------------------- 定義

        [Test]
        public async Task 明細の表は取引のレコードの後ろに並び列はuniquekeyとtransactionとlinesequencenumberと行の項目()
        {
            var tables = await _h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);
            var names = tables!.Select(e => e.Name).ToList();
            Assert.That(names.IndexOf("salesorder_item"), Is.EqualTo(names.IndexOf("salesorder") + 1));
            //取引でないレコードには明細の表は無い
            Assert.That(names.Where(e => e.Contains('_') && e != "customrecord_sample"), Is.EqualTo(new[] { "salesorder_item" }));

            var table = tables!.Single(e => e.Name == "salesorder_item");
            Assert.That(table.Columns.Select(e => (e.Name, e.NetTypeFullName, e.RawDbTypeName)), Is.EqualTo(new[]
            {
                ("uniquekey", typeof(string).FullName, "VARCHAR2"),
                ("transaction", typeof(string).FullName, "REFERENCE"),
                ("linesequencenumber", typeof(long).FullName, "INTEGER"),
                ("item", typeof(string).FullName, "REFERENCE"),
                ("quantity", typeof(decimal).FullName, "NUMBER"),
                ("rate", typeof(decimal).FullName, "NUMBER"),
                ("amount", typeof(decimal).FullName, "NUMBER"),
                ("description", typeof(string).FullName, "VARCHAR2"),
                ("expectedshipdate", typeof(DateOnly).FullName, "DATE"),
            }));
            //1 件で求めても同じ
            var single = await _h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "salesorder_item");
            Assert.That(single!.Columns.Select(e => e.Name), Is.EqualTo(table.Columns.Select(e => e.Name)));
        }

        [TestCase("salesorder_none", TestName = "明細の列でない名前")]
        [TestCase("item_item", TestName = "取引でないレコード")]
        [TestCase("customer_item", TestName = "Recordsに無いレコード")]
        public void 明細の表にならない名前はRecordsに無いレコードと同じ例外(string name)
        {
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, name));
            Assert.That(ex!.Message, Does.Contain($"'{name}' is not listed in Records"));
            ex = Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.QueryAsync(TestConnections.DataSourceName, $"select \"{name}\".\"item\" as \"a\" from \"{name}\"", new()));
            Assert.That(ex!.Message, Does.Contain("not listed in Records"));
        }

        [Test]
        public async Task 明細のある取引でないレコードには明細の表が出ない()
        {
            var data = JsonNode.Parse(File.ReadAllText(MockHarness.DataPath))!;
            data["records"]!.AsObject()["customrecord_lines"] = JsonNode.Parse("""
                { "fields": { "name": "string" }, "sublists": { "entry": { "fields": { "memo": "string" } } },
                  "rows": [ { "id": 1, "name": "A", "entry": { "items": [ { "memo": "x" } ] } } ] }
                """);
            await using var h = await MockHarness.StartAsync(global::NetSuite.Mock.MockNetSuite.FromJson(data.ToJsonString()), e => e.Records.Add("customrecord_lines"));

            var tables = (await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName))!;
            //明細そのものは親の列として読める
            Assert.That(tables.Single(e => e.Name == "customrecord_lines").Columns.Select(e => e.Name), Does.Contain("entry"));
            Assert.That(tables.Select(e => e.Name), Has.None.EqualTo("customrecord_lines_entry"));
            Assert.ThrowsAsync<NetSuiteException>(() => h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customrecord_lines_entry"));
        }

        // ---------------------------------------------------------------- 読み出し

        [Test]
        public async Task 一覧は明細の行を1行ずつ読みリンク先の受注と品目の項目も読む()
        {
            var result = await _h.IO.GetListAsync(new SearchCondition("LineRow"), 0);

            //並びを指定しなければ本体の既定どおり Id (uniquekey) の降順
            var expected = InitialLines();
            Assert.That(Lines(result.Items), Is.EqualTo(Enumerable.Reverse(expected)));
            Assert.That(result.Items.Select(Id), Is.EqualTo(Enumerable.Range(1, expected.Count).Reverse().Select(e => e.ToString())));

            var first = result.Items[^1];
            Assert.That(Field<LinkFieldData>(first, "Order")!.DisplayText, Is.EqualTo("First order"));
            Assert.That(Field<DateFieldData>(first, "Order.TranDate")!.Value, Is.EqualTo(new DateOnly(2026, 4, 1)));
            Assert.That(Field<LinkFieldData>(first, "Item")!.DisplayText, Is.EqualTo("WIDGET-S"));
            Assert.That(Field<NumberFieldData>(first, "Seq")!.Value, Is.EqualTo(1m));
            Assert.That(Field<NumberFieldData>(first, "Rate")!.Value, Is.EqualTo(120m));
            Assert.That(Field<NumberFieldData>(first, "Amount")!.Value, Is.EqualTo(1200m));
            Assert.That(Field<TextFieldData>(first, "Description")!.Value, Is.EqualTo("Widget Small"));
            Assert.That(Field<DateFieldData>(first, "ShipDate")!.Value, Is.EqualTo(new DateOnly(2026, 4, 10)));
            Assert.That(Field<NumberFieldData>(result.Items[^2], "Seq")!.Value, Is.EqualTo(2m));
            Assert.That(Field<DateFieldData>(result.Items[^2], "ShipDate")?.Value, Is.Null);

            //明細の表は transactionline の派生表、親の受注は transaction の派生表。明細の列の読み出し (レコードの GET) は出ない
            var q = SuiteQlQueries.Single();
            Assert.That(q, Does.Contain("from (select tl.* from transactionline tl inner join transaction t on t.id = tl.transaction where t.type = 'SalesOrd' and tl.mainline = 'F' and tl.taxline = 'F') salesorder_item"));
            Assert.That(q, Does.Contain("join (select * from transaction where type = 'SalesOrd') "));
            Assert.That(q, Does.EndWith("order by salesorder_item.uniquekey desc"));
            Assert.That(_h.Recorder.Requests.Where(e => e.Method == HttpMethod.Get && e.Path.StartsWith("/services/rest/record/v1/salesorder/")), Is.Empty);
        }

        [Test]
        public async Task 詳細はuniquekeyで1件読む()
        {
            var condition = new SearchCondition("LineRow") { Condition = Match("Id.Value", MatchComparison.Equal, new StringValue { Value = "4" }) };

            var row = (await _h.IO.GetListAsync(condition, 0)).Items.Single();

            Assert.That(Lines(new[] { row }), Is.EqualTo(new[] { InitialLines()[3] }));
            Assert.That(Field<LinkFieldData>(row, "Item")!.DisplayText, Is.EqualTo("GADGET"));
        }

        [Test]
        public async Task 品目と数量と親の項目をリンク越しに検索できる()
        {
            async Task<List<(string Order, string Item, decimal? Quantity)>> SearchAsync(MatchConditionBase condition)
                => Lines(await ListAsync(condition));
            var all = InitialLines();

            Assert.That(await SearchAsync(Match("Item.ItemId.Value", MatchComparison.Equal, new StringValue { Value = "CABLE-1M" })), Is.EqualTo(all.Where(e => e.Item == "4")));
            Assert.That(await SearchAsync(Match("Item.Value", MatchComparison.Equal, new StringValue { Value = "1" })), Is.EqualTo(all.Where(e => e.Item == "1")));
            Assert.That(await SearchAsync(Match("Quantity.Value", MatchComparison.GreaterThanOrEqual, new DecimalValue { Value = 5 })), Is.EqualTo(all.Where(e => 5 <= e.Quantity)));
            Assert.That(await SearchAsync(Match("Order.Memo.Value", MatchComparison.Like, new StringValue { Value = "order" })), Is.EqualTo(all.Where(e => e.Order is "1" or "2")));
            Assert.That(await SearchAsync(Match("Order.TranDate.Value", MatchComparison.GreaterThanOrEqual, new DateOnlyValue { Value = new DateOnly(2026, 5, 1) })),
                Is.EqualTo(all.Where(e => e.Order is "3" or "4")));
        }

        [Test]
        public async Task 並びとページングは並びの最後をuniquekeyにしてページをまたいでも重ならない()
        {
            var condition = new SearchCondition("LineRow") { LimitCount = 3 };
            condition.SortConditions.Add(new SortCondition { Variable = "Item.ItemId.Value" });

            var pages = new List<ModuleData>();
            Paging<ModuleData> page;
            for (var i = 0; (page = await _h.IO.GetListAsync(condition, i)).Items.Count != 0; i++) pages.AddRange(page.Items);

            //品目名の順。同じ品目の行は uniquekey の順
            var expected = InitialLines().Select((e, i) => (Line: e, Key: i + 1))
                .OrderBy(e => MockHarness.Rows("item").Single(r => r.Id() == e.Line.Item).Text("itemId"), StringComparer.Ordinal).ThenBy(e => e.Key).ToList();
            Assert.That(page.TotalCount, Is.EqualTo(expected.Count));
            Assert.That(pages.Select(Id), Is.EqualTo(expected.Select(e => e.Key.ToString())));
            Assert.That(SuiteQlQueries.Where(e => e.Contains(" order by ")), Has.All.EndWith(", salesorder_item.uniquekey"));
        }

        [Test]
        public async Task 品目ごとの数量の合計と件数を集計できる()
        {
            var result = await _h.IO.AggregateAsync(new AggregateCondition("LineRow")
            {
                Groups = { new ValueGroup { Variable = "Item.Value" } },
                Measures = { new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Quantity.Value" }, new AggregateMeasure { Function = AggregateFunction.Count } },
            });

            //品目 (表示名は品目の itemId)・数量の合計・行数
            var names = new Dictionary<string, string> { ["1"] = "WIDGET-S", ["2"] = "WIDGET-L", ["3"] = "GADGET", ["4"] = "CABLE-1M" };
            var expected = InitialLines().GroupBy(e => e.Item).Select(e => ((object?)e.Key, names[e.Key], (object?)e.Sum(l => l.Quantity), (object?)(decimal)e.Count()));
            Assert.That(result.Rows.Select(e => (e.Key(0), e.Keys[0].DisplayText, e.Value(0), e.Value(1))), Is.EquivalentTo(expected));

            //親の項目で絞った集計
            var condition = new AggregateCondition("LineRow") { Measures = { new AggregateMeasure { Function = AggregateFunction.Sum, Variable = "Amount.Value" } } };
            condition.Condition = Match("Order.Memo.Value", MatchComparison.Equal, new StringValue { Value = "Gadgets" });
            Assert.That((await _h.IO.AggregateAsync(condition)).Rows.Single().Value(0), Is.EqualTo(980.5m + 440m + 45m));
        }

        [Test]
        public async Task 親の明細をRESTで書き換えると明細の表に反映され残った行のuniquekeyは変わらない()
        {
            //受注 3 の行 1 を消し、行 3 の数量を変え、行を 1 つ足す
            var updated = _h.Mock.UpdateRecord("salesOrder", "3", """{"item":{"items":[{"line":2},{"line":3,"quantity":9},{"item":{"id":"2"},"quantity":6}]}}""", "item");
            Assert.That(updated.Status, Is.EqualTo(204));
            //受注を足す (明細 1 行)
            _h.Mock.AddRecord("salesOrder", """{"entity":{"id":"5"},"tranDate":"2026-06-01","item":{"items":[{"item":{"id":"3"},"quantity":2}]}}""");

            var rows = await ListAsync(Match("Order.TranDate.Value", MatchComparison.GreaterThanOrEqual, new DateOnlyValue { Value = new DateOnly(2026, 5, 1) }));

            Assert.That(Lines(rows), Is.EqualTo(new[] { ("3", "1", (decimal?)4m), ("3", "4", 9m), ("4", "4", 20m), ("3", "2", 6m), ("5", "3", 2m) }));
            //消した行 (uniquekey 4) は無くなり、残った行は同じ uniquekey。足した行は新しい番号
            Assert.That(rows.Select(Id), Is.EqualTo(new[] { "5", "6", "7", "8", "9" }));
            Assert.That(rows.Select(e => Field<NumberFieldData>(e, "Seq")!.Value), Is.EqualTo(new decimal?[] { 2, 3, 1, 4, 1 }));
        }

        [Test]
        public async Task 画面で受注の明細を保存した後に明細の表を読むと反映されている()
        {
            var order = (await _h.IO.GetListAsync(new SearchCondition("Order") { Condition = Match("Id.Value", MatchComparison.Equal, new StringValue { Value = "2" }) }, 0)).Items.Single();
            var items = Field<ListFieldData>(order, "Items")!;
            Field<NumberFieldData>(items.Children[0], "Quantity")!.Value = 7;
            var row = new ModuleData { Name = "OrderLine" };
            row.Fields["Item"] = new TextFieldData { Value = "4" };
            row.Fields["Quantity"] = new NumberFieldData { Value = 3 };
            items.Children.Add(row);
            var update = new ModuleData { Name = "Order" };
            update.Fields["Id"] = new IdFieldData { Value = "2" };
            update.Fields["Items"] = items;
            var result = (await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Order", Id = "2", Update = new() { update } } })).Single();
            Assert.That(result.ExceptionMessage, Is.Empty);

            Assert.That(Lines(await ListAsync(Match("Order.Value", MatchComparison.Equal, new StringValue { Value = "2" }))), Is.EqualTo(new[] { ("2", "2", (decimal?)7m), ("2", "4", 3m) }));
        }

        // ---------------------------------------------------------------- 書き込み

        [Test]
        public async Task 明細の表への作成と更新と削除は読み取り専用のエラーでNetSuiteに書かない()
        {
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "LineRow" };
            add.Fields["Id"] = tempId;
            add.Fields["Order"] = new LinkFieldData { Value = "1" };
            add.Fields["Item"] = new LinkFieldData { Value = "1" };
            add.Fields["Quantity"] = new NumberFieldData { Value = 1 };
            var update = new ModuleData { Name = "LineRow" };
            update.Fields["Id"] = new IdFieldData { Value = "1" };
            update.Fields["Quantity"] = new NumberFieldData { Value = 99 };

            var submits = new[]
            {
                new ModuleSubmitData { ModuleName = "LineRow", Id = tempId.Value!, Add = new() { add } },
                new ModuleSubmitData { ModuleName = "LineRow", Id = "1", Update = new() { update } },
                new ModuleSubmitData { ModuleName = "LineRow", Delete = new() { new ModuleDeleteInfo { ModuleName = "LineRow", Id = "1" } } },
            };
            foreach (var submit in submits)
            {
                var result = (await _h.IO.SubmitWithTransactionAsync(new() { submit })).Single();
                Assert.That(result.ExceptionMessage, Does.Contain("'salesorder_item' is a read-only table of the lines of sublist 'item' of record type 'salesorder'"));
            }
            Assert.That(_h.Recorder.Requests.Where(e => e.Method != HttpMethod.Get && !e.Path.EndsWith("/suiteql") && !e.Path.EndsWith("/token")), Is.Empty);
            Assert.That(Lines(await ListAsync()), Is.EqualTo(InitialLines()));
        }

        [TestCase("update \"salesorder_item\" set \"quantity\"=:p0 where \"uniquekey\"=:pid0", TestName = "更新")]
        [TestCase("delete from \"salesorder_item\" where \"uniquekey\"=:pid0", TestName = "削除")]
        [TestCase("BEGIN INSERT INTO \"salesorder_item\"(\"quantity\") VALUES (:p0) RETURNING \"uniquekey\" INTO :returning_id; END;", TestName = "作成")]
        public void 本体が組む書き込みのSQLも読み取り専用のエラー(string sql)
        {
            var args = new Dictionary<string, object?> { [":p0"] = 1m, [":pid0"] = "1", [":returning_id"] = null };
            var ex = sql.StartsWith("BEGIN")
                ? Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.InsertAsync(TestConnections.DataSourceName, sql, args))
                : Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, sql, args));
            Assert.That(ex!.Message, Does.Contain("read-only"));
            Assert.That(ex.Kind, Is.EqualTo(NetSuiteErrorKind.NotSupported));
        }

        // ---------------------------------------------------------------- メタ情報のファイル

        [Test]
        public async Task メタ情報のファイルに明細の表が親と明細の名前つきで入り_ファイルからでも同じ定義で読める()
        {
            var directory = Path.Combine(Path.GetTempPath(), "netsuite-line-table-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                await NetSuiteMetadataFile.UpdateAsync(_h.Connection, directory);
                var file = JsonNode.Parse(File.ReadAllText(NetSuiteMetadataFile.GetPath(directory, TestConnections.DataSourceName)))!;
                var record = file["Records"]!.AsArray().Single(e => e!["Name"]!.GetValue<string>() == "salesorder_item")!;
                Assert.That(record["Parent"]!.GetValue<string>(), Is.EqualTo("salesorder"));
                Assert.That(record["Sublist"]!.GetValue<string>(), Is.EqualTo("item"));
                Assert.That(file["Records"]!.AsArray().Where(e => e!["Name"]!.GetValue<string>() == "customer").Single()!.AsObject().ContainsKey("Parent"), Is.False);

                var expected = await _h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "salesorder_item");
                await using var fromFile = await StartAsync(null, directory);
                var saved = await fromFile.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "salesorder_item");
                Assert.That(System.Text.Json.JsonSerializer.Serialize(saved), Is.EqualTo(System.Text.Json.JsonSerializer.Serialize(expected)));
                Assert.That((await fromFile.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName))!.Select(e => e.Name), Does.Contain("salesorder_item"));
                Assert.That(fromFile.Recorder.Requests, Is.Empty);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public async Task 親をRecordsから外すとファイルにあっても明細の表は返さない()
        {
            var directory = Path.Combine(Path.GetTempPath(), "netsuite-line-table-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                await NetSuiteMetadataFile.UpdateAsync(_h.Connection, directory);
                await using var h = await MockHarness.StartAsync(null, e => e.Records.Add("item"), null, directory);
                var names = (await h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName))!.Select(e => e.Name).ToList();
                Assert.That(names, Has.None.StartsWith("salesorder"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        // ---------------------------------------------------------------- モック単体

        [Test]
        public void モックのSuiteQLはtransactionlineで明細の行だけを返す()
        {
            var body = new JsonObject { ["q"] = "select uniquekey, transaction, id, linesequencenumber, mainline, taxline, item, quantity, expectedshipdate from transactionline order by uniquekey" };
            var response = _h.Mock.QuerySuiteQl("100", "0", "transient", body.ToJsonString());
            Assert.That(response.Status, Is.EqualTo(200));
            var items = JsonNode.Parse(response.Body!)!["items"]!.AsArray().Select(e => e!.AsObject()).ToList();

            Assert.That(items, Has.Count.EqualTo(InitialLines().Count));
            var first = items[0];
            Assert.That(new[] { "uniquekey", "transaction", "id", "linesequencenumber", "mainline", "taxline", "item", "quantity", "expectedshipdate" }
                .Select(e => first[e]!.GetValue<string>()), Is.EqualTo(new[] { "1", "1", "1", "1", "F", "F", "1", "10", "2026-04-10" }));
            Assert.That(items.Select(e => e["transaction"]!.GetValue<string>()), Is.EqualTo(InitialLines().Select(e => e.Order)));
        }
    }
}
