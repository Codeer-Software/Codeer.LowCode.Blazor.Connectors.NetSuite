using Codeer.LowCode.Blazor.Aggregation;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>本体の ModuleDataIO.AggregateAsync を通して、NetSuite に送る SuiteQL と集計の結果を確かめる。</summary>
    public class NetSuiteAggregateTest
    {
        NetSuiteHarness _h = null!;

        [SetUp]
        public void SetUp() => _h = CreateHarness();

        [TearDown]
        public void TearDown() => _h.Dispose();

        const string DealSchema = """
            {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "name": { "type": "string" },
                "custrecord_status": { "type": "string" },
                "custrecord_amount": { "type": "number" },
                "custrecord_qty": { "type": "integer" },
                "custrecord_closed": { "type": "boolean" },
                "custrecord_closedate": { "type": "string", "format": "date" },
                "created": { "type": "string", "format": "date-time" },
                "custrecord_customer": { "type": "object", "properties": { "id": { "type": "string" }, "refName": { "type": "string" } } },
                "links": { "type": "array", "items": { "type": "object" } },
                "refName": { "type": "string" }
              }
            }
            """;

        const string EmployeeSchema = """
            { "type": "object", "properties": { "id": { "type": "string" }, "entityid": { "type": "string" } } }
            """;

        static NetSuiteHarness CreateHarness()
        {
            var h = new NetSuiteHarness(null, AddModules, fake => fake.Schemas["customrecord_deal"] = DealSchema);
            //メタ情報のファイルに無いレコード (定義は NetSuite にあっても読ませない)
            h.Fake.Schemas["employee"] = EmployeeSchema;
            return h;
        }

        /// <summary>Deal (customrecord_deal) と、メタ情報のファイルに無いレコードのモジュール。</summary>
        static void AddModules(DesignData d)
        {
            var modules = (IEditableModuleDesign)d.Modules;

            var deal = new ModuleDesign { Name = "Deal", DataSourceName = TestConnections.DataSourceName, DbTable = "customrecord_deal" };
            deal.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            deal.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "name" });
            deal.Fields.Add(new SelectFieldDesign { Name = "Status", DbColumn = "custrecord_status", Candidates = new() { "Open,open", "Won,won", "Lost,lost" } });
            deal.Fields.Add(new NumberFieldDesign { Name = "Amount", DbColumn = "custrecord_amount" });
            deal.Fields.Add(new NumberFieldDesign { Name = "Qty", DbColumn = "custrecord_qty" });
            deal.Fields.Add(new BooleanFieldDesign { Name = "IsClosed", DbColumn = "custrecord_closed" });
            deal.Fields.Add(new DateFieldDesign { Name = "CloseDate", DbColumn = "custrecord_closedate" });
            deal.Fields.Add(new DateTimeFieldDesign { Name = "Created", DbColumn = "created", SaveAsUtc = true });
            deal.Fields.Add(new LinkFieldDesign
            {
                Name = "Customer",
                DbColumn = "custrecord_customer",
                SearchCondition = new SearchCondition("Customer"),
                ValueVariable = "Id.Value",
                DisplayTextVariable = "CompanyName.Value",
            });
            modules.Add(deal);

            var employee = new ModuleDesign { Name = "Employee", DataSourceName = TestConnections.DataSourceName, DbTable = "employee" };
            employee.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            employee.Fields.Add(new TextFieldDesign { Name = "EntityId", DbColumn = "entityid" });
            modules.Add(employee);
        }

        // ---------------------------------------------------------------- 応答

        /// <summary>
        /// SuiteQL の応答: グループ数の数え直し (select count(*) from (...)) には groupCount、行数の数え直しには count、それ以外 (集計の本体) は rows。
        /// 値はすべて文字列で、null の列は省く (実物の SuiteQL の結果と同じ形)。
        /// </summary>
        void Respond(List<Dictionary<string, string>> rows, int? count = null, int? groupCount = null)
        {
            _h.Fake.SuiteQl = call =>
            {
                if (call.Q.StartsWith("select count(*) as nsx0 from ("))
                    return new SuiteQlResult { Items = new() { new() { ["nsx0"] = (groupCount ?? rows.Count).ToString() } } };
                if (call.Q.StartsWith("select count(*) as nsx0 from "))
                    return new SuiteQlResult { Items = new() { new() { ["nsx0"] = (count ?? 0).ToString() } } };
                return new SuiteQlResult { Items = rows.Skip(call.Offset).Take(call.Limit).ToList(), HasMore = call.Offset + call.Limit < rows.Count };
            };
        }

        static Dictionary<string, string> Row(params (string Key, string? Value)[] values)
            => values.Where(e => e.Value != null).ToDictionary(e => e.Key, e => e.Value!);

        static AggregateMeasure Measure(AggregateFunction function, string variable = "") => new() { Function = function, Variable = variable };

        Task<AggregateResult> AggregateAsync(AggregateCondition condition) => _h.IO.AggregateAsync(condition);

        /// <summary>本体が軸の空値を最後に並べるために付ける印の式。</summary>
        static string NullFlag(string expression) => $"case when {expression} is null then 1 else 0 end";

        const string StatusRank = "case customrecord_deal.custrecord_status when 'open' then 0 when 'won' then 1 when 'lost' then 2 else 3 end";

        const string CustomerJoin = "left join customer customer_0 on customrecord_deal.custrecord_customer = customer_0.id";

        // ---------------------------------------------------------------- 軸なし

        [Test]
        public async Task 軸なしの件数だけ()
        {
            Respond(new() { Row(("m0", "5"), ("n", "5")) });

            var result = await AggregateAsync(new AggregateCondition("Deal") { Measures = { Measure(AggregateFunction.Count) } });

            var call = _h.Fake.SuiteQlCalls.Single();
            Assert.That(call.Q, Is.EqualTo("select count(*) as m0, count(*) as n from customrecord_deal"));
            Assert.That(call.HasParamsProperty, Is.False);
            Assert.That((call.Limit, call.Offset), Is.EqualTo((1000, 0)));

            var row = result.Rows.Single();
            Assert.That(row.Keys, Is.Empty);
            Assert.That(row.Value(0), Is.TypeOf<decimal>().And.EqualTo(5m));
            Assert.That(result.TotalCount, Is.EqualTo(5));
            Assert.That(result.GroupCount, Is.EqualTo(1));
            Assert.That(result.IsLimited, Is.False);
            //NetSuite のデータソースは包んだ IDbAccessor に渡らない
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 軸なしの合計と平均と最小と最大と重複なしの件数()
        {
            Respond(new() { Row(("m0", "4"), ("m1", "1001.5"), ("m2", "250.375"), ("m3", "0.5"), ("m4", "1000"), ("m5", "3"), ("n", "4")) });

            var result = await AggregateAsync(new AggregateCondition("Deal")
            {
                Measures =
                {
                    Measure(AggregateFunction.Count), Measure(AggregateFunction.Sum, "Amount.Value"), Measure(AggregateFunction.Avg, "Amount.Value"),
                    Measure(AggregateFunction.Min, "Amount.Value"), Measure(AggregateFunction.Max, "Amount.Value"), Measure(AggregateFunction.CountDistinct, "Customer.Value"),
                },
            });

            Assert.That(_h.Fake.SuiteQlCalls.Single().Q, Is.EqualTo(
                "select count(*) as m0, sum(customrecord_deal.custrecord_amount) as m1, avg(customrecord_deal.custrecord_amount) as m2," +
                " min(customrecord_deal.custrecord_amount) as m3, max(customrecord_deal.custrecord_amount) as m4," +
                " count(distinct customrecord_deal.custrecord_customer) as m5, count(*) as n from customrecord_deal"));
            var row = result.Rows.Single();
            Assert.That(row.Values.Select(e => e.GetValue()), Is.EqualTo(new object[] { 4m, 1001.5m, 250.375m, 0.5m, 1000m, 3m }));
            Assert.That(row.Values.Select(e => e.GetValue()), Is.All.TypeOf<decimal>());
            Assert.That(result.TotalCount, Is.EqualTo(4));
            Assert.That(result.GroupCount, Is.EqualTo(1));
        }

        [Test]
        public async Task 軸なしで該当が無ければ件数0で合計は空()
        {
            //SuiteQL は null の列を省く
            Respond(new() { Row(("m0", "0"), ("n", "0")) });

            var result = await AggregateAsync(new AggregateCondition("Deal") { Measures = { Measure(AggregateFunction.Count), Measure(AggregateFunction.Sum, "Amount.Value"), Measure(AggregateFunction.Max, "CloseDate.Value") } });

            var row = result.Rows.Single();
            Assert.That(row.Value(0), Is.EqualTo(0m));
            Assert.That(row.Values[1], Is.TypeOf<NullValue>());
            Assert.That(row.Values[2], Is.TypeOf<NullValue>());
            Assert.That(result.TotalCount, Is.EqualTo(0));
            Assert.That(result.GroupCount, Is.EqualTo(1));
        }

        [Test]
        public async Task 平均の桁が多くても数値で返る()
        {
            //Oracle の avg は 38 桁まで返る (decimal の桁を超える)
            Respond(new() { Row(("m0", "33.333333333333333333333333333333333333"), ("n", "3")) });

            var result = await AggregateAsync(new AggregateCondition("Deal") { Measures = { Measure(AggregateFunction.Avg, "Amount.Value") } });

            Assert.That(result.Rows.Single().Value(0), Is.TypeOf<decimal>());
            Assert.That((decimal)result.Rows.Single().Value(0)!, Is.EqualTo(33.333333333333333333333333333m).Within(0.000000000001m));
        }

        // ---------------------------------------------------------------- 値そのままの軸

        [Test]
        public async Task 文字の項目を軸に件数と合計()
        {
            Respond(new()
            {
                Row(("g0", "Alpha"), ("z0", "0"), ("m0", "2"), ("m1", "300"), ("n", "2")),
                Row(("g0", "Beta"), ("z0", "0"), ("m0", "1"), ("m1", "12.5"), ("n", "1")),
                //軸が空値のグループ (合計も空)
                Row(("z0", "1"), ("m0", "1"), ("n", "1")),
            });

            var result = await AggregateAsync(new AggregateCondition("Deal")
            {
                Groups = { new ValueGroup { Variable = "Name.Value" } },
                Measures = { Measure(AggregateFunction.Count), Measure(AggregateFunction.Sum, "Amount.Value") },
            });

            var g = "customrecord_deal.name";
            var call = _h.Fake.SuiteQlCalls.Single();
            Assert.That(call.Q, Is.EqualTo(
                $"select {g} as g0, {NullFlag(g)} as z0, count(*) as m0, sum(customrecord_deal.custrecord_amount) as m1, count(*) as n" +
                $" from customrecord_deal group by {g}, {NullFlag(g)} order by z0, g0"));
            //集計は並びの最後に id を足さない (group by が付いている)
            Assert.That(call.Q, Does.Not.Contain("customrecord_deal.id"));

            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(new object?[] { "Alpha", "Beta", null }));
            Assert.That(result.Rows[2].Keys[0].Value, Is.TypeOf<NullValue>());
            Assert.That(result.Rows.Select(e => e.Value(0)), Is.EqualTo(new object[] { 2m, 1m, 1m }));
            Assert.That(result.Rows.Select(e => e.Value(1)), Is.EqualTo(new object?[] { 300m, 12.5m, null }));
            Assert.That(result.Rows.Select(e => e.Keys[0].DisplayText), Is.All.Empty);
            //HAVING も上限も無いので、行数はグループごとの行数の合計 (数え直さない)
            Assert.That(result.TotalCount, Is.EqualTo(4));
            Assert.That(result.GroupCount, Is.EqualTo(3));
            Assert.That(result.IsLimited, Is.False);
        }

        [Test]
        public async Task リンクを軸にすると表示名がリンク先の表示項目で付く()
        {
            Respond(new()
            {
                Row(("g0", "3"), ("z0", "0"), ("d0", "Acme"), ("m0", "2"), ("n", "2")),
                Row(("g0", "7"), ("z0", "0"), ("d0", "Beta"), ("m0", "1"), ("n", "1")),
                Row(("z0", "1"), ("m0", "4"), ("n", "4")),
            });

            var result = await AggregateAsync(new AggregateCondition("Deal") { Groups = { new ValueGroup { Variable = "Customer.Value" } }, Measures = { Measure(AggregateFunction.Count) } });

            var g = "customrecord_deal.custrecord_customer";
            Assert.That(_h.Fake.SuiteQlCalls.Single().Q, Is.EqualTo(
                $"select {g} as g0, {NullFlag(g)} as z0, min(customer_0.companyname) as d0, count(*) as m0, count(*) as n" +
                $" from customrecord_deal {CustomerJoin} group by {g}, {NullFlag(g)} order by z0, d0, g0"));

            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(new object?[] { "3", "7", null }));
            Assert.That(result.Rows.Select(e => e.Keys[0].DisplayText), Is.EqualTo(new[] { "Acme", "Beta", "" }));
            Assert.That(result.Rows.Select(e => e.KeyText(0)), Is.EqualTo(new[] { "Acme", "Beta", "" }));
            Assert.That(result.TotalCount, Is.EqualTo(7));
        }

        [Test]
        public async Task 候補つきの選択を軸にすると候補の順の列が入り表示名は候補名()
        {
            Respond(new()
            {
                Row(("g0", "open"), ("z0", "0"), ("r0", "0"), ("m0", "2"), ("n", "2")),
                Row(("g0", "won"), ("z0", "0"), ("r0", "1"), ("m0", "1"), ("n", "1")),
                //候補に無い値
                Row(("g0", "other"), ("z0", "0"), ("r0", "3"), ("m0", "1"), ("n", "1")),
            });

            var result = await AggregateAsync(new AggregateCondition("Deal") { Groups = { new ValueGroup { Variable = "Status.Value" } }, Measures = { Measure(AggregateFunction.Count) } });

            var g = "customrecord_deal.custrecord_status";
            Assert.That(_h.Fake.SuiteQlCalls.Single().Q, Is.EqualTo(
                $"select {g} as g0, {NullFlag(g)} as z0, {StatusRank} as r0, count(*) as m0, count(*) as n" +
                $" from customrecord_deal group by {g}, {NullFlag(g)}, {StatusRank} order by z0, r0, g0"));
            //候補のコードは設計で決まった値なので、パラメータでなくリテラルのまま
            Assert.That(_h.Fake.SuiteQlCalls.Single().HasParamsProperty, Is.False);

            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(new object?[] { "open", "won", "other" }));
            Assert.That(result.Rows.Select(e => e.Keys[0].DisplayText), Is.EqualTo(new[] { "Open", "Won", "" }));
        }

        [Test]
        public async Task 日付の項目を丸めずに軸にするとキーは日付()
        {
            Respond(new()
            {
                Row(("g0", "2026-03-05"), ("z0", "0"), ("m0", "2"), ("n", "2")),
                Row(("z0", "1"), ("m0", "1"), ("n", "1")),
            });

            var result = await AggregateAsync(new AggregateCondition("Deal") { Groups = { new ValueGroup { Variable = "CloseDate.Value" } }, Measures = { Measure(AggregateFunction.Count) } });

            var g = "customrecord_deal.custrecord_closedate";
            Assert.That(_h.Fake.SuiteQlCalls.Single().Q, Is.EqualTo(
                $"select TO_CHAR({g}, 'YYYY-MM-DD') as g0, {NullFlag(g)} as z0, count(*) as m0, count(*) as n" +
                $" from customrecord_deal group by {g}, {NullFlag(g)} order by z0, g0"));
            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(new object?[] { new DateOnly(2026, 3, 5), null }));
        }

        // ---------------------------------------------------------------- 日付の丸め

        const string CloseDate = "customrecord_deal.custrecord_closedate";
        const string Created = "customrecord_deal.created";
        const string Created540 = "(customrecord_deal.created + NUMTODSINTERVAL(540, 'MINUTE'))";
        const string CreatedMinus300 = "(customrecord_deal.created + NUMTODSINTERVAL(- 300, 'MINUTE'))";

        //日付の項目は時差で動かさない (UtcOffsetMinutes を付けても)
        [TestCase("CloseDate", 540, DateBucket.Hour, 1, "TRUNC(" + CloseDate + ", 'HH')", "2026-03-05 00:00:00", "2026-03-05 00:00:00")]
        [TestCase("CloseDate", 540, DateBucket.Day, 1, "TRUNC(" + CloseDate + ")", "2026-03-05 00:00:00", "2026-03-05")]
        [TestCase("CloseDate", 540, DateBucket.Week, 1, "TRUNC(" + CloseDate + ", 'IW')", "2026-03-02 00:00:00", "2026-03-02")]
        [TestCase("CloseDate", 540, DateBucket.Month, 1, "TRUNC(" + CloseDate + ", 'MM')", "2026-03-01 00:00:00", "2026-03-01")]
        [TestCase("CloseDate", 540, DateBucket.Quarter, 1, "TRUNC(" + CloseDate + ", 'Q')", "2026-01-01 00:00:00", "2026-01-01")]
        [TestCase("CloseDate", 540, DateBucket.Quarter, 4, "ADD_MONTHS(TRUNC(ADD_MONTHS(" + CloseDate + ", - 3), 'Q'), 3)", "2026-01-01 00:00:00", "2026-01-01")]
        [TestCase("CloseDate", 540, DateBucket.Year, 1, "TRUNC(" + CloseDate + ", 'YYYY')", "2026-01-01 00:00:00", "2026-01-01")]
        [TestCase("CloseDate", 540, DateBucket.Year, 4, "ADD_MONTHS(TRUNC(ADD_MONTHS(" + CloseDate + ", - 3), 'YYYY'), 3)", "2025-04-01 00:00:00", "2025-04-01")]
        //UTC で保存した日時は時差を足してから丸める
        [TestCase("Created", 540, DateBucket.Hour, 1, "TRUNC(" + Created540 + ", 'HH')", "2026-03-05 09:00:00", "2026-03-05 09:00:00")]
        [TestCase("Created", 540, DateBucket.Day, 1, "TRUNC(" + Created540 + ")", "2026-03-05 00:00:00", "2026-03-05")]
        [TestCase("Created", 540, DateBucket.Month, 1, "TRUNC(" + Created540 + ", 'MM')", "2026-03-01 00:00:00", "2026-03-01")]
        [TestCase("Created", 540, DateBucket.Quarter, 4, "ADD_MONTHS(TRUNC(ADD_MONTHS(" + Created540 + ", - 3), 'Q'), 3)", "2026-01-01 00:00:00", "2026-01-01")]
        [TestCase("Created", 540, DateBucket.Year, 4, "ADD_MONTHS(TRUNC(ADD_MONTHS(" + Created540 + ", - 3), 'YYYY'), 3)", "2025-04-01 00:00:00", "2025-04-01")]
        [TestCase("Created", -300, DateBucket.Day, 1, "TRUNC(" + CreatedMinus300 + ")", "2026-03-04 00:00:00", "2026-03-04")]
        //時差 0 なら時差の式は入らない
        [TestCase("Created", 0, DateBucket.Hour, 1, "TRUNC(" + Created + ", 'HH')", "2026-03-05 00:00:00", "2026-03-05 00:00:00")]
        [TestCase("Created", 0, DateBucket.Day, 1, "TRUNC(" + Created + ")", "2026-03-05 00:00:00", "2026-03-05")]
        [TestCase("Created", 0, DateBucket.Month, 1, "TRUNC(" + Created + ", 'MM')", "2026-03-01 00:00:00", "2026-03-01")]
        [TestCase("Created", 0, DateBucket.Quarter, 1, "TRUNC(" + Created + ", 'Q')", "2026-01-01 00:00:00", "2026-01-01")]
        [TestCase("Created", 0, DateBucket.Year, 1, "TRUNC(" + Created + ", 'YYYY')", "2026-01-01 00:00:00", "2026-01-01")]
        [TestCase("Created", 0, DateBucket.Year, 4, "ADD_MONTHS(TRUNC(ADD_MONTHS(" + Created + ", - 3), 'YYYY'), 3)", "2025-04-01 00:00:00", "2025-04-01")]
        public async Task 日付と日時を区切って軸にするとキーは期間の開始(string field, int utcOffsetMinutes, DateBucket bucket, int fiscalYearStartMonth, string expression, string returned, string expectedKey)
        {
            Respond(new()
            {
                Row(("g0", returned), ("z0", "0"), ("m0", "2"), ("n", "2")),
                Row(("z0", "1"), ("m0", "1"), ("n", "1")),
            });

            var result = await AggregateAsync(new AggregateCondition("Deal")
            {
                UtcOffsetMinutes = utcOffsetMinutes,
                Groups = { new DateGroup { Variable = field + ".Value", Bucket = bucket, FiscalYearStartMonth = fiscalYearStartMonth } },
                Measures = { Measure(AggregateFunction.Count) },
            });

            //丸めた値は時刻まで持つことがあるので、日時の形にして受け取る
            Assert.That(_h.Fake.SuiteQlCalls.Single().Q, Is.EqualTo(
                $"select TO_CHAR({expression}, 'YYYY-MM-DD HH24:MI:SS') as g0, {NullFlag(expression)} as z0, count(*) as m0, count(*) as n" +
                $" from customrecord_deal group by {expression}, {NullFlag(expression)} order by z0, g0"));

            //鍵は期間の開始日 (時は開始時刻)
            object key = bucket == DateBucket.Hour
                ? DateTime.ParseExact(expectedKey, "yyyy-MM-dd HH:mm:ss", null)
                : DateOnly.ParseExact(expectedKey, "yyyy-MM-dd", null);
            Assert.That(result.Rows[0].Key(0), Is.TypeOf(key.GetType()).And.EqualTo(key));
            Assert.That(result.Rows[1].Keys[0].Value, Is.TypeOf<NullValue>());
            Assert.That(result.TotalCount, Is.EqualTo(3));
            Assert.That(result.GroupCount, Is.EqualTo(2));
        }

        // ---------------------------------------------------------------- 最小・最大

        [Test]
        public async Task 最小と最大は項目の型で返る()
        {
            Respond(new() { Row(("m0", "1"), ("m1", "1000.5"), ("m2", "2026-01-02"), ("m3", "2026-03-04 05:06:07"), ("m4", "Alpha"), ("m5", "99"), ("n", "3")) });

            var result = await AggregateAsync(new AggregateCondition("Deal")
            {
                Measures =
                {
                    Measure(AggregateFunction.Min, "Qty.Value"), Measure(AggregateFunction.Max, "Amount.Value"), Measure(AggregateFunction.Min, "CloseDate.Value"),
                    Measure(AggregateFunction.Max, "Created.Value"), Measure(AggregateFunction.Min, "Name.Value"), Measure(AggregateFunction.Max, "Id.Value"),
                },
            });

            Assert.That(_h.Fake.SuiteQlCalls.Single().Q, Is.EqualTo(
                "select min(customrecord_deal.custrecord_qty) as m0, max(customrecord_deal.custrecord_amount) as m1," +
                " TO_CHAR(min(customrecord_deal.custrecord_closedate), 'YYYY-MM-DD') as m2, TO_CHAR(max(customrecord_deal.created), 'YYYY-MM-DD HH24:MI:SS') as m3," +
                " min(customrecord_deal.name) as m4, max(customrecord_deal.id) as m5, count(*) as n from customrecord_deal"));
            var values = result.Rows.Single().Values.Select(e => e.GetValue()).ToList();
            Assert.That(values[0], Is.TypeOf<decimal>().And.EqualTo(1m));
            Assert.That(values[1], Is.TypeOf<decimal>().And.EqualTo(1000.5m));
            Assert.That(values[2], Is.TypeOf<DateOnly>().And.EqualTo(new DateOnly(2026, 1, 2)));
            Assert.That(values[3], Is.TypeOf<DateTime>().And.EqualTo(new DateTime(2026, 3, 4, 5, 6, 7)));
            Assert.That(values[4], Is.TypeOf<string>().And.EqualTo("Alpha"));
            Assert.That(values[5], Is.TypeOf<string>().And.EqualTo("99"));
        }

        [Test]
        public async Task 軸ごとの日付の最小と日時の最大()
        {
            Respond(new()
            {
                Row(("g0", "won"), ("z0", "0"), ("r0", "1"), ("m0", "2026-01-02"), ("m1", "2026-03-04 05:06:07"), ("n", "2")),
                Row(("g0", "lost"), ("z0", "0"), ("r0", "2"), ("n", "1")),
            });

            var result = await AggregateAsync(new AggregateCondition("Deal")
            {
                Groups = { new ValueGroup { Variable = "Status.Value" } },
                Measures = { Measure(AggregateFunction.Min, "CloseDate.Value"), Measure(AggregateFunction.Max, "Created.Value") },
            });

            var g = "customrecord_deal.custrecord_status";
            Assert.That(_h.Fake.SuiteQlCalls.Single().Q, Is.EqualTo(
                $"select {g} as g0, {NullFlag(g)} as z0, {StatusRank} as r0," +
                " TO_CHAR(min(customrecord_deal.custrecord_closedate), 'YYYY-MM-DD') as m0, TO_CHAR(max(customrecord_deal.created), 'YYYY-MM-DD HH24:MI:SS') as m1, count(*) as n" +
                $" from customrecord_deal group by {g}, {NullFlag(g)}, {StatusRank} order by z0, r0, g0"));
            Assert.That(result.Rows[0].Value(0), Is.EqualTo(new DateOnly(2026, 1, 2)));
            Assert.That(result.Rows[0].Value(1), Is.EqualTo(new DateTime(2026, 3, 4, 5, 6, 7)));
            Assert.That(result.Rows[1].Values, Is.All.TypeOf<NullValue>());
        }

        // ---------------------------------------------------------------- HAVING・並び・上限

        AggregateCondition SumByCustomer() => new("Deal")
        {
            Groups = { new ValueGroup { Variable = "Customer.Value" } },
            Measures = { Measure(AggregateFunction.Count), Measure(AggregateFunction.Sum, "Amount.Value") },
        };

        const string SumByCustomerSelect =
            "select customrecord_deal.custrecord_customer as g0, case when customrecord_deal.custrecord_customer is null then 1 else 0 end as z0," +
            " min(customer_0.companyname) as d0, count(*) as m0, sum(customrecord_deal.custrecord_amount) as m1, count(*) as n" +
            " from customrecord_deal left join customer customer_0 on customrecord_deal.custrecord_customer = customer_0.id" +
            " group by customrecord_deal.custrecord_customer, case when customrecord_deal.custrecord_customer is null then 1 else 0 end";

        const string SumByCustomerGroupCount =
            "select count(*) as nsx0 from (select 1 as c_l_b_g from customrecord_deal left join customer customer_0 on customrecord_deal.custrecord_customer = customer_0.id" +
            " group by customrecord_deal.custrecord_customer, case when customrecord_deal.custrecord_customer is null then 1 else 0 end";

        static List<Dictionary<string, string>> CustomerRows() => new()
        {
            Row(("g0", "3"), ("z0", "0"), ("d0", "Acme"), ("m0", "4"), ("m1", "900"), ("n", "4")),
            Row(("g0", "7"), ("z0", "0"), ("d0", "Beta"), ("m0", "2"), ("m1", "500.5"), ("n", "2")),
            Row(("g0", "9"), ("z0", "0"), ("d0", "Gamma"), ("m0", "1"), ("m1", "101"), ("n", "1")),
        };

        [Test]
        public async Task 値での並び()
        {
            Respond(CustomerRows());
            var condition = SumByCustomer();
            condition.SortConditions.Add(new AggregateSort { Target = AggregateSortTarget.Measure, Index = 1, IsDescending = true });

            var result = await AggregateAsync(condition);

            var call = _h.Fake.SuiteQlCalls.Single();
            Assert.That(call.Q, Is.EqualTo(SumByCustomerSelect + " order by m1 desc, z0, d0, g0"));
            Assert.That((call.Limit, call.Offset), Is.EqualTo((1000, 0)));
            //返った順のまま
            Assert.That(result.Rows.Select(e => e.Key(0)), Is.EqualTo(new[] { "3", "7", "9" }));
            Assert.That(result.TotalCount, Is.EqualTo(7));
            Assert.That(result.GroupCount, Is.EqualTo(3));
        }

        [Test]
        public async Task HAVINGがあれば行数を数え直す()
        {
            Respond(CustomerRows(), count: 10);
            var condition = SumByCustomer();
            condition.Having.Add(new AggregateHaving { MeasureIndex = 1, Comparison = MatchComparison.GreaterThan, Value = 100.5m });

            var result = await AggregateAsync(condition);

            var calls = _h.Fake.SuiteQlCalls;
            Assert.That(calls.Select(e => e.Q), Is.EqualTo(new[]
            {
                SumByCustomerSelect + " having sum(customrecord_deal.custrecord_amount) > 100.5 order by z0, d0, g0",
                "select count(*) as nsx0 from customrecord_deal " + CustomerJoin,
            }));
            //HAVING で外れた行も含めた、条件に合った行数
            Assert.That(result.TotalCount, Is.EqualTo(10));
            Assert.That(result.GroupCount, Is.EqualTo(3));
            Assert.That(result.IsLimited, Is.False);
        }

        [Test]
        public async Task LimitCountを付けると上限をURLのlimitにして行数とグループ数を数え直す()
        {
            Respond(CustomerRows(), count: 20, groupCount: 5);
            var condition = SumByCustomer();
            condition.Having.Add(new AggregateHaving { MeasureIndex = 1, Comparison = MatchComparison.GreaterThan, Value = 100.5m });
            condition.SortConditions.Add(new AggregateSort { Target = AggregateSortTarget.Measure, Index = 1, IsDescending = true });
            condition.LimitCount = 3;

            var result = await AggregateAsync(condition);

            var calls = _h.Fake.SuiteQlCalls;
            Assert.That(calls.Select(e => e.Q), Is.EqualTo(new[]
            {
                SumByCustomerSelect + " having sum(customrecord_deal.custrecord_amount) > 100.5 order by m1 desc, z0, d0, g0",
                "select count(*) as nsx0 from customrecord_deal " + CustomerJoin,
                SumByCustomerGroupCount + " having sum(customrecord_deal.custrecord_amount) > 100.5) c_l_b_agg",
            }));
            //本体のページング句 (offset 0 rows fetch next 3 rows only) は URL の limit / offset になる
            Assert.That((calls[0].Limit, calls[0].Offset), Is.EqualTo((3, 0)));
            var request = _h.Fake.Requests.First(e => e.IsSuiteQl);
            Assert.That(request.Url, Is.EqualTo(FakeNetSuite.RestUrl + "/query/v1/suiteql?limit=3&offset=0"));
            Assert.That(calls.Select(e => e.Q), Has.None.Contains("fetch").And.None.Contains("offset"));

            Assert.That(result.Rows, Has.Count.EqualTo(3));
            Assert.That(result.TotalCount, Is.EqualTo(20));
            Assert.That(result.GroupCount, Is.EqualTo(5));
            Assert.That(result.IsLimited, Is.True);
        }

        [Test]
        public async Task LimitCountよりグループが少なければIsLimitedは偽()
        {
            Respond(CustomerRows().Take(2).ToList(), count: 6, groupCount: 2);
            var condition = SumByCustomer();
            condition.LimitCount = 3;

            var result = await AggregateAsync(condition);

            var calls = _h.Fake.SuiteQlCalls;
            Assert.That(calls.Select(e => e.Q), Is.EqualTo(new[]
            {
                SumByCustomerSelect + " order by z0, d0, g0",
                "select count(*) as nsx0 from customrecord_deal " + CustomerJoin,
                SumByCustomerGroupCount + ") c_l_b_agg",
            }));
            Assert.That(result.Rows, Has.Count.EqualTo(2));
            Assert.That(result.TotalCount, Is.EqualTo(6));
            Assert.That(result.GroupCount, Is.EqualTo(2));
            Assert.That(result.IsLimited, Is.False);
        }

        // ---------------------------------------------------------------- 行を選ぶ定義 (WithinGroupsOf)

        [Test]
        public async Task WithinGroupsOfは派生表との内部結合になる()
        {
            Respond(new()
            {
                Row(("g0", "2026-03-01 00:00:00"), ("z0", "0"), ("g1", "won"), ("z1", "0"), ("r1", "1"), ("m0", "1500"), ("n", "3")),
                Row(("g0", "2026-04-01 00:00:00"), ("z0", "0"), ("g1", "open"), ("z1", "0"), ("r1", "0"), ("m0", "20"), ("n", "1")),
            });

            var result = await AggregateAsync(new AggregateCondition("Deal")
            {
                Groups = { new DateGroup { Variable = "CloseDate.Value", Bucket = DateBucket.Month }, new ValueGroup { Variable = "Status.Value" } },
                Measures = { Measure(AggregateFunction.Sum, "Amount.Value") },
                WithinGroupsOf = new AggregateCondition("Deal") { Groups = { new ValueGroup { Variable = "Status.Value" } }, Measures = { Measure(AggregateFunction.Sum, "Amount.Value") } },
            });

            const string month = "TRUNC(customrecord_deal.custrecord_closedate, 'MM')";
            const string status = "customrecord_deal.custrecord_status";
            //派生表の中は軸を TO_CHAR で包まない (外の式と日付同士で比べるため)
            var within =
                $"select {status} as g0, {NullFlag(status)} as z0, {StatusRank} as r0, sum(customrecord_deal.custrecord_amount) as m0, count(*) as n" +
                $" from customrecord_deal group by {status}, {NullFlag(status)}, {StatusRank}";
            Assert.That(_h.Fake.SuiteQlCalls.Single().Q, Is.EqualTo(
                $"select TO_CHAR({month}, 'YYYY-MM-DD HH24:MI:SS') as g0, {NullFlag(month)} as z0, {status} as g1, {NullFlag(status)} as z1, {StatusRank} as r1," +
                " sum(customrecord_deal.custrecord_amount) as m0, count(*) as n from customrecord_deal" +
                $" inner join ({within}) c_l_b_sel on ({status} = c_l_b_sel.g0 or ({status} is null and c_l_b_sel.g0 is null))" +
                $" group by {month}, {NullFlag(month)}, {status}, {NullFlag(status)}, {StatusRank} order by z0, g0, z1, r1, g1"));

            Assert.That(result.Rows.Select(e => (e.Key(0), e.Key(1), e.Value(0))), Is.EqualTo(new[]
            {
                ((object?)new DateOnly(2026, 3, 1), (object?)"won", (object?)1500m),
                (new DateOnly(2026, 4, 1), "open", 20m),
            }));
            Assert.That(result.Rows.Select(e => e.Keys[1].DisplayText), Is.EqualTo(new[] { "Won", "Open" }));
            Assert.That(result.TotalCount, Is.EqualTo(4));
        }

        [Test]
        public async Task WithinGroupsOfの上限は派生表の中のページング句としてSuiteQLに送らない()
        {
            //「合計の上位 2 件の得意先の行だけ」。本体は派生表の中に order by ... offset 0 rows fetch next 2 rows only を書く。
            //このコネクターは本体のページング句を SuiteQL に書けないもの (URL の limit / offset に移すもの) として扱っているので、
            //派生表の中のものも、そのまま送らず読み替えるか、送る前に拒否するのが正しい
            Respond(new() { Row(("g0", "3"), ("z0", "0"), ("d0", "Acme"), ("m0", "900"), ("n", "4")) });
            var condition = new AggregateCondition("Deal")
            {
                Groups = { new ValueGroup { Variable = "Customer.Value" } },
                Measures = { Measure(AggregateFunction.Sum, "Amount.Value") },
                WithinGroupsOf = new AggregateCondition("Deal")
                {
                    Groups = { new ValueGroup { Variable = "Customer.Value" } },
                    Measures = { Measure(AggregateFunction.Sum, "Amount.Value") },
                    SortConditions = { new AggregateSort { Target = AggregateSortTarget.Measure, Index = 0, IsDescending = true } },
                    LimitCount = 2,
                },
            };

            try
            {
                await AggregateAsync(condition);
            }
            catch (NetSuiteException)
            {
                //送る前に拒否するのでもよい
            }
            Assert.That(_h.Fake.SuiteQlCalls.Select(e => e.Q), Has.None.Contains("fetch next"));
        }

        // ---------------------------------------------------------------- 検索条件

        [Test]
        public async Task 検索条件つきは文字だけパラメータで数値と真偽と日付はリテラル()
        {
            Respond(new() { Row(("g0", "won"), ("z0", "0"), ("r0", "1"), ("m0", "2"), ("n", "2")) }, count: 2, groupCount: 1);
            var condition = new AggregateCondition("Deal")
            {
                Groups = { new ValueGroup { Variable = "Status.Value" } },
                Measures = { Measure(AggregateFunction.Count) },
                LimitCount = 10,
            };
            condition.Condition = MultiMatchCondition.And(
                new FieldValueMatchCondition { SearchTargetVariable = "Name.Value", Comparison = MatchComparison.Like, Value = new StringValue { Value = "a%b" } },
                new FieldValueMatchCondition { SearchTargetVariable = "Status.Value", Comparison = MatchComparison.Equal, Value = new StringValue { Value = "won" } },
                new FieldValueMatchCondition { SearchTargetVariable = "IsClosed.Value", Comparison = MatchComparison.Equal, Value = new BooleanValue { Value = true } },
                new FieldValueMatchCondition { SearchTargetVariable = "Amount.Value", Comparison = MatchComparison.GreaterThanOrEqual, Value = new DecimalValue { Value = 10.5m } },
                new FieldValueMatchCondition { SearchTargetVariable = "CloseDate.Value", Comparison = MatchComparison.LessThan, Value = new DateOnlyValue { Value = new DateOnly(2026, 4, 1) } },
                new FieldValueMatchCondition { SearchTargetVariable = "Created.Value", Comparison = MatchComparison.GreaterThanOrEqual, Value = new DateTimeValue { Value = new DateTime(2026, 1, 2, 3, 4, 5) } },
                //リンク越しの項目の条件は結合を入れる
                new FieldValueMatchCondition { SearchTargetVariable = "Customer.CompanyName.Value", Comparison = MatchComparison.Equal, Value = new StringValue { Value = "Acme" } });

            var result = await AggregateAsync(condition);

            const string where =
                " where (customrecord_deal.name like ? escape '\\') and (customrecord_deal.custrecord_status = ?) and (customrecord_deal.custrecord_closed = 'T')" +
                " and (customrecord_deal.custrecord_amount >= 10.5) and (customrecord_deal.custrecord_closedate < TO_DATE('2026-04-01', 'YYYY-MM-DD'))" +
                " and (customrecord_deal.created >= TO_DATE('2026-01-02 03:04:05', 'YYYY-MM-DD HH24:MI:SS')) and (customer_0.companyname = ?)";
            const string status = "customrecord_deal.custrecord_status";
            var groupBy = $" group by {status}, {NullFlag(status)}, {StatusRank}";
            var calls = _h.Fake.SuiteQlCalls;
            Assert.That(calls.Select(e => e.Q), Is.EqualTo(new[]
            {
                $"select {status} as g0, {NullFlag(status)} as z0, {StatusRank} as r0, count(*) as m0, count(*) as n from customrecord_deal {CustomerJoin}{where}{groupBy} order by z0, r0, g0",
                $"select count(*) as nsx0 from customrecord_deal {CustomerJoin}{where}",
                $"select count(*) as nsx0 from (select 1 as c_l_b_g from customrecord_deal {CustomerJoin}{where}{groupBy}) c_l_b_agg",
            }));
            //文字の値は ? の順に params へ (どの問い合わせにも)
            Assert.That(calls.Select(e => e.Params), Is.All.EqualTo(new[] { "%a\\%b%", "won", "Acme" }));
            Assert.That(result.Rows.Single().Key(0), Is.EqualTo("won"));
            Assert.That(result.TotalCount, Is.EqualTo(2));
            Assert.That(result.GroupCount, Is.EqualTo(1));
        }

        // ---------------------------------------------------------------- 読み替えの約束

        [Test]
        public async Task 集計のどの問い合わせにも並びのidは足されず引用符も残らない()
        {
            var conditions = new List<AggregateCondition>
            {
                new("Deal") { Measures = { Measure(AggregateFunction.Count) }, LimitCount = 5 },
                new("Deal") { Groups = { new ValueGroup { Variable = "Name.Value" } }, Measures = { Measure(AggregateFunction.Min, "Name.Value") }, LimitCount = 5 },
                new("Deal") { UtcOffsetMinutes = 540, Groups = { new DateGroup { Variable = "Created.Value", Bucket = DateBucket.Month } }, Measures = { Measure(AggregateFunction.Max, "CloseDate.Value") }, LimitCount = 5 },
                new("Deal") { Groups = { new ValueGroup { Variable = "Customer.Value" } }, Measures = { Measure(AggregateFunction.Max, "Created.Value") }, LimitCount = 5 },
            };
            foreach (var condition in conditions)
            {
                Respond(new());
                await AggregateAsync(condition);
            }

            var queries = _h.Fake.SuiteQlCalls.Select(e => e.Q).ToList();
            //上限つきなので、どれも集計の本体・行数・(軸があれば) グループ数
            Assert.That(queries, Has.Count.EqualTo(1 + 1 + 3 * 3));
            Assert.That(queries, Has.None.Contains("customrecord_deal.id"));
            Assert.That(queries, Has.None.Contains("\""));
            Assert.That(queries, Has.None.Contains("fetch").And.None.Contains(" offset "));
        }

        [Test]
        public void メタ情報のファイルに無いレコードの集計は要求を出さずに拒否する()
        {
            Assert.That(async () => await AggregateAsync(new AggregateCondition("Employee") { Measures = { Measure(AggregateFunction.Count) } }),
                Throws.InstanceOf<NetSuiteException>().With.Message.Contains("employee"));
            Assert.That(_h.Fake.SuiteQlCalls, Is.Empty);
            Assert.That(_h.Fake.Requests.Where(e => e.Path.Contains("employee")), Is.Empty);
        }
    }
}
