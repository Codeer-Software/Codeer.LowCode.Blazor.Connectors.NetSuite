using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetSuite.Mock
{
    /// <summary>
    /// OpenID Connect のプロバイダー (NetSuite のアカウントでアプリにログインする)。公開仕様 (NetSuite as OIDC Provider) に寄せる:
    /// 認可コード + PKCE (S256)・スコープは openid と email・ID トークンは PS256・sub は「ロール;従業員の id」・表示名のクレームは無い。
    /// ログイン画面は employee のレコードからログインできる人 (メールがある・無効でない) を選ぶだけ (パスワードは無い)。
    /// クライアント ID・シークレットは中身を確かめない (空でなければ通す)。iss は要求を受けた URL (discovery の issuer と同じ)。
    /// </summary>
    public sealed partial class MockNetSuite
    {
        /// <summary>認可の入口 (実物は https://&lt;アカウント ID&gt;.app.netsuite.com/app/login/oauth2/authorize.nl)。</summary>
        public const string AuthorizePath = "/app/login/oauth2/authorize.nl";

        /// <summary>ID トークンの sub のロールの部分 (モックのロールは 1 つだけ)。</summary>
        public const string MockRoleId = "1111";

        /// <summary>認可コードの有効期間。</summary>
        public static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromMinutes(5);

        //ID トークンの署名の鍵 (起動ごとに作る。鍵の公開側は JWKS で返す)
        readonly RSA _signingKey = RSA.Create(2048);
        readonly string _keyId = Base64Url(RandomNumberGenerator.GetBytes(8));
        readonly Dictionary<string, AuthorizationCode> _codes = new(StringComparer.Ordinal);

        sealed class AuthorizationCode
        {
            public string ClientId { get; init; } = string.Empty;
            public string RedirectUri { get; init; } = string.Empty;
            public string CodeChallenge { get; init; } = string.Empty;
            public string? Nonce { get; init; }
            public bool Email { get; init; }
            public long EmployeeId { get; init; }
            public string? EmployeeEmail { get; init; }
            public DateTime ExpiresAt { get; init; }
        }

        // ---------------------------------------------------------------- discovery・鍵

        /// <summary>GET /.well-known/openid-configuration。</summary>
        public MockResponse GetOpenIdConfiguration(string baseUrl)
            => Json(200, w =>
            {
                w.WriteStartObject();
                w.WriteString("issuer", baseUrl);
                w.WriteString("authorization_endpoint", baseUrl + AuthorizePath);
                w.WriteString("token_endpoint", baseUrl + "/services/rest/auth/oauth2/v1/token");
                w.WriteString("jwks_uri", baseUrl + "/services/rest/auth/oauth2/v1/keys");
                WriteArray(w, "response_types_supported", "code");
                WriteArray(w, "response_modes_supported", "query", "form_post");
                WriteArray(w, "grant_types_supported", "authorization_code", "client_credentials");
                WriteArray(w, "subject_types_supported", "public");
                WriteArray(w, "id_token_signing_alg_values_supported", "PS256");
                WriteArray(w, "scopes_supported", "openid", "email");
                WriteArray(w, "claims_supported", "iss", "sub", "aud", "exp", "iat", "auth_time", "nonce", "at_hash", "email");
                WriteArray(w, "code_challenge_methods_supported", "S256");
                WriteArray(w, "token_endpoint_auth_methods_supported", "client_secret_basic", "client_secret_post");
                w.WriteEndObject();
            });

        static void WriteArray(Utf8JsonWriter w, string name, params string[] values)
        {
            w.WriteStartArray(name);
            foreach (var value in values) w.WriteStringValue(value);
            w.WriteEndArray();
        }

        /// <summary>GET services/rest/auth/oauth2/v1/keys (ID トークンの署名を確かめる鍵)。</summary>
        public MockResponse GetSigningKeys()
        {
            var parameters = _signingKey.ExportParameters(false);
            return Json(200, w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("keys");
                w.WriteStartObject();
                w.WriteString("kty", "RSA");
                w.WriteString("use", "sig");
                w.WriteString("alg", "PS256");
                w.WriteString("kid", _keyId);
                w.WriteString("n", Base64Url(parameters.Modulus!));
                w.WriteString("e", Base64Url(parameters.Exponent!));
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            });
        }

        // ---------------------------------------------------------------- 認可 (ログイン画面)

        /// <summary>ログイン画面に出す従業員 (メールがある・無効でない)。employee のレコードが無ければ空。</summary>
        public List<(long Id, string Name, string Email)> GetLoginEmployees()
        {
            var record = FindRecord("employee");
            if (record?.FindField("email") is not { } email) return new();
            var name = record.FindField("entityId");
            var inactive = record.FindField("isInactive");
            lock (_lock)
            {
                using var command = _db.CreateCommand();
                command.CommandText = $"select id, {(name == null ? "NULL" : $"\"{name.Column}\"")}, \"{email.Column}\" from \"{record.Name}\" " +
                    $"where \"{email.Column}\" is not null and \"{email.Column}\" <> ''" +
                    (inactive == null ? string.Empty : $" and (\"{inactive.Column}\" is null or \"{inactive.Column}\" <> 'T')") + " order by id";
                using var reader = command.ExecuteReader();
                var list = new List<(long, string, string)>();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    list.Add((id, reader.IsDBNull(1) ? id.ToString(CultureInfo.InvariantCulture) : reader.GetString(1), reader.GetString(2)));
                }
                return list;
            }
        }

        /// <summary>
        /// 認可の入口。GET (submitted = false) はログイン画面、POST (submitted = true) は画面で選んだ結果 (employee = 従業員の id / cancel)。
        /// 選ばれたら redirect_uri へ code と state を返す (response_mode=form_post なら自動で送る form、それ以外はクエリ)。
        /// </summary>
        public MockResponse Authorize(IReadOnlyDictionary<string, string> values, bool submitted)
        {
            string Get(string key) => values.TryGetValue(key, out var v) ? v : string.Empty;
            var clientId = Get("client_id");
            var redirectUri = Get("redirect_uri");
            var state = Get("state");
            var responseMode = Get("response_mode");

            //戻り先が分からない誤りは画面に出す (戻さない)
            if (string.IsNullOrEmpty(clientId)) return Page(400, "エラー", "<p>client_id がありません。</p>");
            if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect) || (redirect.Scheme != Uri.UriSchemeHttps && redirect.Scheme != Uri.UriSchemeHttp))
                return Page(400, "エラー", "<p>redirect_uri が正しくありません。</p>");

            if (Get("response_type") != "code") return Redirect(redirectUri, responseMode, ("error", "unsupported_response_type"), ("state", state));
            var scopes = Get("scope").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!scopes.Contains("openid")) return Redirect(redirectUri, responseMode, ("error", "invalid_scope"), ("error_description", "The scope must contain openid."), ("state", state));
            var unknown = scopes.FirstOrDefault(e => e != "openid" && e != "email");
            if (unknown != null) return Redirect(redirectUri, responseMode, ("error", "invalid_scope"), ("error_description", $"Unknown scope '{unknown}'."), ("state", state));
            if (string.IsNullOrEmpty(Get("code_challenge")) || Get("code_challenge_method") != "S256")
                return Redirect(redirectUri, responseMode, ("error", "invalid_request"), ("error_description", "PKCE (code_challenge with code_challenge_method=S256) is required."), ("state", state));

            var employees = GetLoginEmployees();
            if (!submitted) return LoginPage(values, employees);
            if (values.ContainsKey("cancel")) return Redirect(redirectUri, responseMode, ("error", "access_denied"), ("state", state));

            if (!long.TryParse(Get("employee"), NumberStyles.None, CultureInfo.InvariantCulture, out var employeeId) || !employees.Any(e => e.Id == employeeId))
                return LoginPage(values, employees, "ログインできる従業員を選んでください。");
            var employee = employees.First(e => e.Id == employeeId);

            var code = Base64Url(RandomNumberGenerator.GetBytes(32));
            lock (_lock)
            {
                _codes[code] = new AuthorizationCode
                {
                    ClientId = clientId,
                    RedirectUri = redirectUri,
                    CodeChallenge = Get("code_challenge"),
                    Nonce = values.TryGetValue("nonce", out var nonce) && nonce.Length != 0 ? nonce : null,
                    Email = scopes.Contains("email"),
                    EmployeeId = employee.Id,
                    EmployeeEmail = employee.Email,
                    ExpiresAt = UtcNow() + AuthorizationCodeLifetime,
                };
            }
            return Redirect(redirectUri, responseMode, ("code", code), ("state", state));
        }

        //認可の要求のうち、画面の form に持ち回す項目
        static readonly string[] AuthorizeParameters =
            { "client_id", "redirect_uri", "response_type", "response_mode", "scope", "state", "nonce", "code_challenge", "code_challenge_method" };

        static MockResponse LoginPage(IReadOnlyDictionary<string, string> values, List<(long Id, string Name, string Email)> employees, string? error = null)
        {
            var sb = new StringBuilder();
            if (error != null) sb.Append($"<p id=\"error\" style=\"color:#b00020\">{Html(error)}</p>");
            sb.Append("<p>ログインする従業員を選んでください (モックなのでパスワードはありません)。</p>");
            sb.Append($"<form method=\"post\" action=\"{Html(AuthorizePath)}\">");
            foreach (var key in AuthorizeParameters)
            {
                if (values.TryGetValue(key, out var value)) sb.Append($"<input type=\"hidden\" name=\"{key}\" value=\"{Html(value)}\" />");
            }
            if (employees.Count == 0) sb.Append("<p id=\"no-employee\">ログインできる従業員がいません (データの employee に、メールがあり無効でないレコードが要ります)。</p>");
            sb.Append("<ul style=\"list-style:none;padding:0\">");
            foreach (var (id, name, email) in employees)
            {
                var idText = id.ToString(CultureInfo.InvariantCulture);
                sb.Append($"<li style=\"margin:6px 0\"><button type=\"submit\" id=\"employee-{idText}\" name=\"employee\" value=\"{idText}\">{Html(name)} ({Html(email)})</button></li>");
            }
            sb.Append("</ul>");
            sb.Append("<button type=\"submit\" id=\"cancel\" name=\"cancel\" value=\"1\">キャンセル</button>");
            sb.Append("</form>");
            return Page(200, "NetSuite (モック) にログイン", sb.ToString());
        }

        static MockResponse Redirect(string redirectUri, string responseMode, params (string Key, string Value)[] parameters)
        {
            var values = parameters.Where(e => !string.IsNullOrEmpty(e.Value)).ToList();
            if (responseMode == "form_post")
            {
                var sb = new StringBuilder();
                sb.Append($"<form method=\"post\" action=\"{Html(redirectUri)}\">");
                foreach (var (key, value) in values) sb.Append($"<input type=\"hidden\" name=\"{key}\" value=\"{Html(value)}\" />");
                sb.Append("<noscript><button type=\"submit\">続ける</button></noscript></form>");
                sb.Append("<script>document.forms[0].submit();</script>");
                return Page(200, "NetSuite (モック)", sb.ToString());
            }
            var query = string.Join("&", values.Select(e => $"{e.Key}={Uri.EscapeDataString(e.Value)}"));
            var response = new MockResponse { Status = 302 };
            response.Headers["Location"] = redirectUri + (redirectUri.Contains('?') ? "&" : "?") + query;
            return response;
        }

        static MockResponse Page(int status, string title, string body)
            => new()
            {
                Status = status,
                ContentType = "text/html",
                Body = $"<!DOCTYPE html><html lang=\"ja\"><head><meta charset=\"utf-8\" /><title>{Html(title)}</title></head>" +
                       $"<body style=\"font-family:sans-serif;margin:32px\"><h1 style=\"font-size:1.3rem\">{Html(title)}</h1>{body}</body></html>",
            };

        static string Html(string text) => WebUtility.HtmlEncode(text);

        // ---------------------------------------------------------------- トークン (authorization_code)

        /// <summary>
        /// 認可コードをトークンにする。クライアントは Authorization: Basic か form の client_id / client_secret (どちらも中身は問わず空でなければよい)。
        /// コードは 1 回だけ・期限つき。redirect_uri とクライアント ID は認可の要求と同じこと。PKCE (S256) の code_verifier を確かめる。
        /// </summary>
        MockResponse IssueAuthorizationCodeToken(IReadOnlyDictionary<string, string> form, string? authorization, string issuer)
        {
            string Get(string key) => form.TryGetValue(key, out var v) ? v : string.Empty;

            var clientId = Get("client_id");
            var clientSecret = Get("client_secret");
            const string basic = "Basic ";
            if (authorization != null && authorization.StartsWith(basic, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var pair = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[basic.Length..].Trim()));
                    var colon = pair.IndexOf(':');
                    if (0 <= colon)
                    {
                        clientId = Uri.UnescapeDataString(pair[..colon]);
                        clientSecret = Uri.UnescapeDataString(pair[(colon + 1)..]);
                    }
                }
                catch (FormatException) { }
            }
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                return TokenError("invalid_client", "The client ID and the client secret are required.", 401);

            AuthorizationCode? code;
            lock (_lock)
            {
                //使ったコードは消す (2 回目は通らない)
                if (_codes.Remove(Get("code"), out code) && code.ExpiresAt <= UtcNow()) code = null;
            }
            if (code == null) return TokenError("invalid_grant", "The authorization code is invalid or has expired.");
            if (code.ClientId != clientId) return TokenError("invalid_grant", "The authorization code was issued to another client.");
            if (code.RedirectUri != Get("redirect_uri")) return TokenError("invalid_grant", "redirect_uri does not match the authorization request.");
            var verifier = Get("code_verifier");
            if (string.IsNullOrEmpty(verifier) || Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != code.CodeChallenge)
                return TokenError("invalid_grant", "code_verifier does not match the code_challenge.");

            var accessToken = Base64Url(RandomNumberGenerator.GetBytes(32));
            var idToken = CreateIdToken(issuer, clientId, code, accessToken);
            var seconds = ((long)TokenLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            return Json(200, w =>
            {
                w.WriteStartObject();
                w.WriteString("access_token", accessToken);
                w.WriteString("expires_in", seconds);
                w.WriteString("token_type", "Bearer");
                w.WriteString("id_token", idToken);
                w.WriteEndObject();
            });
        }

        //ID トークン (PS256)。at_hash = アクセストークンの SHA-256 の前半 16 バイト (OIDC Core 3.3.2.11)
        string CreateIdToken(string issuer, string clientId, AuthorizationCode code, string accessToken)
        {
            var now = new DateTimeOffset(DateTime.SpecifyKind(UtcNow(), DateTimeKind.Utc)).ToUnixTimeSeconds();
            var header = JsonBytes(w =>
            {
                w.WriteStartObject();
                w.WriteString("alg", "PS256");
                w.WriteString("typ", "JWT");
                w.WriteString("kid", _keyId);
                w.WriteEndObject();
            });
            var payload = JsonBytes(w =>
            {
                w.WriteStartObject();
                w.WriteString("iss", issuer);
                w.WriteString("sub", $"{MockRoleId};{code.EmployeeId.ToString(CultureInfo.InvariantCulture)}");
                w.WriteString("aud", clientId);
                w.WriteNumber("iat", now);
                w.WriteNumber("auth_time", now);
                w.WriteNumber("exp", now + (long)TokenLifetime.TotalSeconds);
                if (code.Nonce != null) w.WriteString("nonce", code.Nonce);
                w.WriteString("at_hash", Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))[..16]));
                if (code.Email && code.EmployeeEmail != null) w.WriteString("email", code.EmployeeEmail);
                w.WriteEndObject();
            });
            var signingInput = Base64Url(header) + "." + Base64Url(payload);
            var signature = _signingKey.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            return signingInput + "." + Base64Url(signature);
        }

        static byte[] JsonBytes(Action<Utf8JsonWriter> write)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) write(writer);
            return stream.ToArray();
        }
    }
}
