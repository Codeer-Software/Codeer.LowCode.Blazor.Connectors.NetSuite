using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DataIO.Db.Definition;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;
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
        readonly NetSuiteMetadataFile _metadataFile;

        /// <param name="inner">NetSuite 以外のデータソースを受け持つ IDbAccessor。</param>
        /// <param name="connections">NetSuite への接続 (<see cref="NetSuiteConnection.FromDataSources"/>)。</param>
        /// <param name="metadataFile">
        /// 表の定義の出所。表の定義はこのファイルから読み、NetSuite に取りに行かない (ファイルに無いレコードは読み書きしない)。
        /// デザイナは <see cref="NetSuiteMetadataFile.FromProject"/>、Web アプリは <see cref="NetSuiteMetadataFile.FromDeployedDesign"/>。
        /// </param>
        public NetSuiteDbAccessor(IDbAccessor inner, IEnumerable<NetSuiteConnection> connections, NetSuiteMetadataFile metadataFile)
        {
            _inner = inner;
            _connections = connections.ToDictionary(e => e.Settings.Name);
            _metadataFile = metadataFile;
        }

        //1 回の読み書きの間は同じ定義を使う (配られたデザインが途中で差し替わっても混ざらない)
        Definitions GetDefinitions(NetSuiteConnection connection) => new(_metadataFile, connection.Settings.Name, _metadataFile.Load(connection.Settings.Name));

        sealed class Definitions
        {
            readonly NetSuiteMetadataFile _file;
            readonly string _dataSourceName;

            public NetSuiteMetadata Metadata { get; }

            public Definitions(NetSuiteMetadataFile file, string dataSourceName, NetSuiteMetadata metadata)
            {
                _file = file;
                _dataSourceName = dataSourceName;
                Metadata = metadata;
            }

            /// <summary>レコードか明細の表の定義。ファイルに無ければ、メタ情報の更新 (と配り直し) を案内するエラー。</summary>
            public NetSuiteRecordDefinition Get(string table) => Metadata.Find(table) ?? throw _file.RecordNotFound(_dataSourceName, table);

            public Task<NetSuiteRecordDefinition> GetAsync(string table) => Task.FromResult(Get(table));
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
            return GetDefinitions(connection).Metadata.Records.Select(e => e.ToTableDefinition()).ToList();
        }

        public async Task<DbTableDefinition?> GetCustomTableDefinitionsAsync(string dataSourceName, string tableNames)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.GetCustomTableDefinitionsAsync(dataSourceName, tableNames);
            return GetDefinitions(connection).Get(tableNames).ToTableDefinition();
        }

        // ---------------------------------------------------------------- 読み出し

        public async Task<List<IDictionary<string, object>>> QueryAsync(string dataSourceName, string query, Dictionary<string, ParamAndRawDbTypeName> args)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.QueryAsync(dataSourceName, query, args);
            return await QueryCoreAsync(connection, GetDefinitions(connection), query, args.ToDictionary(e => e.Key, e => e.Value.Value));
        }

        static async Task<List<IDictionary<string, object>>> QueryCoreAsync(NetSuiteConnection connection, Definitions definitions, string query, Dictionary<string, object?> args)
        {
            var select = await SuiteQlTranslator.TranslateSelectAsync(query, args, definitions.GetAsync);
            var items = await ReadItemsAsync(connection, select);
            var rows = items.Select(e => ToRow(select, e)).ToList();
            if (select.Sublists.Count != 0) await ReadSublistsAsync(connection, select, items, rows);
            if (select.Attachments.Count != 0) await ReadAttachmentsAsync(connection, select, items, rows);
            return rows;
        }

        //添付の列: 読んだ行の全部を、保存検索 1 回 (最後のページまで) で引き当てる。保存検索が未設定なら空
        static async Task ReadAttachmentsAsync(NetSuiteConnection connection, SuiteQlSelect select, List<Dictionary<string, string?>> items, List<IDictionary<string, object>> rows)
        {
            var ids = items.Select(e => e.TryGetValue(select.IdSourceName, out var id) ? id : null).ToList();
            var files = await connection.ReadAttachedFilesAsync(ids.OfType<string>().Where(e => e.Length != 0).Distinct().ToList());
            for (var i = 0; i < rows.Count; i++)
            {
                var value = ids[i] is { } id && files.TryGetValue(id, out var list) ? NetSuiteAttachments.ToColumnValue(list) : null;
                foreach (var column in select.Attachments) rows[i][column.Key] = value!;
            }
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

            var definitions = GetDefinitions(connection);
            CheckWritable(definitions, query);
            var inserts = SuiteQlTranslator.ParseInserts(query);
            if (inserts.Count != 1) throw new NetSuiteException("This operation is not supported for a NetSuite data source.");
            var id = await InsertCoreAsync(connection, definitions, inserts[0], args);
            if (!string.IsNullOrEmpty(inserts[0].ReturningParameter)) args[inserts[0].ReturningParameter] = id;
            return id;
        }

        public async Task<int> ExecuteAsync(string dataSourceName, string query, Dictionary<string, object?> args)
        {
            var connection = Find(dataSourceName);
            if (connection == null) return await _inner.ExecuteAsync(dataSourceName, query, args);

            var definitions = GetDefinitions(connection);
            CheckWritable(definitions, query);
            if (SuiteQlTranslator.IsUpdate(query))
            {
                var update = SuiteQlTranslator.ParseUpdate(query);
                if (!await ExistsAsync(connection, definitions, update.Table, update.Where, args)) return 0;
                var id = GetId(update.Where, args);
                var definition = definitions.Get(update.Table);
                var change = GetAttachmentChange(connection, definition, update.Values, args);
                var body = await CreateRecordBodyAsync(connection, definition, update.Values, args, id);
                if (body == null) return 0;

                //外すのは、保存の直前に保存検索で読んだ「このレコードのファイル」に ID が一致するものだけ (クライアントの ID だけでは外さない)
                var detach = new List<string>();
                if (change is { Deletions.Count: > 0 })
                {
                    var current = await connection.ReadAttachedFilesAsync(new[] { id });
                    if (current.TryGetValue(id, out var files)) detach = change.Deletions.Where(e => files.Any(f => f.Id == e)).ToList();
                }
                var additions = change?.Additions ?? new();
                var hasAttachmentWork = additions.Count != 0 || detach.Count != 0;

                if (body.IsEmpty)
                {
                    //変わった項目も明細の差も添付の変更も無ければ送らない
                    if (!hasAttachmentWork) return 1;
                    //添付だけの変更でも、対象のレコードがあることを確かめてから (無いレコードのためにファイルを登録しない)
                    if (!await ExistsAsync(connection, definitions, update.Table, update.Where, args, true)) return 0;
                }
                else if (!await connection.UpdateRecordAsync(update.Table, id, body.Json, body.Replace))
                {
                    return 0;
                }
                if (hasAttachmentWork) await ApplyAttachmentsAsync(connection, update.Table, id, additions, detach);
                return 1;
            }
            if (SuiteQlTranslator.IsDelete(query))
            {
                var delete = SuiteQlTranslator.ParseDelete(query);
                if (!await ExistsAsync(connection, definitions, delete.Table, delete.Where, args)) return 0;
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
                    created.Add(await InsertCoreAsync(connection, definitions, insert, args));
                }
                catch (NetSuiteException e) when (created.Count > 0 || (e.RecordId != null && 1 < inserts.Count))
                {
                    //1 件目で失敗しても、そのレコードが作れて添付だけ失敗したなら (RecordId あり)、作成済みと送っていない件数を伝える
                    var unsent = inserts.Count - created.Count - 1;
                    //添付の途中で失敗したレコードは作成済み
                    var ids = created.ToList();
                    if (e.RecordId != null) ids.Add(e.RecordId);
                    var failure = e.RecordId != null
                        ? $"Record {created.Count + 1} was created (internal ID: {e.RecordId}) but its files were not fully attached"
                        : $"Record {created.Count + 1} {(e.Kind == NetSuiteErrorKind.WriteResultUnknown ? "may or may not have been created" : "failed")}";
                    var before = created.Count == 0
                        ? $"No records of {inserts.Count} were created before the error. "
                        : $"{created.Count} of {inserts.Count} records were created before the error (internal IDs: {string.Join(", ", created)}). ";
                    throw new NetSuiteException(before +
                        $"{failure}: {e.Message}" +
                        (unsent > 0 ? $" The remaining {unsent} records were not sent." : string.Empty), e, ids);
                }
            }
            return inserts.Count;
        }

        //書けるのはファイルにあるレコードだけ。明細の表は読むだけ (明細の行は親のレコードの明細の列で書く)
        static void CheckWritable(Definitions definitions, string query)
        {
            foreach (var table in SuiteQlTranslator.GetWriteTables(query))
            {
                var definition = definitions.Get(table);
                if (definition.IsLineTable)
                    throw new NetSuiteException($"'{definition.Name}' is a read-only table of the lines of sublist '{definition.LineSublist}' of record type '{definition.LineParent}'. " +
                        $"Write the lines through the sublist column of the '{definition.LineParent}' record.");
            }
        }

        static async Task<string> InsertCoreAsync(NetSuiteConnection connection, Definitions definitions, RecordInsert insert, Dictionary<string, object?> args)
        {
            var definition = definitions.Get(insert.Table);
            //作成では足すファイルだけ (外す指定は見ない)。レコードの作成が成功して internal ID が決まってから、登録 → 添付
            var change = GetAttachmentChange(connection, definition, insert.Values, args);
            var body = await CreateRecordBodyAsync(connection, definition, insert.Values, args, null);
            var id = await connection.CreateRecordAsync(insert.Table, body!.Json);
            if (change is { Additions.Count: > 0 }) await ApplyAttachmentsAsync(connection, insert.Table, id, change.Additions, Array.Empty<string>());
            return id;
        }

        //添付の列の変更 (書き込みの値に添付の列があれば)。足すファイルがあるのに保存先のフォルダが設定されていなければ、何も書く前にエラー
        static NetSuiteAttachments.Change? GetAttachmentChange(NetSuiteConnection connection, NetSuiteRecordDefinition definition, List<(string Column, string Parameter)> values, Dictionary<string, object?> args)
        {
            NetSuiteAttachments.Change? change = null;
            foreach (var (column, parameter) in values)
            {
                var field = definition.FindByColumn(column);
                if (field?.Kind != NetSuiteFieldKind.Attachments) continue;
                args.TryGetValue(parameter, out var value);
                change = NetSuiteAttachments.ParseChange(definition.Name, field.Column, value);
            }
            if (change is { Additions.Count: > 0 } && string.IsNullOrEmpty(connection.Settings.AttachmentFolderId))
                throw new NetSuiteException($"Files cannot be added to NetSuite record type '{definition.Name}' because {nameof(NetSuiteConnectionSettings.AttachmentFolderId)} " +
                    $"(the File Cabinet folder for added files) is not set in the connection string of NetSuite data source '{connection.Settings.Name}'.");
            return change;
        }

        /// <summary>
        /// 保存済みのレコードに、ファイルを 1 つずつ登録して添付し (POST document/v1/file/ → !attach)、そのあと外す (!detach)。
        /// 書き込みはやり直さない。途中で失敗したら、どこまで済んだか (レコードの ID・登録したが添付できなかったファイルの ID) を入れたエラーにする。登録したファイルは消さない。
        /// NetSuiteException 以外の例外も、結果不明 (WriteResultUnknown) として同じエラーに包む (レコードは保存済みなので、素の例外にすると作成が保存し直しで二重になる)。
        /// </summary>
        static async Task ApplyAttachmentsAsync(NetSuiteConnection connection, string table, string recordId, IReadOnlyList<NetSuiteAttachments.Addition> additions, IReadOnlyList<string> detach)
        {
            var attached = new List<string>();
            for (var i = 0; i < additions.Count; i++)
            {
                var addition = additions[i];
                string fileId;
                try
                {
                    fileId = await connection.UploadFileAsync(connection.Settings.AttachmentFolderId, addition.Name, addition.ContentType, addition.Content);
                }
                catch (Exception ex)
                {
                    var e = AsNetSuiteException(ex);
                    var step = e.Kind == NetSuiteErrorKind.WriteResultUnknown
                        ? $"whether the file '{addition.Name}' was registered in the File Cabinet is unknown (check the File Cabinet before adding it again)"
                        : $"the file '{addition.Name}' could not be registered in the File Cabinet";
                    throw AttachmentFailed(e, table, recordId, step, attached, additions.Count - i - 1, detach.Count, Array.Empty<string>());
                }
                try
                {
                    await connection.AttachFileAsync(table, recordId, fileId);
                }
                catch (Exception ex)
                {
                    var e = AsNetSuiteException(ex);
                    var step = e.Kind == NetSuiteErrorKind.WriteResultUnknown
                        ? $"the file '{addition.Name}' was registered in the File Cabinet (file ID: {fileId}) but whether it was attached is unknown"
                        : $"the file '{addition.Name}' was registered in the File Cabinet (file ID: {fileId}) but could not be attached";
                    throw AttachmentFailed(e, table, recordId, step, attached, additions.Count - i - 1, detach.Count, new[] { fileId });
                }
                attached.Add(fileId);
            }
            for (var i = 0; i < detach.Count; i++)
            {
                try
                {
                    await connection.DetachFileAsync(table, recordId, detach[i]);
                }
                catch (Exception ex)
                {
                    var e = AsNetSuiteException(ex);
                    var step = e.Kind == NetSuiteErrorKind.WriteResultUnknown
                        ? $"whether the file {detach[i]} was detached is unknown"
                        : $"the file {detach[i]} could not be detached";
                    throw AttachmentFailed(e, table, recordId, step, attached, 0, detach.Count - i - 1, Array.Empty<string>());
                }
            }
        }

        //添付の処理の途中の NetSuiteException 以外の例外 (取り消し・ヘッダーの組み立てなど) は、送ったかどうか分からないので結果不明として扱う
        static NetSuiteException AsNetSuiteException(Exception e)
            => e as NetSuiteException ?? new NetSuiteException($"The request to NetSuite failed and whether it was processed is unknown ({e.GetType().Name}: {e.Message})", NetSuiteErrorKind.WriteResultUnknown, e);

        static NetSuiteException AttachmentFailed(NetSuiteException e, string table, string recordId, string step, List<string> attached, int unsentAdditions, int unsentDetaches, IReadOnlyList<string> unattached)
        {
            var message = $"The NetSuite record '{table}' (internal ID: {recordId}) was saved, but {step}: {e.Message}";
            if (attached.Count != 0) message += $" Files attached before the error: {string.Join(", ", attached)}.";
            if (unsentAdditions != 0) message += $" {unsentAdditions} more file(s) to add were not sent.";
            if (unsentDetaches != 0) message += $" {unsentDetaches} file(s) to detach were not detached.";
            return new NetSuiteException(message, e, recordId, unattached);
        }

        static string GetId(RecordWhere where, Dictionary<string, object?> args)
        {
            var id = args.TryGetValue(where.IdParameter, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
            if (string.IsNullOrEmpty(id)) throw new NetSuiteException("The internal ID of the NetSuite record to write is missing.");
            return id;
        }

        //Id のほかに条件 (楽観ロック・論理削除など) が付いた書き込みは、条件に合う行があるかを先に読んで確かめる。
        //確かめてから書くまでの間に別の更新が入ることは防げない (NetSuite の REST に条件つきの更新は無い)
        static async Task<bool> ExistsAsync(NetSuiteConnection connection, Definitions definitions, string table, RecordWhere where, Dictionary<string, object?> args, bool always = false)
        {
            if (!where.HasOtherConditions && !always) return true;
            var rows = await QueryCoreAsync(connection, definitions, $"select count(*) from \"{table}\" where {where.Sql}", args);
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
        static async Task<RecordBody?> CreateRecordBodyAsync(NetSuiteConnection connection, NetSuiteRecordDefinition definition, List<(string Column, string Parameter)> values, Dictionary<string, object?> args, string? updateId)
        {
            var table = definition.Name;
            var fields = new List<(NetSuiteFieldDefinition Field, object? Value)>();
            foreach (var (column, parameter) in values)
            {
                var field = definition.FindByColumn(column);
                if (field == null) throw new NetSuiteException($"Record type '{table}' has no field '{column}'.");
                if (field.Column == "id") throw new NetSuiteException("The internal ID of a NetSuite record is assigned by NetSuite and cannot be written.");
                //添付は本文に入れない (レコードの保存の後で、ファイルの登録と添付にする)
                if (field.Kind == NetSuiteFieldKind.Attachments) continue;
                if (!args.TryGetValue(parameter, out var value)) throw new NetSuiteException($"The value of parameter '{parameter}' is missing.");
                if (field.IsReadOnly)
                {
                    //作成では、NetSuite が自分で決める項目 (メタデータで読み取り専用) の値は送らない
                    //(レコードのコピーは、これらを表示しているフィールドの値も写して作成に乗せる)。更新の対象に入っていたら送る前にエラー
                    if (updateId == null) continue;
                    throw ReadOnlyField(table, field.RestName);
                }
                fields.Add((field, value));
            }

            //更新の明細は今の明細との差にする (同じ項目を同時に変えたときは後から保存したほうが残る。ほかで足された行は残し、ほかで消された行を変えようとしたらエラー)
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
                                var (items, replace) = NetSuiteSublistJson.CreateUpdateItems(table, field, rows, current.RootElement);
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

        // ---------------------------------------------------------------- 添付ファイルのダウンロード

        /// <summary>添付のフィールド (画面用パッケージの NetSuiteAttachmentsFieldDesign) の型の名前。このパッケージは画面用パッケージを参照しないので名前で見る。</summary>
        internal const string AttachmentsFieldDesignTypeFullName = "Codeer.LowCode.Blazor.Connectors.NetSuite.NetSuiteAttachmentsFieldDesign";

        /// <summary>
        /// 添付のフィールドのファイル 1 つの中身を、現在の利用者の権限で読む (ホストのダウンロードの Controller から呼ぶ)。
        /// 手順: モジュールがこのアクセサの NetSuite のデータソースで、フィールドが添付のフィールド (添付の列) で、Id が internal ID の列であることを確かめる →
        /// io の通常の読み出し (モジュール・行・フィールドの権限・論理削除が効く) で Id が一致するレコードのそのフィールドだけを読む (保存検索で引き当てる) →
        /// その値に fileId があることを確かめる → NetSuite から中身を読む。
        /// どれかが満たせなければ null (理由は区別しない。NetSuite が中身を返さない (権限・10 MB 超など) ときも null。例外を投げない)。
        /// 連携用のロールは利用者が読めないレコードのファイルも読めるので、ファイル ID だけで中身を返す口をホストに作らない。
        /// </summary>
        /// <param name="io">現在の利用者の ModuleDataIO。</param>
        /// <param name="designData">io と同じデザイン。</param>
        /// <param name="moduleName">モジュール名。</param>
        /// <param name="id">レコードの Id (internal ID)。</param>
        /// <param name="fieldName">添付のフィールドの名前。</param>
        /// <param name="fileId">ファイルの internal ID (レコードを読んだときの値)。</param>
        public async Task<NetSuiteAttachmentFile?> GetAttachmentAsync(ModuleDataIO io, DesignData designData, string? moduleName, string? id, string? fieldName, string? fileId)
        {
            try
            {
                if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(id) || !NetSuiteAttachments.IsFileId(fileId)) return null;

                var module = designData.Modules.Find(moduleName);
                if (module == null || string.IsNullOrEmpty(module.DataSourceName)) return null;
                var connection = Find(module.DataSourceName);
                if (connection == null || !connection.Settings.HasAttachmentSavedSearch) return null;
                var field = module.Fields.FirstOrDefault(e => e.Name == fieldName);
                if (field?.TypeFullName != AttachmentsFieldDesignTypeFullName) return null;
                //型は名前で見たので、列の名前は公開のプロパティから読む
                if (field.GetType().GetProperty("DbColumn")?.GetValue(field) is not string dbColumn) return null;
                var definition = GetDefinitions(connection).Metadata.Find(module.DbTable);
                if (definition?.FindByColumn(dbColumn) is not { Kind: NetSuiteFieldKind.Attachments }) return null;
                var idField = module.Fields.OfType<IdFieldDesign>().FirstOrDefault();
                if (idField == null || !string.Equals(idField.DbColumn, "id", StringComparison.OrdinalIgnoreCase)) return null;

                var condition = new SearchCondition(moduleName)
                {
                    Condition = MultiMatchCondition.And(new FieldValueMatchCondition
                    {
                        SearchTargetVariable = idField.Name + ".Value",
                        Comparison = MatchComparison.Equal,
                        Value = MultiTypeValue.Create(id),
                    }),
                };
                condition.SelectFields.Add(fieldName);
                var rows = (await io.GetListAsync(condition, 0)).Items;
                if (rows.Count != 1 || !rows[0].Fields.TryGetValue(fieldName, out var data) || data is not ValueFieldDataBase<string> value) return null;
                //クライアントの文字は突き合わせだけに使い、NetSuite へは保存検索の結果にあった ID を送る
                var file = NetSuiteAttachments.ReadFiles(value.Value).FirstOrDefault(e => string.Equals(e.Id, fileId, StringComparison.Ordinal));
                if (file.Id == null) return null;

                var content = await connection.DownloadFileAsync(file.Id);
                return new NetSuiteAttachmentFile(string.IsNullOrEmpty(file.Name) ? "download" : file.Name, content);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static NetSuiteException ReadOnlyField(string table, string field)
            => new($"Field '{field}' of NetSuite record type '{table}' is read-only and cannot be written.", NetSuiteErrorKind.InvalidRequest);

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
