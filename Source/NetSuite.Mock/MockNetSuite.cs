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
        /// <summary>バイナリの本文 (ファイルの中身)。あれば Body より先に使う。</summary>
        public byte[]? Data { get; init; }
        public string ContentType { get; init; } = "application/json";
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// NetSuite のモック。レコードの定義とデータ (SQLite のインメモリ) とアクセストークンを持ち、REST の要求に応える。
    /// HTTP への結び付けは <see cref="MockNetSuiteEndpoints.MapMockNetSuite"/>。OpenID Connect のプロバイダー (アプリのログイン) は MockOidc.cs。
    /// </summary>
    public sealed partial class MockNetSuite : IDisposable
    {
        readonly object _lock = new();
        readonly SqliteConnection _db;
        readonly List<MockRecordType> _records;
        readonly Dictionary<string, DateTime> _tokens = new(StringComparer.Ordinal);
        long _lastTransactionId;
        long _lastLineKey;

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
            var mock = new MockNetSuite(records, rows.Select(e => (e.Item1, e.Item2.Clone())).ToList());
            try
            {
                mock.LoadFileCabinet(document.RootElement);
            }
            catch
            {
                mock.Dispose();
                throw;
            }
            return mock;
        }

        /// <summary>mock-data.json の形のファイルから作る。</summary>
        public static MockNetSuite FromFile(string path) => FromJson(File.ReadAllText(path));

        public void Dispose()
        {
            _db.Dispose();
            _signingKey.Dispose();
        }

        MockRecordType? FindRecord(string name) => _records.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        // ---------------------------------------------------------------- データ

        static string ColumnDefinition(MockField field)
        {
            var type = field.Type switch
            {
                MockFieldType.Integer => "INTEGER",
                MockFieldType.Number => "NUMERIC",
                _ => "TEXT",
            };
            return $"\"{field.Column}\" {type}";
        }

        void CreateTables()
        {
            foreach (var record in _records)
            {
                var columns = new List<string> { "id INTEGER PRIMARY KEY AUTOINCREMENT" };
                columns.AddRange(record.Fields.Select(ColumnDefinition));
                Execute($"create table \"{record.Name}\" ({string.Join(", ", columns)})");

                //明細の行は明細ごとの表に親の id と line で持つ。uniquekey は全部の明細を通して行を一意に指す番号 (SuiteQL の transactionline 用)
                foreach (var sublist in record.Sublists)
                {
                    var lineColumns = new List<string> { "parent INTEGER NOT NULL", "line INTEGER NOT NULL", "uniquekey INTEGER NOT NULL UNIQUE" };
                    lineColumns.AddRange(sublist.Fields.Select(ColumnDefinition));
                    Execute($"create table \"{record.SublistTable(sublist)}\" ({string.Join(", ", lineColumns)}, primary key (parent, line))");
                }
            }
            //明細の、親ごとの最後の line (消した番号は使い回さない)
            Execute("create table mock_sublist_lines (sublist TEXT NOT NULL, parent INTEGER NOT NULL, last INTEGER NOT NULL, primary key (sublist, parent))");

            //取引のレコードを合わせた SuiteQL の transaction 表 (type 列つき)
            var transactions = _records.Where(e => e.IsTransaction).ToList();
            if (transactions.Count != 0)
            {
                var columns = transactions.SelectMany(e => e.Fields.Select(f => f.Column)).Distinct().ToList();
                var selects = transactions.Select(record => $"select id, '{record.TransactionType}' as type" +
                    string.Concat(columns.Select(c => record.Fields.Any(f => f.Column == c) ? $", \"{c}\"" : $", NULL as \"{c}\"")) + $" from \"{record.Name}\"");
                Execute($"create view \"transaction\" as {string.Join(" union all ", selects)}");
            }

            //取引の明細の行を合わせた SuiteQL の transactionline 表 (明細の行だけ。mainline・taxline は常に 'F'。id と linesequencenumber は line)
            var lines = transactions.SelectMany(record => record.Sublists.Select(sublist => (Record: record, Sublist: sublist))).ToList();
            if (lines.Count != 0)
            {
                var columns = lines.SelectMany(e => e.Sublist.Fields.Select(f => f.Column)).Distinct().Where(e => !TransactionLineColumns.Contains(e)).ToList();
                var selects = lines.Select(e => "select s.uniquekey, s.parent as \"transaction\", s.line as id, s.line as linesequencenumber, 'F' as mainline, 'F' as taxline" +
                    string.Concat(columns.Select(c => e.Sublist.Fields.Any(f => f.Column == c) ? $", s.\"{c}\"" : $", NULL as \"{c}\"")) + $" from \"{e.Record.SublistTable(e.Sublist)}\" s");
                Execute($"create view transactionline as {string.Join(" union all ", selects)}");
            }
        }

        //transactionline の決まった列 (同じ名前の明細の項目はこちらが優先)
        static readonly HashSet<string> TransactionLineColumns = new(StringComparer.OrdinalIgnoreCase) { "uniquekey", "transaction", "id", "linesequencenumber", "mainline", "taxline" };

        /// <summary>
        /// SuiteQL で読める表か (取引でないレコード名と、取引のレコードがあるときの transaction、明細のある取引があるときの transactionline)。
        /// 取引のレコード名 (salesOrder など) は NetSuite と同じく表として読めない (transaction の表を type で絞って読む)。
        /// </summary>
        bool IsTable(string name)
            => FindRecord(name) is { IsTransaction: false } ||(string.Equals(name, "transaction", StringComparison.OrdinalIgnoreCase) && _records.Any(e => e.IsTransaction)) ||
               (string.Equals(name, "transactionline", StringComparison.OrdinalIgnoreCase) && _records.Any(e => e.IsTransaction && e.Sublists.Count != 0));

        void LoadRows(List<(string Record, JsonElement Row)> rows)
        {
            //取引の id は取引のレコード同士で重ならないよう、書いてある id の次から振る
            foreach (var (name, row) in rows)
            {
                if (FindRecord(name)!.IsTransaction && row.ValueKind == JsonValueKind.Object && row.TryGetProperty("id", out var given) &&
                    given.ValueKind == JsonValueKind.Number && given.TryGetInt64(out var n)) _lastTransactionId = Math.Max(_lastTransactionId, n);
            }

            //参照の先は全部入れてから確かめる (レコードの並びに依らない)
            foreach (var (name, row) in rows)
            {
                var record = FindRecord(name)!;
                if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"mock data: a row of record '{record.Name}' must be an object.");
                long? id = null;
                var values = new List<(MockField, object?)>();
                var sublists = new Dictionary<MockSublist, List<SublistLine>>();
                foreach (var property in row.EnumerateObject())
                {
                    if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var n) && 0 < n) id = n;
                        else if (property.Value.ValueKind == JsonValueKind.String && long.TryParse(property.Value.GetString(), out var s) && 0 < s) id = s;
                        else throw new InvalidDataException($"mock data: the id of a row of record '{record.Name}' must be a positive integer.");
                        continue;
                    }
                    if (record.FindSublist(property.Name) is { } sublist)
                    {
                        var lines = new List<SublistLine>();
                        var failed = ReadSublistLines(sublist, property.Value, true, true, lines);
                        if (failed != null) throw new InvalidDataException($"mock data: record '{record.Name}' (id {id?.ToString() ?? "?"}): {failed.Body}");
                        sublists[sublist] = lines;
                        continue;
                    }
                    var field = record.FindField(property.Name) ?? throw new InvalidDataException($"mock data: record '{record.Name}' has no field '{property.Name}'.");
                    var value = MockValues.ToDbValue(field, property.Value, true, out var error);
                    if (error != null) throw new InvalidDataException($"mock data: record '{record.Name}' (id {id?.ToString() ?? "?"}): {error}");
                    values.Add((field, value));
                }
                var missing = record.Fields.FirstOrDefault(f => f.Required && !values.Any(v => v.Item1 == f && HasValue(v.Item2)));
                if (missing != null) throw new InvalidDataException($"mock data: a row of record '{record.Name}' has no value for the required field '{missing.Name}'.");
                var missingSublist = record.Sublists.FirstOrDefault(s => s.Required && !(sublists.TryGetValue(s, out var l) && l.Count != 0));
                if (missingSublist != null) throw new InvalidDataException($"mock data: a row of record '{record.Name}' has no line in the required sublist '{missingSublist.Name}'.");
                try
                {
                    var newId = Insert(record, values, id);
                    foreach (var (sublist, lines) in sublists) InsertLines(record, sublist, newId, lines);
                }
                catch (SqliteException e)
                {
                    throw new InvalidDataException($"mock data: a row of record '{record.Name}' could not be added: {e.Message}");
                }
            }
            foreach (var record in _records)
            {
                foreach (var (table, owner, field) in ReferenceFields(record))
                {
                    var target = FindRecord(field.Record)!;
                    using var command = _db.CreateCommand();
                    command.CommandText = $"select r.{(table == record.Name ? "id" : "parent")}, r.\"{field.Column}\" from \"{table}\" r where r.\"{field.Column}\" is not null and not exists (select 1 from \"{target.Name}\" t where t.id = r.\"{field.Column}\")";
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                        throw new InvalidDataException($"mock data: record '{record.Name}' (id {reader.GetValue(0)}) refers to {target.Name} {reader.GetValue(1)} in '{owner}', which does not exist.");
                }
            }
            if (_records.Any(e => e.IsTransaction))
            {
                using var command = _db.CreateCommand();
                command.CommandText = "select id from \"transaction\" group by id having count(*) > 1";
                var duplicated = command.ExecuteScalar();
                if (duplicated != null) throw new InvalidDataException($"mock data: the id {duplicated} is used by two transaction records (ids of transaction records must not overlap).");
            }
        }

        //参照の項目 (表・項目の名前・項目)。明細の行の参照も含む
        static IEnumerable<(string Table, string Name, MockField Field)> ReferenceFields(MockRecordType record)
        {
            foreach (var field in record.Fields.Where(e => e.Type == MockFieldType.Reference)) yield return (record.Name, field.Name, field);
            foreach (var sublist in record.Sublists)
            {
                foreach (var field in sublist.Fields.Where(e => e.Type == MockFieldType.Reference)) yield return (record.SublistTable(sublist), $"{sublist.Name}.{field.Name}", field);
            }
        }

        long Insert(MockRecordType record, List<(MockField Field, object? Value)> values, long? id)
        {
            //取引の id は取引のレコード同士で通しの番号
            if (record.IsTransaction)
            {
                if (id == null) id = ++_lastTransactionId;
                else _lastTransactionId = Math.Max(_lastTransactionId, id.Value);
            }
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

        // ---------------------------------------------------------------- 明細の行

        /// <summary>要求・データの明細の 1 行。Line は既存の行を指すとき (更新) だけ。</summary>
        sealed class SublistLine
        {
            public long? Line { get; set; }
            public List<(MockField Field, object? Value)> Values { get; } = new();
        }

        const string RequiredSublistMessage = "You must enter at least one line item for this transaction.";

        /// <summary>
        /// 明細の本文 ({"items":[...]}) を行にする。合わなければ 400 の応答。
        /// loose は mock-data.json の初期データ (参照を 3 / "3" でも書けて、参照先の有無は全部入れてから確かめる)。
        /// </summary>
        MockResponse? ReadSublistLines(MockSublist sublist, JsonElement value, bool isCreate, bool loose, List<SublistLine> lines)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return Error(400, "INVALID_CONTENT", $"The sublist '{sublist.Name}' must be an object with \"items\": [ ... ].", sublist.Name);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return Error(400, "INVALID_CONTENT", $"A line of sublist '{sublist.Name}' must be an object.", sublist.Name);
                var line = new SublistLine();
                foreach (var property in item.EnumerateObject())
                {
                    var path = $"{sublist.Name}.items.{property.Name}";
                    if (property.Name == "links") continue;
                    if (property.Name == "line")
                    {
                        if (isCreate) return Error(400, "INVALID_FLD_VALUE", "The line key cannot be given when the record is created.", path);
                        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var n) || n < 1)
                            return Error(400, "INVALID_FLD_VALUE", "The line key must be a positive integer.", path);
                        line.Line = n;
                        continue;
                    }
                    var field = sublist.FindField(property.Name);
                    if (field == null) return Error(400, "INVALID_FIELD", $"Invalid field name '{property.Name}' for sublist '{sublist.Name}'.", path);
                    if (line.Values.Any(e => e.Field == field)) return Error(400, "INVALID_CONTENT", $"The field '{property.Name}' is given twice.", path);
                    var v = MockValues.ToDbValue(field, property.Value, loose, out var error);
                    if (error != null) return Error(400, "INVALID_FLD_VALUE", error, path);
                    if (!loose && field.Type == MockFieldType.Reference && v != null && !Exists(FindRecord(field.Record)!, (string)v))
                        return Error(400, "INVALID_KEY_OR_REF", $"Invalid {property.Name} reference key {v}.", path);
                    line.Values.Add((field, v));
                }
                //必須の項目: 新しい行では省けず、既存の行では空にできない
                var missing = sublist.Fields.FirstOrDefault(f => f.Required && (line.Line == null
                    ? !line.Values.Any(v => v.Field == f && HasValue(v.Value))
                    : line.Values.Any(v => v.Field == f && !HasValue(v.Value))));
                if (missing != null) return Error(400, "USER_ERROR", $"Please enter value(s) for: {DisplayName(missing)}", $"{sublist.Name}.items.{missing.Name}");
                lines.Add(line);
            }
            return null;
        }

        void InsertLines(MockRecordType record, MockSublist sublist, long parent, IEnumerable<SublistLine> lines)
        {
            foreach (var line in lines) InsertLine(record.SublistTable(sublist), parent, line.Values);
        }

        //新しい行。line は親ごとに 1 からの連番 (消した番号は使い回さない)
        void InsertLine(string table, long parent, List<(MockField Field, object? Value)> values)
        {
            long line;
            using (var next = _db.CreateCommand())
            {
                next.CommandText = "insert into mock_sublist_lines (sublist, parent, last) values (@t, @p, 1) on conflict (sublist, parent) do update set last = last + 1; " +
                    "select last from mock_sublist_lines where sublist = @t and parent = @p;";
                next.Parameters.AddWithValue("@t", table);
                next.Parameters.AddWithValue("@p", parent);
                line = Convert.ToInt64(next.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            using var command = _db.CreateCommand();
            var columns = new List<string> { "parent", "line", "uniquekey" };
            columns.AddRange(values.Select(e => $"\"{e.Field.Column}\""));
            var parameters = new List<string> { "@parent", "@line", "@uniquekey" };
            parameters.AddRange(values.Select((_, i) => "@v" + i));
            command.Parameters.AddWithValue("@parent", parent);
            command.Parameters.AddWithValue("@line", line);
            command.Parameters.AddWithValue("@uniquekey", ++_lastLineKey);
            for (var i = 0; i < values.Count; i++) command.Parameters.AddWithValue("@v" + i, values[i].Value ?? DBNull.Value);
            command.CommandText = $"insert into \"{table}\" ({string.Join(", ", columns)}) values ({string.Join(", ", parameters)})";
            command.ExecuteNonQuery();
        }

        void UpdateLine(string table, long parent, long line, List<(MockField Field, object? Value)> values)
        {
            if (values.Count == 0) return;
            using var command = _db.CreateCommand();
            command.CommandText = $"update \"{table}\" set {string.Join(", ", values.Select((e, i) => $"\"{e.Field.Column}\" = @v{i}"))} where parent = @parent and line = @line";
            for (var i = 0; i < values.Count; i++) command.Parameters.AddWithValue("@v" + i, values[i].Value ?? DBNull.Value);
            command.Parameters.AddWithValue("@parent", parent);
            command.Parameters.AddWithValue("@line", line);
            command.ExecuteNonQuery();
        }

        List<long> GetLines(string table, long parent)
        {
            using var command = _db.CreateCommand();
            command.CommandText = $"select line from \"{table}\" where parent = @parent order by line";
            command.Parameters.AddWithValue("@parent", parent);
            using var reader = command.ExecuteReader();
            var lines = new List<long>();
            while (reader.Read()) lines.Add(reader.GetInt64(0));
            return lines;
        }

        void DeleteLine(string table, long parent, long line)
        {
            using var command = _db.CreateCommand();
            command.CommandText = $"delete from \"{table}\" where parent = @parent and line = @line";
            command.Parameters.AddWithValue("@parent", parent);
            command.Parameters.AddWithValue("@line", line);
            command.ExecuteNonQuery();
        }

        //参照の表示名 (refName)。参照先のレコードの最初の文字の項目
        string? RefName(MockField field, object id)
        {
            var target = FindRecord(field.Record);
            var name = target?.Fields.FirstOrDefault(e => e.Type == MockFieldType.String);
            if (target == null || name == null) return null;
            using var command = _db.CreateCommand();
            command.CommandText = $"select \"{name.Column}\" from \"{target.Name}\" where id = @id";
            command.Parameters.AddWithValue("@id", id);
            return command.ExecuteScalar() as string;
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

        static MockResponse TokenError(string error, string description, int status = 400)
            => Json(status, w => { w.WriteStartObject(); w.WriteString("error", error); w.WriteString("error_description", description); w.WriteEndObject(); });

        /// <summary>
        /// トークンの要求 (form の値)。client_credentials (M2M) は JWT の形と exp を確かめる (署名は確かめない)。
        /// authorization_code (アプリのログイン) は <see cref="IssueAuthorizationCodeToken"/>。authorization は Authorization ヘッダー (client_secret_basic)、baseUrl は ID トークンの iss。
        /// </summary>
        public MockResponse IssueToken(IReadOnlyDictionary<string, string> form, string? authorization = null, string baseUrl = "")
        {
            static MockResponse Fail(string error, string description) => TokenError(error, description);

            string Get(string key) => form.TryGetValue(key, out var v) ? v : string.Empty;
            if (Get("grant_type") == "authorization_code") return IssueAuthorizationCodeToken(form, authorization, baseUrl);
            if (Get("grant_type") != "client_credentials") return Fail("unsupported_grant_type", "grant_type must be client_credentials or authorization_code.");
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

        /// <summary>
        /// レコードの JSON Schema (GET record/v1/metadata-catalog/{type}、Accept: application/schema+json)。
        /// 明細は $ref (&lt;レコード名&gt;-&lt;明細&gt;Collection) で書き、その先 (…Collection → items → …Element) も同じ入口で返す。
        /// </summary>
        public MockResponse GetMetadata(string recordType, string? accept, string baseUrl = "")
        {
            var record = FindRecord(recordType);
            MockSublist? sublist = null;
            var isElement = false;
            if (record == null)
            {
                //明細の定義: <レコード名>-<明細>Collection / <レコード名>-<明細>Element
                var separator = recordType.IndexOf('-');
                if (0 < separator)
                {
                    record = FindRecord(recordType[..separator]);
                    var rest = recordType[(separator + 1)..];
                    isElement = rest.EndsWith("Element", StringComparison.Ordinal);
                    var name = isElement ? rest[..^"Element".Length] : rest.EndsWith("Collection", StringComparison.Ordinal) ? rest[..^"Collection".Length] : null;
                    sublist = name == null ? null : record?.FindSublist(name);
                }
                if (record == null || sublist == null) return RecordNotFound(recordType);
            }
            if (accept == null || !accept.Contains("application/schema+json", StringComparison.OrdinalIgnoreCase))
                return Error(406, "INVALID_HEADER", "This mock returns the metadata only as JSON Schema. Send the header 'Accept: application/schema+json'.");

            var catalog = $"{baseUrl}/services/rest/record/v1/metadata-catalog";
            if (sublist != null && !isElement)
            {
                return Json(200, w =>
                {
                    w.WriteStartObject();
                    w.WriteString("type", "object");
                    w.WriteString("title", $"{record.Name}-{sublist.Name}Collection");
                    w.WriteStartObject("properties");
                    WriteLinksSchema(w);
                    w.WriteStartObject("items");
                    w.WriteString("type", "array");
                    w.WriteStartObject("items");
                    w.WriteString("$ref", $"{catalog}/{record.Name}-{sublist.Name}Element");
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteStartObject("totalResults");
                    w.WriteString("type", "integer");
                    w.WriteEndObject();
                    w.WriteEndObject();
                    w.WriteEndObject();
                }, "application/schema+json");
            }

            return Json(200, w =>
            {
                w.WriteStartObject();
                w.WriteString("type", "object");
                w.WriteString("title", sublist == null ? record.Name : $"{record.Name}-{sublist.Name}Element");
                w.WriteStartObject("properties");
                if (sublist == null)
                {
                    w.WriteStartObject("id");
                    w.WriteString("title", "Internal ID");
                    w.WriteString("type", "string");
                    w.WriteEndObject();
                }
                else
                {
                    w.WriteStartObject("line");
                    w.WriteString("title", "Line");
                    w.WriteString("type", "integer");
                    w.WriteString("format", "int64");
                    w.WriteEndObject();
                }
                var fields = sublist == null ? record.Fields : sublist.Fields;
                foreach (var field in fields) WriteFieldSchema(w, field);
                if (sublist == null)
                {
                    foreach (var s in record.Sublists)
                    {
                        w.WriteStartObject(s.Name);
                        w.WriteString("title", s.Name);
                        w.WriteString("$ref", $"{catalog}/{record.Name}-{s.Name}Collection");
                        w.WriteEndObject();
                    }
                }
                WriteLinksSchema(w);
                if (sublist == null)
                {
                    w.WriteStartObject("refName");
                    w.WriteString("title", "Reference Name");
                    w.WriteString("type", "string");
                    w.WriteEndObject();
                }
                w.WriteEndObject();
                var required = fields.Where(e => e.Required).Select(e => e.Name).ToList();
                if (required.Count != 0)
                {
                    w.WriteStartArray("required");
                    foreach (var name in required) w.WriteStringValue(name);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }, "application/schema+json");
        }

        static void WriteFieldSchema(Utf8JsonWriter w, MockField field)
        {
            w.WriteStartObject(field.Name);
            w.WriteString("title", DisplayName(field));
            if (field.ReadOnly) w.WriteBoolean("readOnly", true);
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
                case MockFieldType.Select:
                    //固定の選択肢: id の enum に内部の値だけを並べる (表示名はメタデータに出さない)
                    w.WriteString("type", "object");
                    w.WriteStartObject("properties");
                    w.WriteStartObject("id");
                    w.WriteString("title", "Internal identifier");
                    w.WriteString("type", "string");
                    w.WriteStartArray("enum");
                    foreach (var value in field.Values) w.WriteStringValue(value.Key);
                    w.WriteEndArray();
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

        static void WriteLinksSchema(Utf8JsonWriter w)
        {
            w.WriteStartObject("links");
            w.WriteString("title", "Links");
            w.WriteString("type", "array");
            w.WriteStartObject("items");
            w.WriteString("type", "object");
            w.WriteEndObject();
            w.WriteEndObject();
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
                var sql = MockSuiteQl.Prepare(query, parameters.Count, IsTable);
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
        MockResponse? ReadRecordBody(MockRecordType record, string body, bool isCreate, List<(MockField Field, object? Value)> values,
            Dictionary<MockSublist, List<SublistLine>> sublists)
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
                    if (record.FindSublist(property.Name) is { } sublist)
                    {
                        var lines = new List<SublistLine>();
                        var failed = ReadSublistLines(sublist, property.Value, isCreate, false, lines);
                        if (failed != null) return failed;
                        sublists[sublist] = lines;
                        continue;
                    }
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
                //明細が要るレコード (取引) は、明細なしでは作れない
                var missingSublist = record.Sublists.FirstOrDefault(s => s.Required && !(sublists.TryGetValue(s, out var l) && l.Count != 0));
                if (missingSublist != null) return Error(400, "USER_ERROR", RequiredSublistMessage, missingSublist.Name);
            }
            return null;
        }

        static bool HasValue(object? value) => value != null && !(value is string s && s.Length == 0);

        static string DisplayName(MockField field) => string.IsNullOrEmpty(field.Title) ? field.Name : field.Title;

        /// <summary>レコードの作成 (POST record/v1/{type})。204 と Location。明細は "<明細>": {"items":[...]} で同梱する。</summary>
        public MockResponse CreateRecord(string recordType, string body, string baseUrl)
        {
            var record = FindRecord(recordType);
            if (record == null) return RecordNotFound(recordType);
            lock (_lock)
            {
                var values = new List<(MockField, object?)>();
                var sublists = new Dictionary<MockSublist, List<SublistLine>>();
                var error = ReadRecordBody(record, body, true, values, sublists);
                if (error != null) return error;
                var id = Insert(record, values, null);
                foreach (var (sublist, lines) in sublists) InsertLines(record, sublist, id, lines);
                var response = NoContent();
                response.Headers["Location"] = $"{baseUrl}/services/rest/record/v1/{record.Name}/{id}";
                return response;
            }
        }

        /// <summary>
        /// レコードの更新 (PATCH record/v1/{type}/{id})。送られた項目だけ。null は空にする。
        /// 明細は line が一致する行を更新し、line の無い行を足す (line が今の行に無ければ 400)。
        /// replace (?replace=明細,...) の明細は、要求に無い行を消す。
        /// </summary>
        public MockResponse UpdateRecord(string recordType, string id, string body, string? replace = null)
        {
            var record = FindRecord(recordType);
            if (record == null) return RecordNotFound(recordType);
            lock (_lock)
            {
                if (!IsId(id) || !Exists(record, id)) return IdNotFound(record.Name, id);
                var replaced = new List<MockSublist>();
                foreach (var name in (replace ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var sublist = record.FindSublist(name);
                    if (sublist == null)
                        return Error(400, "INVALID_PARAMETER", $"Invalid value for the parameter 'replace'. '{name}' is not a sublist of record type '{record.Name}'.", "replace");
                    if (!replaced.Contains(sublist)) replaced.Add(sublist);
                }
                var values = new List<(MockField Field, object? Value)>();
                var sublists = new Dictionary<MockSublist, List<SublistLine>>();
                var error = ReadRecordBody(record, body, false, values, sublists);
                if (error != null) return error;
                var parent = long.Parse(id, CultureInfo.InvariantCulture);

                //明細は全部確かめてから書く (途中で失敗して一部だけ変わらないように)
                var targets = sublists.Keys.Union(replaced).ToList();
                foreach (var sublist in targets)
                {
                    var existing = GetLines(record.SublistTable(sublist), parent);
                    var lines = sublists.TryGetValue(sublist, out var l) ? l : new();
                    var given = new HashSet<long>();
                    foreach (var line in lines.Where(e => e.Line != null))
                    {
                        var path = $"{sublist.Name}.items.line";
                        if (!existing.Contains(line.Line!.Value)) return Error(400, "INVALID_KEY_OR_REF", $"The line {line.Line} of sublist '{sublist.Name}' does not exist.", path);
                        if (!given.Add(line.Line.Value)) return Error(400, "INVALID_CONTENT", $"The line {line.Line} of sublist '{sublist.Name}' is given twice.", path);
                    }
                    var added = lines.Count(e => e.Line == null);
                    var remaining = (replaced.Contains(sublist) ? given.Count : existing.Count) + added;
                    if (sublist.Required && remaining == 0) return Error(400, "USER_ERROR", RequiredSublistMessage, sublist.Name);
                }

                if (values.Count != 0)
                {
                    using var command = _db.CreateCommand();
                    command.CommandText = $"update \"{record.Name}\" set {string.Join(", ", values.Select((e, i) => $"\"{e.Field.Column}\" = @v{i}"))} where id = @id";
                    for (var i = 0; i < values.Count; i++) command.Parameters.AddWithValue("@v" + i, values[i].Value ?? DBNull.Value);
                    command.Parameters.AddWithValue("@id", id);
                    command.ExecuteNonQuery();
                }
                foreach (var sublist in targets)
                {
                    var table = record.SublistTable(sublist);
                    var lines = sublists.TryGetValue(sublist, out var l) ? l : new();
                    if (replaced.Contains(sublist))
                    {
                        var keep = lines.Where(e => e.Line != null).Select(e => e.Line!.Value).ToHashSet();
                        foreach (var line in GetLines(table, parent).Where(e => !keep.Contains(e))) DeleteLine(table, parent, line);
                    }
                    foreach (var line in lines)
                    {
                        if (line.Line is long n) UpdateLine(table, parent, n, line.Values);
                        else InsertLine(table, parent, line.Values);
                    }
                }
                return NoContent();
            }
        }

        /// <summary>レコードの削除 (DELETE record/v1/{type}/{id})。ほかのレコード (明細の行を含む) から参照されていれば消さない。明細の行も消える。</summary>
        public MockResponse DeleteRecord(string recordType, string id)
        {
            var record = FindRecord(recordType);
            if (record == null) return RecordNotFound(recordType);
            lock (_lock)
            {
                if (!IsId(id) || !Exists(record, id)) return IdNotFound(record.Name, id);
                foreach (var other in _records)
                {
                    foreach (var (table, name, field) in ReferenceFields(other).Where(e => string.Equals(e.Field.Record, record.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        using var check = _db.CreateCommand();
                        check.CommandText = $"select count(*) from \"{table}\" where \"{field.Column}\" = @id";
                        check.Parameters.AddWithValue("@id", id);
                        if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                            return Error(400, "THIS_RECORD_CANNOT_BE_DELETED_BECAUSE_IT_HAS_DEPENDENT_RECORDS",
                                $"This record cannot be deleted because it has dependent records ({other.Name}.{name}).");
                    }
                }
                foreach (var sublist in record.Sublists)
                {
                    using var lines = _db.CreateCommand();
                    lines.CommandText = $"delete from \"{record.SublistTable(sublist)}\" where parent = @id; delete from mock_sublist_lines where sublist = @t and parent = @id;";
                    lines.Parameters.AddWithValue("@id", id);
                    lines.Parameters.AddWithValue("@t", record.SublistTable(sublist));
                    lines.ExecuteNonQuery();
                }
                using var command = _db.CreateCommand();
                command.CommandText = $"delete from \"{record.Name}\" where id = @id";
                command.Parameters.AddWithValue("@id", id);
                command.ExecuteNonQuery();
                RemoveAttachments(record.Name, id);
                return NoContent();
            }
        }

        /// <summary>
        /// レコードの読み出し (GET record/v1/{type}/{id})。
        /// 明細は expandSubResources なら {"items":[...]} (行は line と項目。参照は id と refName)、そうでなければ links だけ。
        /// </summary>
        public MockResponse GetRecord(string recordType, string id, string baseUrl, bool expandSubResources = false)
        {
            var record = FindRecord(recordType);
            if (record == null) return RecordNotFound(recordType);
            lock (_lock)
            {
                if (!IsId(id)) return IdNotFound(record.Name, id);
                var values = ReadRow(record.Name, "id = @id", id);
                if (values.Count == 0) return IdNotFound(record.Name, id);
                var lines = new Dictionary<MockSublist, List<Dictionary<string, object?>>>();
                if (expandSubResources)
                {
                    foreach (var sublist in record.Sublists)
                    {
                        lines[sublist] = ReadRows(record.SublistTable(sublist), "parent = @id order by line", id);
                        foreach (var row in lines[sublist])
                        {
                            foreach (var field in sublist.Fields.Where(e => e.Type == MockFieldType.Reference))
                            {
                                if (row[field.Column] is { } refId) row[field.Column + "\u0001refName"] = RefName(field, refId);
                            }
                        }
                    }
                }
                var self = $"{baseUrl}/services/rest/record/v1/{record.Name}/{id}";
                return Json(200, w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("links");
                    w.WriteStartObject();
                    w.WriteString("rel", "self");
                    w.WriteString("href", self);
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteString("id", Convert.ToString(values["id"], CultureInfo.InvariantCulture));
                    foreach (var field in record.Fields)
                    {
                        if (values[field.Column] is not { } value) continue;
                        w.WritePropertyName(field.Name);
                        MockValues.WriteRestValue(w, field, value);
                    }
                    foreach (var sublist in record.Sublists)
                    {
                        w.WriteStartObject(sublist.Name);
                        w.WriteStartArray("links");
                        w.WriteStartObject();
                        w.WriteString("rel", "self");
                        w.WriteString("href", $"{self}/{sublist.Name}");
                        w.WriteEndObject();
                        w.WriteEndArray();
                        if (lines.TryGetValue(sublist, out var rows))
                        {
                            w.WriteStartArray("items");
                            foreach (var row in rows)
                            {
                                w.WriteStartObject();
                                w.WriteStartArray("links");
                                w.WriteEndArray();
                                w.WriteNumber("line", Convert.ToInt64(row["line"], CultureInfo.InvariantCulture));
                                foreach (var field in sublist.Fields)
                                {
                                    if (row[field.Column] is not { } value) continue;
                                    w.WritePropertyName(field.Name);
                                    if (field.Type == MockFieldType.Reference)
                                    {
                                        w.WriteStartObject();
                                        w.WriteString("id", Convert.ToString(value, CultureInfo.InvariantCulture));
                                        if (row.TryGetValue(field.Column + "\u0001refName", out var refName) && refName is string text) w.WriteString("refName", text);
                                        w.WriteEndObject();
                                        continue;
                                    }
                                    MockValues.WriteRestValue(w, field, value);
                                }
                                w.WriteEndObject();
                            }
                            w.WriteEndArray();
                            w.WriteNumber("totalResults", rows.Count);
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                });
            }
        }

        //表の行 (列名 → 値。null は null)
        List<Dictionary<string, object?>> ReadRows(string table, string where, string id)
        {
            using var command = _db.CreateCommand();
            command.CommandText = $"select * from \"{table}\" where {where}";
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }

        Dictionary<string, object?> ReadRow(string table, string where, string id)
            => ReadRows(table, where, id).FirstOrDefault() ?? new();

        static bool IsId(string id) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && 0 < n;
    }
}
