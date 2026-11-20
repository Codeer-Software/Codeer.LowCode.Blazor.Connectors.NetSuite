using System.Globalization;
using System.Text.Json;

namespace NetSuite.Mock
{
    /// <summary>項目の種類 (mock-data.json の "type")。</summary>
    public enum MockFieldType
    {
        String,
        Boolean,
        Integer,
        Number,
        Date,
        DateTime,
        /// <summary>ほかのレコードへの参照。値は相手の internal ID。</summary>
        Reference,
        /// <summary>固定の選択肢 (値は選択肢の内部の値。REST では {"id","refName"}、メタデータでは id の enum)。</summary>
        Select,
    }

    /// <summary>レコードの項目 1 つ。</summary>
    public sealed class MockField
    {
        /// <summary>REST の項目名 (例: companyName)。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>SuiteQL の列名 (REST の項目名の小文字)。</summary>
        public string Column => Name.ToLowerInvariant();

        public MockFieldType Type { get; init; }

        /// <summary>参照先のレコード名 (<see cref="MockFieldType.Reference"/> のとき)。</summary>
        public string Record { get; init; } = string.Empty;

        public bool Required { get; init; }

        /// <summary>表示名 (メタデータの title)。省略すると項目名。</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>読み取り専用 (メタデータに readOnly: true を書く。書き込みは拒まない)。</summary>
        public bool ReadOnly { get; init; }

        /// <summary>選択肢 (<see cref="MockFieldType.Select"/> のとき)。内部の値 → 表示名 (refName)。</summary>
        public List<KeyValuePair<string, string>> Values { get; init; } = new();
    }

    /// <summary>明細 (行キー line を持つサブリスト) 1 つの定義。</summary>
    public sealed class MockSublist
    {
        /// <summary>REST の項目名 (例: item)。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>1 行以上が要る (作成・更新で行を 0 にできない)。</summary>
        public bool Required { get; init; }

        /// <summary>行の項目 (line は含まない。line は親ごとに 1 からの連番で自動で付く)。</summary>
        public List<MockField> Fields { get; } = new();

        public MockField? FindField(string name) => Fields.FirstOrDefault(e => e.Name == name);
    }

    /// <summary>レコード種別 1 つの定義。</summary>
    public sealed class MockRecordType
    {
        public string Name { get; init; } = string.Empty;
        public List<MockField> Fields { get; } = new();

        /// <summary>取引のレコードなら SuiteQL の transaction 表の type の値 (例: SalesOrd)。取引でなければ空。</summary>
        public string TransactionType { get; init; } = string.Empty;

        public bool IsTransaction => !string.IsNullOrEmpty(TransactionType);

        /// <summary>明細。</summary>
        public List<MockSublist> Sublists { get; } = new();

        /// <summary>REST の項目名で探す (大文字・小文字を区別する)。</summary>
        public MockField? FindField(string name) => Fields.FirstOrDefault(e => e.Name == name);

        public MockSublist? FindSublist(string name) => Sublists.FirstOrDefault(e => e.Name == name);

        /// <summary>明細の行を持つ SQLite の表 (parent = 親の id・line・行の項目)。SuiteQL からは読めない。</summary>
        internal string SublistTable(MockSublist sublist) => $"sublist_{Name}_{sublist.Name}";

        internal static bool IsIdentifier(string text)
            => !string.IsNullOrEmpty(text) && (char.IsAsciiLetter(text[0]) || text[0] == '_') && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    /// <summary>mock-data.json の読み取りと、値の変換 (REST の JSON ⇔ SQLite)。</summary>
    static class MockValues
    {
        internal const string DateFormat = "yyyy-MM-dd";
        internal const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>
        /// REST の JSON の値を SQLite に入れる値にする。合わなければ error に理由。
        /// 参照の値は <paramref name="loose"/> なら "3" / 3 も受け付ける (mock-data.json の初期データ用)。REST の要求では {"id":"3"} だけ。
        /// </summary>
        internal static object? ToDbValue(MockField field, JsonElement value, bool loose, out string? error)
        {
            error = null;
            if (value.ValueKind == JsonValueKind.Null) return null;
            switch (field.Type)
            {
                case MockFieldType.String:
                    if (value.ValueKind == JsonValueKind.String) return value.GetString();
                    break;
                case MockFieldType.Boolean:
                    if (value.ValueKind == JsonValueKind.True) return "T";
                    if (value.ValueKind == JsonValueKind.False) return "F";
                    break;
                case MockFieldType.Integer:
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer)) return integer;
                    break;
                case MockFieldType.Number:
                    if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
                    break;
                case MockFieldType.Date:
                    if (value.ValueKind == JsonValueKind.String &&
                        DateTime.TryParseExact(value.GetString(), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        return date.ToString(DateFormat, CultureInfo.InvariantCulture);
                    break;
                case MockFieldType.DateTime:
                    if (value.ValueKind == JsonValueKind.String && TryParseRestDateTime(value.GetString()!, out var dateTime))
                        return dateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture);
                    break;
                case MockFieldType.Reference:
                    {
                        var id = GetReferenceId(value, loose);
                        if (id != null) return id;
                        break;
                    }
                case MockFieldType.Select:
                    {
                        //{"id":"内部の値"} か {"refName":"表示名"} (NetSuite はどちらも受け付ける)。初期データは文字だけでもよい
                        string? selected = null;
                        if (value.ValueKind == JsonValueKind.Object)
                        {
                            if (value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) selected = field.Values.FirstOrDefault(e => e.Key == id.GetString()).Key;
                            else if (value.TryGetProperty("refName", out var refName) && refName.ValueKind == JsonValueKind.String) selected = field.Values.FirstOrDefault(e => e.Value == refName.GetString()).Key;
                        }
                        else if (loose && value.ValueKind == JsonValueKind.String)
                        {
                            selected = field.Values.FirstOrDefault(e => e.Key == value.GetString()).Key;
                        }
                        if (selected != null) return selected;
                        break;
                    }
            }
            error = field.Type switch
            {
                MockFieldType.String => "a string",
                MockFieldType.Boolean => "true or false",
                MockFieldType.Integer => "an integer",
                MockFieldType.Number => "a number",
                MockFieldType.Date => "a date string (YYYY-MM-DD)",
                MockFieldType.DateTime => "a date-time string (e.g. 2026-03-04T05:06:07Z)",
                MockFieldType.Select => "one of the values ({\"id\":\"...\"} or {\"refName\":\"...\"}): " + string.Join(", ", field.Values.Select(e => e.Key)),
                _ => "a reference object ({\"id\":\"...\"})",
            };
            error = $"Invalid value for field '{field.Name}'. The value must be {error}.";
            return null;
        }

        static string? GetReferenceId(JsonElement value, bool loose)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty("id", out var id)) return null;
                value = id;
            }
            else if (!loose)
            {
                return null;
            }
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : null;
        }

        //時刻帯つきの ISO 8601 (2026-03-04T05:06:07Z / +09:00)。UTC にする
        static bool TryParseRestDateTime(string text, out DateTime utc)
        {
            utc = default;
            if (text.Length < 20 || text[10] != 'T') return false;
            var last = text[^1];
            if (!(last == 'Z' || text.LastIndexOfAny(new[] { '+', '-' }) > 10)) return false;
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset)) return false;
            utc = offset.UtcDateTime;
            return true;
        }

        /// <summary>SQLite の値を REST の JSON の値として書く。</summary>
        internal static void WriteRestValue(Utf8JsonWriter writer, MockField field, object value)
        {
            switch (field.Type)
            {
                case MockFieldType.Boolean:
                    writer.WriteBooleanValue(Convert.ToString(value, CultureInfo.InvariantCulture) == "T");
                    break;
                case MockFieldType.Integer:
                    writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
                case MockFieldType.Number:
                    writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    break;
                case MockFieldType.DateTime:
                    {
                        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                        writer.WriteStringValue(DateTime.TryParseExact(text, DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                            ? d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : text);
                        break;
                    }
                case MockFieldType.Reference:
                    writer.WriteStartObject();
                    writer.WriteString("id", Convert.ToString(value, CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                    break;
                case MockFieldType.Select:
                    {
                        var id = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                        writer.WriteStartObject();
                        writer.WriteString("id", id);
                        var label = field.Values.FirstOrDefault(e => e.Key == id).Value;
                        if (label != null) writer.WriteString("refName", label);
                        writer.WriteEndObject();
                        break;
                    }
                default:
                    writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }

        /// <summary>SuiteQL の結果の値の文字。数値は 15 桁に丸めて余計な 0 を付けない。</summary>
        internal static string ToResultText(object value)
        {
            switch (value)
            {
                case string s: return s;
                case long l: return l.ToString(CultureInfo.InvariantCulture);
                case double d:
                    try
                    {
                        return ((decimal)d).ToString(CultureInfo.InvariantCulture);
                    }
                    catch (OverflowException)
                    {
                        return d.ToString("R", CultureInfo.InvariantCulture);
                    }
                case byte[] bytes: return Convert.ToBase64String(bytes);
                default: return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        // ---------------------------------------------------------------- mock-data.json

        internal static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

        /// <summary>定義を読む。初期の行は (レコード名, 行) の並びで返す。</summary>
        internal static List<MockRecordType> ReadDefinitions(JsonElement root, List<(string Record, JsonElement Row)> rows)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("mock data: the root must be an object with \"records\": { \"<record name>\": { ... } }.");

            var list = new List<MockRecordType>();
            foreach (var record in records.EnumerateObject())
            {
                if (!MockRecordType.IsIdentifier(record.Name))
                    throw new InvalidDataException($"mock data: '{record.Name}' cannot be used as a record name (letters, digits and _ only).");
                if (list.Any(e => string.Equals(e.Name, record.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"mock data: record '{record.Name}' is defined twice.");
                if (record.Value.ValueKind != JsonValueKind.Object || !record.Value.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"mock data: record '{record.Name}' needs \"fields\": {{ ... }}.");

                var transactionType = string.Empty;
                if (record.Value.TryGetProperty("transactionType", out var transaction))
                {
                    if (transaction.ValueKind != JsonValueKind.String || !MockRecordType.IsIdentifier(transaction.GetString()!))
                        throw new InvalidDataException($"mock data: \"transactionType\" of record '{record.Name}' must be a name (letters, digits and _ only), e.g. \"SalesOrd\".");
                    transactionType = transaction.GetString()!;
                    if (list.Any(e => e.TransactionType == transactionType))
                        throw new InvalidDataException($"mock data: transactionType '{transactionType}' is used twice.");
                }

                var type = new MockRecordType { Name = record.Name, TransactionType = transactionType };
                ReadFields($"mock data: field '{record.Name}", fields, type.Fields, transactionType.Length != 0 ? "type" : null);

                if (record.Value.TryGetProperty("sublists", out var sublists))
                {
                    if (sublists.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"mock data: \"sublists\" of record '{record.Name}' must be an object.");
                    foreach (var sublist in sublists.EnumerateObject())
                    {
                        var where = $"mock data: sublist '{record.Name}.{sublist.Name}'";
                        if (!MockRecordType.IsIdentifier(sublist.Name)) throw new InvalidDataException($"{where}: letters, digits and _ only.");
                        if (type.Fields.Any(e => e.Column == sublist.Name.ToLowerInvariant()) || type.Sublists.Any(e => e.Name.ToLowerInvariant() == sublist.Name.ToLowerInvariant()) ||
                            string.Equals(sublist.Name, "id", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"{where}: the name is used twice (names are compared in lower case).");
                        if (sublist.Value.ValueKind != JsonValueKind.Object || !sublist.Value.TryGetProperty("fields", out var sublistFields) || sublistFields.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException($"{where}: needs \"fields\": {{ ... }}.");
                        var required = sublist.Value.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
                        var definition = new MockSublist { Name = sublist.Name, Required = required };
                        ReadFields($"mock data: field '{record.Name}.{sublist.Name}", sublistFields, definition.Fields, "line");
                        type.Sublists.Add(definition);
                    }
                }
                list.Add(type);

                if (record.Value.TryGetProperty("rows", out var rowArray))
                {
                    if (rowArray.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"mock data: \"rows\" of record '{record.Name}' must be an array.");
                    foreach (var row in rowArray.EnumerateArray()) rows.Add((record.Name, row));
                }
            }

            foreach (var type in list)
            {
                foreach (var (owner, field) in type.Fields.Select(e => (type.Name, e)).Concat(type.Sublists.SelectMany(s => s.Fields.Select(e => ($"{type.Name}.{s.Name}", e)))))
                {
                    if (field.Type != MockFieldType.Reference) continue;
                    if (!list.Any(e => string.Equals(e.Name, field.Record, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"mock data: field '{owner}.{field.Name}' refers to record '{field.Record}', which is not defined.");
                }
            }
            if (list.Any(e => e.IsTransaction) && list.Any(e => string.Equals(e.Name, "transaction", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("mock data: 'transaction' cannot be used as a record name when there are transaction records (it is the SuiteQL table of them).");
            return list;
        }

        //項目の定義 ("fields")。reserved は使えない名前 (取引の type・明細の line)
        static void ReadFields(string prefix, JsonElement fields, List<MockField> list, string? reserved)
        {
            foreach (var field in fields.EnumerateObject())
            {
                var where = $"{prefix}.{field.Name}'";
                if (!MockRecordType.IsIdentifier(field.Name)) throw new InvalidDataException($"{where}: letters, digits and _ only.");
                if (string.Equals(field.Name, "id", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{where}: 'id' is added automatically.");
                if (reserved != null && string.Equals(field.Name, reserved, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{where}: '{reserved}' is reserved.");
                if (list.Any(e => e.Column == field.Name.ToLowerInvariant())) throw new InvalidDataException($"{where}: the name is used twice (names are compared in lower case).");

                string typeName;
                bool required = false, readOnly = false;
                string target = string.Empty, title = string.Empty;
                var values = new List<KeyValuePair<string, string>>();
                if (field.Value.ValueKind == JsonValueKind.String)
                {
                    typeName = field.Value.GetString()!;
                }
                else if (field.Value.ValueKind == JsonValueKind.Object && field.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    typeName = t.GetString()!;
                    if (field.Value.TryGetProperty("required", out var r)) required = r.ValueKind == JsonValueKind.True;
                    if (field.Value.TryGetProperty("record", out var rec) && rec.ValueKind == JsonValueKind.String) target = rec.GetString()!;
                    if (field.Value.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String) title = ti.GetString()!;
                    if (field.Value.TryGetProperty("readOnly", out var ro)) readOnly = ro.ValueKind == JsonValueKind.True;
                    if (field.Value.TryGetProperty("values", out var vs))
                    {
                        if (vs.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where}: \"values\" must be an object {{ \"<value>\": \"<display name>\" }}.");
                        foreach (var v in vs.EnumerateObject())
                        {
                            if (v.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{where}: the display name of value '{v.Name}' must be a string.");
                            values.Add(new(v.Name, v.Value.GetString()!));
                        }
                    }
                }
                else
                {
                    throw new InvalidDataException($"{where}: write {{ \"type\": \"string\" }} (or just \"string\").");
                }
                MockFieldType kind = typeName switch
                {
                    "string" => MockFieldType.String,
                    "boolean" => MockFieldType.Boolean,
                    "integer" => MockFieldType.Integer,
                    "number" => MockFieldType.Number,
                    "date" => MockFieldType.Date,
                    "dateTime" => MockFieldType.DateTime,
                    "reference" => MockFieldType.Reference,
                    "select" => MockFieldType.Select,
                    _ => throw new InvalidDataException($"{where}: unknown type '{typeName}' (string / boolean / integer / number / date / dateTime / reference / select)."),
                };
                if (kind == MockFieldType.Select && values.Count == 0) throw new InvalidDataException($"{where}: a select needs \"values\": {{ \"<value>\": \"<display name>\" }}.");
                if (kind == MockFieldType.Reference && string.IsNullOrEmpty(target)) throw new InvalidDataException($"{where}: a reference needs \"record\": \"<record name>\".");
                list.Add(new MockField { Name = field.Name, Type = kind, Record = target, Required = required, Title = title, ReadOnly = readOnly, Values = values });
            }
        }
    }
}
