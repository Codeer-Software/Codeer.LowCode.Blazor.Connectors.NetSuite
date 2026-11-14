using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Match;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>モック + 本物のコネクター + 本体の ModuleDataIO で、作成・更新・削除・エラー・トークンを端から端まで確かめる。</summary>
    public class MockWriteTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp() => _h = await MockHarness.StartAsync();

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        async Task<ModuleData?> ReadAsync(string module, string id)
        {
            var condition = new SearchCondition(module)
            {
                Condition = new FieldValueMatchCondition { SearchTargetVariable = "Id.Value", Comparison = MatchComparison.Equal, Value = new StringValue { Value = id } },
            };
            return (await _h.IO.GetListAsync(condition, 0)).Items.SingleOrDefault();
        }

        Task<List<ModuleSubmitResult>> SubmitAsync(ModuleSubmitData data) => _h.IO.SubmitWithTransactionAsync(new() { data });

        static T? Field<T>(ModuleData data, string name) where T : FieldDataBase
            => data.Fields.TryGetValue(name, out var value) ? (T)value : null;

        /// <summary>REST のレコードの読み出し (コネクターを通さず、モックの GET で中身を見る)。</summary>
        async Task<JsonObject?> GetRestRecordAsync(string type, string id)
        {
            using var client = _h.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/services/rest/record/v1/{type}/{id}");
            request.Headers.Authorization = new("Bearer", await MockServerTest.GetTokenAsync(client));
            using var response = await client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }

        // ---------------------------------------------------------------- 作成

        [Test]
        public async Task 新規は採番されたIdで読み直せる()
        {
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Customer" };
            add.Fields["Id"] = tempId;
            add.Fields["CompanyName"] = new TextFieldData { Value = "Omega Works" };
            add.Fields["Email"] = new TextFieldData { Value = "omega@example.com" };
            add.Fields["IsInactive"] = new BooleanFieldData { Value = true };
            add.Fields["CreditLimit"] = new NumberFieldData { Value = 12.5m };
            add.Fields["Priority"] = new NumberFieldData { Value = 4 };
            add.Fields["StartDate"] = new DateFieldData { Value = new DateOnly(2026, 3, 4) };
            add.Fields["LastModified"] = new DateTimeFieldData { Value = new DateTime(2026, 3, 4, 5, 6, 7) };
            add.Fields["Subsidiary"] = new LinkFieldData { Value = "3" };

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Id = tempId.Value!, Add = new() { add } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            var id = results.Single().DestinationId;
            Assert.That(id, Is.EqualTo((MockHarness.Rows("customer").Count + 1).ToString()));

            var read = (await ReadAsync("Customer", id))!;
            Assert.That(Field<TextFieldData>(read, "CompanyName")!.Value, Is.EqualTo("Omega Works"));
            Assert.That(Field<TextFieldData>(read, "Email")!.Value, Is.EqualTo("omega@example.com"));
            Assert.That(Field<BooleanFieldData>(read, "IsInactive")!.Value, Is.True);
            Assert.That(Field<NumberFieldData>(read, "CreditLimit")!.Value, Is.EqualTo(12.5m));
            Assert.That(Field<NumberFieldData>(read, "Priority")!.Value, Is.EqualTo(4m));
            Assert.That(Field<DateFieldData>(read, "StartDate")!.Value, Is.EqualTo(new DateOnly(2026, 3, 4)));
            Assert.That(Field<DateTimeFieldData>(read, "LastModified")!.Value, Is.EqualTo(new DateTime(2026, 3, 4, 5, 6, 7)));
            Assert.That(Field<LinkFieldData>(read, "Subsidiary")!.Value, Is.EqualTo("3"));
            Assert.That(Field<LinkFieldData>(read, "Subsidiary")!.DisplayText, Is.EqualTo("US Sales"));

            //REST の形でも同じ値
            var rest = (await GetRestRecordAsync("customer", id))!;
            Assert.That(rest["id"]!.GetValue<string>(), Is.EqualTo(id));
            Assert.That(rest["isInactive"]!.GetValue<bool>(), Is.True);
            Assert.That(rest["startDate"]!.GetValue<string>(), Is.EqualTo("2026-03-04"));
            Assert.That(rest["lastModifiedDate"]!.GetValue<string>(), Is.EqualTo("2026-03-04T05:06:07Z"));
            Assert.That(rest["subsidiary"]!.ToJsonString(), Is.EqualTo("""{"id":"3"}"""));
            Assert.That(_h.Inner.DataSourceCalls, Is.Empty);
        }

        [Test]
        public async Task 必須の項目が無い新規は保存の結果のエラーになる()
        {
            var tempId = IdFieldData.NewId();
            var add = new ModuleData { Name = "Customer" };
            add.Fields["Id"] = tempId;
            add.Fields["CompanyName"] = new TextFieldData { Value = "No Subsidiary" };

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Id = tempId.Value!, Add = new() { add } });

            Assert.That(results.Single().ExceptionMessage, Does.Contain("Please enter value(s) for: Subsidiary"));
            Assert.That((await _h.IO.GetListAsync(new SearchCondition("Customer"), 0)).Items, Has.Count.EqualTo(MockHarness.Rows("customer").Count));
        }

        [Test]
        public void 必須の項目が無い作成はNetSuiteExceptionのInvalidRequestで項目の名前が付く()
        {
            var args = new Dictionary<string, object?> { [":p0"] = "x@example.com", [":returning_id"] = null };
            var ex = Assert.ThrowsAsync<NetSuiteException>(() =>
                _h.Accessor.InsertAsync(TestConnections.DataSourceName, "BEGIN INSERT INTO \"customer\"(\"email\") VALUES (:p0) RETURNING \"id\" INTO :returning_id; END;", args));

            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.InvalidRequest));
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(ex.ErrorCode, Is.EqualTo("USER_ERROR"));
            Assert.That(ex.ErrorPath, Is.EqualTo("companyName"));
        }

        static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

        [TestCase("""{"companyName":"A","subsidiary":{"id":"1"},"unknownField":"x"}""", "INVALID_FIELD", "unknownField")]
        [TestCase("""{"companyName":"A","subsidiary":{"id":"1"},"creditLimit":"abc"}""", "INVALID_FLD_VALUE", "creditLimit")]
        [TestCase("""{"companyName":"A","subsidiary":{"id":"1"},"isInactive":"T"}""", "INVALID_FLD_VALUE", "isInactive")]
        [TestCase("""{"companyName":"A","subsidiary":{"id":"1"},"startDate":"2026/03/04"}""", "INVALID_FLD_VALUE", "startDate")]
        [TestCase("""{"companyName":"A","subsidiary":{"id":"1"},"lastModifiedDate":"2026-03-04 05:06:07"}""", "INVALID_FLD_VALUE", "lastModifiedDate")]
        [TestCase("""{"companyName":"A","subsidiary":"1"}""", "INVALID_FLD_VALUE", "subsidiary")]
        [TestCase("""{"companyName":"A","subsidiary":{"id":"99"}}""", "INVALID_KEY_OR_REF", "subsidiary")]
        [TestCase("""{"companyName":"A","subsidiary":{"id":"1"},"priority":1.5}""", "INVALID_FLD_VALUE", "priority")]
        [TestCase("""{"id":"5","companyName":"A","subsidiary":{"id":"1"}}""", "INVALID_FLD_VALUE", "id")]
        [TestCase("""{"companyName":null,"subsidiary":{"id":"1"}}""", "USER_ERROR", "companyName")]
        public void 受け付けない値の作成はInvalidRequestで項目の名前が付く(string body, string errorCode, string errorPath)
        {
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => _h.Connection.CreateRecordAsync("customer", Json(body)));

            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.InvalidRequest));
            Assert.That(ex.ErrorCode, Is.EqualTo(errorCode));
            Assert.That(ex.ErrorPath, Is.EqualTo(errorPath));
        }

        [Test]
        public async Task 項目名は大文字と小文字を区別する()
        {
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => _h.Connection.CreateRecordAsync("customer", Json("""{"companyname":"A","subsidiary":{"id":"1"}}""")));
            Assert.That(ex!.ErrorPath, Is.EqualTo("companyname"));
            //REST の名前なら通る
            var id = await _h.Connection.CreateRecordAsync("customer", Json("""{"companyName":"A","subsidiary":{"id":"1"}}"""));
            Assert.That(id, Is.EqualTo("21"));
        }

        // ---------------------------------------------------------------- 更新・空にする

        [Test]
        public async Task 更新は送った項目だけを変える()
        {
            var update = new ModuleData { Name = "Customer" };
            update.Fields["Id"] = new IdFieldData { Value = "2" };
            update.Fields["CompanyName"] = new TextFieldData { Value = "Beta Foods Holdings" };
            update.Fields["CreditLimit"] = new NumberFieldData { Value = 999.25m };
            update.Fields["Subsidiary"] = new LinkFieldData { Value = "1" };

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Id = "2", Update = new() { update } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            var read = (await ReadAsync("Customer", "2"))!;
            Assert.That(Field<TextFieldData>(read, "CompanyName")!.Value, Is.EqualTo("Beta Foods Holdings"));
            Assert.That(Field<NumberFieldData>(read, "CreditLimit")!.Value, Is.EqualTo(999.25m));
            Assert.That(Field<LinkFieldData>(read, "Subsidiary")!.DisplayText, Is.EqualTo("Headquarters"));
            //送っていない項目はそのまま
            var row = MockHarness.Rows("customer").Single(e => e.Id() == "2");
            Assert.That(Field<TextFieldData>(read, "Email")!.Value, Is.EqualTo(row.Text("email")));
            Assert.That(Field<DateFieldData>(read, "StartDate")!.Value, Is.EqualTo(row.Date("startDate")));
        }

        [Test]
        public async Task 空にした項目は値が消える()
        {
            var update = new ModuleData { Name = "Customer" };
            update.Fields["Id"] = new IdFieldData { Value = "1" };
            update.Fields["Email"] = new TextFieldData { Value = null };
            update.Fields["CreditLimit"] = new NumberFieldData { Value = null };
            update.Fields["StartDate"] = new DateFieldData { Value = null };
            update.Fields["LastModified"] = new DateTimeFieldData { Value = null };

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Id = "1", Update = new() { update } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            var read = (await ReadAsync("Customer", "1"))!;
            Assert.That(Field<TextFieldData>(read, "Email")?.Value, Is.Null);
            Assert.That(Field<NumberFieldData>(read, "CreditLimit")?.Value, Is.Null);
            Assert.That(Field<DateFieldData>(read, "StartDate")?.Value, Is.Null);
            Assert.That(Field<DateTimeFieldData>(read, "LastModified")?.Value, Is.Null);
            var rest = (await GetRestRecordAsync("customer", "1"))!;
            Assert.That(rest.ContainsKey("email"), Is.False);
            Assert.That(rest["companyName"]!.GetValue<string>(), Is.EqualTo("Alpha Trading"));
        }

        [Test]
        public async Task 必須の項目は空にできない()
        {
            var update = new ModuleData { Name = "Customer" };
            update.Fields["Id"] = new IdFieldData { Value = "1" };
            update.Fields["CompanyName"] = new TextFieldData { Value = null };

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Id = "1", Update = new() { update } });

            Assert.That(results.Single().ExceptionMessage, Does.Contain("Company Name"));
            Assert.That(Field<TextFieldData>((await ReadAsync("Customer", "1"))!, "CompanyName")!.Value, Is.EqualTo("Alpha Trading"));
        }

        // ---------------------------------------------------------------- 必須の項目を空にする

        //種類ごとの必須の項目を持つレコード (Required) と、参照先 (parent)
        const string RequiredData = """
            {
              "records": {
                "parent": { "fields": { "name": "string" }, "rows": [ { "id": 1, "name": "P1" } ] },
                "required": {
                  "fields": {
                    "num": { "type": "integer", "required": true },
                    "day": { "type": "date", "required": true },
                    "flag": { "type": "boolean", "required": true },
                    "text": { "type": "string", "required": true },
                    "parent": { "type": "reference", "record": "parent", "required": true }
                  },
                  "rows": [ { "id": 1, "num": 5, "day": "2026-01-02", "flag": true, "text": "x", "parent": { "id": "1" } } ]
                }
              }
            }
            """;

        static async Task<MockHarness> StartRequiredAsync()
            => await MockHarness.StartAsync(global::NetSuite.Mock.MockNetSuite.FromJson(RequiredData), e => e.Records = new() { "parent", "required" }, d =>
            {
                var m = new Codeer.LowCode.Blazor.Repository.Design.ModuleDesign { Name = "Required", DataSourceName = TestConnections.DataSourceName, DbTable = "required", CanUpdate = true };
                m.Fields.Add(new Codeer.LowCode.Blazor.Repository.Design.IdFieldDesign { Name = "Id", DbColumn = "id" });
                m.Fields.Add(new Codeer.LowCode.Blazor.Repository.Design.NumberFieldDesign { Name = "Num", DbColumn = "num" });
                m.Fields.Add(new Codeer.LowCode.Blazor.Repository.Design.DateFieldDesign { Name = "Day", DbColumn = "day" });
                m.Fields.Add(new Codeer.LowCode.Blazor.Repository.Design.BooleanFieldDesign { Name = "Flag", DbColumn = "flag" });
                m.Fields.Add(new Codeer.LowCode.Blazor.Repository.Design.TextFieldDesign { Name = "Text", DbColumn = "text" });
                m.Fields.Add(new Codeer.LowCode.Blazor.Repository.Design.LinkFieldDesign { Name = "Parent", DbColumn = "parent", SearchCondition = new SearchCondition("Parent"), ValueVariable = "Id.Value" });
                ((Codeer.LowCode.Blazor.DesignLogic.IEditableModuleDesign)d.Modules).Add(m);
            });

        static FieldDataBase EmptyData(string field) => field switch
        {
            "Num" => new NumberFieldData { Value = null },
            "Day" => new DateFieldData { Value = null },
            "Flag" => new BooleanFieldData { Value = null },
            "Text" => new TextFieldData { Value = null },
            _ => new LinkFieldData { Value = null },
        };

        [TestCase("Num", "num")]
        [TestCase("Day", "day")]
        [TestCase("Flag", "flag")]
        [TestCase("Text", "text")]
        [TestCase("Parent", "parent")]
        public async Task 必須の項目を空にした更新はnullを送って必須違反になり値は変わらない(string field, string restName)
        {
            await _h.DisposeAsync();
            _h = await StartRequiredAsync();
            var update = new ModuleData { Name = "Required" };
            update.Fields["Id"] = new IdFieldData { Value = "1" };
            update.Fields[field] = EmptyData(field);

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Required", Id = "1", Update = new() { update } });

            //型の既定値 (0・0001-01-01・false・"") ではなく null を送る
            var patch = _h.Recorder.Requests.Single(e => e.Method == HttpMethod.Patch);
            Assert.That(patch.Body, Is.EqualTo($$"""{"{{restName}}":null}"""));
            Assert.That(results.Single().ExceptionMessage, Does.Contain("Please enter value(s) for: " + restName));

            //値は変わらない
            var rest = (await GetRestRecordAsync("required", "1"))!;
            Assert.That(rest["num"]!.GetValue<long>(), Is.EqualTo(5));
            Assert.That(rest["day"]!.GetValue<string>(), Is.EqualTo("2026-01-02"));
            Assert.That(rest["flag"]!.GetValue<bool>(), Is.True);
            Assert.That(rest["text"]!.GetValue<string>(), Is.EqualTo("x"));
            Assert.That(rest["parent"]!.ToJsonString(), Is.EqualTo("""{"id":"1"}"""));
        }

        [TestCase("num")]
        [TestCase("day")]
        [TestCase("flag")]
        [TestCase("text")]
        [TestCase("parent")]
        public async Task 必須の項目を空にする更新はNetSuiteExceptionのInvalidRequestで項目の名前が付く(string column)
        {
            await _h.DisposeAsync();
            _h = await StartRequiredAsync();

            var ex = Assert.ThrowsAsync<NetSuiteException>(() => _h.Accessor.ExecuteAsync(TestConnections.DataSourceName,
                $"update \"required\" set \"{column}\"=:p0 where \"id\"=:pid0", new Dictionary<string, object?> { [":p0"] = null, [":pid0"] = "1" }));

            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.InvalidRequest));
            Assert.That(ex.ErrorCode, Is.EqualTo("USER_ERROR"));
            Assert.That(ex.ErrorPath, Is.EqualTo(column));
        }

        // ---------------------------------------------------------------- 削除

        [Test]
        public async Task 削除すると一覧から消える()
        {
            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Sample", Delete = new() { new ModuleDeleteInfo { ModuleName = "Sample", Id = "15" } } });

            Assert.That(results.Single().ExceptionMessage, Is.Empty);
            Assert.That(await ReadAsync("Sample", "15"), Is.Null);
            Assert.That(await GetRestRecordAsync("customrecord_sample", "15"), Is.Null);
            Assert.That((await _h.IO.GetListAsync(new SearchCondition("Sample"), 0)).Items, Has.Count.EqualTo(MockHarness.Rows("customrecord_sample").Count - 1));
        }

        [Test]
        public async Task ほかのレコードから参照されているレコードは消せない()
        {
            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Delete = new() { new ModuleDeleteInfo { ModuleName = "Customer", Id = "1" } } });

            Assert.That(results.Single().ExceptionMessage, Does.Contain("dependent records"));
            Assert.That(await ReadAsync("Customer", "1"), Is.Not.Null);
        }

        // ---------------------------------------------------------------- 無い Id

        [Test]
        public async Task 無いIdの更新と削除は0件()
        {
            var update = await _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, "update \"customer\" set \"companyname\"=:p0 where \"id\"=:pid0",
                new Dictionary<string, object?> { [":p0"] = "X", [":pid0"] = "999" });
            var delete = await _h.Accessor.ExecuteAsync(TestConnections.DataSourceName, "delete from \"customer\" where \"id\"=:pid0",
                new Dictionary<string, object?> { [":pid0"] = "999" });

            Assert.That((update, delete), Is.EqualTo((0, 0)));
        }

        [Test]
        public async Task 無いIdの更新は保存の結果のエラーになる()
        {
            var update = new ModuleData { Name = "Customer" };
            update.Fields["Id"] = new IdFieldData { Value = "999" };
            update.Fields["CompanyName"] = new TextFieldData { Value = "X" };

            var results = await SubmitAsync(new ModuleSubmitData { ModuleName = "Customer", Id = "999", Update = new() { update } });

            Assert.That(results.Single().ExceptionMessage, Is.Not.Empty);
            Assert.That(await ReadAsync("Customer", "999"), Is.Null);
        }

        // ---------------------------------------------------------------- トークン

        [Test]
        public async Task トークンは1回取って使い回し_失効したら401で取り直す()
        {
            await _h.IO.GetListAsync(new SearchCondition("Subsidiary"), 0);
            await _h.IO.GetListAsync(new SearchCondition("Subsidiary"), 0);
            Assert.That(_h.Mock.IssuedTokenCount, Is.EqualTo(1));

            _h.Mock.RevokeTokens();
            var result = await _h.IO.GetListAsync(new SearchCondition("Subsidiary"), 0);

            Assert.That(result.Items, Has.Count.EqualTo(MockHarness.Rows("subsidiary").Count));
            Assert.That(_h.Mock.IssuedTokenCount, Is.EqualTo(2));
        }

        [Test]
        public async Task モックの時計でトークンの期限が切れても取り直して続ける()
        {
            _h.Mock.TokenLifetime = TimeSpan.FromSeconds(10);
            await _h.IO.GetListAsync(new SearchCondition("Subsidiary"), 0);
            var now = DateTime.UtcNow;
            _h.Mock.UtcNow = () => now.AddSeconds(60);

            //コネクターはまだ期限内だと思っているので、401 を受けてから取り直す
            await _h.IO.GetListAsync(new SearchCondition("Subsidiary"), 0);
            Assert.That(_h.Mock.IssuedTokenCount, Is.EqualTo(2));
        }
    }
}
