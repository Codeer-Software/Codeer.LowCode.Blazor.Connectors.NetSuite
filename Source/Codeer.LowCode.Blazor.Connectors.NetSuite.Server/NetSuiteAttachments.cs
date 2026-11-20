using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    /// <summary>レコードに付いているファイル 1 つ (保存検索の結果の 1 行)。</summary>
    internal sealed record NetSuiteAttachedFile(string Id, string Name, long? Size, string? Type);

    /// <summary>
    /// 添付ファイルの仮想の列 (<see cref="NetSuiteFieldKind.Attachments"/>) の値の読み解き。値はファイルの JSON 配列の文字。
    /// 読むと [{"id","name","size","type"}] (size・type は保存検索に列があるときだけ)。書くときに意味を持つのは、base64 を持つ要素
    /// (足す: name・contentType・base64) と deleted が true の要素 (外す: id) だけ。それ以外の要素・ほかのキーは見ない
    /// (クライアントの id・大きさを信じない。外すのは保存の直前に保存検索で読んだ、そのレコードのファイルに一致するものだけ)。
    /// </summary>
    static class NetSuiteAttachments
    {
        /// <summary>送るときに contentType が無いファイルの種類。</summary>
        internal const string DefaultContentType = "application/octet-stream";

        /// <summary>NetSuite の文書サービスの、1 回の要求で送れる大きさの上限 (10 MB)。</summary>
        internal const long MaxFileSize = 10L * 1024 * 1024;

        /// <summary>足すファイル 1 つ。</summary>
        internal sealed record Addition(string Name, string ContentType, byte[] Content);

        /// <summary>1 列分の変更 (足すファイルと外すファイルの internal ID)。</summary>
        internal sealed class Change
        {
            public List<Addition> Additions { get; } = new();
            public List<string> Deletions { get; } = new();
            public bool IsEmpty => Additions.Count == 0 && Deletions.Count == 0;
        }

        /// <summary>ファイルの internal ID の形 (数字だけ・長すぎない)。URL・比較に使う前に確かめる。</summary>
        internal static bool IsFileId(string? text) => !string.IsNullOrEmpty(text) && text.Length <= 20 && text.All(char.IsAsciiDigit);

        /// <summary>
        /// 書き込みの値 → 変更。null・空の文字は変更なし。形が不正 (JSON の配列でない・要素が JSON のオブジェクトでない・足すものに name か base64 が無い / 読めない / name に制御文字がある /
        /// 中身が空・外すものに id が無い・base64 と deleted の両方を持つ) なら NetSuiteException (InvalidRequest)。
        /// </summary>
        internal static Change ParseChange(string table, string column, object? value)
        {
            var change = new Change();
            if (value is DBNull) value = null;
            if (value == null) return change;
            if (value is not string text) throw Invalid(table, column, "the value is not text");
            if (string.IsNullOrWhiteSpace(text)) return change;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException e)
            {
                throw new NetSuiteException(Message(table, column, "the value is not JSON"), NetSuiteErrorKind.InvalidRequest, e);
            }
            if (root is not JsonArray array) throw Invalid(table, column, "the value is not a JSON array");

            foreach (var node in array)
            {
                if (node is not JsonObject item) throw Invalid(table, column, "an element is not a JSON object");
                var deleted = item["deleted"] is JsonValue d && d.TryGetValue<bool>(out var flag) && flag;
                var hasBase64 = item["base64"] != null;
                if (deleted && hasBase64) throw Invalid(table, column, "an element has both base64 and deleted");
                if (deleted)
                {
                    var id = GetText(item["id"]);
                    if (!IsFileId(id)) throw Invalid(table, column, "an element to detach has no valid id");
                    if (!change.Deletions.Contains(id!, StringComparer.Ordinal)) change.Deletions.Add(id!);
                    continue;
                }
                if (!hasBase64) continue;

                var name = GetString(item, "name");
                var base64 = GetString(item, "base64");
                if (string.IsNullOrWhiteSpace(name)) throw Invalid(table, column, "an element to add has no name");
                if (name.Any(char.IsControl)) throw Invalid(table, column, "the name of an element to add contains a control character");
                if (base64 == null) throw Invalid(table, column, $"the base64 of '{name}' is not text");
                var content = FromBase64(base64) ?? throw Invalid(table, column, $"the base64 of '{name}' cannot be read");
                if (content.Length == 0) throw Invalid(table, column, $"'{name}' is empty (0 bytes)");
                var contentType = GetString(item, "contentType");
                change.Additions.Add(new Addition(name, string.IsNullOrWhiteSpace(contentType) ? DefaultContentType : contentType, content));
            }
            return change;
        }

        /// <summary>読んだファイルの並び → 列の値 ([{"id","name","size","type"}])。ファイルが無ければ null。</summary>
        internal static string? ToColumnValue(IReadOnlyList<NetSuiteAttachedFile>? files)
        {
            if (files == null || files.Count == 0) return null;
            var array = new JsonArray();
            foreach (var file in files)
            {
                var item = new JsonObject { ["id"] = file.Id, ["name"] = file.Name };
                if (file.Size != null) item["size"] = file.Size.Value;
                if (!string.IsNullOrEmpty(file.Type)) item["type"] = file.Type;
                array.Add(item);
            }
            return array.ToJsonString();
        }

        /// <summary>読んだ値 (列の値) にあるファイル (id と名前)。id の無い要素・deleted が true の要素は入れない。読めなければ空。</summary>
        internal static List<(string Id, string? Name)> ReadFiles(string? stored)
        {
            var list = new List<(string, string?)>();
            if (string.IsNullOrWhiteSpace(stored)) return list;
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(stored);
            }
            catch (JsonException)
            {
                return list;
            }
            if (root is not JsonArray array) return list;
            foreach (var item in array.OfType<JsonObject>())
            {
                if (item["deleted"] is JsonValue d && d.TryGetValue<bool>(out var flag) && flag) continue;
                var id = GetText(item["id"]);
                if (IsFileId(id)) list.Add((id!, GetString(item, "name")));
            }
            return list;
        }

        /// <summary>保存検索の結果の値を文字にする。文字・数値はそのまま、参照 ({id, refName}) は id か refName。それ以外・空は null。</summary>
        internal static string? ReadSearchValue(JsonElement value, bool preferName)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return string.IsNullOrEmpty(value.GetString()) ? null : value.GetString();
                case JsonValueKind.Number:
                    return value.GetRawText();
                case JsonValueKind.Object:
                    if (preferName && value.TryGetProperty("refName", out var refName) && refName.ValueKind == JsonValueKind.String) return refName.GetString();
                    if (value.TryGetProperty("id", out var id)) return ReadSearchValue(id, false);
                    if (value.TryGetProperty("refName", out refName) && refName.ValueKind == JsonValueKind.String) return refName.GetString();
                    return null;
                default:
                    return null;
            }
        }

        /// <summary>大きさの値 (数値か数字の文字)。読めなければ null。</summary>
        internal static long? ParseSize(string? text)
            => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && 0 <= size && size <= long.MaxValue ? (long)size : null;

        static byte[]? FromBase64(string text)
        {
            if (text.Length % 4 != 0) return null;
            var buffer = new byte[text.Length / 4 * 3];
            return Convert.TryFromBase64String(text, buffer, out var written) ? buffer[..written] : null;
        }

        static string? GetString(JsonObject item, string name)
            => item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        //id は文字でも数値でも読む
        static string? GetText(JsonNode? node)
        {
            if (node is not JsonValue value) return null;
            if (value.TryGetValue<string>(out var text)) return text;
            if (value.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        static string Message(string table, string column, string reason)
            => $"The value of the attachments column '{column}' of NetSuite record type '{table}' is invalid: {reason}. " +
               "Write a JSON array of files to add ({\"name\",\"contentType\",\"base64\"}) and files to detach ({\"id\",\"deleted\":true}).";

        static NetSuiteException Invalid(string table, string column, string reason)
            => new(Message(table, column, reason), NetSuiteErrorKind.InvalidRequest);
    }
}
