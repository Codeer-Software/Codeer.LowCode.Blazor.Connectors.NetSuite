using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Match;
using Codeer.LowCode.Blazor.SystemSettings;
using System.Net;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>本体の ModuleDataIO を通して、NetSuite への要求と戻りを確かめる。</summary>
    public class NetSuiteDbAccessorTest
    {
        NetSuiteHarness _h = null!;

        [SetUp]
        public void SetUp() => _h = new NetSuiteHarness();

        [TearDown]
        public void TearDown() => _h.Dispose();

        const string CustomerSelect =
            "select customer.id as customer_id, customer.companyname as customer_companyname, customer.isinactive as customer_isinactive," +
            " customer.creditlimit as customer_creditlimit, TO_CHAR(customer.startdate, 'YYYY-MM-DD') as customer_startdate," +
            " TO_CHAR(customer.lastmodifieddate, 'YYYY-MM-DD HH24:MI:SS') as customer_lastmodifieddate, subsidiary_0.name as subsidiary_0_name," +
            " customer.subsidiary as customer_subsidiary from customer left join subsidiary subsidiary_0 on customer.subsidiary = subsidiary_0.id";

        // ---------------------------------------------------------------- 一覧

        [Test]
        public async Task 条件と並びとページつきの一覧()
        {
            var condition = new SearchCondition("Customer") { LimitCount = 50 };
            condition.Condition = MultiMatchCondition.And(
                new FieldValueMatchCondition { SearchTargetVariable = "CompanyName.Value", Comparison = MatchComparison.Like, Value = new StringValue { Value = "ab%c" } },
                new FieldValueMatchCondition { SearchTargetVariable = "IsInactive.Value", Comparison = MatchComparison.Equal, Value = new BooleanValue { Value = false } },
                new FieldValueMatchCondition { SearchTargetVariable = "CreditLimit.Value", Comparison = MatchComparison.GreaterThanOrEqual, Value = new DecimalValue { Value = 10 } },
                new FieldValueMatchCondition { SearchTargetVariable = "StartDate.Value", Comparison = MatchComparison.LessThan, Value = new DateOnlyValue { Value = new DateOnly(2026, 1, 2) } });
            condition.SortConditions.Add(new SortCondition { Variable = "CompanyName.Value", IsDescending = true });

            //ページは 0 始まり。2 ページ目 = offset 100。値の無い列は SuiteQL の結果に出てこない
            _h.RespondRows(_rowsForPage(), count: 123);
            var result = await _h.IO.GetListAsync(condition, 2);

            const string where = " where (customer.companyname like ? escape '\\') and (customer.isinactive = 'F') and (customer.creditlimit >= 10)" +
                " and (customer.startdate < TO_DATE('2026-01-02', 'YYYY-MM-DD'))";
            var calls = _h.Fake.SuiteQlCalls;
            Assert.That(calls, Has.Count.EqualTo(2));

            //件数
            Assert.That(calls[0].Q, Is.EqualTo("select count(*) as nsx0 from customer left join subsidiary subsidiary_0 on customer.subsidiary = subsidiary_0.id" + where));
            Assert.That(calls[0].Params, Is.EqualTo(new[] { "%ab\\%c%" }));

            //一覧
            Assert.That(calls[1].Limit, Is.EqualTo(50));
            Assert.That(calls[1].Offset, Is.EqualTo(100));
            Assert.That(calls[1].Q, Is.EqualTo(CustomerSelect + where + " order by customer.companyname desc, customer.id"));
            Assert.That(calls[1].Params, Is.EqualTo(new[] { "%ab\\%c%" }));
            var request = _h.Fake.Requests.Where(e => e.IsSuiteQl).Last();
            Assert.That(request.Url, Is.EqualTo(FakeNetSuite.RestUrl + "/query/v1/suiteql?limit=50&offset=100"));

            Assert.That(result.TotalCount, Is.EqualTo(123));
            Assert.That(result.Items, Has.Count.EqualTo(2));

            var first = result.Items[0].Fields;
            Assert.That(((IdFieldData)first["Id"]).Value, Is.EqualTo("1"));
            Assert.That(((TextFieldData)first["CompanyName"]).Value, Is.EqualTo("Acme"));
            Assert.That(((BooleanFieldData)first["IsInactive"]).Value, Is.EqualTo(false));
            Assert.That(((NumberFieldData)first["CreditLimit"]).Value, Is.EqualTo(1234.5m));
            Assert.That(((DateFieldData)first["StartDate"]).Value, Is.EqualTo(new DateOnly(2026, 1, 1)));
            Assert.That(((DateTimeFieldData)first["LastModified"]).Value, Is.EqualTo(new DateTime(2026, 3, 4, 5, 6, 7)));
            var link = (LinkFieldData)first["Subsidiary"];
            Assert.That(link.Value, Is.EqualTo("3"));
            Assert.That(link.DisplayText, Is.EqualTo("Japan"));

            var second = result.Items[1].Fields;
            Assert.That(((IdFieldData)second["Id"]).Value, Is.EqualTo("2"));
            Assert.That(((BooleanFieldData)second["IsInactive"]).Value, Is.EqualTo(true));
            //値の無い列は本体がフィールドごと省く (あれば null)
            Assert.That(second.TryGetValue("CreditLimit", out var creditLimit) ? ((NumberFieldData)creditLimit).Value : null, Is.Null);
            Assert.That(second.TryGetValue("StartDate", out var startDate) ? ((DateFieldData)startDate).Value : null, Is.Null);
            Assert.That(second.TryGetValue("LastModified", out var lastModified) ? ((DateTimeFieldData)lastModified).Value : null, Is.Null);
            Assert.That(second.TryGetValue("Subsidiary", out var subsidiary) ? ((LinkFieldData)subsidiary).Value : null, Is.Null.Or.Empty);

            //NetSuite のデータソースは包んだ IDbAccessor に渡らない
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        //offset 100 から 2 行を返すための行 (前の 100 行は使われない)
        static List<Dictionary<string, string>> _rowsForPage()
        {
            var rows = Enumerable.Range(0, 100).Select(i => NetSuiteHarness.CustomerRow(1000 + i, "x")).ToList();
            rows.Add(new()
            {
                ["customer_id"] = "1", ["customer_companyname"] = "Acme", ["customer_isinactive"] = "F", ["customer_creditlimit"] = "1234.5",
                ["customer_startdate"] = "2026-01-01", ["customer_lastmodifieddate"] = "2026-03-04 05:06:07",
                ["subsidiary_0_name"] = "Japan", ["customer_subsidiary"] = "3",
            });
            rows.Add(new() { ["customer_id"] = "2", ["customer_companyname"] = "Beta", ["customer_isinactive"] = "T" });
            return rows;
        }

        [Test]
        public async Task 件数を絞らない一覧はhasMoreをたどる()
        {
            var rows = Enumerable.Range(0, 2500).Select(i => NetSuiteHarness.CustomerRow(i, $"c{i}")).ToList();
            _h.RespondRows(rows);

            var result = await _h.IO.GetListAsync(new SearchCondition("Customer"), 0);

            Assert.That(result.Items, Has.Count.EqualTo(2500));
            Assert.That(result.Items.Select(e => ((IdFieldData)e.Fields["Id"]).Value), Is.EqualTo(rows.Select(e => e["customer_id"])));
            var listCalls = _h.Fake.SuiteQlCalls.Where(e => !e.Q.StartsWith("select count(*)")).ToList();
            Assert.That(listCalls.Select(e => (e.Limit, e.Offset)), Is.EqualTo(new[] { (1000, 0), (1000, 1000), (1000, 2000) }));
            //ページングが無いので id を足さない
            Assert.That(listCalls[0].Q, Does.EndWith("order by customer.id desc"));
        }

        [Test]
        public async Task 件数を絞らない一覧を複数回に分けて読むときも並びの最後にidを足す()
        {
            //1000 行ずつ分けて読むので、並びが一意でないと要求の間で行が重複・欠落しうる
            var rows = Enumerable.Range(0, 1500).Select(i => NetSuiteHarness.CustomerRow(i, "same")).ToList();
            _h.RespondRows(rows);
            var condition = new SearchCondition("Customer");
            condition.SortConditions.Add(new SortCondition { Variable = "CompanyName.Value" });

            await _h.IO.GetListAsync(condition, 0);

            var listCalls = _h.Fake.SuiteQlCalls.Where(e => !e.Q.StartsWith("select count(*)")).ToList();
            Assert.That(listCalls, Has.Count.EqualTo(2));
            Assert.That(listCalls[0].Q, Does.EndWith("order by customer.companyname, customer.id"));
        }

        [Test]
        public void 件数を絞らない一覧がMaxRowsを超えたらエラー()
        {
            _h.Dispose();
            _h = new NetSuiteHarness(e => e.MaxRows = 1500);
            _h.RespondRows(Enumerable.Range(0, 2500).Select(i => NetSuiteHarness.CustomerRow(i, $"c{i}")).ToList());

            Assert.That(async () => await _h.IO.GetListAsync(new SearchCondition("Customer"), 0),
                Throws.InstanceOf<Exception>().With.Message.Contains("MaxRows"));
            //黙って切り捨てず、超えた時点で止める
            Assert.That(_h.Fake.SuiteQlCalls.Count(e => !e.Q.StartsWith("select count(*)")), Is.EqualTo(2));
        }

        [Test]
        public async Task LimitCountが1000を超えるときは1000行の区切りで読んで要る範囲を取り出す()
        {
            var rows = Enumerable.Range(0, 4000).Select(i => NetSuiteHarness.CustomerRow(i, $"c{i}")).ToList();
            _h.RespondRows(rows);

            var result = await _h.IO.GetListAsync(new SearchCondition("Customer") { LimitCount = 1500 }, 1);

            var listCalls = _h.Fake.SuiteQlCalls.Where(e => !e.Q.StartsWith("select count(*)")).ToList();
            Assert.That(listCalls.Select(e => (e.Limit, e.Offset)), Is.EqualTo(new[] { (1000, 1000), (1000, 2000) }));
            //どの要求も offset が limit の倍数
            Assert.That(listCalls.All(e => e.Offset % e.Limit == 0), Is.True);
            Assert.That(result.Items, Has.Count.EqualTo(1500));
            Assert.That(((IdFieldData)result.Items[0].Fields["Id"]).Value, Is.EqualTo("1500"));
            Assert.That(((IdFieldData)result.Items[^1].Fields["Id"]).Value, Is.EqualTo("2999"));
            Assert.That(result.TotalCount, Is.EqualTo(4000));
        }

        [Test]
        public async Task 最後のページは行が尽きたところで止まる()
        {
            var rows = Enumerable.Range(0, 1700).Select(i => NetSuiteHarness.CustomerRow(i, $"c{i}")).ToList();
            _h.RespondRows(rows);

            var result = await _h.IO.GetListAsync(new SearchCondition("Customer") { LimitCount = 2000 }, 0);

            var listCalls = _h.Fake.SuiteQlCalls.Where(e => !e.Q.StartsWith("select count(*)")).ToList();
            Assert.That(listCalls.Select(e => (e.Limit, e.Offset)), Is.EqualTo(new[] { (1000, 0), (1000, 1000) }));
            Assert.That(result.Items, Has.Count.EqualTo(1700));
        }

        // ---------------------------------------------------------------- 追加

        [Test]
        public async Task 追加はPOSTでREST名と値の形にして採番Idを返す()
        {
            _h.Fake.CreatedId = "456";
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Customer" };
            add.Fields["Id"] = tempId;
            add.Fields["CompanyName"] = new TextFieldData { Value = "New" };
            add.Fields["IsInactive"] = new BooleanFieldData { Value = true };
            add.Fields["CreditLimit"] = new NumberFieldData { Value = 12.5m };
            add.Fields["StartDate"] = new DateFieldData { Value = new DateOnly(2026, 3, 4) };
            add.Fields["Subsidiary"] = new LinkFieldData { Value = "3" };

            var results = await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Customer", Id = tempId.Value!, Add = new() { add } } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            Assert.That(results.Single().DestinationId, Is.EqualTo("456"));

            var post = _h.Fake.WriteRequests.Single();
            Assert.That(post.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(post.Url, Is.EqualTo(FakeNetSuite.RestUrl + "/record/v1/customer"));
            var body = post.Json!.AsObject();
            Assert.That(body.Select(e => e.Key), Is.EquivalentTo(new[] { "companyName", "isInactive", "creditLimit", "startDate", "subsidiary" }));
            Assert.That(body["companyName"]!.GetValue<string>(), Is.EqualTo("New"));
            Assert.That(body["isInactive"]!.GetValue<bool>(), Is.True);
            Assert.That(body["creditLimit"]!.GetValue<decimal>(), Is.EqualTo(12.5m));
            Assert.That(body["startDate"]!.GetValue<string>(), Is.EqualTo("2026-03-04"));
            Assert.That(body["subsidiary"]!.ToJsonString(), Is.EqualTo("""{"id":"3"}"""));
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 追加の日時はUTCのZつきで送る()
        {
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Customer" };
            add.Fields["Id"] = tempId;
            add.Fields["CompanyName"] = new TextFieldData { Value = "New" };
            add.Fields["LastModified"] = new DateTimeFieldData { Value = new DateTime(2026, 3, 4, 5, 6, 7) };

            var results = await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Customer", Id = tempId.Value!, Add = new() { add } } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            var body = _h.Fake.WriteRequests.Single().Json!.AsObject();
            Assert.That(body["lastModifiedDate"]!.GetValue<string>(), Is.EqualTo("2026-03-04T05:06:07Z"));
        }

        [Test]
        public async Task 追加の失敗は結果のExceptionMessageになる()
        {
            _h.Fake.Override(e => e.IsRecordWrite, _ => FakeNetSuite.ErrorDetails(HttpStatusCode.BadRequest, ("Please enter a value for Subsidiary.", "USER_ERROR", "subsidiary")));
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Customer" };
            add.Fields["Id"] = tempId;
            add.Fields["CompanyName"] = new TextFieldData { Value = "New" };

            var results = await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Customer", Id = tempId.Value!, Add = new() { add } } });
            Assert.That(results.Single().ExceptionMessage, Does.Contain("Please enter a value for Subsidiary."));
        }

        [TestCase(HttpStatusCode.InternalServerError)]
        [TestCase(HttpStatusCode.BadGateway)]
        [TestCase(HttpStatusCode.GatewayTimeout)]
        public async Task 追加への5xxは結果不明としてExceptionMessageになり送り直さない(HttpStatusCode code)
        {
            _h.Fake.Override(e => e.IsRecordWrite, _ => FakeNetSuite.Status(code), times: 100);
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Customer" };
            add.Fields["Id"] = tempId;
            add.Fields["CompanyName"] = new TextFieldData { Value = "New" };

            var results = await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Customer", Id = tempId.Value!, Add = new() { add } } });

            Assert.That(results.Single().ExceptionMessage, Does.Contain("Whether the write was processed is unknown"));
            Assert.That(_h.Fake.WriteRequests, Has.Count.EqualTo(1));
        }

        // ---------------------------------------------------------------- 更新・削除

        [Test]
        public async Task 更新はPATCHで変えた項目だけを送りnullはJSONのnull()
        {
            var update = new ModuleData { Name = "Customer" };
            update.Fields["Id"] = new IdFieldData { Value = "5" };
            update.Fields["CompanyName"] = new TextFieldData { Value = null };
            update.Fields["CreditLimit"] = new NumberFieldData { Value = 0 };

            var results = await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Customer", Id = "5", Update = new() { update } } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            Assert.That(results.Single().DestinationId, Is.EqualTo("5"));
            var patch = _h.Fake.WriteRequests.Single();
            Assert.That(patch.Method, Is.EqualTo(HttpMethod.Patch));
            Assert.That(patch.Url, Is.EqualTo(FakeNetSuite.RestUrl + "/record/v1/customer/5"));
            Assert.That(patch.Body, Is.EqualTo("""{"companyName":null,"creditLimit":0}"""));
            //Id だけの条件なので事前の確認はしない
            Assert.That(_h.Fake.SuiteQlCalls, Is.Empty);
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 削除はDELETE()
        {
            var results = await _h.IO.SubmitWithTransactionAsync(new()
            {
                new ModuleSubmitData { ModuleName = "Customer", Delete = new() { new ModuleDeleteInfo { ModuleName = "Customer", Id = "5" } } },
            });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            var delete = _h.Fake.WriteRequests.Single();
            Assert.That(delete.Method, Is.EqualTo(HttpMethod.Delete));
            Assert.That(delete.Url, Is.EqualTo(FakeNetSuite.RestUrl + "/record/v1/customer/5"));
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 無いレコードの更新は結果のExceptionMessageになる()
        {
            _h.Fake.Override(e => e.IsRecordWrite, _ => FakeNetSuite.ErrorDetails(HttpStatusCode.NotFound, ("That record does not exist.", "NONEXISTENT_ID", "")));
            var update = new ModuleData { Name = "Customer" };
            update.Fields["Id"] = new IdFieldData { Value = "999" };
            update.Fields["CompanyName"] = new TextFieldData { Value = "X" };

            var results = await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "Customer", Id = "999", Update = new() { update } } });

            Assert.That(results.Single().ExceptionMessage, Is.Not.Empty);
            Assert.That(_h.Fake.WriteRequests, Has.Count.EqualTo(1));
        }

        static ModuleData LockedUpdate()
        {
            var update = new ModuleData { Name = "CustomerLocked" };
            update.Fields["Id"] = new IdFieldData { Value = "5" };
            update.Fields["CompanyName"] = new TextFieldData { Value = "X" };
            update.Fields[SystemFieldNames.OptimisticLocking] = new OptimisticLockingFieldData { Value = MultiTypeValue.Create(new DateTime(2026, 3, 4, 5, 6, 7)) };
            return update;
        }

        [Test]
        public async Task 楽観ロックつきの更新は先に件数を確かめ0件ならPATCHしない()
        {
            //版の条件つきは 0 件、Id だけなら 1 件 (= 別の更新が入った)
            _h.Fake.SuiteQl = call => new SuiteQlResult { Items = new() { new() { ["nsx0"] = call.Q.Contains("lastmodifieddate") ? "0" : "1" } } };

            var results = await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "CustomerLocked", Id = "5", Update = new() { LockedUpdate() } } });

            Assert.That(results.Single().ExceptionMessage, Is.Not.Empty);
            Assert.That(_h.Fake.WriteRequests, Is.Empty);
            var check = _h.Fake.SuiteQlCalls[0];
            Assert.That(check.Q, Is.EqualTo("select count(*) as nsx0 from customer where id = ? and lastmodifieddate = TO_DATE('2026-03-04 05:06:07', 'YYYY-MM-DD HH24:MI:SS')"));
            Assert.That(check.Params, Is.EqualTo(new[] { "5" }));
        }

        [Test]
        public async Task 楽観ロックつきの更新は件数が1ならPATCHする()
        {
            _h.Fake.SuiteQl = _ => new SuiteQlResult { Items = new() { new() { ["nsx0"] = "1" } } };

            var results = await _h.IO.SubmitWithTransactionAsync(new() { new ModuleSubmitData { ModuleName = "CustomerLocked", Id = "5", Update = new() { LockedUpdate() } } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            Assert.That(_h.Fake.SuiteQlCalls, Has.Count.EqualTo(1));
            var patch = _h.Fake.WriteRequests.Single();
            Assert.That(patch.Method, Is.EqualTo(HttpMethod.Patch));
            Assert.That(patch.Body, Is.EqualTo("""{"companyName":"X"}"""));
            //確かめる読み出しが PATCH より先
            Assert.That(_h.Fake.ApiRequests.Where(e => !e.IsMetadata).Select(e => e.IsSuiteQl), Is.EqualTo(new[] { true, false }));
        }

        // ---------------------------------------------------------------- 振り分け

        [Test]
        public async Task NetSuite以外のデータソースは包んだIDbAccessorに渡る()
        {
            var a = _h.Accessor;
            Assert.That(a.GetDataSource("Main")!.DataSourceType, Is.EqualTo(DataSourceType.SQLite));
            Assert.That((await a.QueryAsync("Main", "select 1", new()))[0]["inner"], Is.EqualTo("row"));
            Assert.That(await a.ExecuteAsync("Main", "update x set a=1", new()), Is.EqualTo(7));
            Assert.That(await a.InsertAsync("Main", "insert into x(a) values(1)", new()), Is.EqualTo("inner-id"));
            Assert.Throws<NotSupportedException>(() => a.GetConnection("Main"));
            Assert.That(a.GetTransaction("Main"), Is.Null);
            Assert.That(a.ConvertFieldValueToDbValue("Main", "X", 1), Is.EqualTo("inner-converted"));
            Assert.That(a.ConvertDbValueToFieldValue("Main", 1, typeof(int)), Is.EqualTo("inner-converted"));
            Assert.That((await a.ExecuteSqlCommandAsync("Main", new DbSqlCommand { CommandText = "x" })).ReturnValue, Is.EqualTo(1));
            Assert.That((await a.GetCustomTableDefinitionsAsync("Main"))!.Single().Name, Is.EqualTo("inner_table"));
            Assert.That((await a.GetCustomTableDefinitionsAsync("Main", "t"))!.Name, Is.EqualTo("t"));

            Assert.That(_h.Inner.DataSourceCalls, Is.EqualTo(new[]
            {
                "GetDataSource:Main", "QueryAsync:Main", "ExecuteAsync:Main", "InsertAsync:Main", "GetConnection:Main", "GetTransaction:Main",
                "ConvertFieldValueToDbValue:Main", "ConvertDbValueToFieldValue:Main", "ExecuteSqlCommandAsync:Main",
                "GetCustomTableDefinitionsAsync:Main", "GetCustomTableDefinitionsAsync1:Main",
            }));
            Assert.That(_h.Fake.Requests, Is.Empty);
        }

        [Test]
        public async Task NetSuiteのデータソースは包んだIDbAccessorに渡らない()
        {
            var a = _h.Accessor;
            _h.RespondRows(new() { NetSuiteHarness.CustomerRow(1, "A") });
            await a.QueryAsync("NS", "select \"customer\".\"id\" as \"customer_id\" from \"customer\"", new());
            Assert.That(a.GetTransaction("NS"), Is.Null);
            Assert.That(a.ConvertFieldValueToDbValue("NS", "VARCHAR2", "v"), Is.EqualTo("v"));
            Assert.That(a.ConvertDbValueToFieldValue("NS", "v", typeof(string)), Is.Null);
            Assert.Throws<NetSuiteException>(() => a.GetConnection("NS"));
            Assert.Throws<NetSuiteException>(() => a.ExecuteSqlCommandAsync("NS", new DbSqlCommand { CommandText = "x" }));
            await a.GetCustomTableDefinitionsAsync("NS", "customer");
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public void GetDataSourceはOracleで接続文字列を返さない()
        {
            var dataSource = _h.Accessor.GetDataSource("NS")!;
            Assert.That(dataSource.Name, Is.EqualTo("NS"));
            Assert.That(dataSource.DataSourceType, Is.EqualTo(DataSourceType.Oracle));
            Assert.That(dataSource.ConnectionString, Is.Empty);
        }

        [Test]
        public async Task GetCustomTableDefinitionsAsyncはRecordsの全レコードを返す()
        {
            var tables = (await _h.Accessor.GetCustomTableDefinitionsAsync("NS"))!;
            Assert.That(tables.Select(e => e.Name), Is.EqualTo(new[] { "customer", "subsidiary" }));
            Assert.That(tables[0].Columns.Select(e => e.Name), Does.Contain("companyname"));
            Assert.That(tables[1].Columns.Select(e => e.Name), Is.EqualTo(new[] { "id", "name" }));
        }

        [Test]
        public void GetConnectionは例外()
        {
            Assert.Throws<NetSuiteException>(() => _h.Accessor.GetConnection("NS"));
        }

        [Test]
        public void SQLの直接実行は例外()
        {
            Assert.Throws<NetSuiteException>(() => _h.Accessor.ExecuteSqlCommandAsync("NS", new DbSqlCommand { CommandText = "select 1 from dual" }));
        }
    }
}
