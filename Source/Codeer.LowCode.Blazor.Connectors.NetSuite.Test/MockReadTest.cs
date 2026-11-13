using NetSuite.Mock;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Match;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>
    /// モック + 本物のコネクター + 本体の ModuleDataIO で、定義・一覧・検索を端から端まで確かめる。
    /// 期待値は mock-data.json の初期の行から計算する。
    /// </summary>
    public class MockReadTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp() => _h = await MockHarness.StartAsync();

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        static FieldValueMatchCondition Match(string variable, MatchComparison comparison, MultiTypeValue value)
            => new() { SearchTargetVariable = variable, Comparison = comparison, Value = value };

        static string Id(ModuleData data) => ((IdFieldData)data.Fields["Id"]).Value!;

        static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
            => data.Fields.TryGetValue(name, out var value) ? (T)value : null;

        // ---------------------------------------------------------------- 定義

        [Test]
        public async Task デザイナが使う定義をメタデータから作り列は全部nullable()
        {
            var tables = await _h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName);

            Assert.That(tables!.Select(e => e.Name), Is.EqualTo(new[] { "subsidiary", "customer", "customrecord_sample" }));
            var customer = tables![1].Columns.ToDictionary(e => e.Name);
            Assert.That(customer.Keys, Is.EquivalentTo(new[] { "id", "companyname", "email", "isinactive", "creditlimit", "priority", "startdate", "lastmodifieddate", "subsidiary" }));
            Assert.That(tables[1].Columns[0].Name, Is.EqualTo("id"));
            Assert.That((customer["id"].NetTypeFullName, customer["id"].IsNullable), Is.EqualTo((typeof(string).FullName, true)));
            //必須 (companyName) も NOT NULL にしない (本体が空の値を型の既定値に置き換えて書かないように。必須は NetSuite が保存時に確かめる)
            Assert.That((customer["companyname"].RawDbTypeName, customer["companyname"].IsNullable), Is.EqualTo(("VARCHAR2", true)));
            Assert.That((customer["email"].RawDbTypeName, customer["email"].IsNullable), Is.EqualTo(("VARCHAR2", true)));
            Assert.That(customer["isinactive"].NetTypeFullName, Is.EqualTo(typeof(bool).FullName));
            Assert.That(customer["creditlimit"].NetTypeFullName, Is.EqualTo(typeof(decimal).FullName));
            Assert.That(customer["priority"].NetTypeFullName, Is.EqualTo(typeof(long).FullName));
            Assert.That(customer["startdate"].NetTypeFullName, Is.EqualTo(typeof(DateOnly).FullName));
            Assert.That(customer["lastmodifieddate"].NetTypeFullName, Is.EqualTo(typeof(DateTime).FullName));
            Assert.That((customer["subsidiary"].RawDbTypeName, customer["subsidiary"].IsNullable), Is.EqualTo(("REFERENCE", true)));
            Assert.That(tables.SelectMany(e => e.Columns).Select(e => e.IsNullable), Is.All.True);

            var one = await _h.Accessor.GetCustomTableDefinitionsAsync(TestConnections.DataSourceName, "customrecord_sample");
            Assert.That(one!.Columns.Single(e => e.Name == "custrecord_customer").RawDbTypeName, Is.EqualTo("REFERENCE"));
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        // ---------------------------------------------------------------- 一覧・検索

        [Test]
        public async Task 部分一致と並びとページングと件数()
        {
            var condition = new SearchCondition("Customer") { LimitCount = 1 };
            condition.Condition = Match("CompanyName.Value", MatchComparison.Like, new StringValue { Value = "Trading" });
            condition.SortConditions.Add(new SortCondition { Variable = "CompanyName.Value" });

            var first = await _h.IO.GetListAsync(condition, 0);
            var second = await _h.IO.GetListAsync(condition, 1);

            var expected = MockHarness.Rows("customer").Where(e => e.Text("companyName")!.Contains("Trading")).OrderBy(e => e.Text("companyName"), StringComparer.Ordinal).ToList();
            Assert.That(expected, Has.Count.EqualTo(2));
            Assert.That(first.TotalCount, Is.EqualTo(2));
            Assert.That(first.Items.Select(Id), Is.EqualTo(new[] { expected[0].Id() }));
            Assert.That(second.Items.Select(Id), Is.EqualTo(new[] { expected[1].Id() }));
        }

        [TestCase("%", "Sigma 100% Organic")]
        [TestCase("_", "Tau_Tech Labs")]
        public async Task 部分一致の記号はそのままの文字として探す(string text, string expected)
        {
            var condition = new SearchCondition("Customer") { Condition = Match("CompanyName.Value", MatchComparison.Like, new StringValue { Value = text }) };

            var result = await _h.IO.GetListAsync(condition, 0);

            Assert.That(result.Items.Select(e => Field<TextFieldData>(e, "CompanyName")!.Value), Is.EqualTo(new[] { expected }));
        }

        [Test]
        public async Task 真偽と数値と日付と日時の条件と値の型()
        {
            var condition = new SearchCondition("Customer");
            condition.Condition = MultiMatchCondition.And(
                Match("IsInactive.Value", MatchComparison.Equal, new BooleanValue { Value = false }),
                Match("CreditLimit.Value", MatchComparison.GreaterThanOrEqual, new DecimalValue { Value = 20000 }),
                Match("StartDate.Value", MatchComparison.LessThan, new DateOnlyValue { Value = new DateOnly(2025, 1, 1) }),
                Match("LastModified.Value", MatchComparison.GreaterThanOrEqual, new DateTimeValue { Value = new DateTime(2026, 2, 1) }));
            condition.SortConditions.Add(new SortCondition { Variable = "CreditLimit.Value", IsDescending = true });

            var result = await _h.IO.GetListAsync(condition, 0);

            var expected = MockHarness.Rows("customer")
                .Where(e => e.Bool("isInactive") == false && e.Number("creditLimit") >= 20000 && e.Date("startDate") < new DateOnly(2025, 1, 1) &&
                            e.DateTimeUtc("lastModifiedDate") >= new DateTime(2026, 2, 1))
                .OrderByDescending(e => e.Number("creditLimit")).ToList();
            Assert.That(expected, Has.Count.GreaterThan(2));
            Assert.That(result.Items.Select(Id), Is.EqualTo(expected.Select(e => e.Id())));
            Assert.That(result.TotalCount, Is.EqualTo(expected.Count));

            //値は列の種類の型で返る
            var item = result.Items[0];
            var row = expected[0];
            Assert.That(Field<TextFieldData>(item, "CompanyName")!.Value, Is.EqualTo(row.Text("companyName")));
            Assert.That(Field<TextFieldData>(item, "Email")!.Value, Is.EqualTo(row.Text("email")));
            Assert.That(Field<BooleanFieldData>(item, "IsInactive")!.Value, Is.False);
            Assert.That(Field<NumberFieldData>(item, "CreditLimit")!.Value, Is.EqualTo(row.Number("creditLimit")));
            Assert.That(Field<NumberFieldData>(item, "Priority")!.Value, Is.EqualTo(row.Number("priority")));
            Assert.That(Field<DateFieldData>(item, "StartDate")!.Value, Is.EqualTo(row.Date("startDate")));
            Assert.That(Field<DateTimeFieldData>(item, "LastModified")!.Value, Is.EqualTo(row.DateTimeUtc("lastModifiedDate")));
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 小数と空値の列も読める()
        {
            var result = await _h.IO.GetListAsync(new SearchCondition("Customer"), 0);
            var items = result.Items.ToDictionary(Id);

            //12000.5
            Assert.That(Field<NumberFieldData>(items["2"], "CreditLimit")!.Value, Is.EqualTo(12000.5m));
            //値の無い列
            var theta = items["8"];
            Assert.That(Field<NumberFieldData>(theta, "CreditLimit")?.Value, Is.Null);
            Assert.That(Field<DateFieldData>(theta, "StartDate")?.Value, Is.Null);
            Assert.That(Field<BooleanFieldData>(theta, "IsInactive")!.Value, Is.True);
            //既定の並びは id の降順
            Assert.That(result.Items.Select(Id), Is.EqualTo(MockHarness.Rows("customer").Select(e => e.Id()).OrderByDescending(int.Parse)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task 並びの空値はOracleと同じく昇順で最後_降順で最初(bool descending)
        {
            var condition = new SearchCondition("Customer");
            condition.SortConditions.Add(new SortCondition { Variable = "CreditLimit.Value", IsDescending = descending });

            var ids = (await _h.IO.GetListAsync(condition, 0)).Items.Select(Id).ToList();

            //Theta Books (8) だけ与信限度額が無い
            Assert.That(descending ? ids.First() : ids.Last(), Is.EqualTo("8"));
        }

        [Test]
        public async Task リンク越しの表示名と検索()
        {
            var condition = new SearchCondition("Customer") { Condition = Match("Subsidiary.Name.Value", MatchComparison.Equal, new StringValue { Value = "US Sales" }) };

            var result = await _h.IO.GetListAsync(condition, 0);

            var expected = MockHarness.Rows("customer").Where(e => e.Ref("subsidiary") == "3").Select(e => e.Id()).ToList();
            Assert.That(result.Items.Select(Id), Is.EquivalentTo(expected));
            Assert.That(result.Items.Select(e => Field<LinkFieldData>(e, "Subsidiary")!.Value), Is.All.EqualTo("3"));
            Assert.That(result.Items.Select(e => Field<LinkFieldData>(e, "Subsidiary")!.DisplayText), Is.All.EqualTo("US Sales"));
        }

        [Test]
        public async Task カスタムレコードからリンク越しに部分一致で探す()
        {
            var condition = new SearchCondition("Sample") { Condition = Match("Customer.CompanyName.Value", MatchComparison.Like, new StringValue { Value = "Alpha" }) };

            var result = await _h.IO.GetListAsync(condition, 0);

            var expected = MockHarness.Rows("customrecord_sample").Where(e => e.Ref("custrecord_customer") == "1").Select(e => e.Id()).ToList();
            Assert.That(result.Items.Select(Id), Is.EquivalentTo(expected));
            Assert.That(result.Items.Select(e => Field<LinkFieldData>(e, "Customer")!.DisplayText), Is.All.EqualTo("Alpha Trading"));
            //選択・真偽・日付
            var first = result.Items.Single(e => Id(e) == "1");
            Assert.That(Field<SelectFieldData>(first, "Status")!.Value, Is.EqualTo("won"));
            Assert.That(Field<BooleanFieldData>(first, "Done")!.Value, Is.True);
            Assert.That(Field<DateFieldData>(first, "OrderDate")!.Value, Is.EqualTo(new DateOnly(2026, 1, 5)));
            Assert.That(Field<NumberFieldData>(first, "Quantity")!.Value, Is.EqualTo(10m));
        }

        // ---------------------------------------------------------------- 1000 行を超える読み出し

        static async Task<MockHarness> StartWithManyRowsAsync(int count)
        {
            var mock = MockNetSuite.FromFile(MockHarness.DataPath);
            for (var i = 0; i < count; i++) mock.AddRecord("customrecord_sample", $$"""{"name":"Bulk {{i:D4}}","custrecord_quantity":{{i}}}""");
            return await MockHarness.StartAsync(mock);
        }

        [Test]
        public async Task 件数を絞らない一覧は1000行ずつ読み進めて全部返す()
        {
            await _h.DisposeAsync();
            _h = await StartWithManyRowsAsync(2500);

            var result = await _h.IO.GetListAsync(new SearchCondition("Sample"), 0);

            Assert.That(result.Items, Has.Count.EqualTo(2515));
            Assert.That(result.Items.Select(Id), Is.EqualTo(Enumerable.Range(1, 2515).Reverse().Select(e => e.ToString())));
        }

        [Test]
        public async Task 表示件数が1000を超えるページも読める()
        {
            await _h.DisposeAsync();
            _h = await StartWithManyRowsAsync(2500);
            var condition = new SearchCondition("Sample") { LimitCount = 1500 };
            condition.SortConditions.Add(new SortCondition { Variable = "Id.Value" });

            var result = await _h.IO.GetListAsync(condition, 1);

            Assert.That(result.TotalCount, Is.EqualTo(2515));
            Assert.That(result.Items.Select(Id), Is.EqualTo(Enumerable.Range(1501, 1015).Select(e => e.ToString())));
        }

        [Test]
        public async Task MaxRowsを超える読み出しはエラー()
        {
            await _h.DisposeAsync();
            var mock = MockNetSuite.FromFile(MockHarness.DataPath);
            for (var i = 0; i < 1100; i++) mock.AddRecord("customrecord_sample", $$"""{"name":"Bulk {{i}}"}""");
            _h = await MockHarness.StartAsync(mock, e => e.MaxRows = 1000);

            Assert.That(async () => await _h.IO.GetListAsync(new SearchCondition("Sample"), 0), Throws.Exception.With.Message.Contains("MaxRows"));
        }
    }
}
