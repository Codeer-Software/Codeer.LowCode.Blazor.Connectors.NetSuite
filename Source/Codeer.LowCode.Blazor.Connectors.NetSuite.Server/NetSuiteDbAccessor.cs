using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.SystemSettings;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    /// <summary>
    /// NetSuite をデータソースとして見せる IDbAccessor。NetSuite 用のデータソースへの読み書きは NetSuite へ
    /// (一覧・検索は SuiteQL、作成・更新・削除は REST のレコード操作)、それ以外のデータソースは包んだ IDbAccessor へそのまま渡す。
    /// NetSuite への書き込みはトランザクションに入らない (1 件ごとに確定し、ロールバックで戻らない)。
    /// </summary>
    public class NetSuiteDbAccessor : IDbAccessor
    {
        const int MaxPageSize = 1000;

        readonly IDbAccessor _inner;
        readonly Dictionary<string, NetSuiteConnection> _connections;

        /// <param name="inner">NetSuite 以外のデータソースを受け持つ IDbAccessor。</param>
        /// <param name="connections">NetSuite への接続 (<see cref="NetSuiteConnection.FromDataSources"/>)。</param>
        /// <param name="metadataDirectory">
        /// 指定すると、表の定義をこのフォルダに保存したファイル (<see cref="NetSuiteMetadataFile"/>) から答える (NetSuite に取りに行かない)。デザイナ用。
        /// </param>
        public NetSuiteDbAccessor(IDbAccessor inner, IEnumerable<NetSuiteConnection> connections, string? metadataDirectory = null)
        {
            _inner = inner;
            _connections = connections.ToDictionary(e => e.Settings.Name);
            _metadataDirectory = metadataDirectory;
        }

        readonly string? _metadataDirectory;

        //保存してある定義 (Records に書かれたレコードだけ)。ファイルから答えない設定なら null
        List<NetSuiteRecordDefinition>? LoadSavedDefinitions(NetSuiteConnection connection)
        {
            if (string.IsNullOrEmpty(_metadataDirectory)) return null;
            var saved = NetSuiteMetadataFile.Load(_metadataDirectory, connection.Settings.Name) ?? new();
            //明細の表は、親のレコードが Records にあるときだけ
            return saved.Where(e => connection.IsAllowedRecord(e.LineParent ?? e.Name)).ToList();
        }

        NetSuiteConnection? Find(string dataSourceName)
            => _connections.TryGetValue(dataSourceName, out var connection) ? connection : null;

        public DataSource? GetDataSource(string dataSource)
        {
            if (Find(dataSource) == null) return _inner.GetDataSource(dataSource);
            //本体には Oracle として見せる (SuiteQL は Oracle の書き方を受け付ける)。接続の設定は返さない
            return new DataSource { Name = dataSource, DataSourceType = DataSourceType.Oracle };
        }

        public void StartTransaction() => _inner.StartTransaction();
        public Task CommitAsync() => _inner.CommitAsync();
        public Task RollbackAsync() => _inner.RollbackAsync();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        public DbTableDefinitionCache? DbTableDefinitionCache => _inner.DbTableDefinitionCache;

        public int CommandTimeoutSeconds
        {
            get => _inner.CommandTimeoutSeconds;
            set => _inner.CommandTimeoutSeconds = value;
        }

        public DbConnection GetConnection(string dataSourceName)
        {
            if (Find(dataSourceName) == null) return _inner.GetConnection(dataSourceName);
            throw new NetSuiteException("A NetSuite data source has no database connection (SQL cannot be executed on it directly).");
        }

        public IDbTransaction? GetTransaction(string dataSourceName)
            => Find(dataSourceName) == null ? _inner.GetTransaction(dataSourceName) : null;

        public object? ConvertFieldValueToDbValue(string dataSourceName, string rawDbTypeName, object? value)
            => Find(dataSourceName) == null ? _inner.ConvertFieldValueToDbValue(dataSourceName, rawDbTypeName, value) : value;

        public object? ConvertDbValueToFieldValue(string dataSourceName, object dbValue, Type fieldType)
            => Find(dataSourceName) == null ? _inner.ConvertDbValueToFieldValue(dataSourceName, dbValue, fieldType) : null;

        public Task<DbSqlCommandResult> ExecuteSqlCommandAsync(string dataSourceName, DbSqlCommand command)
        {
            if (Find(dataSourceName) == null) return _inner.ExecuteSqlCommandAsync(dataSourceName, command);
            throw new NetSuiteException("SQL and stored procedures cannot be executed on a NetSuite data source.");
        }

        // ---------------------------------------------------------------- 定義

        public async Task<List<DbTableDefinition>?> GetCustomTableDefinitionsAsync(string dataSourceName)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.GetCustomTableDefinitionsAsync(dataSourceName);
            if (LoadSavedDefinitions(connection) is { } saved) return saved.Select(e => e.ToTableDefinition()).ToList();
            return (await connection.GetRecordDefinitionsAsync()).Select(e => e.ToTableDefinition()).ToList();
        }

        public async Task<DbTableDefinition?> GetCustomTableDefinitionsAsync(string dataSourceName, string tableNames)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.GetCustomTableDefinitionsAsync(dataSourceName, tableNames);
            if (LoadSavedDefinitions(connection) is { } saved)
            {
                var definition = saved.FirstOrDefault(e => string.Equals(e.Name, tableNames, StringComparison.OrdinalIgnoreCase));
                if (definition == null)
                    throw new NetSuiteException($"Record type '{tableNames}' is not in the saved NetSuite metadata of data source '{dataSourceName}'. Update the NetSuite metadata.");
                return definition.ToTableDefinition();
            }
            return (await connection.GetRecordDefinitionAsync(tableNames)).ToTableDefinition();
        }

        // ---------------------------------------------------------------- 読み出し

        public async Task<List<IDictionary<string, object>>> QueryAsync(string dataSourceName, string query, Dictionary<string, ParamAndRawDbTypeName> args)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.QueryAsync(dataSourceName, query, args);
            return await QueryCoreAsync(connection, query, args.ToDictionary(e => e.Key, e => e.Value.Value));
        }

        static async Task<List<IDictionary<string, object>>> QueryCoreAsync(NetSuiteConnection connection, string query, Dictionary<string, object?> args)
        {
            var select = await SuiteQlTranslator.TranslateSelectAsync(query, args, connection.IsReadableTable, connection.GetRecordDefinitionAsync);
            var items = await ReadItemsAsync(connection, select);
            var rows = items.Select(e => ToRow(select, e)).ToList();
            if (select.Sublists.Count != 0) await ReadSublistsAsync(connection, select, items, rows);
            return rows;
        }

        static async Task<List<Dictionary<string, string?>>> ReadItemsAsync(NetSuiteConnection connection, SuiteQlSelect select)
        {
            var items = new List<Dictionary<string, string?>>();
            if (select.Limit == 0) return items;

            if (select.Limit is int limit)
            {
                if (limit <= MaxPageSize)
                {
                    var page = await connection.QuerySuiteQlAsync(select.Sql, select.Parameters, limit, select.Offset);
                    items.AddRange(page.Items);
                    return items;
                }
                //1 回の要求は 1000 行まで、offset は limit の倍数でなければならない。1000 行の区切りで読み、要る範囲だけ取り出す
                var skip = select.Offset % MaxPageSize;
                for (var offset = select.Offset - skip; items.Count < skip + limit; offset += MaxPageSize)
                {
                    var page = await connection.QuerySuiteQlAsync(select.Sql, select.Parameters, MaxPageSize, offset);
                    items.AddRange(page.Items);
                    if (!page.HasMore || page.Items.Count == 0) break;
                }
                return items.Skip(skip).Take(limit).ToList();
            }

            for (var offset = 0; ; offset += MaxPageSize)
            {
                var page = await connection.QuerySuiteQlAsync(select.Sql, select.Parameters, MaxPageSize, offset);
                items.AddRange(page.Items);
                //多すぎる結果を黙って切り捨てない
                if (connection.Settings.MaxRows < items.Count)
                    throw new NetSuiteException($"The query returned more than {connection.Settings.MaxRows} rows (MaxRows of the NetSuite data source). Narrow the condition or use paging.", NetSuiteErrorKind.InvalidRequest);
                if (!page.HasMore || page.Items.Count == 0) break;
            }
            return items;
        }

        //明細の列: 行ごとにレコードを明細つきで読み、行の JSON 配列の文字列にする (同時に送る数は接続の同時実行の枠まで)
        static async Task ReadSublistsAsync(NetSuiteConnection connection, SuiteQlSelect select, List<Dictionary<string, string?>> items, List<IDictionary<string, object>> rows)
        {
            await Task.WhenAll(items.Select(async (item, index) =>
            {
                var row = rows[index];
                item.TryGetValue(select.IdSourceName, out var id);
                using var record = string.IsNullOrEmpty(id) ? null : await connection.GetRecordAsync(select.Table, id);
                foreach (var column in select.Sublists)
                {
                    //読んでいる間に消されたレコードは明細なし
                    row[column.Key] = record == null ? null! : NetSuiteSublistJson.ToColumnValue(record.RootElement, column.Sublist!);
                }
            }));
        }

        static IDictionary<string, object> ToRow(SuiteQlSelect select, Dictionary<string, string?> item)
        {
            var row = new Dictionary<string, object>();
            foreach (var column in select.Columns)
            {
                //SuiteQL の結果は値が無い列を省く
                item.TryGetValue(column.SourceName, out var text);
                row[column.Key] = ConvertResult(column, text)!;
            }
            return row;
        }

        static object? ConvertResult(SuiteQlResultColumn column, string? text)
        {
            if (text == null) return null;
            switch (column.Kind)
            {
                case NetSuiteFieldKind.Boolean:
                    return text == "T" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
                case NetSuiteFieldKind.Integer:
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
                    break;
                case NetSuiteFieldKind.Number:
                    if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
                    break;
                case NetSuiteFieldKind.Date:
                    if (DateOnly.TryParseExact(text, SuiteQlTranslator.DateFormatNet, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
                    break;
                case NetSuiteFieldKind.DateTime:
                    if (DateTime.TryParseExact(text, SuiteQlTranslator.DateTimeFormatNet, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime)) return dateTime;
                    break;
                case null:
                    if (column.IsNumericExpression && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        return value == decimal.Truncate(value) && long.MinValue <= value && value <= long.MaxValue ? (long)value : value;
                    break;
            }
            return text;
        }

        // ---------------------------------------------------------------- 書き込み

        public async Task<string> InsertAsync(string dataSourceName, string query, Dictionary<string, object?> args)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.InsertAsync(dataSourceName, query, args);

            await CheckWritableAsync(connection, query);
            var inserts = SuiteQlTranslator.ParseInserts(query);
            if (inserts.Count != 1) throw new NetSuiteException("This operation is not supported for a NetSuite data source.");
            var id = await InsertCoreAsync(connection, inserts[0], args);
            if (!string.IsNullOrEmpty(inserts[0].ReturningParameter)) args[inserts[0].ReturningParameter] = id;
            return id;
        }

        public async Task<int> ExecuteAsync(string dataSourceName, string query, Dictionary<string, object?> args)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.ExecuteAsync(dataSourceName, query, args);

            await CheckWritableAsync(connection, query);
            if (SuiteQlTranslator.IsUpdate(query))
            {
                var update = SuiteQlTranslator.ParseUpdate(query);
                if (!await ExistsAsync(connection, update.Table, update.Where, args)) return 0;
                var id = GetId(update.Where, args);
                var body = await CreateRecordBodyAsync(connection, update.Table, update.Values, args, id);
                if (body == null) return 0;
                //変わった項目も明細の差も無ければ送らない
                if (body.IsEmpty) return 1;
                return await connection.UpdateRecordAsync(update.Table, id, body.Json, body.Replace) ? 1 : 0;
            }
            if (SuiteQlTranslator.IsDelete(query))
            {
                var delete = SuiteQlTranslator.ParseDelete(query);
                if (!await ExistsAsync(connection, delete.Table, delete.Where, args)) return 0;
                return await connection.DeleteRecordAsync(delete.Table, GetId(delete.Where, args)) ? 1 : 0;
            }

            //一括の追加 (1 件ずつ作る。途中で失敗しても、そこまでに作ったレコードは残る)
            //途中で失敗したら、作れたレコードの internal ID をエラーに入れる (照合して再開できるように)
            var inserts = SuiteQlTranslator.ParseInserts(query);
            var created = new List<string>();
            foreach (var insert in inserts)
            {
                try
                {
                    created.Add(await InsertCoreAsync(connection, insert, args));
                }
                catch (NetSuiteException e) when (created.Count > 0)
                {
                    throw new NetSuiteException($"{created.Count} of {inserts.Count} records were created before the error (internal IDs: {string.Join(", ", created)}). " +
                        $"Record {created.Count + 1} failed: {e.Message}", e.Kind, e);
                }
            }
            return inserts.Count;
        }

        //明細の表は読むだけ (明細の行は親のレコードの明細の列で書く)
        static async Task CheckWritableAsync(NetSuiteConnection connection, string query)
        {
            foreach (var table in SuiteQlTranslator.GetWriteTables(query))
            {
                if (connection.IsAllowedRecord(table) || !connection.IsReadableTable(table)) continue;
                var definition = await connection.GetRecordDefinitionAsync(table);
                if (definition.IsLineTable)
                    throw new NetSuiteException($"'{definition.Name}' is a read-only table of the lines of sublist '{definition.LineSublist}' of record type '{definition.LineParent}'. " +
                        $"Write the lines through the sublist column of the '{definition.LineParent}' record.");
            }
        }

        static async Task<string> InsertCoreAsync(NetSuiteConnection connection, RecordInsert insert, Dictionary<string, object?> args)
        {
            var body = await CreateRecordBodyAsync(connection, insert.Table, insert.Values, args, null);
            return await connection.CreateRecordAsync(insert.Table, body!.Json);
        }

        static string GetId(RecordWhere where, Dictionary<string, object?> args)
        {
            var id = args.TryGetValue(where.IdParameter, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
            if (string.IsNullOrEmpty(id)) throw new NetSuiteException("The internal ID of the NetSuite record to write is missing.");
            return id;
        }

        //Id のほかに条件 (楽観ロック・論理削除など) が付いた書き込みは、条件に合う行があるかを先に読んで確かめる。
        //確かめてから書くまでの間に別の更新が入ることは防げない (NetSuite の REST に条件つきの更新は無い)
        static async Task<bool> ExistsAsync(NetSuiteConnection connection, string table, RecordWhere where, Dictionary<string, object?> args)
        {
            if (!where.HasOtherConditions) return true;
            var rows = await QueryCoreAsync(connection, $"select count(*) from \"{table}\" where {where.Sql}", args);
            var count = rows.FirstOrDefault()?.Values.FirstOrDefault();
            return count != null && 0 < Convert.ToDecimal(count, CultureInfo.InvariantCulture);
        }

        class RecordBody
        {
            public byte[] Json { get; set; } = Array.Empty<byte>();
            /// <summary>要求に無い行を消す明細 (?replace=)。</summary>
            public List<string> Replace { get; } = new();
            /// <summary>送る項目が 1 つも無い。</summary>
            public bool IsEmpty { get; set; }
        }

        /// <summary>
        /// 要求の本文。明細の列は、作成なら行を全部、更新 (<paramref name="updateId"/> あり) なら今の明細と比べた差だけにする。
        /// 更新でレコードが無ければ null。
        /// </summary>
        static async Task<RecordBody?> CreateRecordBodyAsync(NetSuiteConnection connection, string table, List<(string Column, string Parameter)> values, Dictionary<string, object?> args, string? updateId)
        {
            var definition = await connection.GetRecordDefinitionAsync(table);
            var fields = new List<(NetSuiteFieldDefinition Field, object? Value)>();
            foreach (var (column, parameter) in values)
            {
                var field = definition.FindByColumn(column);
                if (field == null) throw new NetSuiteException($"Record type '{table}' has no field '{column}'.");
                if (field.Column == "id") throw new NetSuiteException("The internal ID of a NetSuite record is assigned by NetSuite and cannot be written.");
                if (!args.TryGetValue(parameter, out var value)) throw new NetSuiteException($"The value of parameter '{parameter}' is missing.");
                fields.Add((field, value));
            }

            //更新の明細は今の明細との差にする (同時に編集されたときは後から保存したほうが残る)
            JsonDocument? current = null;
            if (updateId != null && fields.Any(e => e.Field.Kind == NetSuiteFieldKind.Sublist))
            {
                current = await connection.GetRecordAsync(table, updateId);
                if (current == null) return null;
            }
            using (current)
            {
                var body = new RecordBody();
                var count = 0;
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var (field, value) in fields)
                    {
                        if (field.Kind == NetSuiteFieldKind.Sublist)
                        {
                            var rows = NetSuiteSublistJson.ParseColumnValue(field, value);
                            if (current == null)
                            {
                                //空の明細は項目ごと送らない
                                if (rows.Count == 0) continue;
                                writer.WritePropertyName(field.RestName);
                                NetSuiteSublistJson.CreateInsertItems(field, rows).WriteTo(writer);
                            }
                            else
                            {
                                var (items, replace) = NetSuiteSublistJson.CreateUpdateItems(field, rows, current.RootElement);
                                if (items == null) continue;
                                writer.WritePropertyName(field.RestName);
                                items.WriteTo(writer);
                                if (replace) body.Replace.Add(field.RestName);
                            }
                            count++;
                            continue;
                        }
                        writer.WritePropertyName(field.RestName);
                        WriteValue(writer, field, value);
                        count++;
                    }
                    writer.WriteEndObject();
                }
                body.Json = stream.ToArray();
                body.IsEmpty = count == 0;
                return body;
            }
        }

        static void WriteValue(Utf8JsonWriter writer, NetSuiteFieldDefinition field, object? value)
        {
            if (value == null || value is DBNull)
            {
                writer.WriteNullValue();
                return;
            }
            switch (field.Kind)
            {
                case NetSuiteFieldKind.Boolean:
                    writer.WriteBooleanValue(value switch
                    {
                        bool b => b,
                        string s => s == "T" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1",
                        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0,
                    });
                    break;
                case NetSuiteFieldKind.Integer:
                    writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    break;
                case NetSuiteFieldKind.Number:
                    writer.WriteNumberValue(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                    break;
                case NetSuiteFieldKind.Date:
                    //日付だけの項目は時刻帯で動かさない
                    writer.WriteStringValue(value switch
                    {
                        DateOnly date => date.ToString(SuiteQlTranslator.DateFormatNet, CultureInfo.InvariantCulture),
                        DateTime dateTime => dateTime.ToString(SuiteQlTranslator.DateFormatNet, CultureInfo.InvariantCulture),
                        DateTimeOffset offset => offset.ToString(SuiteQlTranslator.DateFormatNet, CultureInfo.InvariantCulture),
                        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
                    });
                    break;
                case NetSuiteFieldKind.DateTime:
                    //日時は UTC の瞬間として送る (時刻帯の無い値は UTC として扱う = DateTimeField の SaveAsUtc)
                    writer.WriteStringValue(value switch
                    {
                        DateTime dateTime => (dateTime.Kind == DateTimeKind.Local ? dateTime.ToUniversalTime() : dateTime).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        DateTimeOffset offset => offset.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                        DateOnly date => date.ToString(SuiteQlTranslator.DateFormatNet, CultureInfo.InvariantCulture) + "T00:00:00Z",
                        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
                    });
                    break;
                case NetSuiteFieldKind.Reference:
                    {
                        var id = Convert.ToString(value, CultureInfo.InvariantCulture);
                        if (string.IsNullOrEmpty(id))
                        {
                            writer.WriteNullValue();
                            break;
                        }
                        writer.WriteStartObject();
                        writer.WriteString("id", id);
                        writer.WriteEndObject();
                        break;
                    }
                default:
                    writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
            }
        }
    }
}
