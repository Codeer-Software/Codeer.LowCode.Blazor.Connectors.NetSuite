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
        /// <summary>明細 (行キー line を持つサブリスト)。列の値は行の JSON 配列の文字列 (本体の列モードの一覧)。</summary>
        Sublist,
    }

    class NetSuiteFieldDefinition
    {
        /// <summary>REST のレコードでの項目名 (例: companyName)。書き込みに使う。</summary>
        public string RestName { get; init; } = string.Empty;

        /// <summary>列名 (REST の項目名を小文字にしたもの)。モジュールの DbColumn・SuiteQL に使う。</summary>
        public string Column { get; init; } = string.Empty;

        public NetSuiteFieldKind Kind { get; init; }
        public bool IsNullable { get; init; } = true;

        /// <summary>明細 (<see cref="NetSuiteFieldKind.Sublist"/>) の行の項目。line を含む。</summary>
        public List<NetSuiteFieldDefinition> SublistFields { get; init; } = new();

        /// <summary>明細の行の項目を REST の名前で探す (大文字・小文字を区別する)。</summary>
        public NetSuiteFieldDefinition? FindSublistField(string restName) => SublistFields.FirstOrDefault(e => e.RestName == restName);
    }

    /// <summary>
    /// レコード種別 1 つの定義。REST のメタデータ (JSON Schema) の、値を 1 つ持つ項目と、行キー line を持つ明細を列にする
    /// (サブレコード・行キーの無いサブリストは列にしない)。
    /// </summary>
    class NetSuiteRecordDefinition
    {
        public string Name { get; init; } = string.Empty;
        public List<NetSuiteFieldDefinition> Fields { get; } = new();

        /// <summary>明細の表 (取引の明細の行を 1 行 = 1 レコードで読む、読み取り専用の表) なら親のレコード名。レコードなら null。</summary>
        public string? LineParent { get; init; }

        /// <summary>明細の表なら、親のレコードでの明細の REST 名。</summary>
        public string? LineSublist { get; init; }

        public bool IsLineTable => LineParent != null;

        /// <summary>行を一意に指す列 (レコードは id、明細の表は uniquekey)。ページングの並びの最後に足す。</summary>
        public string KeyColumn => IsLineTable ? LineKeyColumn : "id";

        internal const string LineKeyColumn = "uniquekey";

        public NetSuiteFieldDefinition? FindByColumn(string column)
            => Fields.FirstOrDefault(e => string.Equals(e.Column, column, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 明細の表の定義 (取引のレコードの、明細の列ごとに 1 つ)。取引でなければ空。
        /// 表名は &lt;レコード名&gt;_&lt;明細の列名&gt; (小文字)。列は uniquekey・transaction (親への参照)・linesequencenumber と、行の項目 (line を除く)。
        /// </summary>
        internal List<NetSuiteRecordDefinition> CreateLineTables()
        {
            if (IsLineTable || SuiteQlTranslator.GetTransactionType(Name) == null) return new();
            return Fields.Where(e => e.Kind == NetSuiteFieldKind.Sublist).Select(CreateLineTable).ToList();
        }

        NetSuiteRecordDefinition CreateLineTable(NetSuiteFieldDefinition sublist)
        {
            var table = new NetSuiteRecordDefinition { Name = (Name + "_" + sublist.Column).ToLowerInvariant(), LineParent = Name, LineSublist = sublist.RestName };
            table.Fields.Add(new NetSuiteFieldDefinition { RestName = LineKeyColumn, Column = LineKeyColumn, Kind = NetSuiteFieldKind.String, IsNullable = false });
            table.Fields.Add(new NetSuiteFieldDefinition { RestName = "transaction", Column = "transaction", Kind = NetSuiteFieldKind.Reference, IsNullable = false });
            table.Fields.Add(new NetSuiteFieldDefinition { RestName = "linesequencenumber", Column = "linesequencenumber", Kind = NetSuiteFieldKind.Integer });
            foreach (var field in sublist.SublistFields)
            {
                //行キー line は REST の明細の番号なので列にしない。名前が重なる項目は先の列を残す
                //SuiteQL の名前に使えない項目は列にしない
                if (field.RestName == "line" || !SuiteQlTranslator.IsIdentifier(field.Column) || table.FindByColumn(field.Column) != null) continue;
                table.Fields.Add(new NetSuiteFieldDefinition { RestName = field.RestName, Column = field.Column, Kind = field.Kind, IsNullable = field.IsNullable });
            }
            return table;
        }

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
                    //明細は行の JSON 配列の文字列 (列モードの一覧で読み書きする)
                    NetSuiteFieldKind.Sublist => (typeof(string), "CLOB"),
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

        //$ref をたどる回数の上限 (明細は 集まり → 行 の 2 回)
        const int MaxReferenceDepth = 4;

        internal static NetSuiteRecordDefinition FromJsonSchema(string recordType, JsonElement schema)
            => FromJsonSchemaAsync(recordType, schema, null).GetAwaiter().GetResult();

        /// <param name="getSchema">
        /// metadata-catalog の名前 (例: salesorder-itemCollection) の JSON Schema を返す。読めなければ null。明細の $ref の先を読むのに使う。null なら $ref をたどらない。
        /// </param>
        internal static async Task<NetSuiteRecordDefinition> FromJsonSchemaAsync(string recordType, JsonElement schema, Func<string, Task<JsonElement?>>? getSchema)
        {
            var definition = new NetSuiteRecordDefinition { Name = recordType };
            if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                throw new NetSuiteException($"NetSuite: the metadata of record type '{recordType}' has no properties.", NetSuiteErrorKind.InvalidRequest);

            var required = GetRequired(schema);
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties.EnumerateObject())
            {
                if (_ignoredProperties.Contains(property.Name)) continue;
                var column = property.Name.ToLowerInvariant();
                if (!SuiteQlTranslator.IsIdentifier(column) || columns.Contains(column)) continue;

                var kind = GetKind(property.Value);
                List<NetSuiteFieldDefinition>? sublistFields = null;
                if (kind == null && column != "id")
                {
                    //明細 (行キー line を持つサブリスト) だけを列にする。読めない・行キーの無いものは今までどおり列にしない
                    sublistFields = await GetSublistFieldsAsync(property.Value, getSchema);
                    if (sublistFields != null) kind = NetSuiteFieldKind.Sublist;
                }
                if (kind == null) continue;
                columns.Add(column);
                definition.Fields.Add(new NetSuiteFieldDefinition
                {
                    RestName = property.Name,
                    Column = column,
                    //internal ID は NetSuite が採番する文字列
                    Kind = column == "id" ? NetSuiteFieldKind.String : kind.Value,
                    IsNullable = column == "id" || !required.Contains(property.Name),
                    SublistFields = sublistFields ?? new(),
                });
            }
            definition.Fields.Sort((a, b) => a.Column == "id" ? -1 : b.Column == "id" ? 1 : string.CompareOrdinal(a.Column, b.Column));
            return definition;
        }

        static HashSet<string> GetRequired(JsonElement schema)
        {
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (schema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in requiredElement.EnumerateArray()) if (e.ValueKind == JsonValueKind.String) required.Add(e.GetString()!);
            }
            return required;
        }

        //値を 1 つ持つ項目の種類。明細 (サブリスト)・サブレコード・知らない形は null
        static NetSuiteFieldKind? GetKind(JsonElement property)
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
            if (GetReferenceName(property) is { } target) return target.Contains('-') ? null : NetSuiteFieldKind.Reference;

            //その場に書かれたオブジェクト。id を持ち items を持たないものが参照 (選択肢を含む)
            if (property.TryGetProperty("properties", out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                if (inner.TryGetProperty("items", out _)) return null;
                if (inner.TryGetProperty("id", out _)) return NetSuiteFieldKind.Reference;
            }
            return null;
        }

        //$ref の先の名前 (URL の最後の部分 = metadata-catalog の名前)。$ref が無ければ null
        static string? GetReferenceName(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("$ref", out var reference) || reference.ValueKind != JsonValueKind.String) return null;
            return (reference.GetString() ?? string.Empty).Split('/').LastOrDefault() ?? string.Empty;
        }

        /// <summary>metadata-catalog の名前として送ってよい文字だけか (英数字・_・-)。</summary>
        internal static bool IsCatalogName(string name)
            => !string.IsNullOrEmpty(name) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');

        //$ref ならその先 (同じ metadata-catalog から読む)。読めなければ null
        static async Task<JsonElement?> ResolveAsync(JsonElement element, Func<string, Task<JsonElement?>>? getSchema)
        {
            for (var depth = 0; ; depth++)
            {
                var name = GetReferenceName(element);
                if (name == null) return element;
                if (getSchema == null || MaxReferenceDepth <= depth || !IsCatalogName(name)) return null;
                var target = await getSchema(name);
                if (target == null) return null;
                element = target.Value;
            }
        }

        /// <summary>
        /// 明細 (行キー line を持つサブリスト) なら行の項目。それ以外は null。
        /// 形は 2 通りを受ける: その場に書かれた {properties:{items:{type:array, items:{properties:{...}}}}} と、$ref (…Collection → items → …Element)。
        /// </summary>
        static async Task<List<NetSuiteFieldDefinition>?> GetSublistFieldsAsync(JsonElement property, Func<string, Task<JsonElement?>>? getSchema)
        {
            if (await ResolveAsync(property, getSchema) is not { ValueKind: JsonValueKind.Object } collection ||
                !collection.TryGetProperty("properties", out var collectionProperties) || collectionProperties.ValueKind != JsonValueKind.Object ||
                !collectionProperties.TryGetProperty("items", out var itemsProperty)) return null;
            if (await ResolveAsync(itemsProperty, getSchema) is not { ValueKind: JsonValueKind.Object } items || !items.TryGetProperty("items", out var elementProperty)) return null;
            if (items.TryGetProperty("type", out var type) && !(type.ValueKind == JsonValueKind.String && type.GetString() == "array")) return null;
            if (await ResolveAsync(elementProperty, getSchema) is not { ValueKind: JsonValueKind.Object } element ||
                !element.TryGetProperty("properties", out var rowProperties) || rowProperties.ValueKind != JsonValueKind.Object) return null;
            if (!rowProperties.TryGetProperty("line", out _)) return null;

            var required = GetRequired(element);
            var fields = new List<NetSuiteFieldDefinition>();
            foreach (var row in rowProperties.EnumerateObject())
            {
                if (_ignoredProperties.Contains(row.Name)) continue;
                //行の中の入れ子 (サブレコード・サブリスト) は扱わない
                var kind = row.Name == "line" ? NetSuiteFieldKind.Integer : GetKind(row.Value);
                if (kind == null || fields.Any(f => f.RestName == row.Name)) continue;
                fields.Add(new NetSuiteFieldDefinition
                {
                    RestName = row.Name,
                    Column = row.Name.ToLowerInvariant(),
                    Kind = kind.Value,
                    IsNullable = row.Name == "line" || !required.Contains(row.Name),
                });
            }
            return fields;
        }
    }
}
