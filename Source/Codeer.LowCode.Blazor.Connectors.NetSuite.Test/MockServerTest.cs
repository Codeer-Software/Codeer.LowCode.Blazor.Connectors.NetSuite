using NetSuite.Mock;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>モック単体 (コネクターを通さない HTTP) の約束を確かめる。</summary>
    public class MockServerTest
    {
        MockHarness _h = null!;
        HttpClient _client = null!;

        [SetUp]
        public async Task SetUp()
        {
            _h = await MockHarness.StartAsync();
            _client = _h.CreateClient();
        }

        [TearDown]
        public async Task TearDown()
        {
            _client.Dispose();
            await _h.DisposeAsync();
        }

        static string Base64Url(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        static string Jwt(long exp) => Base64Url("""{"typ":"JWT","alg":"PS256","kid":"mock-cert"}""") + "." + Base64Url($$"""{"iss":"mock-client","exp":{{exp}}}""") + ".c2lnbmF0dXJl";

        static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        static Task<HttpResponseMessage> PostTokenAsync(HttpClient client, string assertion, string grantType = "client_credentials")
            => client.PostAsync("/services/rest/auth/oauth2/v1/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = grantType,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion,
            }));

        /// <summary>モックからアクセストークンを取る。</summary>
        public static async Task<string> GetTokenAsync(HttpClient client)
        {
            using var response = await PostTokenAsync(client, Jwt(Now + 300));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["access_token"]!.GetValue<string>();
        }

        async Task<HttpResponseMessage> SuiteQlAsync(string q, string[]? parameters = null, string query = "?limit=10&offset=0", bool prefer = true, bool auth = true)
        {
            var body = new JsonObject { ["q"] = q };
            if (parameters != null) body["params"] = new JsonArray(parameters.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
            var request = new HttpRequestMessage(HttpMethod.Post, "/services/rest/query/v1/suiteql" + query)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (prefer) request.Headers.TryAddWithoutValidation("Prefer", "transient");
            if (auth) request.Headers.Authorization = new("Bearer", await GetTokenAsync(_client));
            return await _client.SendAsync(request);
        }

        static async Task<JsonObject> BodyAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        static async Task AssertNetSuiteErrorAsync(HttpResponseMessage response, HttpStatusCode status, string? detailContains = null)
        {
            Assert.That(response.StatusCode, Is.EqualTo(status));
            var body = await BodyAsync(response);
            Assert.That(body["status"]!.GetValue<int>(), Is.EqualTo((int)status));
            var detail = body["o:errorDetails"]![0]!;
            Assert.That(detail["o:errorCode"]!.GetValue<string>(), Is.Not.Empty);
            if (detailContains != null) Assert.That(detail["detail"]!.GetValue<string>(), Does.Contain(detailContains));
        }

        // ---------------------------------------------------------------- 認証

        [Test]
        public async Task トークンの応答の形()
        {
            using var response = await PostTokenAsync(_client, Jwt(Now + 300));
            var body = await BodyAsync(response);
            Assert.That(body["access_token"]!.GetValue<string>(), Is.Not.Empty);
            Assert.That(body["expires_in"]!.GetValue<string>(), Is.EqualTo("3600"));
            Assert.That(body["token_type"]!.GetValue<string>(), Is.EqualTo("Bearer"));
        }

        [TestCase("not-a-jwt", "client_credentials", "invalid_request")]
        [TestCase("a.b.c", "client_credentials", "invalid_request")]
        [TestCase("", "client_credentials", "invalid_request")]
        [TestCase(null, "password", "unsupported_grant_type")]
        public async Task 形の合わないトークン要求は400(string? assertion, string grantType, string error)
        {
            using var response = await PostTokenAsync(_client, assertion ?? Jwt(Now + 300), grantType);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That((await BodyAsync(response))["error"]!.GetValue<string>(), Is.EqualTo(error));
        }

        [Test]
        public async Task 期限の過ぎたJWTは400()
        {
            using var response = await PostTokenAsync(_client, Jwt(Now - 60));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task 認証なし_知らないトークンは401()
        {
            using var none = await SuiteQlAsync("select id from customer", auth: false);
            await AssertNetSuiteErrorAsync(none, HttpStatusCode.Unauthorized);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/services/rest/record/v1/customer/1");
            request.Headers.Authorization = new("Bearer", "unknown");
            using var unknown = await _client.SendAsync(request);
            await AssertNetSuiteErrorAsync(unknown, HttpStatusCode.Unauthorized);
        }

        // ---------------------------------------------------------------- SuiteQL の要求の検証

        [Test]
        public async Task Preferが無ければ400()
        {
            using var response = await SuiteQlAsync("select id from customer", prefer: false);
            await AssertNetSuiteErrorAsync(response, HttpStatusCode.BadRequest, "Prefer");
        }

        [TestCase("?limit=10&offset=5")]
        [TestCase("?limit=1001&offset=0")]
        [TestCase("?limit=0&offset=0")]
        [TestCase("?offset=10")]
        public async Task limitとoffsetの制約に合わなければ400(string query)
        {
            using var response = await SuiteQlAsync("select id from customer", query: query);
            await AssertNetSuiteErrorAsync(response, HttpStatusCode.BadRequest);
        }

        [TestCase("delete from customer")]
        [TestCase("update customer set companyname = 'x'")]
        [TestCase("select id from customer; select id from subsidiary")]
        [TestCase("select id from employee")]
        [TestCase("select c.id from customer c join employee e on e.id = c.id")]
        [TestCase("select name from sqlite_master")]
        [TestCase("select nosuchcolumn from customer")]
        [TestCase("select id from customer where id = ?")]
        [TestCase("select TO_CHAR(startdate, 'Month') from customer")]
        public async Task 受け付けない文は400で理由を返す(string q)
        {
            using var response = await SuiteQlAsync(q);
            await AssertNetSuiteErrorAsync(response, HttpStatusCode.BadRequest, "Invalid search query");
        }

        // ---------------------------------------------------------------- SuiteQL の結果

        [Test]
        public async Task 結果の値は文字列でnullは省きキーは小文字で真偽はTF()
        {
            using var response = await SuiteQlAsync(
                "select id, companyName as CompanyName, isinactive, creditlimit, priority, email, TO_CHAR(startdate, 'YYYY-MM-DD') as sd, TO_CHAR(lastmodifieddate, 'YYYY-MM-DD HH24:MI:SS') as lm" +
                " from customer where id = ? or companyname = ? order by id", new[] { "20", "Beta Foods" });

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            var body = await BodyAsync(response);
            Assert.That(body["count"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(body["totalResults"]!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(body["hasMore"]!.GetValue<bool>(), Is.False);
            Assert.That(body["offset"]!.GetValue<int>(), Is.EqualTo(0));
            var items = body["items"]!.AsArray();
            var beta = items[0]!.AsObject();
            Assert.That(beta.Select(e => e.Key), Is.EqualTo(new[] { "links", "id", "companyname", "isinactive", "creditlimit", "priority", "email", "sd", "lm" }));
            Assert.That(beta.Where(e => e.Key != "links").Select(e => e.Value!.GetValueKind()), Is.All.EqualTo(JsonValueKind.String));
            Assert.That(beta["id"]!.GetValue<string>(), Is.EqualTo("2"));
            Assert.That(beta["isinactive"]!.GetValue<string>(), Is.EqualTo("F"));
            Assert.That(beta["creditlimit"]!.GetValue<string>(), Is.EqualTo("12000.5"));
            Assert.That(beta["priority"]!.GetValue<string>(), Is.EqualTo("2"));
            Assert.That(beta["sd"]!.GetValue<string>(), Is.EqualTo("2024-06-15"));
            Assert.That(beta["lm"]!.GetValue<string>(), Is.EqualTo("2026-02-10 23:30:00"));
            //email の無い行はキーごと無い
            var upsilon = items[1]!.AsObject();
            Assert.That(upsilon["id"]!.GetValue<string>(), Is.EqualTo("20"));
            Assert.That(upsilon.ContainsKey("email"), Is.False);
        }

        [Test]
        public async Task ページはlimitとoffsetで切りhasMoreとtotalResultsが付く()
        {
            using var first = await SuiteQlAsync("select id from customer order by id", query: "?limit=8&offset=0");
            using var last = await SuiteQlAsync("select id from customer order by id", query: "?limit=8&offset=16");

            var a = await BodyAsync(first);
            var b = await BodyAsync(last);
            Assert.That((a["count"]!.GetValue<int>(), a["hasMore"]!.GetValue<bool>(), a["totalResults"]!.GetValue<int>()), Is.EqualTo((8, true, 20)));
            Assert.That((b["count"]!.GetValue<int>(), b["hasMore"]!.GetValue<bool>(), b["offset"]!.GetValue<int>()), Is.EqualTo((4, false, 16)));
            Assert.That(b["items"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()), Is.EqualTo(new[] { "17", "18", "19", "20" }));
        }

        [Test]
        public async Task Oracleの関数()
        {
            using var response = await SuiteQlAsync(
                "select TO_CHAR(TRUNC(TO_DATE('2026-03-05 10:20:30', 'YYYY-MM-DD HH24:MI:SS'), 'IW'), 'YYYY-MM-DD HH24:MI:SS') as w," +
                " TO_CHAR(ADD_MONTHS(TO_DATE('2026-01-31', 'YYYY-MM-DD'), 1), 'YYYY-MM-DD') as m," +
                " TO_CHAR((TO_DATE('2026-03-31 20:00:00', 'YYYY-MM-DD HH24:MI:SS') + NUMTODSINTERVAL(540, 'MINUTE')), 'YYYY-MM-DD HH24:MI:SS') as tz," +
                " TO_CHAR(TRUNC(TO_DATE('2026-08-15', 'YYYY-MM-DD'), 'Q'), 'YYYY-MM-DD') as q," +
                " NVL(email, 'none') as e" +
                " from customer where id = 20");

            var item = (await BodyAsync(response))["items"]![0]!;
            Assert.That(item["w"]!.GetValue<string>(), Is.EqualTo("2026-03-02 00:00:00"));
            Assert.That(item["m"]!.GetValue<string>(), Is.EqualTo("2026-02-28"));
            Assert.That(item["tz"]!.GetValue<string>(), Is.EqualTo("2026-04-01 05:00:00"));
            Assert.That(item["q"]!.GetValue<string>(), Is.EqualTo("2026-07-01"));
            Assert.That(item["e"]!.GetValue<string>(), Is.EqualTo("none"));
        }

        [Test]
        public async Task ROWNUMで並べたあとの上位だけ()
        {
            using var response = await SuiteQlAsync("select c.id from (select * from (select id, creditlimit from customer order by creditlimit desc) where ROWNUM <= 3) c order by c.id");
            Assert.That((await BodyAsync(response))["items"]!.AsArray().Select(e => e!["id"]!.GetValue<string>()), Is.EqualTo(new[] { "6", "8", "17" }));
        }

        // ---------------------------------------------------------------- メタデータ・レコード

        [Test]
        public async Task メタデータはJSONSchemaで知らないレコードは404()
        {
            var token = await GetTokenAsync(_client);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/services/rest/record/v1/metadata-catalog/customer");
            request.Headers.Authorization = new("Bearer", token);
            request.Headers.Accept.ParseAdd("application/schema+json");
            using var response = await _client.SendAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/schema+json"));
            var schema = await BodyAsync(response);
            Assert.That(schema["properties"]!["id"]!["type"]!.GetValue<string>(), Is.EqualTo("string"));
            Assert.That(schema["properties"]!["startDate"]!["format"]!.GetValue<string>(), Is.EqualTo("date"));
            Assert.That(schema["properties"]!["subsidiary"]!["properties"]!["id"], Is.Not.Null);
            Assert.That(schema["required"]!.AsArray().Select(e => e!.GetValue<string>()), Is.EquivalentTo(new[] { "companyName", "subsidiary" }));

            using var unknownRequest = new HttpRequestMessage(HttpMethod.Get, "/services/rest/record/v1/metadata-catalog/employee");
            unknownRequest.Headers.Authorization = new("Bearer", token);
            unknownRequest.Headers.Accept.ParseAdd("application/schema+json");
            using var unknown = await _client.SendAsync(unknownRequest);
            await AssertNetSuiteErrorAsync(unknown, HttpStatusCode.NotFound);
        }

        [Test]
        public async Task 作成は204でLocationが絶対URL()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/services/rest/record/v1/subsidiary")
            {
                Content = new StringContent("""{"name":"EU Sales","country":"DE"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new("Bearer", await GetTokenAsync(_client));
            using var response = await _client.SendAsync(request);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(response.Headers.Location!.ToString(), Is.EqualTo(MockHarness.ServiceUrl + "/services/rest/record/v1/subsidiary/4"));
        }

        [Test]
        public async Task 無いIdと知らないレコードは404()
        {
            var token = await GetTokenAsync(_client);
            foreach (var (method, path) in new[] { ("GET", "customer/999"), ("PATCH", "customer/999"), ("DELETE", "customer/999"), ("DELETE", "customer/abc"), ("POST", "employee") })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), "/services/rest/record/v1/" + path);
                request.Headers.Authorization = new("Bearer", token);
                if (method is "PATCH" or "POST") request.Content = new StringContent("""{"companyName":"x"}""", Encoding.UTF8, "application/json");
                using var response = await _client.SendAsync(request);
                await AssertNetSuiteErrorAsync(response, HttpStatusCode.NotFound);
            }
        }

        // ---------------------------------------------------------------- mock-data.json

        [TestCase("""{"records":{"a":{"fields":{"x":"text"}}}}""", "unknown type")]
        [TestCase("""{"records":{"a":{"fields":{"x":{"type":"reference"}}}}}""", "record")]
        [TestCase("""{"records":{"a":{"fields":{"x":{"type":"reference","record":"b"}}}}}""", "not defined")]
        [TestCase("""{"records":{"a":{"fields":{"x":"string"},"rows":[{"y":"1"}]}}}""", "no field 'y'")]
        [TestCase("""{"records":{"a":{"fields":{"x":{"type":"string","required":true}},"rows":[{"id":1}]}}}""", "required")]
        [TestCase("""{"records":{"a":{"fields":{"x":"integer"},"rows":[{"x":"1"}]}}}""", "integer")]
        [TestCase("""{"records":{"a":{"fields":{"b":{"type":"reference","record":"a"}},"rows":[{"id":1,"b":2}]}}}""", "does not exist")]
        public void 定義とデータの誤りは起動時に分かる(string json, string message)
        {
            Assert.That(() => MockNetSuite.FromJson(json), Throws.InstanceOf<InvalidDataException>().With.Message.Contains(message));
        }
    }
}
