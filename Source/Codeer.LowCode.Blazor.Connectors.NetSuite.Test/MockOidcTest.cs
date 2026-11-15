using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>
    /// モックの OpenID Connect のプロバイダー (アプリのログイン): discovery・ログイン画面・認可コード + PKCE・ID トークン (PS256)。
    /// ID トークンの確かめは ASP.NET Core の OpenID Connect と同じ部品 (Microsoft.IdentityModel) でする。
    /// </summary>
    public class MockOidcTest
    {
        const string ClientId = "test-client";
        const string ClientSecret = "test-secret";
        const string RedirectUri = "https://app.example.com/signin-netsuite";

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

        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        static Dictionary<string, string> AuthorizeParameters(string verifier, string scope = "openid email", string responseMode = "query") => new()
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["response_mode"] = responseMode,
            ["scope"] = scope,
            ["state"] = "state-1",
            ["nonce"] = "nonce-1",
            ["code_challenge"] = Challenge(verifier),
            ["code_challenge_method"] = "S256",
        };

        static string Query(Dictionary<string, string> values) => string.Join("&", values.Select(e => $"{e.Key}={Uri.EscapeDataString(e.Value)}"));

        //画面で従業員を選んで、戻り先へのクエリ (code / state / error)
        async Task<Dictionary<string, string>> SelectEmployeeAsync(Dictionary<string, string> parameters, string employeeId)
        {
            var form = new Dictionary<string, string>(parameters) { ["employee"] = employeeId };
            using var response = await _client.PostAsync("/app/login/oauth2/authorize.nl", new FormUrlEncodedContent(form));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
            var location = response.Headers.Location!.ToString();
            Assert.That(location, Does.StartWith(RedirectUri + "?"));
            return location[(RedirectUri.Length + 1)..].Split('&').Select(e => e.Split('=', 2)).ToDictionary(e => e[0], e => Uri.UnescapeDataString(e[1]));
        }

        async Task<(HttpStatusCode Status, JsonObject Body)> TokenAsync(Dictionary<string, string> form)
        {
            using var response = await _client.PostAsync("/services/rest/auth/oauth2/v1/token", new FormUrlEncodedContent(form));
            return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
        }

        static Dictionary<string, string> TokenForm(string code, string verifier) => new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
        };

        [Test]
        public async Task discoveryは自分のURLをissuerにしてPS256とS256を出す()
        {
            var config = JsonNode.Parse(await _client.GetStringAsync("/.well-known/openid-configuration"))!;
            Assert.That(config["issuer"]!.GetValue<string>(), Is.EqualTo(MockHarness.ServiceUrl));
            Assert.That(config["authorization_endpoint"]!.GetValue<string>(), Is.EqualTo(MockHarness.ServiceUrl + "/app/login/oauth2/authorize.nl"));
            Assert.That(config["token_endpoint"]!.GetValue<string>(), Is.EqualTo(MockHarness.ServiceUrl + "/services/rest/auth/oauth2/v1/token"));
            Assert.That(config["jwks_uri"]!.GetValue<string>(), Is.EqualTo(MockHarness.ServiceUrl + "/services/rest/auth/oauth2/v1/keys"));
            Assert.That(config["id_token_signing_alg_values_supported"]!.AsArray().Select(e => e!.GetValue<string>()), Is.EqualTo(new[] { "PS256" }));
            Assert.That(config["scopes_supported"]!.AsArray().Select(e => e!.GetValue<string>()), Is.EqualTo(new[] { "openid", "email" }));
            Assert.That(config["code_challenge_methods_supported"]!.AsArray().Select(e => e!.GetValue<string>()), Is.EqualTo(new[] { "S256" }));
        }

        [Test]
        public async Task ログイン画面にはメールがあり無効でない従業員だけが出る()
        {
            var html = await _client.GetStringAsync("/app/login/oauth2/authorize.nl?" + Query(AuthorizeParameters("v".PadRight(43, 'x'))));
            var ids = Regex.Matches(html, "id=\"employee-(\\d+)\"").Select(e => e.Groups[1].Value).ToList();
            //4 = 無効 (isInactive)、5 = メールなし
            Assert.That(ids, Is.EqualTo(new[] { "1", "2", "3" }));
            Assert.That(html, Does.Contain("ichiro.sato@example.com"));
        }

        [Test]
        public async Task 認可コードとPKCEでIDトークンが返りASPNETCoreと同じ検証に通る()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var parameters = AuthorizeParameters(verifier);
            var redirect = await SelectEmployeeAsync(parameters, "1");
            Assert.That(redirect["state"], Is.EqualTo("state-1"));

            var (status, body) = await TokenAsync(TokenForm(redirect["code"], verifier));
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToJsonString());
            var idToken = body["id_token"]!.GetValue<string>();
            var accessToken = body["access_token"]!.GetValue<string>();
            Assert.That(body["token_type"]!.GetValue<string>(), Is.EqualTo("Bearer"));

            //署名 (JWKS の鍵)・発行者・受け手・期限
            var keys = new JsonWebKeySet(await _client.GetStringAsync("/services/rest/auth/oauth2/v1/keys"));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            handler.ValidateToken(idToken, new TokenValidationParameters
            {
                ValidIssuer = MockHarness.ServiceUrl,
                ValidAudience = ClientId,
                IssuerSigningKeys = keys.GetSigningKeys(),
                ValidAlgorithms = new[] { SecurityAlgorithms.RsaSsaPssSha256 },
            }, out var validated);
            var jwt = (JwtSecurityToken)validated;
            Assert.That(jwt.Header.Alg, Is.EqualTo("PS256"));
            Assert.That(jwt.Subject, Is.EqualTo("1111;1"));
            Assert.That(jwt.Payload["email"], Is.EqualTo("ichiro.sato@example.com"));
            Assert.That(jwt.Payload.ContainsKey("name"), Is.False);

            //nonce と at_hash (ASP.NET Core の OpenID Connect がトークンの応答に対してする確かめ)
            //nonce の形 (時刻つき) は ASP.NET Core が作るもの。モックは受け取った nonce をそのまま返すだけなので時刻は見ない
            var validator = new OpenIdConnectProtocolValidator { RequireStateValidation = false, RequireTimeStampInNonce = false };
            Assert.DoesNotThrow(() => validator.ValidateTokenResponse(new OpenIdConnectProtocolValidationContext
            {
                ProtocolMessage = new OpenIdConnectMessage { IdToken = idToken, AccessToken = accessToken },
                ValidatedIdToken = jwt,
                Nonce = "nonce-1",
            }));
            Assert.Throws<OpenIdConnectProtocolInvalidAtHashException>(() => validator.ValidateTokenResponse(new OpenIdConnectProtocolValidationContext
            {
                ProtocolMessage = new OpenIdConnectMessage { IdToken = idToken, AccessToken = accessToken + "x" },
                ValidatedIdToken = jwt,
                Nonce = "nonce-1",
            }));
        }

        [Test]
        public async Task emailのスコープが無ければemailのクレームは無い()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var redirect = await SelectEmployeeAsync(AuthorizeParameters(verifier, "openid"), "2");
            var (_, body) = await TokenAsync(TokenForm(redirect["code"], verifier));
            var jwt = new JwtSecurityToken(body["id_token"]!.GetValue<string>());
            Assert.That(jwt.Subject, Is.EqualTo("1111;2"));
            Assert.That(jwt.Payload.ContainsKey("email"), Is.False);
        }

        [Test]
        public async Task 違うverifier_2回目のコード_シークレットなしは通らない()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var code = (await SelectEmployeeAsync(AuthorizeParameters(verifier), "1"))["code"];

            var noSecret = TokenForm(code, verifier);
            noSecret.Remove("client_secret");
            Assert.That((await TokenAsync(noSecret)).Status, Is.EqualTo(HttpStatusCode.Unauthorized));

            var (wrong, wrongBody) = await TokenAsync(TokenForm(code, verifier + "x"));
            Assert.That((wrong, wrongBody["error"]!.GetValue<string>()), Is.EqualTo((HttpStatusCode.BadRequest, "invalid_grant")));

            //1 回失敗したコードも、使ったコードとして消える
            Assert.That((await TokenAsync(TokenForm(code, verifier))).Status, Is.EqualTo(HttpStatusCode.BadRequest));

            var code2 = (await SelectEmployeeAsync(AuthorizeParameters(verifier), "1"))["code"];
            Assert.That((await TokenAsync(TokenForm(code2, verifier))).Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That((await TokenAsync(TokenForm(code2, verifier))).Status, Is.EqualTo(HttpStatusCode.BadRequest));
        }

        [Test]
        public async Task client_secret_basicでも通る()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var code = (await SelectEmployeeAsync(AuthorizeParameters(verifier), "3"))["code"];
            var form = TokenForm(code, verifier);
            form.Remove("client_id");
            form.Remove("client_secret");
            using var request = new HttpRequestMessage(HttpMethod.Post, "/services/rest/auth/oauth2/v1/token") { Content = new FormUrlEncodedContent(form) };
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));
            using var response = await _client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task ログインできない従業員とPKCEなしは通らない()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var form = new Dictionary<string, string>(AuthorizeParameters(verifier)) { ["employee"] = "4" };
            using (var response = await _client.PostAsync("/app/login/oauth2/authorize.nl", new FormUrlEncodedContent(form)))
            {
                //無効の従業員は選べない (画面に戻る)
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("id=\"error\""));
            }

            var noPkce = AuthorizeParameters(verifier);
            noPkce.Remove("code_challenge");
            using (var response = await _client.GetAsync("/app/login/oauth2/authorize.nl?" + Query(noPkce)))
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
                Assert.That(response.Headers.Location!.ToString(), Does.Contain("error=invalid_request"));
            }
        }

        [Test]
        public async Task form_postなら戻り先へ送るformを返す()
        {
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var form = new Dictionary<string, string>(AuthorizeParameters(verifier, responseMode: "form_post")) { ["employee"] = "1" };
            using var response = await _client.PostAsync("/app/login/oauth2/authorize.nl", new FormUrlEncodedContent(form));
            var html = await response.Content.ReadAsStringAsync();
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(html, Does.Contain($"action=\"{RedirectUri}\""));
            Assert.That(html, Does.Contain("name=\"code\""));
            Assert.That(html, Does.Contain("name=\"state\" value=\"state-1\""));
        }
    }
}
