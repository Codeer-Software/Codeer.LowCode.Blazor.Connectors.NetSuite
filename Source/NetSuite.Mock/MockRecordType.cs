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
    }

    /// <summary>レコード種別 1 つの定義。</summary>
    public sealed class MockRecordType
    {
        public string Name { get; init; } = string.Empty;
        public List<MockField> Fields { get; } = new();

        /// <summary>REST の項目名で探す (大文字・小文字を区別する)。</summary>
        public MockField? FindField(string name) => Fields.FirstOrDefault(e => e.Name == name);

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
            }
            error = field.Type switch
            {
                MockFieldType.String => "a string",
                MockFieldType.Boolean => "true or false",
                MockFieldType.Integer => "an integer",
                MockFieldType.Number => "a number",
                MockFieldType.Date => "a date string (YYYY-MM-DD)",
                MockFieldType.DateTime => "a date-time string (e.g. 2026-03-04T05:06:07Z)",
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

                var type = new MockRecordType { Name = record.Name };
                foreach (var field in fields.EnumerateObject())
                {
                    var where = $"mock data: field '{record.Name}.{field.Name}'";
                    if (!MockRecordType.IsIdentifier(field.Name)) throw new InvalidDataException($"{where}: letters, digits and _ only.");
                    if (string.Equals(field.Name, "id", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{where}: 'id' is added automatically.");
                    if (type.Fields.Any(e => e.Column == field.Name.ToLowerInvariant())) throw new InvalidDataException($"{where}: the name is used twice (names are compared in lower case).");

                    string typeName;
                    bool required = false;
                    string target = string.Empty, title = string.Empty;
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
                        _ => throw new InvalidDataException($"{where}: unknown type '{typeName}' (string / boolean / integer / number / date / dateTime / reference)."),
                    };
                    if (kind == MockFieldType.Reference && string.IsNullOrEmpty(target)) throw new InvalidDataException($"{where}: a reference needs \"record\": \"<record name>\".");
                    type.Fields.Add(new MockField { Name = field.Name, Type = kind, Record = target, Required = required, Title = title });
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
                foreach (var field in type.Fields.Where(e => e.Type == MockFieldType.Reference))
                {
                    if (!list.Any(e => string.Equals(e.Name, field.Record, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"mock data: field '{type.Name}.{field.Name}' refers to record '{field.Record}', which is not defined.");
                }
            }
            return list;
        }
    }
}
