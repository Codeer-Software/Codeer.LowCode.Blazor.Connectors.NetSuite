using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    public class SuiteQlTranslatorTest
    {
        static readonly Dictionary<string, NetSuiteRecordDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["customer"] = Definition("customer", FakeNetSuite.CustomerSchema),
            ["subsidiary"] = Definition("subsidiary", FakeNetSuite.SubsidiarySchema),
        };

        static NetSuiteRecordDefinition Definition(string name, string schema)
        {
            using var json = JsonDocument.Parse(schema);
            return NetSuiteRecordDefinition.FromJsonSchema(name, json.RootElement);
        }

        static Task<SuiteQlSelect> TranslateAsync(string sql, Dictionary<string, object?>? args = null)
            => SuiteQlTranslator.TranslateSelectAsync(sql, args ?? new(), e => _definitions.ContainsKey(e), e => Task.FromResult(_definitions[e]));

        // ---------------------------------------------------------------- 読み出し

        [Test]
        public async Task 引用符が外れる()
        {
            var select = await TranslateAsync("select \"customer\".\"id\" as \"customer_id\",\n\"customer\".\"companyname\" as \"customer_companyname\"\nfrom \"customer\"");
            Assert.That(select.Sql, Is.EqualTo("select customer.id as customer_id, customer.companyname as customer_companyname from customer order by customer.id"));
            Assert.That(select.Limit, Is.Null);
            Assert.That(select.Offset, Is.EqualTo(0));
            Assert.That(select.Columns.Select(e => (e.Key, e.SourceName)), Is.EqualTo(new[] { ("customer_id", "customer_id"), ("customer_companyname", "customer_companyname") }));
        }

        [Test]
        public async Task 文字のパラメータは疑問符になり出現順に入る()
        {
            var select = await TranslateAsync(
                "select \"customer\".\"id\" as \"a\" from \"customer\" where (\"customer\".\"companyname\" = :p0 or \"customer\".\"companyname\" = :p0) and \"customer\".\"id\" = :p1",
                new() { [":p0"] = "O'Neil", [":p1"] = "5" });
            Assert.That(select.Sql, Is.EqualTo("select customer.id as a from customer where (customer.companyname = ? or customer.companyname = ?) and customer.id = ? order by customer.id"));
            Assert.That(select.Parameters, Is.EqualTo(new[] { "O'Neil", "O'Neil", "5" }));
        }

        [Test]
        public async Task 真偽と数値と日付はリテラルになる()
        {
            var select = await TranslateAsync(
                "select \"customer\".\"id\" as \"a\" from \"customer\" where \"customer\".\"isinactive\" = :p0 and \"customer\".\"isinactive\" <> :p1 and \"customer\".\"creditlimit\" >= :p2" +
                " and \"customer\".\"creditlimit\" < :p3 and \"customer\".\"startdate\" < :p4 and \"customer\".\"lastmodifieddate\" >= :p5 and \"customer\".\"companyname\" = :p6",
                new()
                {
                    [":p0"] = false,
                    [":p1"] = true,
                    [":p2"] = 10.5m,
                    [":p3"] = 3,
                    [":p4"] = new DateOnly(2026, 1, 2),
                    [":p5"] = new DateTime(2026, 1, 2, 3, 4, 5),
                    [":p6"] = null,
                });
            Assert.That(select.Sql, Is.EqualTo(
                "select customer.id as a from customer where customer.isinactive = 'F' and customer.isinactive <> 'T' and customer.creditlimit >= 10.5" +
                " and customer.creditlimit < 3 and customer.startdate < TO_DATE('2026-01-02', 'YYYY-MM-DD')" +
                " and customer.lastmodifieddate >= TO_DATE('2026-01-02 03:04:05', 'YYYY-MM-DD HH24:MI:SS') and customer.companyname = NULL order by customer.id"));
            Assert.That(select.Parameters, Is.Empty);
        }

        [Test]
        public async Task ページング句が外れてLimitとOffsetになる()
        {
            var select = await TranslateAsync(
                "select \"customer\".\"id\" as \"customer_id\"\r\nfrom \"customer\"\r\n\r\n\r\norder by \"customer\".\"companyname\" desc\r\noffset 100 rows fetch next 50 rows only");
            Assert.That(select.Limit, Is.EqualTo(50));
            Assert.That(select.Offset, Is.EqualTo(100));
            Assert.That(select.Sql, Does.Not.Contain("offset").And.Not.Contain("fetch"));
            //ページングつきなので並びの最後に主表の id が足される
            Assert.That(select.Sql, Is.EqualTo("select customer.id as customer_id from customer order by customer.companyname desc, customer.id"));
        }

        [Test]
        public async Task 日付と日時の列はTO_CHARで包まれる()
        {
            var select = await TranslateAsync(
                "select \"customer\".\"startdate\" as \"customer_startdate\", \"customer\".\"lastmodifieddate\" as \"customer_lastmodifieddate\", \"customer\".\"creditlimit\" as \"c\" from \"customer\"");
            Assert.That(select.Sql, Is.EqualTo(
                "select TO_CHAR(customer.startdate, 'YYYY-MM-DD') as customer_startdate, TO_CHAR(customer.lastmodifieddate, 'YYYY-MM-DD HH24:MI:SS') as customer_lastmodifieddate," +
                " customer.creditlimit as c from customer order by customer.id"));
            Assert.That(select.Columns.Select(e => e.Kind), Is.EqualTo(new NetSuiteFieldKind?[] { NetSuiteFieldKind.Date, NetSuiteFieldKind.DateTime, NetSuiteFieldKind.Number }));
        }

        [Test]
        public async Task 結合した表の列も種類が分かる()
        {
            var select = await TranslateAsync(
                "select \"customer\".\"subsidiary\" as \"customer_subsidiary\", \"subsidiary_0\".\"name\" as \"subsidiary_0_name\" from \"customer\" left join \"subsidiary\" \"subsidiary_0\" on \"customer\".\"subsidiary\"=\"subsidiary_0\".\"id\"");
            Assert.That(select.Sql, Is.EqualTo(
                "select customer.subsidiary as customer_subsidiary, subsidiary_0.name as subsidiary_0_name from customer left join subsidiary subsidiary_0 on customer.subsidiary = subsidiary_0.id order by customer.id"));
            Assert.That(select.Columns.Select(e => e.Kind), Is.EqualTo(new NetSuiteFieldKind?[] { NetSuiteFieldKind.Reference, NetSuiteFieldKind.String }));
        }

        [Test]
        public async Task 並びに既にidがあれば足さない()
        {
            var select = await TranslateAsync("select \"customer\".\"id\" as \"a\" from \"customer\" order by \"customer\".\"id\" desc offset 0 rows fetch next 10 rows only");
            Assert.That(select.Sql, Is.EqualTo("select customer.id as a from customer order by customer.id desc"));
        }

        [Test]
        public async Task 並びが無ければorder_byを作ってidを足す()
        {
            var select = await TranslateAsync("select \"customer\".\"id\" as \"a\" from \"customer\" where \"customer\".\"isinactive\" = :p0 offset 20 rows fetch next 10 rows only",
                new() { [":p0"] = true });
            Assert.That(select.Sql, Is.EqualTo("select customer.id as a from customer where customer.isinactive = 'T' order by customer.id"));
            Assert.That(select.Limit, Is.EqualTo(10));
            Assert.That(select.Offset, Is.EqualTo(20));
        }

        [Test]
        public async Task group_byがあればidを足さない()
        {
            var select = await TranslateAsync(
                "select \"customer\".\"isinactive\" as \"k\", count(*) as \"c\" from \"customer\" group by \"customer\".\"isinactive\" order by \"customer\".\"isinactive\" offset 0 rows fetch next 10 rows only");
            Assert.That(select.Sql, Is.EqualTo("select customer.isinactive as k, count(*) as c from customer group by customer.isinactive order by customer.isinactive"));
        }

        [Test]
        public async Task ページングが無くてもidを足す()
        {
            //件数を絞らない読み出しも 1000 行ずつ分けて読むので、順序を固定する
            var select = await TranslateAsync("select \"customer\".\"id\" as \"a\" from \"customer\" order by \"customer\".\"companyname\"");
            Assert.That(select.Sql, Is.EqualTo("select customer.id as a from customer order by customer.companyname, customer.id"));
        }

        [Test]
        public async Task 件数の問い合わせにはidを足さない()
        {
            var select = await TranslateAsync("select count(*) from \"customer\" where \"customer\".\"isinactive\" = :p0", new() { [":p0"] = false });
            Assert.That(select.Sql, Is.EqualTo("select count(*) as nsx0 from customer where customer.isinactive = 'F'"));
        }

        [Test]
        public async Task LIKEのエスケープ文字が逆スラッシュになりパターン中の逆スラッシュは二重になる()
        {
            //本体のエスケープ文字は (char)1。利用者の入力 "a%b\c" は "%a<esc>%b\c%" で来る
            var esc = (char)1;
            var select = await TranslateAsync(
                "select \"customer\".\"id\" as \"a\" from \"customer\" where (\"customer\".\"companyname\" like :p0 escape :pescape)",
                new() { [":p0"] = $"%a{esc}%b\\c%", [":pescape"] = esc });
            Assert.That(select.Sql, Is.EqualTo("select customer.id as a from customer where (customer.companyname like ? escape '\\') order by customer.id"));
            Assert.That(select.Parameters, Is.EqualTo(new[] { "%a\\%b\\\\c%" }));
        }

        [Test]
        public void Recordsに無い表は例外()
        {
            Assert.ThrowsAsync<NetSuiteException>(() => TranslateAsync("select \"employee\".\"id\" as \"a\" from \"employee\""));
            Assert.ThrowsAsync<NetSuiteException>(() => TranslateAsync(
                "select \"customer\".\"id\" as \"a\" from \"customer\" left join \"employee\" \"employee_0\" on \"customer\".\"id\"=\"employee_0\".\"id\""));
        }

        [Test]
        public void 副問い合わせの中のRecordsに無い表も例外()
        {
            Assert.ThrowsAsync<NetSuiteException>(() => TranslateAsync(
                "select \"customer\".\"id\" as \"a\" from \"customer\" where \"customer\".\"id\" in (select \"employee\".\"id\" from \"employee\")"));
            Assert.ThrowsAsync<NetSuiteException>(() => TranslateAsync(
                "select \"customer\".\"id\" as \"a\" from \"customer\" where exists (select 1 from \"employee\" where \"employee\".\"id\" = \"customer\".\"id\")"));
        }

        [Test]
        public void セミコロンを含むSQLは例外()
        {
            Assert.ThrowsAsync<NetSuiteException>(() => TranslateAsync("select \"customer\".\"id\" as \"a\" from \"customer\"; delete from \"customer\""));
        }

        [Test]
        public async Task 別名の無いcountのキーは式そのもの()
        {
            var select = await TranslateAsync("select count(*) from \"customer\" where \"customer\".\"isinactive\" = :p0", new() { [":p0"] = false });
            Assert.That(select.Sql, Is.EqualTo("select count(*) as nsx0 from customer where customer.isinactive = 'F'"));
            Assert.That(select.Columns.Single().Key, Is.EqualTo("count(*)"));
            Assert.That(select.Columns.Single().SourceName, Is.EqualTo("nsx0"));
            Assert.That(select.Columns.Single().IsNumericExpression, Is.True);
        }

        [Test]
        public void 無い列は例外()
        {
            Assert.ThrowsAsync<NetSuiteException>(() => TranslateAsync("select \"customer\".\"nosuchcolumn\" as \"a\" from \"customer\""));
        }

        [Test]
        public void パラメータの値が無ければ例外()
        {
            Assert.ThrowsAsync<NetSuiteException>(() => TranslateAsync("select \"customer\".\"id\" as \"a\" from \"customer\" where \"customer\".\"id\" = :p0"));
        }

        // ---------------------------------------------------------------- 集計 (本体の AggregateSqlCreator が Oracle 向けに組む形)

        [Test]
        public async Task 集計_軸と値と上限()
        {
            var select = await TranslateAsync(
                "select \"customer\".\"companyname\" as \"g0\",\r\ncase when \"customer\".\"companyname\" is null then 1 else 0 end as \"z0\",\r\ncount(*) as \"m0\",\r\n" +
                "sum(\"customer\".\"creditlimit\") as \"m1\",\r\ncount(*) as \"n\"\r\nfrom \"customer\"\r\n\r\nwhere (\"customer\".\"isinactive\" = :p0)\r\n" +
                "group by \"customer\".\"companyname\", case when \"customer\".\"companyname\" is null then 1 else 0 end\r\nhaving sum(\"customer\".\"creditlimit\") > 100.5\r\n" +
                "order by \"m1\" desc, \"z0\", \"g0\"\r\noffset 0 rows fetch next 10 rows only",
                new() { [":p0"] = false });
            Assert.That(select.Sql, Is.EqualTo(
                "select customer.companyname as g0, case when customer.companyname is null then 1 else 0 end as z0, count(*) as m0, sum(customer.creditlimit) as m1, count(*) as n" +
                " from customer where (customer.isinactive = 'F') group by customer.companyname, case when customer.companyname is null then 1 else 0 end" +
                " having sum(customer.creditlimit) > 100.5 order by m1 desc, z0, g0"));
            Assert.That(select.Limit, Is.EqualTo(10));
            Assert.That(select.Offset, Is.EqualTo(0));
            Assert.That(select.Columns.Select(e => (e.Key, e.Kind, e.IsNumericExpression)), Is.EqualTo(new (string, NetSuiteFieldKind?, bool)[]
            {
                ("g0", NetSuiteFieldKind.String, false),
                //case 式は種類なし (文字のまま返す)
                ("z0", null, false),
                ("m0", null, true),
                ("m1", null, true),
                ("n", null, true),
            }));
        }

        [Test]
        public async Task 集計_日付の丸めは日時として受け取る()
        {
            var select = await TranslateAsync(
                "select TRUNC(\"customer\".\"startdate\", 'MM') as \"g0\", ADD_MONTHS(TRUNC(ADD_MONTHS(\"customer\".\"startdate\", -3), 'YYYY'), 3) as \"g1\"," +
                " TRUNC((\"customer\".\"lastmodifieddate\" + NUMTODSINTERVAL(540, 'MINUTE')), 'HH') as \"g2\", count(*) as \"n\" from \"customer\"" +
                " group by TRUNC(\"customer\".\"startdate\", 'MM'), ADD_MONTHS(TRUNC(ADD_MONTHS(\"customer\".\"startdate\", -3), 'YYYY'), 3)," +
                " TRUNC((\"customer\".\"lastmodifieddate\" + NUMTODSINTERVAL(540, 'MINUTE')), 'HH')");
            Assert.That(select.Sql, Is.EqualTo(
                "select TO_CHAR(TRUNC(customer.startdate, 'MM'), 'YYYY-MM-DD HH24:MI:SS') as g0," +
                " TO_CHAR(ADD_MONTHS(TRUNC(ADD_MONTHS(customer.startdate, - 3), 'YYYY'), 3), 'YYYY-MM-DD HH24:MI:SS') as g1," +
                " TO_CHAR(TRUNC((customer.lastmodifieddate + NUMTODSINTERVAL(540, 'MINUTE')), 'HH'), 'YYYY-MM-DD HH24:MI:SS') as g2, count(*) as n from customer" +
                //group by の式は丸めたまま (TO_CHAR で包むのは select の項目だけ)
                " group by TRUNC(customer.startdate, 'MM'), ADD_MONTHS(TRUNC(ADD_MONTHS(customer.startdate, - 3), 'YYYY'), 3)," +
                " TRUNC((customer.lastmodifieddate + NUMTODSINTERVAL(540, 'MINUTE')), 'HH')"));
            Assert.That(select.Columns.Take(3).Select(e => e.Kind), Is.All.EqualTo(NetSuiteFieldKind.DateTime));
        }

        [Test]
        public async Task 集計_括弧で始まる日付の式も日時として受け取る()
        {
            var select = await TranslateAsync("select (\"customer\".\"lastmodifieddate\" + NUMTODSINTERVAL(60, 'MINUTE')) as \"g0\", count(*) as \"n\" from \"customer\" group by (\"customer\".\"lastmodifieddate\" + NUMTODSINTERVAL(60, 'MINUTE'))");
            Assert.That(select.Sql, Does.StartWith("select TO_CHAR((customer.lastmodifieddate + NUMTODSINTERVAL(60, 'MINUTE')), 'YYYY-MM-DD HH24:MI:SS') as g0,"));
            Assert.That(select.Columns[0].Kind, Is.EqualTo(NetSuiteFieldKind.DateTime));
        }

        [Test]
        public async Task 集計_最小と最大は中の列の種類()
        {
            var select = await TranslateAsync(
                "select min(\"customer\".\"startdate\") as \"m0\", max(\"customer\".\"lastmodifieddate\") as \"m1\", min(\"customer\".\"creditlimit\") as \"m2\"," +
                " max(\"customer\".\"companyname\") as \"m3\", min(\"subsidiary_0\".\"name\") as \"d0\", max(\"customer\".\"isinactive\") as \"m4\", count(*) as \"n\"" +
                " from \"customer\" left join \"subsidiary\" \"subsidiary_0\" on \"customer\".\"subsidiary\"=\"subsidiary_0\".\"id\"");
            Assert.That(select.Sql, Is.EqualTo(
                "select TO_CHAR(min(customer.startdate), 'YYYY-MM-DD') as m0, TO_CHAR(max(customer.lastmodifieddate), 'YYYY-MM-DD HH24:MI:SS') as m1," +
                " min(customer.creditlimit) as m2, max(customer.companyname) as m3, min(subsidiary_0.name) as d0, max(customer.isinactive) as m4, count(*) as n" +
                " from customer left join subsidiary subsidiary_0 on customer.subsidiary = subsidiary_0.id"));
            Assert.That(select.Columns.Select(e => e.Kind), Is.EqualTo(new NetSuiteFieldKind?[]
            {
                NetSuiteFieldKind.Date, NetSuiteFieldKind.DateTime, NetSuiteFieldKind.Number, NetSuiteFieldKind.String, NetSuiteFieldKind.String, NetSuiteFieldKind.Boolean, null,
            }));
            Assert.That(select.Columns.Take(6).Select(e => e.IsNumericExpression), Is.All.False);
        }

        [Test]
        public async Task 集計_丸めた日付の最小は日時()
        {
            var select = await TranslateAsync("select min(TRUNC(\"customer\".\"startdate\", 'MM')) as \"m0\", count(*) as \"n\" from \"customer\"");
            Assert.That(select.Sql, Is.EqualTo("select TO_CHAR(min(TRUNC(customer.startdate, 'MM')), 'YYYY-MM-DD HH24:MI:SS') as m0, count(*) as n from customer"));
            Assert.That(select.Columns[0].Kind, Is.EqualTo(NetSuiteFieldKind.DateTime));
        }

        [Test]
        public async Task 集計_重複なしの件数と候補の順は日付扱いしない()
        {
            var select = await TranslateAsync(
                "select count(distinct \"customer\".\"subsidiary\") as \"m0\", case \"customer\".\"companyname\" when 'A' then 0 when 'B''s' then 1 else 2 end as \"r0\", count(*) as \"n\" from \"customer\"" +
                " group by case \"customer\".\"companyname\" when 'A' then 0 when 'B''s' then 1 else 2 end");
            Assert.That(select.Sql, Is.EqualTo(
                "select count(distinct customer.subsidiary) as m0, case customer.companyname when 'A' then 0 when 'B''s' then 1 else 2 end as r0, count(*) as n from customer" +
                " group by case customer.companyname when 'A' then 0 when 'B''s' then 1 else 2 end"));
            Assert.That(select.Columns.Select(e => (e.Kind, e.IsNumericExpression)), Is.EqualTo(new (NetSuiteFieldKind?, bool)[] { (null, true), (null, false), (null, true) }));
            Assert.That(select.Parameters, Is.Empty);
        }

        [Test]
        public async Task 集計_グループ数の数え直し()
        {
            var select = await TranslateAsync(
                "select count(*) from (select 1 as c_l_b_g from \"customer\" left join \"subsidiary\" \"subsidiary_0\" on \"customer\".\"subsidiary\"=\"subsidiary_0\".\"id\"" +
                " where (\"subsidiary_0\".\"name\" = :p0) group by \"customer\".\"subsidiary\", case when \"customer\".\"subsidiary\" is null then 1 else 0 end having count(*) > 1) c_l_b_agg",
                new() { [":p0"] = "Japan" });
            Assert.That(select.Sql, Is.EqualTo(
                "select count(*) as nsx0 from (select 1 as c_l_b_g from customer left join subsidiary subsidiary_0 on customer.subsidiary = subsidiary_0.id" +
                " where (subsidiary_0.name = ?) group by customer.subsidiary, case when customer.subsidiary is null then 1 else 0 end having count(*) > 1) c_l_b_agg"));
            Assert.That(select.Parameters, Is.EqualTo(new[] { "Japan" }));
            Assert.That(select.Limit, Is.Null);
            Assert.That(select.Columns.Single().Key, Is.EqualTo("count(*)"));
            Assert.That(select.Columns.Single().IsNumericExpression, Is.True);
        }

        [Test]
        public void 集計_派生表の中のRecordsに無い表は例外()
        {
            Assert.ThrowsAsync<NetSuiteException>(() => TranslateAsync("select count(*) from (select 1 as c_l_b_g from \"employee\" group by \"employee\".\"id\") c_l_b_agg"));
            Assert.ThrowsAsync<NetSuiteException>(() => TranslateAsync(
                "select count(*) as \"n\" from \"customer\" inner join (select \"employee\".\"id\" as \"g0\" from \"employee\" group by \"employee\".\"id\") c_l_b_sel on (\"customer\".\"id\" = c_l_b_sel.\"g0\")"));
        }

        // ---------------------------------------------------------------- 追加

        [Test]
        public void ParseInserts_1件とreturning()
        {
            var sql = "\nBEGIN\n    INSERT INTO \"customer\"(\"companyname\",\"isinactive\",\"subsidiary\")\n    VALUES (:p0,:p1,:p2)\n    RETURNING \"id\" INTO :returning_id;\nEND;\n";
            var inserts = SuiteQlTranslator.ParseInserts(sql);
            Assert.That(inserts, Has.Count.EqualTo(1));
            Assert.That(inserts[0].Table, Is.EqualTo("customer"));
            Assert.That(inserts[0].Values, Is.EqualTo(new[] { ("companyname", ":p0"), ("isinactive", ":p1"), ("subsidiary", ":p2") }));
            Assert.That(inserts[0].ReturningParameter, Is.EqualTo(":returning_id"));
        }

        [Test]
        public void ParseInserts_一括()
        {
            var sql = "BEGIN\n  INSERT INTO \"customer\" (\"companyname\",\"creditlimit\") VALUES (:p0_0,:p0_1);\n  INSERT INTO \"customer\" (\"companyname\",\"creditlimit\") VALUES (:p1_0,:p1_1);\nEND;";
            var inserts = SuiteQlTranslator.ParseInserts(sql);
            Assert.That(inserts, Has.Count.EqualTo(2));
            Assert.That(inserts[0].Values, Is.EqualTo(new[] { ("companyname", ":p0_0"), ("creditlimit", ":p0_1") }));
            Assert.That(inserts[1].Values, Is.EqualTo(new[] { ("companyname", ":p1_0"), ("creditlimit", ":p1_1") }));
            Assert.That(inserts.All(e => e.ReturningParameter == string.Empty), Is.True);
        }

        [TestCase("BEGIN INSERT INTO \"customer\"(\"companyname\",\"isinactive\") VALUES (:p0); END;")]
        [TestCase("BEGIN INSERT INTO \"customer\"(\"companyname\") VALUES ('x'); END;")]
        [TestCase("insert into \"customer\"(\"companyname\") select \"name\" from \"subsidiary\"")]
        [TestCase("BEGIN END;")]
        [TestCase("BEGIN INSERT INTO \"customer\"(\"companyname\") VALUES (:p0); delete from \"customer\"; END;")]
        public void ParseInserts_形が違えば例外(string sql)
        {
            Assert.Throws<NetSuiteException>(() => SuiteQlTranslator.ParseInserts(sql));
        }

        [Test]
        public void ParseInserts_returningがid以外なら例外()
        {
            var e = Assert.Throws<NetSuiteException>(() => SuiteQlTranslator.ParseInserts(
                "BEGIN INSERT INTO \"customer\"(\"companyname\") VALUES (:p0) RETURNING \"entityid\" INTO :returning_id; END;"));
            Assert.That(e!.Message, Does.Contain("'id'"));
        }

        // ---------------------------------------------------------------- 更新・削除

        [Test]
        public void ParseUpdate_idだけ()
        {
            var update = SuiteQlTranslator.ParseUpdate("update \"customer\" set \"companyname\"=:p0,\"creditlimit\"=:p1 where \"id\"=:pid0");
            Assert.That(update.Table, Is.EqualTo("customer"));
            Assert.That(update.Values, Is.EqualTo(new[] { ("companyname", ":p0"), ("creditlimit", ":p1") }));
            Assert.That(update.Where.IdParameter, Is.EqualTo(":pid0"));
            Assert.That(update.Where.HasOtherConditions, Is.False);
        }

        [Test]
        public void ParseUpdate_追加条件あり()
        {
            var update = SuiteQlTranslator.ParseUpdate("update \"customer\" set \"companyname\"=:p0 where \"id\"=:pid0 and \"lastmodifieddate\"=:poptimistic_current");
            Assert.That(update.Where.IdParameter, Is.EqualTo(":pid0"));
            Assert.That(update.Where.HasOtherConditions, Is.True);
            Assert.That(update.Where.Sql, Is.EqualTo("\"id\" = :pid0 and \"lastmodifieddate\" = :poptimistic_current"));
        }

        [TestCase("update \"customer\" set \"companyname\"=:p0 where \"companyname\"=:p1")]
        [TestCase("update \"customer\" set \"companyname\"=:p0 where :pid0=\"id\"")]
        [TestCase("update \"customer\" set \"companyname\"=:p0 where \"id\"='5'")]
        public void ParseUpdate_先頭がidの一致でなければ例外(string sql)
        {
            Assert.Throws<NetSuiteException>(() => SuiteQlTranslator.ParseUpdate(sql));
        }

        [TestCase("update \"customer\" set \"companyname\"=:p0 where \"id\"=:pid0 or 1=1")]
        [TestCase("update \"customer\" set \"companyname\"=:p0 where \"id\"=:pid0; delete from \"customer\"")]
        [TestCase("update \"customer\" set \"companyname\"='x' where \"id\"=:pid0")]
        public void ParseUpdate_その他の形は例外(string sql)
        {
            Assert.Throws<NetSuiteException>(() => SuiteQlTranslator.ParseUpdate(sql));
        }

        [Test]
        public void ParseDelete_idだけ()
        {
            var delete = SuiteQlTranslator.ParseDelete("delete from \"customer\" where \"id\"=:pid0");
            Assert.That(delete.Table, Is.EqualTo("customer"));
            Assert.That(delete.Where.IdParameter, Is.EqualTo(":pid0"));
            Assert.That(delete.Where.HasOtherConditions, Is.False);
        }

        [Test]
        public void ParseDelete_追加条件あり()
        {
            var delete = SuiteQlTranslator.ParseDelete("delete from \"customer\" where \"id\"=:pid0 and \"lastmodifieddate\"=:poptimistic_current");
            Assert.That(delete.Where.HasOtherConditions, Is.True);
        }

        [TestCase("delete from \"customer\" where \"companyname\"=:p0")]
        [TestCase("delete from \"customer\"")]
        public void ParseDelete_先頭がidの一致でなければ例外(string sql)
        {
            Assert.Throws<NetSuiteException>(() => SuiteQlTranslator.ParseDelete(sql));
        }
    }
}
