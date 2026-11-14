using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    enum NetSuiteFieldKind
    {
        String,
        Boolean,
        Integer,
        Number,
        Date,
        DateTime,
        /// <summary>ほかのレコード・選択肢への参照。値は相手の internal ID。</summary>
        Reference,
    }

    class NetSuiteFieldDefinition
    {
        /// <summary>REST のレコードでの項目名 (例: companyName)。書き込みに使う。</summary>
        public string RestName { get; init; } = string.Empty;

        /// <summary>列名 (REST の項目名を小文字にしたもの)。モジュールの DbColumn・SuiteQL に使う。</summary>
        public string Column { get; init; } = string.Empty;

        public NetSuiteFieldKind Kind { get; init; }
        public bool IsNullable { get; init; } = true;
    }

    /// <summary>
    /// レコード種別 1 つの定義。REST のメタデータ (JSON Schema) の、値を 1 つ持つ項目だけを列にする (明細・サブレコードは列にしない)。
    /// </summary>
    class NetSuiteRecordDefinition
    {
        public string Name { get; init; } = string.Empty;
        public List<NetSuiteFieldDefinition> Fields { get; } = new();

        public NetSuiteFieldDefinition? FindByColumn(string column)
            => Fields.FirstOrDefault(e => string.Equals(e.Column, column, StringComparison.OrdinalIgnoreCase));

        public DbTableDefinition ToTableDefinition()
        {
            var table = new DbTableDefinition { Name = Name };
            foreach (var field in Fields)
            {
                var (netType, rawType) = field.Kind switch
                {
                    NetSuiteFieldKind.Boolean => (typeof(bool), "BOOLEAN"),
                    NetSuiteFieldKind.Integer => (typeof(long), "INTEGER"),
                    NetSuiteFieldKind.Number => (typeof(decimal), "NUMBER"),
                    NetSuiteFieldKind.Date => (typeof(DateOnly), "DATE"),
                    //UTC の瞬間を持つ列型の名前にしておく (デザイナが DateTimeField の SaveAsUtc を既定でオンにする)
                    NetSuiteFieldKind.DateTime => (typeof(DateTime), "TIMESTAMP WITH LOCAL TIME ZONE"),
                    NetSuiteFieldKind.Reference => (typeof(string), "REFERENCE"),
                    _ => (typeof(string), "VARCHAR2"),
                };
                table.Columns.Add(new DbColumnDefinition
                {
                    Name = field.Column,
                    NetTypeFullName = netType.FullName!,
                    RawDbTypeName = rawType,
                    //必須の項目も null を許す列として見せる。NOT NULL にすると、本体が空の値を型の既定値 (0・0001-01-01・false) に置き換えて書いてしまう。
                    //必須かどうかは NetSuite が保存のときに確かめる
                    IsNullable = true,
                });
            }
            return table;
        }

        //REST の応答に必ず付く、レコードの項目ではないもの
        static readonly HashSet<string> _ignoredProperties = new(StringComparer.OrdinalIgnoreCase) { "links", "refName" };

        internal static NetSuiteRecordDefinition FromJsonSchema(string recordType, JsonElement schema)
        {
            var definition = new NetSuiteRecordDefinition { Name = recordType };
            if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                throw new NetSuiteException($"NetSuite: the metadata of record type '{recordType}' has no properties.", NetSuiteErrorKind.InvalidRequest);

            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (schema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in requiredElement.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) required.Add(e.GetString()!);
            }

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties.EnumerateObject())
            {
                if (_ignoredProperties.Contains(property.Name)) continue;
                var kind = GetKind(property.Name, property.Value);
                if (kind == null) continue;
                var column = property.Name.ToLowerInvariant();
                if (!SuiteQlTranslator.IsIdentifier(column) || !columns.Add(column)) continue;
                definition.Fields.Add(new NetSuiteFieldDefinition
                {
                    RestName = property.Name,
                    Column = column,
                    //internal ID は NetSuite が採番する文字列
                    Kind = column == "id" ? NetSuiteFieldKind.String : kind.Value,
                    IsNullable = column == "id" || !required.Contains(property.Name),
                });
            }
            definition.Fields.Sort((a, b) => a.Column == "id" ? -1 : b.Column == "id" ? 1 : string.CompareOrdinal(a.Column, b.Column));
            return definition;
        }

        //値を 1 つ持つ項目の種類。明細 (サブリスト)・サブレコード・知らない形は null (列にしない)
        static NetSuiteFieldKind? GetKind(string name, JsonElement property)
        {
            if (property.ValueKind != JsonValueKind.Object) return null;

            var type = property.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
            var format = property.TryGetProperty("format", out var formatElement) && formatElement.ValueKind == JsonValueKind.String ? formatElement.GetString() : null;

            switch (type)
            {
                case "string":
                    return format switch
                    {
                        "date" => NetSuiteFieldKind.Date,
                        "date-time" => NetSuiteFieldKind.DateTime,
                        _ => NetSuiteFieldKind.String,
                    };
                case "boolean": return NetSuiteFieldKind.Boolean;
                case "integer": return NetSuiteFieldKind.Integer;
                case "number": return NetSuiteFieldKind.Number;
                case "array": return null;
            }

            //ほかの定義への参照。明細・サブレコードの定義は「<レコード名>-<項目名>...」の名前になっている
            if (property.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
            {
                var target = (reference.GetString() ?? string.Empty).Split('/').LastOrDefault() ?? string.Empty;
                return target.Contains('-') ? null : NetSuiteFieldKind.Reference;
            }

            //その場に書かれたオブジェクト。id を持ち items を持たないものが参照 (選択肢を含む)
            if (property.TryGetProperty("properties", out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                if (inner.TryGetProperty("items", out _)) return null;
                if (inner.TryGetProperty("id", out _)) return NetSuiteFieldKind.Reference;
            }
            return null;
        }
    }
}
