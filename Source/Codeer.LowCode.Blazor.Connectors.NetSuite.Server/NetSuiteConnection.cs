using Codeer.LowCode.Blazor.SystemSettings;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    /// <summary>
    /// NetSuite への接続 1 つ。アクセストークン・レコードの定義・同時実行の枠を持つので、アプリの中で使い回す
    /// (<see cref="FromDataSources"/> は同じ設定に同じ接続を返す)。
    /// </summary>
    public sealed class NetSuiteConnection
    {
        static readonly ConcurrentDictionary<string, NetSuiteConnection> _shared = new();

        readonly HttpClient _http;
        readonly SemaphoreSlim _concurrency;
        readonly SemaphoreSlim _tokenLock = new(1, 1);
        readonly ConcurrentDictionary<string, NetSuiteRecordDefinition> _records = new(StringComparer.OrdinalIgnoreCase);
        string _accessToken = string.Empty;
        DateTime _accessTokenExpiresAt;

        public NetSuiteConnectionSettings Settings { get; }

        //テストで待ち時間を無くす・時刻を動かすための差し替え口
        internal Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <param name="handler">HTTP の送信を差し替えるとき (プロキシ・テスト)。null なら既定。</param>
        public NetSuiteConnection(NetSuiteConnectionSettings settings, HttpMessageHandler? handler = null)
        {
            settings.Validate();
            Settings = settings;
            _http = handler == null ? new HttpClient() : new HttpClient(handler, false);
            _http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
            _concurrency = new SemaphoreSlim(settings.MaxConcurrency, settings.MaxConcurrency);
        }

        /// <summary>
        /// データソースの中から NetSuite 用 (ConnectionString が Provider=NetSuite) のものを接続にする。同じ設定には同じ接続を返す。
        /// </summary>
        public static List<NetSuiteConnection> FromDataSources(IEnumerable<DataSource> dataSources)
        {
            var list = new List<NetSuiteConnection>();
            foreach (var dataSource in dataSources)
            {
                var settings = NetSuiteConnectionSettings.FromDataSource(dataSource);
                if (settings == null) continue;
                list.Add(_shared.GetOrAdd(settings.GetIdentityKey(), _ => new NetSuiteConnection(settings)));
            }
            return list;
        }

        string RestUrl => Settings.ServiceUrl + "/services/rest";

        //明細の表になりうる名前 (<Records の取引>_...) は、Records に書かれていてもレコードとして扱わない (読み取り専用の明細の表のまま)
        internal bool IsAllowedRecord(string recordType)
            => Settings.Records.Contains(recordType, StringComparer.OrdinalIgnoreCase) && !Settings.Records.Any(e => IsLineTableCandidate(recordType, e));

        void CheckAllowedRecord(string recordType)
        {
            if (!IsAllowedRecord(recordType)) throw NotListed(recordType);
        }

        NetSuiteException NotListed(string recordType)
            => new($"NetSuite data source '{Settings.Name}': record type '{recordType}' is not listed in Records.");

        // ---------------------------------------------------------------- レコードの定義

        /// <summary>SuiteQL で読める表の名前か (Records のレコードと、その取引の明細の表になりうる名前)。明細の表かどうかは定義を読んで確かめる。</summary>
        internal bool IsReadableTable(string name)
            => IsAllowedRecord(name) || Settings.Records.Any(e => IsLineTableCandidate(name, e));

        static bool IsLineTableCandidate(string name, string parent)
            => SuiteQlTranslator.GetTransactionType(parent) != null && parent.Length + 1 < name.Length && name.StartsWith(parent + "_", StringComparison.OrdinalIgnoreCase);

        /// <summary>レコード (Records に書いたもの) か、明細の表 (&lt;取引のレコード名&gt;_&lt;明細の列名&gt;) の定義。</summary>
        internal async Task<NetSuiteRecordDefinition> GetRecordDefinitionAsync(string recordType)
        {
            if (!IsAllowedRecord(recordType)) return await GetLineTableDefinitionAsync(recordType);
            //定義は接続が生きている間持つ (本体も表の定義をプロセスの間持つ。NetSuite 側の項目を変えたらアプリを再起動する)
            if (_records.TryGetValue(recordType, out var cached)) return cached;

            using var json = await GetSchemaAsync(recordType);
            //明細の行の定義 ($ref の先) は同じ metadata-catalog から読む。無ければその明細は列にしない
            var definition = await NetSuiteRecordDefinition.FromJsonSchemaAsync(recordType, json.RootElement, async name =>
            {
                try
                {
                    using var target = await GetSchemaAsync(name);
                    return target.RootElement.Clone();
                }
                catch (NetSuiteException e) when (e.Kind is NetSuiteErrorKind.NotFound or NetSuiteErrorKind.InvalidRequest or NetSuiteErrorKind.PermissionDenied)
                {
                    //明細の定義が読めないだけで、レコードごと使えなくしない (通信の失敗・認証の失敗はそのまま上げる)
                    return null;
                }
                catch (JsonException)
                {
                    //JSON でない本文も同じ
                    return null;
                }
            });
            _records[recordType] = definition;
            return definition;
        }

        //明細の表。名前は Records のレコード名 + _ + そのレコードの明細の列名で照合する (レコード名に _ が入るものがあるので分割しない)
        async Task<NetSuiteRecordDefinition> GetLineTableDefinitionAsync(string name)
        {
            if (_records.TryGetValue(name, out var cached) && cached.IsLineTable) return cached;
            foreach (var parent in Settings.Records.Where(e => IsLineTableCandidate(name, e)))
            {
                var line = (await GetRecordDefinitionAsync(parent)).CreateLineTables().FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if (line != null) return _records.GetOrAdd(line.Name, line);
            }
            throw NotListed(name);
        }

        async Task<JsonDocument> GetSchemaAsync(string name)
        {
            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{RestUrl}/record/v1/metadata-catalog/{Uri.EscapeDataString(name)}");
                request.Headers.Accept.ParseAdd("application/schema+json");
                return request;
            }, true);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        internal async Task<List<NetSuiteRecordDefinition>> GetRecordDefinitionsAsync()
        {
            //取引のレコードの後ろに、その明細の表を並べる
            var list = new List<NetSuiteRecordDefinition>();
            foreach (var record in Settings.Records.Where(IsAllowedRecord))
            {
                var definition = await GetRecordDefinitionAsync(record);
                list.Add(definition);
                foreach (var line in definition.CreateLineTables()) list.Add(_records.GetOrAdd(line.Name, line));
            }
            return list;
        }

        /// <summary>持っている定義を捨てて、Records のレコードの定義を NetSuite から取り直す。</summary>
        internal async Task<List<NetSuiteRecordDefinition>> ReloadRecordDefinitionsAsync()
        {
            _records.Clear();
            return await GetRecordDefinitionsAsync();
        }

        // ---------------------------------------------------------------- SuiteQL

        internal class SuiteQlPage
        {
            public List<Dictionary<string, string?>> Items { get; } = new();
            public bool HasMore { get; set; }
        }

        internal async Task<SuiteQlPage> QuerySuiteQlAsync(string query, List<string> parameters, int limit, int offset)
        {
            var body = new MemoryStream();
            using (var writer = new Utf8JsonWriter(body))
            {
                writer.WriteStartObject();
                writer.WriteString("q", query);
                if (parameters.Count != 0)
                {
                    writer.WriteStartArray("params");
                    foreach (var p in parameters) writer.WriteStringValue(p);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            var bytes = body.ToArray();

            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{RestUrl}/query/v1/suiteql?limit={limit}&offset={offset}");
                request.Headers.TryAddWithoutValidation("Prefer", "transient");
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return request;
            }, true);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var page = new SuiteQlPage();
            if (json.RootElement.TryGetProperty("hasMore", out var hasMore)) page.HasMore = hasMore.ValueKind == JsonValueKind.True;
            if (json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in item.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Array || property.Value.ValueKind == JsonValueKind.Object) continue; //links
                        row[property.Name] = property.Value.ValueKind switch
                        {
                            JsonValueKind.Null => null,
                            JsonValueKind.String => property.Value.GetString(),
                            JsonValueKind.True => "T",
                            JsonValueKind.False => "F",
                            _ => property.Value.GetRawText(),
                        };
                    }
                    page.Items.Add(row);
                }
            }
            return page;
        }

        // ---------------------------------------------------------------- レコードの読み出し

        /// <summary>レコードを明細つきで読む (GET record/v1/{type}/{id}?expandSubResources=true)。対象が無ければ null。</summary>
        internal async Task<JsonDocument?> GetRecordAsync(string recordType, string id)
        {
            CheckAllowedRecord(recordType);
            try
            {
                using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
                    $"{RestUrl}/record/v1/{Uri.EscapeDataString(recordType)}/{Uri.EscapeDataString(id)}?expandSubResources=true"), true);
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            }
            catch (NetSuiteException e) when (e.Kind == NetSuiteErrorKind.NotFound)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- レコードの書き込み

        /// <summary>レコードを作り、NetSuite が採番した internal ID を返す。</summary>
        internal async Task<string> CreateRecordAsync(string recordType, byte[] json)
        {
            CheckAllowedRecord(recordType);
            using var response = await SendAsync(() => CreateWriteRequest(HttpMethod.Post, $"{RestUrl}/record/v1/{Uri.EscapeDataString(recordType)}", json), false);
            var location = response.Headers.Location?.ToString() ?? string.Empty;
            var id = location.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(id))
                throw new NetSuiteException($"NetSuite created a '{recordType}' record but did not return its id.", NetSuiteErrorKind.WriteResultUnknown);
            return Uri.UnescapeDataString(id);
        }

        /// <summary>レコードを更新する。対象が無ければ false。</summary>
        /// <param name="replace">要求に無い行を消す明細の REST 名 (?replace=)。空なら付けない。</param>
        internal async Task<bool> UpdateRecordAsync(string recordType, string id, byte[] json, IReadOnlyCollection<string>? replace = null)
        {
            CheckAllowedRecord(recordType);
            var url = $"{RestUrl}/record/v1/{Uri.EscapeDataString(recordType)}/{Uri.EscapeDataString(id)}";
            if (replace != null && replace.Count != 0) url += "?replace=" + string.Join(",", replace.Select(Uri.EscapeDataString));
            try
            {
                using var response = await SendAsync(() => CreateWriteRequest(HttpMethod.Patch, url, json), false);
                return true;
            }
            catch (NetSuiteException e) when (e.Kind == NetSuiteErrorKind.NotFound)
            {
                return false;
            }
        }

        /// <summary>レコードを削除する。対象が無ければ false。</summary>
        internal async Task<bool> DeleteRecordAsync(string recordType, string id)
        {
            CheckAllowedRecord(recordType);
            try
            {
                using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{RestUrl}/record/v1/{Uri.EscapeDataString(recordType)}/{Uri.EscapeDataString(id)}"), false);
                return true;
            }
            catch (NetSuiteException e) when (e.Kind == NetSuiteErrorKind.NotFound)
            {
                return false;
            }
        }

        static HttpRequestMessage CreateWriteRequest(HttpMethod method, string url, byte[] json)
        {
            var request = new HttpRequestMessage(method, url);
            //知らない項目名を黙って無視させない (無視されると「保存できたのに入っていない」になる)
            request.Headers.TryAddWithoutValidation("X-NetSuite-PropertyNameValidation", "Error");
            request.Headers.TryAddWithoutValidation("X-NetSuite-PropertyValueValidation", "Error");
            request.Content = new ByteArrayContent(json);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return request;
        }

        // ---------------------------------------------------------------- 送信

        const int MaxAttempts = 4;

        /// <summary>
        /// 認証を付けて送る。読み出し (isRead) は一時的な失敗をやり直す。書き込みは、実行されていないと分かる失敗 (401・429) だけやり直し、
        /// 送ったあと応答を受け取れなかった・サーバー側の失敗 (503 以外の 5xx) が返ったら「結果不明」にする (同じ作成をもう一度送らない)。
        /// </summary>
        async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, bool isRead)
        {
            await _concurrency.WaitAsync();
            try
            {
                var refreshedToken = false;
                for (var attempt = 1; ; attempt++)
                {
                    var token = await GetAccessTokenAsync();
                    HttpResponseMessage response;
                    using (var request = createRequest())
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        try
                        {
                            response = await _http.SendAsync(request);
                        }
                        catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
                        {
                            if (!isRead)
                                throw new NetSuiteException("The request was sent to NetSuite but no response was received. Whether it was processed is unknown. Check the record in NetSuite before retrying.", NetSuiteErrorKind.WriteResultUnknown, e);
                            if (attempt < MaxAttempts)
                            {
                                await Delay(GetBackoff(attempt, null));
                                continue;
                            }
                            throw new NetSuiteException("Could not reach NetSuite: " + e.Message, NetSuiteErrorKind.ServiceUnavailable, e);
                        }
                    }

                    if (response.IsSuccessStatusCode) return response;

                    using (response)
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedToken)
                        {
                            //トークンが切れた・失効した。取り直して 1 回だけやり直す
                            refreshedToken = true;
                            InvalidateAccessToken(token);
                            continue;
                        }
                        var isBusy = response.StatusCode == HttpStatusCode.TooManyRequests;
                        var isTemporary = response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                        if ((isBusy || (isRead && isTemporary)) && attempt < MaxAttempts)
                        {
                            await Delay(GetBackoff(attempt, response.Headers.RetryAfter?.Delta));
                            continue;
                        }
                        var exception = await CreateExceptionAsync(response);
                        //書き込みへのサーバー側の失敗 (503 = 受け付けていない、を除く 5xx) は、実行されたかどうかが分からない
                        if (!isRead && 500 <= (int)response.StatusCode && response.StatusCode != HttpStatusCode.ServiceUnavailable)
                            throw new NetSuiteException(exception.Message + " Whether the write was processed is unknown. Check the record in NetSuite before retrying.",
                                NetSuiteErrorKind.WriteResultUnknown, response.StatusCode, exception.ErrorCode, exception.ErrorPath);
                        throw exception;
                    }
                }
            }
            finally
            {
                _concurrency.Release();
            }
        }

        static TimeSpan GetBackoff(int attempt, TimeSpan? retryAfter)
        {
            if (retryAfter != null && TimeSpan.Zero < retryAfter && retryAfter < TimeSpan.FromSeconds(60)) return retryAfter.Value;
            return TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        }

        static async Task<NetSuiteException> CreateExceptionAsync(HttpResponseMessage response)
        {
            var body = string.Empty;
            try { body = await response.Content.ReadAsStringAsync(); } catch { }

            string detail = string.Empty, code = string.Empty, path = string.Empty;
            try
            {
                using var json = JsonDocument.Parse(body);
                //ゲートウェイなどが返す、オブジェクトでない本文は読まない (ステータスだけで分類する)
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                if (json.RootElement.TryGetProperty("o:errorDetails", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    var messages = new List<string>();
                    foreach (var e in details.EnumerateArray())
                    {
                        if (e.TryGetProperty("detail", out var d)) messages.Add(d.GetString() ?? string.Empty);
                        if (string.IsNullOrEmpty(code) && e.TryGetProperty("o:errorCode", out var c)) code = c.GetString() ?? string.Empty;
                        if (string.IsNullOrEmpty(path) && e.TryGetProperty("o:errorPath", out var p)) path = p.GetString() ?? string.Empty;
                    }
                    detail = string.Join(" ", messages.Where(e => !string.IsNullOrEmpty(e)));
                }
                if (string.IsNullOrEmpty(detail) && json.RootElement.TryGetProperty("title", out var title)) detail = title.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(detail) && json.RootElement.TryGetProperty("error_description", out var description)) detail = description.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(detail) && json.RootElement.TryGetProperty("error", out var error)) detail = error.ToString();
            }
            catch (Exception e) when (e is JsonException || e is InvalidOperationException) { }

            var kind = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => NetSuiteErrorKind.Authentication,
                HttpStatusCode.Forbidden => NetSuiteErrorKind.PermissionDenied,
                HttpStatusCode.NotFound => NetSuiteErrorKind.NotFound,
                HttpStatusCode.TooManyRequests => NetSuiteErrorKind.RateLimited,
                _ => (int)response.StatusCode >= 500 ? NetSuiteErrorKind.ServiceUnavailable : NetSuiteErrorKind.InvalidRequest,
            };
            if (string.IsNullOrEmpty(detail)) detail = $"HTTP {(int)response.StatusCode}";
            return new NetSuiteException($"NetSuite: {detail}", kind, response.StatusCode, code, path);
        }

        // ---------------------------------------------------------------- 認証 (OAuth 2.0 Client Credentials)

        void InvalidateAccessToken(string token)
        {
            if (_accessToken == token) _accessToken = string.Empty;
        }

        async Task<string> GetAccessTokenAsync()
        {
            var token = _accessToken;
            if (!string.IsNullOrEmpty(token) && UtcNow() < _accessTokenExpiresAt) return token;

            await _tokenLock.WaitAsync();
            try
            {
                if (!string.IsNullOrEmpty(_accessToken) && UtcNow() < _accessTokenExpiresAt) return _accessToken;

                var tokenUrl = $"{RestUrl}/auth/oauth2/v1/token";
                using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                    ["client_assertion"] = CreateClientAssertion(tokenUrl),
                });

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request);
                }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
                {
                    throw new NetSuiteException("Could not reach the NetSuite token endpoint: " + e.Message, NetSuiteErrorKind.ServiceUnavailable, e);
                }
                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var inner = await CreateExceptionAsync(response);
                        throw new NetSuiteException("NetSuite authentication failed. " + inner.Message, NetSuiteErrorKind.Authentication, response.StatusCode, inner.ErrorCode, inner.ErrorPath);
                    }
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var accessToken = json.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrEmpty(accessToken))
                        throw new NetSuiteException("NetSuite authentication failed. The token response has no access_token.", NetSuiteErrorKind.Authentication);
                    var expiresIn = 3600;
                    if (json.RootElement.TryGetProperty("expires_in", out var e))
                    {
                        if (e.ValueKind == JsonValueKind.Number) expiresIn = e.GetInt32();
                        else if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var parsed)) expiresIn = parsed;
                    }
                    //期限の少し前に取り直す
                    _accessTokenExpiresAt = UtcNow().AddSeconds(Math.Max(30, expiresIn - 120));
                    _accessToken = accessToken;
                    return accessToken;
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        //署名した JWT (client assertion)。秘密鍵で署名し、NetSuite に登録した証明書 (kid) で検証される
        internal string CreateClientAssertion(string tokenUrl)
        {
            var algorithm = Settings.Algorithm.ToUpperInvariant();
            var now = new DateTimeOffset(DateTime.SpecifyKind(UtcNow(), DateTimeKind.Utc)).ToUnixTimeSeconds();

            var header = JsonSerializer.Serialize(new Dictionary<string, object> { ["typ"] = "JWT", ["alg"] = algorithm, ["kid"] = Settings.CertificateId });
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["iss"] = Settings.ClientId,
                ["scope"] = Settings.Scope.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries),
                ["aud"] = tokenUrl,
                ["iat"] = now,
                ["exp"] = now + 300,
            });
            var signingInput = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
            var data = Encoding.ASCII.GetBytes(signingInput);

            var hash = (algorithm.Length < 3 ? string.Empty : algorithm[2..]) switch
            {
                "256" => HashAlgorithmName.SHA256,
                "384" => HashAlgorithmName.SHA384,
                "512" => HashAlgorithmName.SHA512,
                _ => throw new NetSuiteException($"NetSuite data source '{Settings.Name}': unsupported Algorithm '{Settings.Algorithm}'."),
            };
            var pem = GetPrivateKeyPem();
            byte[] signature;
            try
            {
                if (algorithm.StartsWith("PS"))
                {
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(pem);
                    signature = rsa.SignData(data, hash, RSASignaturePadding.Pss);
                }
                else if (algorithm.StartsWith("ES"))
                {
                    using var ecdsa = ECDsa.Create();
                    ecdsa.ImportFromPem(pem);
                    signature = ecdsa.SignData(data, hash);
                }
                else
                {
                    throw new NetSuiteException($"NetSuite data source '{Settings.Name}': unsupported Algorithm '{Settings.Algorithm}'.");
                }
            }
            catch (Exception e) when (e is CryptographicException || e is ArgumentException)
            {
                //鍵の中身は例外に出さない
                throw new NetSuiteException($"NetSuite data source '{Settings.Name}': the private key could not be used with {algorithm}.", NetSuiteErrorKind.Authentication);
            }
            return signingInput + "." + Base64Url(signature);
        }

        string GetPrivateKeyPem()
        {
            if (!string.IsNullOrWhiteSpace(Settings.PrivateKey)) return Settings.PrivateKey.Replace("\\n", "\n");
            try
            {
                return File.ReadAllText(Settings.PrivateKeyPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new NetSuiteException($"NetSuite data source '{Settings.Name}': the private key file could not be read.", NetSuiteErrorKind.Authentication);
            }
        }

        static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
