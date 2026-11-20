using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    public class NetSuiteConnectionTest
    {
        FakeNetSuite _fake = null!;
        List<TimeSpan> _delays = null!;

        [SetUp]
        public void SetUp()
        {
            _fake = new FakeNetSuite();
            _delays = new List<TimeSpan>();
        }

        [TearDown]
        public void TearDown() => _fake.Dispose();

        NetSuiteConnection Create(Action<NetSuiteConnectionSettings>? edit = null) => TestConnections.Create(_fake, _delays, edit);

        static Task<NetSuiteConnection.SuiteQlPage> QueryAsync(NetSuiteConnection connection)
            => connection.QuerySuiteQlAsync("select id from customer", new List<string>(), 10, 0);

        static byte[] Json(string json) => Encoding.UTF8.GetBytes(json);

        static byte[] Base64UrlDecode(string text)
        {
            var s = text.Replace('-', '+').Replace('_', '/');
            s += new string('=', (4 - s.Length % 4) % 4);
            return Convert.FromBase64String(s);
        }

        static Func<RecordedRequest, bool> SuiteQl => e => e.IsSuiteQl;
        static Func<RecordedRequest, bool> RecordWrite => e => e.IsRecordWrite;

        // ---------------------------------------------------------------- トークン

        [Test]
        public async Task トークンは1回取って使い回す()
        {
            var connection = Create();
            await QueryAsync(connection);
            await QueryAsync(connection);
            await connection.UpdateRecordAsync("customer", "5", Json("{}"));

            Assert.That(_fake.TokenRequests, Has.Count.EqualTo(1));
            Assert.That(_fake.ApiRequests.Select(e => e.Header("Authorization")), Is.All.EqualTo("Bearer token-1"));
        }

        [Test]
        public async Task 期限が来たら取り直す()
        {
            var now = new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);
            var connection = Create();
            connection.UtcNow = () => now;
            _fake.ExpiresIn = 3600;

            await QueryAsync(connection);
            //期限 (expires_in - 120 秒) の手前では使い回す
            now = now.AddSeconds(3479);
            await QueryAsync(connection);
            Assert.That(_fake.TokenRequests, Has.Count.EqualTo(1));

            now = now.AddSeconds(1);
            await QueryAsync(connection);
            Assert.That(_fake.TokenRequests, Has.Count.EqualTo(2));
            Assert.That(_fake.ApiRequests.Last().Header("Authorization"), Is.EqualTo("Bearer token-2"));
        }

        [Test]
        public async Task トークン要求の本文()
        {
            await QueryAsync(Create());
            var token = _fake.TokenRequests.Single();
            Assert.That(token.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(token.Url, Is.EqualTo(FakeNetSuite.TokenUrl));
            Assert.That(token.Header("Content-Type"), Does.StartWith("application/x-www-form-urlencoded"));
            var form = token.Form();
            Assert.That(form["grant_type"], Is.EqualTo("client_credentials"));
            Assert.That(form["client_assertion_type"], Is.EqualTo("urn:ietf:params:oauth:client-assertion-type:jwt-bearer"));
            Assert.That(form["client_assertion"].Split('.'), Has.Length.EqualTo(3));
            //トークン要求には Bearer を付けない
            Assert.That(token.Header("Authorization"), Is.Null);
        }

        // ---------------------------------------------------------------- client assertion (JWT)

        static (JsonElement Header, JsonElement Payload, byte[] Data, byte[] Signature) DecodeJwt(string jwt)
        {
            var parts = jwt.Split('.');
            Assert.That(parts, Has.Length.EqualTo(3));
            var header = JsonDocument.Parse(Base64UrlDecode(parts[0])).RootElement;
            var payload = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement;
            return (header, payload, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64UrlDecode(parts[2]));
        }

        [TestCase("PS256")]
        [TestCase("PS384")]
        [TestCase("PS512")]
        public void JWT_PS系はRSAのPSSで署名する(string algorithm)
        {
            var now = new DateTime(2026, 11, 13, 1, 2, 3, DateTimeKind.Utc);
            var connection = Create(e => e.Algorithm = algorithm);
            connection.UtcNow = () => now;

            var (header, payload, data, signature) = DecodeJwt(connection.CreateClientAssertion(FakeNetSuite.TokenUrl));

            Assert.That(header.GetProperty("typ").GetString(), Is.EqualTo("JWT"));
            Assert.That(header.GetProperty("alg").GetString(), Is.EqualTo(algorithm));
            Assert.That(header.GetProperty("kid").GetString(), Is.EqualTo("cert-1"));
            Assert.That(payload.GetProperty("iss").GetString(), Is.EqualTo("client-1"));
            Assert.That(payload.GetProperty("aud").GetString(), Is.EqualTo(FakeNetSuite.TokenUrl));
            Assert.That(payload.GetProperty("scope").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "rest_webservices" }));
            var iat = payload.GetProperty("iat").GetInt64();
            Assert.That(iat, Is.EqualTo(new DateTimeOffset(now).ToUnixTimeSeconds()));
            Assert.That(payload.GetProperty("exp").GetInt64() - iat, Is.EqualTo(300));

            var hash = algorithm switch { "PS256" => HashAlgorithmName.SHA256, "PS384" => HashAlgorithmName.SHA384, _ => HashAlgorithmName.SHA512 };
            using var publicKey = RSA.Create();
            publicKey.ImportSubjectPublicKeyInfo(TestConnections.RsaKey.ExportSubjectPublicKeyInfo(), out _);
            Assert.That(publicKey.VerifyData(data, signature, hash, RSASignaturePadding.Pss), Is.True);
            Assert.That(publicKey.VerifyData(data, signature, hash, RSASignaturePadding.Pkcs1), Is.False);
        }

        [Test]
        public void JWT_ES256はECDsaで署名する()
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var connection = Create(e =>
            {
                e.Algorithm = "ES256";
                e.PrivateKey = ec.ExportPkcs8PrivateKeyPem();
            });

            var (header, _, data, signature) = DecodeJwt(connection.CreateClientAssertion(FakeNetSuite.TokenUrl));
            Assert.That(header.GetProperty("alg").GetString(), Is.EqualTo("ES256"));
            //JWS の ES256 は r||s の 64 バイト
            Assert.That(signature, Has.Length.EqualTo(64));
            using var publicKey = ECDsa.Create();
            publicKey.ImportSubjectPublicKeyInfo(ec.ExportSubjectPublicKeyInfo(), out _);
            Assert.That(publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256), Is.True);
        }

        [Test]
        public void JWT_scopeは区切って配列にする()
        {
            var connection = Create(e => e.Scope = "rest_webservices, restlets");
            var (_, payload, _, _) = DecodeJwt(connection.CreateClientAssertion(FakeNetSuite.TokenUrl));
            Assert.That(payload.GetProperty("scope").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "rest_webservices", "restlets" }));
        }

        [Test]
        public void JWT_改行をバックスラッシュnで書いた鍵も使える()
        {
            var connection = Create(e => e.PrivateKey = TestConnections.RsaPrivateKeyPem.Replace("\r\n", "\n").Replace("\n", "\\n"));
            var (_, _, data, signature) = DecodeJwt(connection.CreateClientAssertion(FakeNetSuite.TokenUrl));
            Assert.That(TestConnections.RsaKey.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), Is.True);
        }

        [Test]
        public void JWT_鍵ファイルから読む()
        {
            var path = Path.Combine(Path.GetTempPath(), $"ns_test_{Guid.NewGuid():N}.pem");
            File.WriteAllText(path, TestConnections.RsaPrivateKeyPem);
            try
            {
                var connection = Create(e => { e.PrivateKey = ""; e.PrivateKeyPath = path; });
                var (_, _, data, signature) = DecodeJwt(connection.CreateClientAssertion(FakeNetSuite.TokenUrl));
                Assert.That(TestConnections.RsaKey.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), Is.True);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Test]
        public void JWT_鍵ファイルが無ければ認証のエラー()
        {
            var connection = Create(e => { e.PrivateKey = ""; e.PrivateKeyPath = Path.Combine(Path.GetTempPath(), $"ns_missing_{Guid.NewGuid():N}.pem"); });
            var ex = Assert.Throws<NetSuiteException>(() => connection.CreateClientAssertion(FakeNetSuite.TokenUrl));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.Authentication));
        }

        [Test]
        public void JWT_方式と鍵が合わなければ認証のエラーで鍵は出さない()
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var pem = ec.ExportPkcs8PrivateKeyPem();
            var connection = Create(e => { e.Algorithm = "PS256"; e.PrivateKey = pem; });
            var ex = Assert.Throws<NetSuiteException>(() => connection.CreateClientAssertion(FakeNetSuite.TokenUrl));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.Authentication));
            Assert.That(ex.Message, Does.Not.Contain("PRIVATE KEY"));
        }

        [TestCase("RS256")]
        [TestCase("PS")]
        [TestCase("PS999")]
        [TestCase("X")]
        public void JWT_扱えない方式はNetSuiteException(string algorithm)
        {
            var connection = Create(e => e.Algorithm = algorithm);
            Assert.Throws<NetSuiteException>(() => connection.CreateClientAssertion(FakeNetSuite.TokenUrl));
        }

        [Test]
        public void トークンが取れなければ認証のエラー()
        {
            _fake.Override(e => e.IsToken, _ => FakeNetSuite.Status(HttpStatusCode.BadRequest, """{"error":"invalid_client","error_description":"Invalid client assertion."}"""));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => QueryAsync(Create()));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.Authentication));
            Assert.That(ex.Message, Does.Contain("Invalid client assertion."));
            Assert.That(_fake.ApiRequests, Is.Empty);
        }

        [Test]
        public void トークンの要求が届かなければServiceUnavailable()
        {
            _fake.Throw(e => e.IsToken, () => new HttpRequestException("down"));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => QueryAsync(Create()));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));
        }

        // ---------------------------------------------------------------- やり直し

        [Test]
        public async Task 応答401でトークンを取り直して1回だけやり直す()
        {
            _fake.Override(SuiteQl, _ => FakeNetSuite.Status(HttpStatusCode.Unauthorized));
            var connection = Create();
            await QueryAsync(connection);

            Assert.That(_fake.TokenRequests, Has.Count.EqualTo(2));
            var queries = _fake.Requests.Where(e => e.IsSuiteQl).ToList();
            Assert.That(queries.Select(e => e.Header("Authorization")), Is.EqualTo(new[] { "Bearer token-1", "Bearer token-2" }));
            Assert.That(_delays, Is.Empty);
        }

        [Test]
        public void 応答401が続けば認証のエラー()
        {
            _fake.Override(SuiteQl, _ => FakeNetSuite.Status(HttpStatusCode.Unauthorized), times: 10);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => QueryAsync(Create()));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.Authentication));
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(_fake.Requests.Count(e => e.IsSuiteQl), Is.EqualTo(2));
        }

        [Test]
        public async Task 書き込みも401ならトークンを取り直してやり直す()
        {
            _fake.Override(RecordWrite, _ => FakeNetSuite.Status(HttpStatusCode.Unauthorized));
            var id = await Create().CreateRecordAsync("customer", Json("""{"companyName":"A"}"""));
            Assert.That(id, Is.EqualTo("123"));
            Assert.That(_fake.WriteRequests, Has.Count.EqualTo(2));
            Assert.That(_fake.TokenRequests, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task 応答429は読み出しをやり直す()
        {
            _fake.Override(SuiteQl, _ => FakeNetSuite.Status(HttpStatusCode.TooManyRequests), times: 2);
            await QueryAsync(Create());
            Assert.That(_fake.Requests.Count(e => e.IsSuiteQl), Is.EqualTo(3));
            Assert.That(_delays, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task 応答429は書き込みもやり直す()
        {
            _fake.Override(RecordWrite, _ => FakeNetSuite.Status(HttpStatusCode.TooManyRequests), times: 3);
            await Create().UpdateRecordAsync("customer", "5", Json("{}"));
            Assert.That(_fake.WriteRequests, Has.Count.EqualTo(4));
            Assert.That(_delays, Has.Count.EqualTo(3));
        }

        [Test]
        public void 応答429が続けば4回でRateLimited()
        {
            _fake.Override(RecordWrite, _ => FakeNetSuite.Status(HttpStatusCode.TooManyRequests), times: 100);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().DeleteRecordAsync("customer", "5"));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.RateLimited));
            Assert.That(_fake.WriteRequests, Has.Count.EqualTo(4));
            Assert.That(_delays, Has.Count.EqualTo(3));
        }

        [Test]
        public async Task RetryAfterがあればその時間待つ()
        {
            _fake.Override(SuiteQl, _ =>
            {
                var response = FakeNetSuite.Status(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return response;
            });
            await QueryAsync(Create());
            Assert.That(_delays, Is.EqualTo(new[] { TimeSpan.FromSeconds(7) }));
        }

        [TestCase(HttpStatusCode.ServiceUnavailable)]
        [TestCase(HttpStatusCode.BadGateway)]
        [TestCase(HttpStatusCode.GatewayTimeout)]
        public async Task 一時的な障害は読み出しだけやり直す(HttpStatusCode code)
        {
            _fake.Override(SuiteQl, _ => FakeNetSuite.Status(code), times: 3);
            await QueryAsync(Create());
            Assert.That(_fake.Requests.Count(e => e.IsSuiteQl), Is.EqualTo(4));
            Assert.That(_delays, Has.Count.EqualTo(3));
        }

        [Test]
        public void 応答503が続けば読み出しはServiceUnavailable()
        {
            _fake.Override(SuiteQl, _ => FakeNetSuite.Status(HttpStatusCode.ServiceUnavailable), times: 100);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => QueryAsync(Create()));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));
            Assert.That(_fake.Requests.Count(e => e.IsSuiteQl), Is.EqualTo(4));
        }

        [Test]
        public void 応答503の書き込みはやり直さずエラー()
        {
            _fake.Override(RecordWrite, _ => FakeNetSuite.Status(HttpStatusCode.ServiceUnavailable));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().CreateRecordAsync("customer", Json("{}")));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(_fake.WriteRequests, Has.Count.EqualTo(1));
            Assert.That(_delays, Is.Empty);
        }

        [Test]
        public void 書き込みで通信が失敗したら結果不明()
        {
            _fake.Throw(RecordWrite, () => new HttpRequestException("connection reset"));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().CreateRecordAsync("customer", Json("{}")));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.WriteResultUnknown));
            Assert.That(ex.InnerException, Is.TypeOf<HttpRequestException>());
            Assert.That(_fake.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public void 書き込みがタイムアウトしたら結果不明()
        {
            _fake.Throw(RecordWrite, () => new TaskCanceledException("timeout"));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().UpdateRecordAsync("customer", "5", Json("{}")));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.WriteResultUnknown));
            Assert.That(_fake.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task 読み出しの通信失敗はやり直す()
        {
            _fake.Throw(SuiteQl, () => new HttpRequestException("connection reset"), times: 2);
            await QueryAsync(Create());
            Assert.That(_fake.Requests.Count(e => e.IsSuiteQl), Is.EqualTo(3));
            Assert.That(_delays, Has.Count.EqualTo(2));
        }

        [Test]
        public void 読み出しの通信失敗が続けばServiceUnavailable()
        {
            _fake.Throw(SuiteQl, () => new HttpRequestException("connection reset"), times: 100);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => QueryAsync(Create()));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));
            Assert.That(ex.InnerException, Is.TypeOf<HttpRequestException>());
            Assert.That(_fake.Requests.Count(e => e.IsSuiteQl), Is.EqualTo(4));
            Assert.That(_delays, Has.Count.EqualTo(3));
        }

        // ---------------------------------------------------------------- エラーの分類

        [Test]
        public void 応答400はInvalidRequestでerrorDetailsを持つ()
        {
            _fake.Override(RecordWrite, _ => FakeNetSuite.ErrorDetails(HttpStatusCode.BadRequest,
                ("Invalid value for companyName.", "INVALID_FLD_VALUE", "companyName"),
                ("Please enter a value for Subsidiary.", "USER_ERROR", "subsidiary")));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().CreateRecordAsync("customer", Json("{}")));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.InvalidRequest));
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(ex.Message, Does.Contain("Invalid value for companyName.").And.Contain("Please enter a value for Subsidiary."));
            Assert.That(ex.ErrorCode, Is.EqualTo("INVALID_FLD_VALUE"));
            Assert.That(ex.ErrorPath, Is.EqualTo("companyName"));
        }

        [Test]
        public void 応答403はPermissionDenied()
        {
            _fake.Override(SuiteQl, _ => FakeNetSuite.ErrorDetails(HttpStatusCode.Forbidden, ("Permission Violation.", "INSUFFICIENT_PERMISSION", "")));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => QueryAsync(Create()));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.PermissionDenied));
            Assert.That(ex.ErrorCode, Is.EqualTo("INSUFFICIENT_PERMISSION"));
            Assert.That(ex.Message, Does.Contain("Permission Violation."));
        }

        [Test]
        public void 応答404はNotFound()
        {
            _fake.Override(e => e.IsMetadata, _ => FakeNetSuite.ErrorDetails(HttpStatusCode.NotFound, ("Record type not found.", "NONEXISTENT_ID", "")));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().FetchRecordDefinitionAsync("customer", "customer"));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.NotFound));
            Assert.That(ex.ErrorCode, Is.EqualTo("NONEXISTENT_ID"));
        }

        [Test]
        public async Task 更新と削除の404は対象なしでfalse()
        {
            _fake.Override(RecordWrite, _ => FakeNetSuite.ErrorDetails(HttpStatusCode.NotFound, ("That record does not exist.", "NONEXISTENT_ID", "")), times: 2);
            var connection = Create();
            Assert.That(await connection.UpdateRecordAsync("customer", "999", Json("{}")), Is.False);
            Assert.That(await connection.DeleteRecordAsync("customer", "999"), Is.False);
        }

        [Test]
        public void 本文がJSONでないエラーはHTTPの番号を出す()
        {
            _fake.Override(RecordWrite, _ => FakeNetSuite.Status(HttpStatusCode.Conflict, "<html>oops</html>"));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().CreateRecordAsync("customer", Json("{}")));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.InvalidRequest));
            Assert.That(ex.Message, Does.Contain("HTTP 409"));
        }

        [TestCase("[]")]
        [TestCase("\"Service Unavailable\"")]
        [TestCase("null")]
        public void 本文がオブジェクトでないJSONでもNetSuiteExceptionにする(string body)
        {
            //ゲートウェイなどが返す、オブジェクトでない JSON の本文
            _fake.Override(RecordWrite, _ => FakeNetSuite.Status(HttpStatusCode.BadGateway, body));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().CreateRecordAsync("customer", Json("{}")));
            //書き込みへの 502 は、実行されたかどうかが分からない
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.WriteResultUnknown));
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
            Assert.That(ex.Message, Does.Contain("HTTP 502"));
        }

        [TestCase("[]")]
        [TestCase("\"Bad Gateway\"")]
        public void 読み出しで本文がオブジェクトでないJSONの502が続けばServiceUnavailable(string body)
        {
            _fake.Override(SuiteQl, _ => FakeNetSuite.Status(HttpStatusCode.BadGateway, body), times: 100);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => QueryAsync(Create()));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));
            Assert.That(ex.Message, Does.Contain("HTTP 502"));
        }

        // ---------------------------------------------------------------- 書き込みへの 5xx

        static Task WriteAsync(NetSuiteConnection connection, string method) => method switch
        {
            "POST" => connection.CreateRecordAsync("customer", Json("""{"companyName":"A"}""")),
            "PATCH" => connection.UpdateRecordAsync("customer", "5", Json("""{"companyName":"B"}""")),
            _ => connection.DeleteRecordAsync("customer", "5"),
        };

        [Test]
        public void 書き込みへの5xxは503以外なら結果不明でやり直さない(
            [Values(HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.GatewayTimeout, HttpStatusCode.NotImplemented)] HttpStatusCode code,
            [Values("POST", "PATCH", "DELETE")] string method)
        {
            _fake.Override(RecordWrite, _ => FakeNetSuite.ErrorDetails(code, ("An unexpected error occurred.", "UNEXPECTED_ERROR", "")), times: 100);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => WriteAsync(Create(), method));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.WriteResultUnknown));
            Assert.That(ex.StatusCode, Is.EqualTo(code));
            //NetSuite のエラーの中身は残す
            Assert.That(ex.Message, Does.Contain("An unexpected error occurred."));
            Assert.That(ex.ErrorCode, Is.EqualTo("UNEXPECTED_ERROR"));
            //同じ書き込みをもう一度送らない
            Assert.That(_fake.WriteRequests, Has.Count.EqualTo(1));
            Assert.That(_fake.WriteRequests[0].Method.Method, Is.EqualTo(method));
            Assert.That(_delays, Is.Empty);
        }

        [Test]
        public void 書き込みへの503は受け付けていないのでServiceUnavailableでやり直さない([Values("POST", "PATCH", "DELETE")] string method)
        {
            _fake.Override(RecordWrite, _ => FakeNetSuite.Status(HttpStatusCode.ServiceUnavailable), times: 100);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => WriteAsync(Create(), method));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));
            Assert.That(ex.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(_fake.WriteRequests, Has.Count.EqualTo(1));
            Assert.That(_delays, Is.Empty);
        }

        [Test]
        public void 書き込みへの4xxは結果不明にしない([Values("POST", "PATCH")] string method)
        {
            _fake.Override(RecordWrite, _ => FakeNetSuite.ErrorDetails(HttpStatusCode.BadRequest, ("Invalid value.", "INVALID_FLD_VALUE", "companyName")));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => WriteAsync(Create(), method));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.InvalidRequest));
            Assert.That(_fake.WriteRequests, Has.Count.EqualTo(1));
        }

        [Test]
        public void 書き込みへの401のあとの5xxも結果不明()
        {
            //401 は実行されていないので 1 回やり直す。やり直した書き込みへの 500 は結果不明
            _fake.Override(RecordWrite, _ => FakeNetSuite.Status(HttpStatusCode.Unauthorized));
            _fake.Override(RecordWrite, _ => FakeNetSuite.Status(HttpStatusCode.InternalServerError));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().CreateRecordAsync("customer", Json("{}")));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.WriteResultUnknown));
            Assert.That(_fake.WriteRequests, Has.Count.EqualTo(2));
        }

        [TestCase(HttpStatusCode.InternalServerError)]
        [TestCase(HttpStatusCode.NotImplemented)]
        public void 読み出しの500と501はやり直さずServiceUnavailable(HttpStatusCode code)
        {
            _fake.Override(SuiteQl, _ => FakeNetSuite.Status(code), times: 100);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => QueryAsync(Create()));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));
            Assert.That(ex.StatusCode, Is.EqualTo(code));
            Assert.That(_fake.Requests.Count(e => e.IsSuiteQl), Is.EqualTo(1));
            Assert.That(_delays, Is.Empty);
        }

        [TestCase(HttpStatusCode.BadGateway)]
        [TestCase(HttpStatusCode.GatewayTimeout)]
        public void 読み出しの502と504が続けば4回でServiceUnavailable(HttpStatusCode code)
        {
            _fake.Override(SuiteQl, _ => FakeNetSuite.Status(code), times: 100);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => QueryAsync(Create()));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));
            Assert.That(ex.StatusCode, Is.EqualTo(code));
            Assert.That(_fake.Requests.Count(e => e.IsSuiteQl), Is.EqualTo(4));
            Assert.That(_delays, Has.Count.EqualTo(3));
        }

        [Test]
        public void 定義の読み出しも5xxは結果不明にしない()
        {
            _fake.Override(e => e.IsMetadata, _ => FakeNetSuite.Status(HttpStatusCode.InternalServerError));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().FetchRecordDefinitionAsync("customer", "customer"));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));
        }

        // ---------------------------------------------------------------- レコードの定義 (メタ情報の更新)

        [Test]
        public async Task レコードの定義はmetadata_catalogからJSON_Schemaで読む()
        {
            var definition = await Create().FetchRecordDefinitionAsync("customer", "customer");
            Assert.That(definition.FindByColumn("companyname"), Is.Not.Null);
            //明細の $ref の先 (customer-addressBookCollection) を除くと、レコードの定義の 1 回
            var metadata = _fake.Requests.Where(e => e.IsMetadata && !e.Path.EndsWith("Collection")).ToList();
            Assert.That(metadata, Has.Count.EqualTo(1));
            Assert.That(metadata[0].Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(metadata[0].Url, Is.EqualTo(FakeNetSuite.RestUrl + "/record/v1/metadata-catalog/customer"));
            Assert.That(metadata[0].Header("Accept"), Is.EqualTo("application/schema+json"));
        }

        const string SimpleSchema = """{ "type": "object", "properties": { "id": { "type": "string" }, "name": { "type": "string" } } }""";

        [Test]
        public async Task 全レコードの定義はレコード種別の一覧から読みSuiteQLで読めるものと対応表の取引だけを小文字の名前で返す()
        {
            _fake.Schemas["Employee"] = SimpleSchema;
            _fake.Schemas["salesOrder"] = SimpleSchema;
            _fake.Schemas["vendor"] = SimpleSchema;
            _fake.Schemas["customrecord_gone"] = SimpleSchema;
            _fake.Schemas["bad name"] = SimpleSchema;
            //vendor は SuiteQL に表が無い (REST だけのレコード・対応表に無い取引と同じ)
            _fake.Override(e => e.IsSuiteQl && e.Body.Contains("from vendor"), _ => FakeNetSuite.ErrorDetails(HttpStatusCode.BadRequest, ("Invalid search query.", "INVALID_PARAMETER", "")));
            //customrecord_gone は一覧にあっても定義が読めない
            _fake.Override(e => e.IsMetadata && e.Path.EndsWith("/customrecord_gone"), _ => FakeNetSuite.ErrorDetails(HttpStatusCode.NotFound, ("Record type not found.", "NONEXISTENT_ID", "")));

            var records = await Create().FetchRecordDefinitionsAsync();

            Assert.That(records.Select(e => e.Name), Is.EqualTo(new[] { "customer", "employee", "salesorder", "subsidiary" }));
            Assert.That(_fake.Requests.Count(e => e.IsMetadataCatalog), Is.EqualTo(1));
            //取引 (対応表にある salesorder) は SuiteQL で確かめない。名前に使えない文字の入ったものは要求を送らない
            var probes = _fake.Requests.Where(e => e.IsSuiteQl).ToList();
            Assert.That(probes.Select(e => e.Json!["q"]!.GetValue<string>()).OrderBy(e => e, StringComparer.Ordinal),
                Is.EqualTo(new[] { "select id from customer", "select id from customrecord_gone", "select id from employee", "select id from subsidiary", "select id from vendor" }));
            Assert.That(probes.All(e => e.Query("limit") == "1" && e.Query("offset") == "0"), Is.True);
            //定義は一覧の名前のまま (大文字を含む) で読む
            Assert.That(_fake.Requests.Where(e => e.IsMetadata).Select(e => e.Path.Split('/').Last()), Does.Contain("salesOrder").And.Contain("Employee").And.Not.Contain("vendor"));
        }

        [TestCase(HttpStatusCode.InternalServerError, NetSuiteErrorKind.ServiceUnavailable, TestName = "サーバー側の失敗")]
        [TestCase(HttpStatusCode.Unauthorized, NetSuiteErrorKind.Authentication, TestName = "認証の失敗")]
        public void 全レコードの定義の読み出しは飛ばす失敗以外で止まる(HttpStatusCode code, NetSuiteErrorKind kind)
        {
            _fake.Override(e => e.IsSuiteQl && e.Body.Contains("from subsidiary"), _ => FakeNetSuite.Status(code), times: 2);
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().FetchRecordDefinitionsAsync());
            Assert.That(ex!.Kind, Is.EqualTo(kind));
        }

        [Test]
        public void 止める失敗が出たらまだ始めていないレコード種別の要求は送らない()
        {
            for (var i = 0; i < 200; i++) _fake.Schemas[$"customrecord_r{i:D3}"] = SimpleSchema;
            var types = _fake.Schemas.Keys.Count(e => !e.Contains('-'));
            //SuiteQL がずっと通信の失敗 (読み出しなので、始めた種別はそれぞれ再試行をやり切る)
            _fake.Throw(e => e.IsSuiteQl, () => new HttpRequestException("connection reset"), int.MaxValue);
            var connection = Create();

            var ex = Assert.ThrowsAsync<NetSuiteException>(() => connection.FetchRecordDefinitionsAsync());

            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.ServiceUnavailable));
            //全部の種別を始めると「種別 × 再試行の回数」の要求になる。始めたのは同時実行の数ほどだけ
            var started = _fake.Requests.Where(e => e.IsSuiteQl).Select(e => e.Json!["q"]!.GetValue<string>()).Distinct().Count();
            Assert.That(started, Is.LessThanOrEqualTo(connection.Settings.MaxConcurrency * 2));
            Assert.That(_fake.ApiRequests, Has.Count.LessThan(types * 2 / 4));
        }

        const string SalesOrderWithItemSchema = """
            {"type":"object","properties":{"id":{"type":"string"},"memo":{"type":"string"},
              "item":{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"line":{"type":"integer"},"quantity":{"type":"number"}}}}}}}}
            """;

        [Test]
        public async Task 明細の表と同じ名前のレコードがあれば明細の表を採る()
        {
            _fake.Schemas["salesOrder"] = SalesOrderWithItemSchema;
            //明細の表 (salesorder_item) と同じ名前の、SuiteQL で読めるレコード
            _fake.Schemas["salesorder_item"] = SimpleSchema;

            var records = await Create().FetchRecordDefinitionsAsync();

            Assert.That(records.Select(e => e.Name), Is.EqualTo(new[] { "customer", "salesorder", "salesorder_item", "subsidiary" }));
            var table = records.Single(e => e.Name == "salesorder_item");
            Assert.That(table.IsLineTable, Is.True);
            Assert.That((table.LineParent, table.LineSublist), Is.EqualTo(("salesorder", "item")));
            Assert.That(table.Fields.Select(e => e.Column), Is.EqualTo(new[] { "uniquekey", "transaction", "linesequencenumber", "quantity" }));
        }

        // ---------------------------------------------------------------- 書き込みの要求

        [Test]
        public async Task 書き込みに項目名と値の検証ヘッダーが付く()
        {
            var connection = Create();
            var id = await connection.CreateRecordAsync("customer", Json("""{"companyName":"A"}"""));
            await connection.UpdateRecordAsync("customer", "5", Json("""{"companyName":"B"}"""));
            await connection.DeleteRecordAsync("customer", "5");

            Assert.That(id, Is.EqualTo("123"));
            var writes = _fake.WriteRequests;
            Assert.That(writes.Select(e => (e.Method.Method, e.Url)), Is.EqualTo(new[]
            {
                ("POST", FakeNetSuite.RestUrl + "/record/v1/customer"),
                ("PATCH", FakeNetSuite.RestUrl + "/record/v1/customer/5"),
                ("DELETE", FakeNetSuite.RestUrl + "/record/v1/customer/5"),
            }));
            foreach (var write in writes.Take(2))
            {
                Assert.That(write.Header("X-NetSuite-PropertyNameValidation"), Is.EqualTo("Error"));
                Assert.That(write.Header("X-NetSuite-PropertyValueValidation"), Is.EqualTo("Error"));
                Assert.That(write.Header("Content-Type"), Does.StartWith("application/json"));
            }
            Assert.That(writes[0].Body, Is.EqualTo("""{"companyName":"A"}"""));
            Assert.That(writes[1].Body, Is.EqualTo("""{"companyName":"B"}"""));
        }

        [Test]
        public void 作成の応答にLocationが無ければ結果不明()
        {
            _fake.Override(RecordWrite, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
            var ex = Assert.ThrowsAsync<NetSuiteException>(() => Create().CreateRecordAsync("customer", Json("{}")));
            Assert.That(ex!.Kind, Is.EqualTo(NetSuiteErrorKind.WriteResultUnknown));
        }

        // ---------------------------------------------------------------- SuiteQL

        [Test]
        public async Task SuiteQLの要求()
        {
            var connection = Create();
            await connection.QuerySuiteQlAsync("select id from customer where companyname = ?", new List<string> { "A\"B" }, 50, 100);
            await connection.QuerySuiteQlAsync("select id from customer", new List<string>(), 1000, 0);

            var requests = _fake.Requests.Where(e => e.IsSuiteQl).ToList();
            Assert.That(requests[0].Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(requests[0].Url, Is.EqualTo(FakeNetSuite.RestUrl + "/query/v1/suiteql?limit=50&offset=100"));
            Assert.That(requests[0].Header("Prefer"), Is.EqualTo("transient"));
            Assert.That(requests[0].Header("Content-Type"), Does.StartWith("application/json"));
            Assert.That(_fake.SuiteQlCalls[0].Q, Is.EqualTo("select id from customer where companyname = ?"));
            Assert.That(_fake.SuiteQlCalls[0].Params, Is.EqualTo(new[] { "A\"B" }));
            //パラメータが無ければ params を送らない
            Assert.That(_fake.SuiteQlCalls[1].HasParamsProperty, Is.False);
            Assert.That(requests[1].Header("Prefer"), Is.EqualTo("transient"));
        }

        [Test]
        public async Task SuiteQLの応答を読む()
        {
            _fake.Override(SuiteQl, _ => FakeNetSuite.Status(HttpStatusCode.OK, """
                {"links":[],"count":2,"hasMore":true,"items":[
                  {"links":[{"rel":"self"}],"id":"1","name":"A","flag":true,"amount":12.5,"nothing":null},
                  {"links":[],"id":"2","FLAG":false}
                ],"offset":0,"totalResults":5}
                """));
            var page = await QueryAsync(Create());
            Assert.That(page.HasMore, Is.True);
            Assert.That(page.Items, Has.Count.EqualTo(2));
            Assert.That(page.Items[0].ContainsKey("links"), Is.False);
            Assert.That(page.Items[0]["id"], Is.EqualTo("1"));
            Assert.That(page.Items[0]["name"], Is.EqualTo("A"));
            Assert.That(page.Items[0]["flag"], Is.EqualTo("T"));
            Assert.That(page.Items[0]["amount"], Is.EqualTo("12.5"));
            Assert.That(page.Items[0]["nothing"], Is.Null);
            //列名は大文字小文字を区別しない
            Assert.That(page.Items[1]["flag"], Is.EqualTo("F"));
        }
    }
}
