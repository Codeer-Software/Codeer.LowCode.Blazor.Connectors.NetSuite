using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetSuite.Mock
{
    /// <summary>応答 1 つ (ステータス・本文・ヘッダー)。</summary>
    public sealed class MockResponse
    {
        public int Status { get; init; }
        public string? Body { get; init; }
        public string ContentType { get; init; } = "application/json";
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NetSuite のモック。レコードの定義とデータ (SQLite のインメモリ) とアクセストークンを持ち、REST の要求に応える。
    /// HTTP への結び付けは <see cref="MockNetSuiteEndpoints.MapMockNetSuite"/>。
    /// </summary>
    public sealed class MockNetSuite : IDisposable
    {
        readonly object _lock = new();
        readonly SqliteConnection _db;
        readonly List<MockRecordType> _records;
        readonly Dictionary<string, DateTime> _tokens = new(StringComparer.Ordinal);

        /// <summary>定義にあるレコード (mock-data.json の順)。</summary>
        public IReadOnlyList<MockRecordType> Records => _records;

        /// <summary>発行するアクセストークンの有効期間。</summary>
        public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(1);

        /// <summary>今の時刻 (UTC)。トークンの期限・JWT の exp の判定に使う。</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>これまでに発行したアクセストークンの数。</summary>
        public int IssuedTokenCount { get; private set; }

        MockNetSuite(List<MockRecordType> records, List<(string Record, JsonElement Row)> rows)
        {
            _records = records;
            _db = new SqliteConnection("Data Source=:memory:");
            _db.Open();
            MockSuiteQl.RegisterFunctions(_db);
            try
            {
                CreateTables();
                LoadRows(rows);
            }
            catch
            {
                _db.Dispose();
                throw;
            }
        }

        /// <summary>mock-data.json の形の JSON から作る。</summary>
        public static MockNetSuite FromJson(string json)
        {
            using var document = JsonDocument.Parse(json, MockValues.JsonOptions);
            var rows = new List<(string, JsonElement)>();
            var records = MockValues.ReadDefinitions(document.RootElement, rows);
            return new MockNetSuite(records, rows.Select(e => (e.Item1, e.Item2.Clone())).ToList());
        }

        /// <summary>mock-data.json の形のファイルから作る。</summary>
        public static MockNetSuite FromFile(string path) => FromJson(File.ReadAllText(path));

        public void Dispose() => _db.Dispose();

        MockRecordType? FindRecord(string name) => _records.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        // ---------------------------------------------------------------- データ

        void CreateTables()
        {
            foreach (var record in _records)
            {
                var columns = new List<string> { "id INTEGER PRIMARY KEY AUTOINCREMENT" };
                foreach (var field in record.Fields)
                {
                    var type = field.Type switch
                    {
                        MockFieldType.Integer => "INTEGER",
                        MockFieldType.Number => "NUMERIC",
                        _ => "TEXT",
                    };
                    columns.Add($"\"{field.Column}\" {type}");
                }
                Execute($"create table \"{record.Name}\" ({string.Join(", ", columns)})");
            }
        }

        void LoadRows(List<(string Record, JsonElement Row)> rows)
        {
            //参照の先は全部入れてから確かめる (レコードの並びに依らない)
            foreach (var (name, row) in rows)
            {
                var record = FindRecord(name)!;
                if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"mock data: a row of record '{record.Name}' must be an object.");
                long? id = null;
                var values = new List<(MockField, object?)>();
                foreach (var property in row.EnumerateObject())
                {
                    if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var n) && 0 < n) id = n;
                        else if (property.Value.ValueKind == JsonValueKind.String && long.TryParse(property.Value.GetString(), out var s) && 0 < s) id = s;
                        else throw new InvalidDataException($"mock data: the id of a row of record '{record.Name}' must be a positive integer.");
                        continue;
                    }
                    var field = record.FindField(property.Name) ?? throw new InvalidDataException($"mock data: record '{record.Name}' has no field '{property.Name}'.");
                    var value = MockValues.ToDbValue(field, property.Value, true, out var error);
                    if (error != null) throw new InvalidDataException($"mock data: record '{record.Name}' (id {id?.ToString() ?? "?"}): {error}");
                    values.Add((field, value));
                }
                var missing = record.Fields.FirstOrDefault(f => f.Required && !values.Any(v => v.Item1 == f && HasValue(v.Item2)));
                if (missing != null) throw new InvalidDataException($"mock data: a row of record '{record.Name}' has no value for the required field '{missing.Name}'.");
                try
                {
                    Insert(record, values, id);
                }
                catch (SqliteException e)
                {
                    throw new InvalidDataException($"mock data: a row of record '{record.Name}' could not be added: {e.Message}");
                }
            }
            foreach (var record in _records)
            {
                foreach (var field in record.Fields.Where(e => e.Type == MockFieldType.Reference))
                {
                    var target = FindRecord(field.Record)!;
                    using var command = _db.CreateCommand();
                    command.CommandText = $"select r.id, r.\"{field.Column}\" from \"{record.Name}\" r where r.\"{field.Column}\" is not null and not exists (select 1 from \"{target.Name}\" t where t.id = r.\"{field.Column}\")";
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                        throw new InvalidDataException($"mock data: record '{record.Name}' (id {reader.GetValue(0)}) refers to {target.Name} {reader.GetValue(1)} in '{field.Name}', which does not exist.");
                }
            }
        }

        long Insert(MockRecordType record, List<(MockField Field, object? Value)> values, long? id)
        {
            using var command = _db.CreateCommand();
            var columns = values.Select(e => $"\"{e.Field.Column}\"").ToList();
            var parameters = values.Select((_, i) => "@v" + i).ToList();
            if (id != null)
            {
                columns.Insert(0, "id");
                parameters.Insert(0, "@id");
                command.Parameters.AddWithValue("@id", id.Value);
            }
            for (var i = 0; i < values.Count; i++) command.Parameters.AddWithValue("@v" + i, values[i].Value ?? DBNull.Value);
            command.CommandText = columns.Count == 0
                ? $"insert into \"{record.Name}\" default values; select last_insert_rowid();"
                : $"insert into \"{record.Name}\" ({string.Join(", ", columns)}) values ({string.Join(", ", parameters)}); select last_insert_rowid();";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        void Execute(string sql)
        {
            using var command = _db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        bool Exists(MockRecordType record, string id)
        {
            using var command = _db.CreateCommand();
            command.CommandText = $"select count(*) from \"{record.Name}\" where id = @id";
            command.Parameters.AddWithValue("@id", id);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }

        /// <summary>
        /// レコードを 1 件足す (REST の作成と同じ検証)。テストで大量の行を用意するとき用。足せなければ InvalidOperationException。
        /// </summary>
        public string AddRecord(string recordType, string json)
        {
            var response = CreateRecord(recordType, json, "http://localhost");
            if (response.Status != 204) throw new InvalidOperationException(response.Body);
            return response.Headers["Location"].Split('/').Last();
        }

        // ---------------------------------------------------------------- エラーの本文

        static string Title(int status) => status switch
        {
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            406 => "Not Acceptable",
            _ => "Internal Server Error",
        };

        static string Section(int status) => status switch
        {
            400 => "15.5.1",
            401 => "15.5.2",
            403 => "15.5.4",
            404 => "15.5.5",
            405 => "15.5.6",
            406 => "15.5.7",
            _ => "15.6.1",
        };

        /// <summary>NetSuite の形のエラー。</summary>
        public static MockResponse Error(int status, string errorCode, string detail, string? errorPath = null)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("type", "https://www.rfc-editor.org/rfc/rfc9110.html#section-" + Section(status));
                writer.WriteString("title", Title(status));
                writer.WriteNumber("status", status);
                writer.WriteStartArray("o:errorDetails");
                writer.WriteStartObject();
                writer.WriteString("detail", detail);
                writer.WriteString("o:errorCode", errorCode);
                if (!string.IsNullOrEmpty(errorPath)) writer.WriteString("o:errorPath", errorPath);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return new MockResponse { Status = status, Body = Encoding.UTF8.GetString(stream.ToArray()) };
        }

        static MockResponse Json(int status, Action<Utf8JsonWriter> write, string contentType = "application/json")
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) write(writer);
            return new MockResponse { Status = status, Body = Encoding.UTF8.GetString(stream.ToArray()), ContentType = contentType };
        }

        static MockResponse NoContent() => new() { Status = 204 };

        static MockResponse RecordNotFound(string recordType)
            => Error(404, "INVALID_RCRD_TYPE", $"Record type '{recordType}' does not exist.");

        static MockResponse IdNotFound(string recordType, string id)
            => Error(404, "NONEXISTENT_ID", $"The record instance does not exist. Provide a valid record instance ID. ({recordType} {id})");

        // ---------------------------------------------------------------- 認証

        /// <summary>OAuth 2.0 Client Credentials のトークン要求 (form の値)。JWT の形と exp を確かめる。署名は確かめない。</summary>
        public MockResponse IssueToken(IReadOnlyDictionary<string, string> form)
        {
            static MockResponse Fail(string error, string description)
                => Json(400, w => { w.WriteStartObject(); w.WriteString("error", error); w.WriteString("error_description", description); w.WriteEndObject(); });

            string Get(string key) => form.TryGetValue(key, out var v) ? v : string.Empty;
            if (Get("grant_type") != "client_credentials") return Fail("unsupported_grant_type", "grant_type must be client_credentials.");
            if (Get("client_assertion_type") != "urn:ietf:params:oauth:client-assertion-type:jwt-bearer")
                return Fail("invalid_request", "client_assertion_type must be urn:ietf:params:oauth:client-assertion-type:jwt-bearer.");
            var parts = Get("client_assertion").Split('.');
            if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) return Fail("invalid_request", "client_assertion must be a signed JWT (header.payload.signature).");

            JsonDocument? header = null, payload = null;
            try
            {
                header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
                payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
                if (header.RootElement.ValueKind != JsonValueKind.Object || payload.RootElement.ValueKind != JsonValueKind.Object)
                    return Fail("invalid_request", "The JWT header and payload must be JSON objects.");
                if (!payload.RootElement.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number || !exp.TryGetInt64(out var expSeconds))
                    return Fail("invalid_request", "The JWT has no exp claim.");
                var now = new DateTimeOffset(DateTime.SpecifyKind(UtcNow(), DateTimeKind.Utc)).ToUnixTimeSeconds();
                if (expSeconds < now) return Fail("invalid_grant", "The JWT has expired.");
            }
            catch (Exception e) when (e is JsonException || e is FormatException)
            {
                return Fail("invalid_request", "The JWT header and payload must be base64url encoded JSON.");
            }
            finally
            {
                header?.Dispose();
                payload?.Dispose();
            }

            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            lock (_lock)
            {
                _tokens[token] = UtcNow() + TokenLifetime;
                IssuedTokenCount++;
            }
            var seconds = ((long)TokenLifetime.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            return Json(200, w =>
            {
                w.WriteStartObject();
                w.WriteString("access_token", token);
                w.WriteString("expires_in", seconds);
                w.WriteString("token_type", "Bearer");
                w.WriteEndObject();
            });
        }

        /// <summary>発行したトークンを全部無効にする (期限切れ・失効の確かめ用)。</summary>
        public void RevokeTokens()
        {
            lock (_lock) _tokens.Clear();
        }

        /// <summary>Authorization ヘッダーを確かめる。通れば null、通らなければ 401 の応答。</summary>
        public MockResponse? Authenticate(string? authorization)
        {
            const string prefix = "Bearer ";
            if (authorization != null && authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var token = authorization[prefix.Length..].Trim();
                lock (_lock)
                {
                    if (_tokens.TryGetValue(token, out var expiresAt))
                    {
                        if (UtcNow() < expiresAt) return null;
                        _tokens.Remove(token);
                    }
                }
            }
            return Error(401, "INVALID_LOGIN", "Invalid login attempt. For more details, see the Login Audit Trail in the NetSuite UI at Setup > Users/Roles > User Management > View Login Audit Trail.");
        }

        static byte[] Base64UrlDecode(string text)
        {
            var s = text.Replace('-', '+').Replace('_', '/');
            s += new string('=', (4 - s.Length % 4) % 4);
            return Convert.FromBase64String(s);
        }

        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // ---------------------------------------------------------------- メタデータ

        /// <summary>メタデータの一覧 (GET record/v1/metadata-catalog)。</summary>
        public MockResponse GetMetadataCatalog(string baseUrl)
            => Json(200, w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("items");
                foreach (var record in _records)
                {
                    w.WriteStartObject();
                    w.WriteString("name", record.Name);
                    w.WriteStartArray("links");
                    w.WriteStartObject();
                    w.WriteString("rel", "canonical");
                    w.WriteString("href", $"{baseUrl}/services/rest/record/v1/metadata-catalog/{record.Name}");
                    w.WriteString("mediaType", "application/schema+json");
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            });

        /// <summary>レコードの JSON Schema (GET record/v1/metadata-catalog/{type}、Accept: application/schema+json)。</summary>
        public MockResponse GetMetadata(string recordType, string? accept)
        {
            var record = FindRecord(recordType);
            if (record == null) return RecordNotFound(recordType);
            if (accept == null || !accept.Contains("application/schema+json", StringComparison.OrdinalIgnoreCase))
                return Error(406, "INVALID_HEADER", "This mock returns the metadata only as JSON Schema. Send the header 'Accept: application/schema+json'.");

            return Json(200, w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "object");
                w.WriteString("title", record.Name);
                w.WriteStartObject("properties");
                w.WriteStartObject("id");
                w.WriteString("title", "Internal ID");
                w.WriteString("type", "string");
                w.WriteEndObject();
                foreach (var field in record.Fields)
                {
                    w.WriteStartObject(field.Name);
                    w.WriteString("title", string.IsNullOrEmpty(field.Title) ? field.Name : field.Title);
                    switch (field.Type)
                    {
                        case MockFieldType.String: w.WriteString("type", "string"); break;
                        case MockFieldType.Boolean: w.WriteString("type", "boolean"); break;
                        case MockFieldType.Integer: w.WriteString("type", "integer"); w.WriteString("format", "int64"); break;
                        case MockFieldType.Number: w.WriteString("type", "number"); w.WriteString("format", "double"); break;
                        case MockFieldType.Date: w.WriteString("type", "string"); w.WriteString("format", "date"); break;
                        case MockFieldType.DateTime: w.WriteString("type", "string"); w.WriteString("format", "date-time"); break;
                        case MockFieldType.Reference:
                            w.WriteString("type", "object");
                            w.WriteStartObject("properties");
                            w.WriteStartObject("id");
                            w.WriteString("title", "Internal identifier");
                            w.WriteString("type", "string");
                            w.WriteEndObject();
                            w.WriteStartObject("refName");
                            w.WriteString("title", "Reference Name");
                            w.WriteString("type", "string");
                            w.WriteEndObject();
                            w.WriteEndObject();
                            break;
                    }
                    w.WriteEndObject();
                }
                w.WriteStartObject("links");
                w.WriteString("title", "Links");
                w.WriteString("type", "array");
                w.WriteStartObject("items");
                w.WriteString("type", "object");
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteStartObject("refName");
                w.WriteString("title", "Reference Name");
                w.WriteString("type", "string");
                w.WriteEndObject();
                w.WriteEndObject();
                var required = record.Fields.Where(e => e.Required).Select(e => e.Name).ToList();
                if (required.Count != 0)
                {
                    w.WriteStartArray("required");
                    foreach (var name in required) w.WriteStringValue(name);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }, "application/schema+json");
        }

        // ---------------------------------------------------------------- SuiteQL

        const int MaxLimit = 1000;

        /// <summary>SuiteQL (POST query/v1/suiteql?limit=&amp;offset=)。</summary>
        public MockResponse QuerySuiteQl(string? limitText, string? offsetText, string? prefer, string body)
        {
            if (prefer == null || !prefer.Split(',').Any(e => string.Equals(e.Trim(), "transient", StringComparison.OrdinalIgnoreCase)))
                return Error(400, "INVALID_HEADER", "A SuiteQL request needs the header 'Prefer: transient'.");

            var limit = MaxLimit;
            if (!string.IsNullOrEmpty(limitText) && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || MaxLimit < limit))
                return Error(400, "INVALID_PARAMETER", $"Invalid value for the parameter 'limit'. The value must be between 1 and {MaxLimit}.", "limit");
            var offset = 0;
            if (!string.IsNullOrEmpty(offsetText) && !int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
                return Error(400, "INVALID_PARAMETER", "Invalid value for the parameter 'offset'.", "offset");
            if (offset % limit != 0)
                return Error(400, "INVALID_PARAMETER", "Invalid value for the parameter 'offset'. The offset must be a multiple of the limit.", "offset");

            string query;
            var parameters = new List<string?>();
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("q", out var q) || q.ValueKind != JsonValueKind.String)
                    return Error(400, "INVALID_CONTENT", "The request body must be a JSON object with the query in \"q\".");
                query = q.GetString()!;
                if (json.RootElement.TryGetProperty("params", out var p))
                {
                    if (p.ValueKind != JsonValueKind.Array) return Error(400, "INVALID_CONTENT", "\"params\" must be an array.");
                    foreach (var e in p.EnumerateArray())
                    {
                        parameters.Add(e.ValueKind switch
                        {
                            JsonValueKind.String => e.GetString(),
                            JsonValueKind.Number => e.GetRawText(),
                            JsonValueKind.True => "T",
                            JsonValueKind.False => "F",
                            JsonValueKind.Null => null,
                            _ => throw new JsonException(),
                        });
                    }
                }
            }
            catch (JsonException)
            {
                return Error(400, "INVALID_CONTENT", "The request body is not valid JSON, or \"params\" has a value that is not a string, number, boolean or null.");
            }

            MockSuiteQl.QueryResult result;
            try
            {
                var sql = MockSuiteQl.Prepare(query, parameters.Count, e => FindRecord(e) != null);
                lock (_lock) result = MockSuiteQl.Execute(_db, sql, parameters, limit, offset);
            }
            catch (MockSqlException e)
            {
                return Error(400, "INVALID_PARAMETER", "Invalid search query. Detailed unprocessed description follows. " + e.Message);
            }

            return Json(200, w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("links");
                w.WriteEndArray();
                w.WriteNumber("count", result.Items.Count);
                w.WriteBoolean("hasMore", offset + result.Items.Count < result.TotalResults);
                w.WriteStartArray("items");
                foreach (var row in result.Items)
                {
                    w.WriteStartObject();
                    w.WriteStartArray("links");
                    w.WriteEndArray();
                    foreach (var (key, value) in row) w.WriteString(key, value);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("offset", offset);
                w.WriteNumber("totalResults", result.TotalResults);
                w.WriteEndObject();
            });
        }

        // ---------------------------------------------------------------- レコード

        /// <summary>
        /// 要求の本文を、項目と値の並びにする。作成なら必須の項目がそろっているかも見る。合わなければ 400 の応答。
        /// </summary>
        MockResponse? ReadRecordBody(MockRecordType record, string body, bool isCreate, List<(MockField Field, object? Value)> values)
        {
            JsonDocument json;
            try
            {
                json = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                return Error(400, "INVALID_CONTENT", "The request body is not valid JSON.");
            }
            using (json)
            {
                if (json.RootElement.ValueKind != JsonValueKind.Object) return Error(400, "INVALID_CONTENT", "The request body must be a JSON object.");
                foreach (var property in json.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                        return Error(400, "INVALID_FLD_VALUE", "The internal ID is assigned by NetSuite and cannot be set.", property.Name);
                    var field = record.FindField(property.Name);
                    if (field == null) return Error(400, "INVALID_FIELD", $"Invalid field name '{property.Name}' for record type '{record.Name}'.", property.Name);
                    if (values.Any(e => e.Field == field)) return Error(400, "INVALID_CONTENT", $"The field '{property.Name}' is given twice.", property.Name);
                    var value = MockValues.ToDbValue(field, property.Value, false, out var error);
                    if (error != null) return Error(400, "INVALID_FLD_VALUE", error, property.Name);
                    //必須の項目は空 (null・空文字) にできない
                    if (!HasValue(value) && field.Required) return Error(400, "USER_ERROR", $"Please enter value(s) for: {DisplayName(field)}", field.Name);
                    if (field.Type == MockFieldType.Reference && value != null && !Exists(FindRecord(field.Record)!, (string)value))
                        return Error(400, "INVALID_KEY_OR_REF", $"Invalid {property.Name} reference key {value}.", property.Name);
                    values.Add((field, value));
                }
            }
            if (isCreate)
            {
                var missing = record.Fields.FirstOrDefault(f => f.Required && !values.Any(v => v.Field == f));
                if (missing != null) return Error(400, "USER_ERROR", $"Please enter value(s) for: {DisplayName(missing)}", missing.Name);
            }
            return null;
        }

        static bool HasValue(object? value) => value != null && !(value is string s && s.Length == 0);

        static string DisplayName(MockField field) => string.IsNullOrEmpty(field.Title) ? field.Name : field.Title;

        /// <summary>レコードの作成 (POST record/v1/{type})。204 と Location。</summary>
        public MockResponse CreateRecord(string recordType, string body, string baseUrl)
        {
            var record = FindRecord(recordType);
            if (record == null) return RecordNotFound(recordType);
            lock (_lock)
            {
                var values = new List<(MockField, object?)>();
                var error = ReadRecordBody(record, body, true, values);
                if (error != null) return error;
                var id = Insert(record, values, null);
                var response = NoContent();
                response.Headers["Location"] = $"{baseUrl}/services/rest/record/v1/{record.Name}/{id}";
                return response;
            }
        }

        /// <summary>レコードの更新 (PATCH record/v1/{type}/{id})。送られた項目だけ。null は空にする。</summary>
        public MockResponse UpdateRecord(string recordType, string id, string body)
        {
            var record = FindRecord(recordType);
            if (record == null) return RecordNotFound(recordType);
            lock (_lock)
            {
                if (!IsId(id) || !Exists(record, id)) return IdNotFound(record.Name, id);
                var values = new List<(MockField Field, object? Value)>();
                var error = ReadRecordBody(record, body, false, values);
                if (error != null) return error;
                if (values.Count != 0)
                {
                    using var command = _db.CreateCommand();
                    command.CommandText = $"update \"{record.Name}\" set {string.Join(", ", values.Select((e, i) => $"\"{e.Field.Column}\" = @v{i}"))} where id = @id";
                    for (var i = 0; i < values.Count; i++) command.Parameters.AddWithValue("@v" + i, values[i].Value ?? DBNull.Value);
                    command.Parameters.AddWithValue("@id", id);
                    command.ExecuteNonQuery();
                }
                return NoContent();
            }
        }

        /// <summary>レコードの削除 (DELETE record/v1/{type}/{id})。ほかのレコードから参照されていれば消さない。</summary>
        public MockResponse DeleteRecord(string recordType, string id)
        {
            var record = FindRecord(recordType);
            if (record == null) return RecordNotFound(recordType);
            lock (_lock)
            {
                if (!IsId(id) || !Exists(record, id)) return IdNotFound(record.Name, id);
                foreach (var other in _records)
                {
                    foreach (var field in other.Fields.Where(e => e.Type == MockFieldType.Reference && string.Equals(e.Record, record.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        using var check = _db.CreateCommand();
                        check.CommandText = $"select count(*) from \"{other.Name}\" where \"{field.Column}\" = @id";
                        check.Parameters.AddWithValue("@id", id);
                        if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                            return Error(400, "THIS_RECORD_CANNOT_BE_DELETED_BECAUSE_IT_HAS_DEPENDENT_RECORDS",
                                $"This record cannot be deleted because it has dependent records ({other.Name}.{field.Name}).");
                    }
                }
                using var command = _db.CreateCommand();
                command.CommandText = $"delete from \"{record.Name}\" where id = @id";
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
                return NoContent();
            }
        }

        /// <summary>レコードの読み出し (GET record/v1/{type}/{id})。手で中身を確かめる用。</summary>
        public MockResponse GetRecord(string recordType, string id, string baseUrl)
        {
            var record = FindRecord(recordType);
            if (record == null) return RecordNotFound(recordType);
            lock (_lock)
            {
                if (!IsId(id)) return IdNotFound(record.Name, id);
                using var command = _db.CreateCommand();
                command.CommandText = $"select * from \"{record.Name}\" where id = @id";
                command.Parameters.AddWithValue("@id", id);
                using var reader = command.ExecuteReader();
                if (!reader.Read()) return IdNotFound(record.Name, id);
                return Json(200, w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("links");
                    w.WriteStartObject();
                    w.WriteString("rel", "self");
                    w.WriteString("href", $"{baseUrl}/services/rest/record/v1/{record.Name}/{id}");
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteString("id", Convert.ToString(reader.GetValue(reader.GetOrdinal("id")), CultureInfo.InvariantCulture));
                    foreach (var field in record.Fields)
                    {
                        var ordinal = reader.GetOrdinal(field.Column);
                        if (reader.IsDBNull(ordinal)) continue;
                        w.WritePropertyName(field.Name);
                        MockValues.WriteRestValue(w, field, reader.GetValue(ordinal));
                    }
                    w.WriteEndObject();
                });
            }
        }

        static bool IsId(string id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && 0 < n;
    }
}
