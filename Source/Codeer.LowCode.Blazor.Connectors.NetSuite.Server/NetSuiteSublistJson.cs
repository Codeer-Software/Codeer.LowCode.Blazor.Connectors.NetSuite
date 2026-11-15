using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    /// <summary>
    /// 明細の列の値 (行の JSON 配列の文字列 = 本体の列モードの一覧) と、REST のサブリスト ({"items":[...]}) の読み替え。
    /// 列の行のキーは REST の項目名そのまま。参照は id の文字列 + 表示名を「キー_refName」、行キー line はそのまま入れる。
    /// </summary>
    static class NetSuiteSublistJson
    {
        const string LineKey = "line";
        const string RefNameSuffix = "_refName";

        // ---------------------------------------------------------------- 読み出し

        /// <summary>レコード (GET ?expandSubResources=true の応答) の明細を、列の値にする。</summary>
        internal static string ToColumnValue(JsonElement record, NetSuiteFieldDefinition sublist)
        {
            var array = new JsonArray();
            foreach (var row in GetRestRows(record, sublist)) array.Add(ToColumnRow(row, sublist));
            return array.ToJsonString();
        }

        static List<JsonElement> GetRestRows(JsonElement record, NetSuiteFieldDefinition sublist)
        {
            var rows = new List<JsonElement>();
            if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty(sublist.RestName, out var value)) return rows;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                rows.AddRange(items.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object));
            return rows;
        }

        static JsonObject ToColumnRow(JsonElement row, NetSuiteFieldDefinition sublist)
        {
            var result = new JsonObject();
            foreach (var property in row.EnumerateObject())
            {
                if (property.Name == "links") continue;
                var field = sublist.FindSublistField(property.Name);
                var value = property.Value;
                if (value.ValueKind == JsonValueKind.Object && (field?.Kind == NetSuiteFieldKind.Reference || (field == null && IsReference(value))))
                {
                    //参照は id の文字列と、表示名 (読み取り専用の表示用)
                    result[property.Name] = value.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null ? GetText(id) : null;
                    if (value.TryGetProperty("refName", out var refName) && refName.ValueKind != JsonValueKind.Null) result[property.Name + RefNameSuffix] = GetText(refName);
                    continue;
                }
                //行の中の入れ子 (サブレコード・サブリスト) は入れない
                if (value.ValueKind == JsonValueKind.Object || value.ValueKind == JsonValueKind.Array) continue;
                result[property.Name] = JsonNode.Parse(value.GetRawText());
            }
            return result;
        }

        static bool IsReference(JsonElement value) => value.TryGetProperty("id", out _) && !value.TryGetProperty("items", out _);

        static string GetText(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

        // ---------------------------------------------------------------- 書き込み

        /// <summary>列の値 (JSON 配列の文字列) を行にする。null・空は行なし。</summary>
        internal static List<JsonObject> ParseColumnValue(NetSuiteFieldDefinition sublist, object? value)
        {
            var rows = new List<JsonObject>();
            if (value == null || value is DBNull) return rows;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text)) return rows;
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException e)
            {
                throw new NetSuiteException($"The value of sublist '{sublist.RestName}' is not a JSON array.", NetSuiteErrorKind.InvalidRequest, e);
            }
            if (node == null) return rows;
            if (node is not JsonArray array) throw new NetSuiteException($"The value of sublist '{sublist.RestName}' is not a JSON array.", NetSuiteErrorKind.InvalidRequest);
            foreach (var element in array)
            {
                if (element is not JsonObject row) throw new NetSuiteException($"A line of sublist '{sublist.RestName}' is not a JSON object.", NetSuiteErrorKind.InvalidRequest);
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>作成の明細 ({"items":[...]})。行は定義にある項目だけ (line・null は送らない)。</summary>
        internal static JsonObject CreateInsertItems(NetSuiteFieldDefinition sublist, List<JsonObject> rows)
            => new() { ["items"] = new JsonArray(rows.Select(e => (JsonNode?)CreateNewRow(sublist, e)).ToArray()) };

        static JsonObject CreateNewRow(NetSuiteFieldDefinition sublist, JsonObject row)
        {
            var result = new JsonObject();
            foreach (var field in sublist.SublistFields)
            {
                if (field.RestName == LineKey) continue;
                var value = ToRestValue(sublist, field, row[field.RestName]);
                if (value != null) result[field.RestName] = value;
            }
            return result;
        }

        /// <summary>
        /// 更新の明細。今の明細 (current) と line で突き合わせ、変わった行は line + 変わった項目、line の無い行は新しい行にする。
        /// 消えた行があれば replace (残す行を全部並べ、変わっていない行は line だけ)。差が無ければ Items は null。
        /// </summary>
        internal static (JsonObject? Items, bool Replace) CreateUpdateItems(NetSuiteFieldDefinition sublist, List<JsonObject> rows, JsonElement current)
        {
            var currentRows = new Dictionary<string, JsonElement>();
            foreach (var row in GetRestRows(current, sublist))
            {
                if (row.TryGetProperty(LineKey, out var line) && ToLineKey(line) is { } key) currentRows.TryAdd(key, row);
            }

            var kept = new HashSet<string>();
            var elements = new List<(JsonObject Element, bool IsChanged)>();
            foreach (var row in rows)
            {
                var key = ToLineKey(row[LineKey]);
                if (key != null && currentRows.TryGetValue(key, out var currentRow) && kept.Add(key))
                {
                    var element = new JsonObject { [LineKey] = long.Parse(key, CultureInfo.InvariantCulture) };
                    foreach (var field in sublist.SublistFields)
                    {
                        if (field.RestName == LineKey) continue;
                        var value = row[field.RestName];
                        currentRow.TryGetProperty(field.RestName, out var currentValue);
                        if (IsSame(field, value, currentValue)) continue;
                        //null にした項目は null を送る
                        element[field.RestName] = ToRestValue(sublist, field, value);
                    }
                    elements.Add((element, 1 < element.Count));
                    continue;
                }
                //line の無い行・今の明細に無い line (ほかで消された行)・同じ line の 2 つ目は新しい行 (同時編集は後勝ち)
                elements.Add((CreateNewRow(sublist, row), true));
            }

            var replace = currentRows.Keys.Any(e => !kept.Contains(e));
            var items = replace ? elements.Select(e => e.Element).ToList() : elements.Where(e => e.IsChanged).Select(e => e.Element).ToList();
            if (items.Count == 0 && !replace) return (null, false);
            return (new JsonObject { ["items"] = new JsonArray(items.Select(e => (JsonNode?)e).ToArray()) }, replace);
        }

        //行キーは整数の文字にそろえる (1 / "1" / 1.0 を同じ行として扱う)
        static string? ToLineKey(JsonNode? node)
            => node is JsonValue && TryNormalize(NetSuiteFieldKind.Integer, node, out var value) && value is decimal d && long.MinValue <= d && d <= long.MaxValue
                ? decimal.ToInt64(d).ToString(CultureInfo.InvariantCulture) : null;

        static string? ToLineKey(JsonElement element)
            => element.ValueKind == JsonValueKind.Undefined ? null : ToLineKey(JsonNode.Parse(element.GetRawText()));

        //種類ごとに正規化して比べる (参照は id・数値は decimal・日時は瞬間・日付は日)。null と項目が無いのは同じ
        static bool IsSame(NetSuiteFieldDefinition field, JsonNode? value, JsonElement current)
        {
            var currentNode = current.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : JsonNode.Parse(current.GetRawText());
            if (TryNormalize(field.Kind, value, out var a) && TryNormalize(field.Kind, currentNode, out var b)) return Equals(a, b);
            return (value?.ToJsonString() ?? "null") == (currentNode?.ToJsonString() ?? "null");
        }

        /// <summary>列の値を、項目の種類の REST の値にする (参照は {"id":"..."}、時差の無い日時は UTC)。</summary>
        static JsonNode? ToRestValue(NetSuiteFieldDefinition sublist, NetSuiteFieldDefinition field, JsonNode? value)
        {
            if (!TryNormalize(field.Kind, value, out var normalized))
                throw new NetSuiteException($"The value of '{field.RestName}' in a line of sublist '{sublist.RestName}' is not valid.", NetSuiteErrorKind.InvalidRequest);
            return normalized switch
            {
                null => null,
                decimal d when field.Kind == NetSuiteFieldKind.Integer => JsonValue.Create(decimal.ToInt64(d)),
                decimal d => JsonValue.Create(d),
                bool b => JsonValue.Create(b),
                DateOnly date => JsonValue.Create(date.ToString(SuiteQlTranslator.DateFormatNet, CultureInfo.InvariantCulture)),
                DateTime utc => JsonValue.Create(utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                string id when field.Kind == NetSuiteFieldKind.Reference => new JsonObject { ["id"] = id },
                _ => JsonValue.Create(Convert.ToString(normalized, CultureInfo.InvariantCulture)),
            };
        }

        //null・空の参照・空文字は null。型に合わなければ false
        static bool TryNormalize(NetSuiteFieldKind kind, JsonNode? node, out object? value)
        {
            value = null;
            if (node == null) return true;
            if (kind == NetSuiteFieldKind.Reference)
            {
                if (node is JsonObject reference) node = reference["id"];
                if (node == null) return true;
                if (node is not JsonValue) return false;
                var id = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
                value = string.IsNullOrEmpty(id) ? null : id;
                return true;
            }
            if (node is not JsonValue) return false;
            var valueKind = node.GetValueKind();
            var text = valueKind == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
            switch (kind)
            {
                case NetSuiteFieldKind.Integer:
                case NetSuiteFieldKind.Number:
                    if (valueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
                        !decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return false;
                    if (kind == NetSuiteFieldKind.Integer && number != decimal.Truncate(number)) return false;
                    value = number;
                    return true;
                case NetSuiteFieldKind.Boolean:
                    if (valueKind == JsonValueKind.True || valueKind == JsonValueKind.False) value = valueKind == JsonValueKind.True;
                    else if (text is "T" or "F") value = text == "T";
                    else return false;
                    return true;
                case NetSuiteFieldKind.Date:
                    if (valueKind != JsonValueKind.String || text.Length < 10 ||
                        !DateOnly.TryParseExact(text[..10], SuiteQlTranslator.DateFormatNet, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
                    value = date;
                    return true;
                case NetSuiteFieldKind.DateTime:
                    //時差の無い日時は UTC (DateTimeField の SaveAsUtc)
                    if (valueKind != JsonValueKind.String ||
                        !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dateTime)) return false;
                    value = dateTime.UtcDateTime;
                    return true;
                default:
                    if (valueKind is JsonValueKind.Object or JsonValueKind.Array) return false;
                    //REST は空の項目を省くので、空文字は値なしと同じに扱う
                    value = string.IsNullOrEmpty(text) ? null : text;
                    return true;
            }
        }
    }
}
