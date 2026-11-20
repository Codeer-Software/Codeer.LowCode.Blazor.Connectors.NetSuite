using Codeer.LowCode.Blazor.SystemSettings;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    /// <summary>
    /// NetSuite への接続 1 つ。アクセストークン・同時実行の枠を持つので、アプリの中で使い回す
    /// (<see cref="FromDataSources"/> は同じ設定に同じ接続を返す)。
    /// </summary>
    public sealed class NetSuiteConnection
    {
        static readonly ConcurrentDictionary<string, NetSuiteConnection> _shared = new();

        readonly HttpClient _http;
        readonly SemaphoreSlim _concurrency;
        readonly SemaphoreSlim _tokenLock = new(1, 1);
        string _accessToken = string.Empty;
        DateTime _accessTokenExpiresAt;

        public NetSuiteConnectionSettings Settings { get; }

        //テストで待ち時間を無くす・時刻を動かすための差し替え口
        internal Func<TimeSpan, Task> Delay { get; set; } = Task.Delay;
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <param name="handler">HTTP の送信を差し替えるとき (プロキシ・テスト)。null なら既定。</param>
        public NetSuiteConnection(NetSuiteConnectionSettings settings, HttpMessageHandler? handler = null)
        {
            settings.Validate();
            Settings = settings;
            _http = handler == null ? new HttpClient() : new HttpClient(handler, false);
            _http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
            _concurrency = new SemaphoreSlim(settings.MaxConcurrency, settings.MaxConcurrency);
        }

        /// <summary>
        /// データソースの中から NetSuite 用 (ConnectionString が Provider=NetSuite) のものを接続にする。同じ設定には同じ接続を返す。
        /// </summary>
        public static List<NetSuiteConnection> FromDataSources(IEnumerable<DataSource> dataSources)
        {
            var list = new List<NetSuiteConnection>();
            foreach (var dataSource in dataSources)
            {
                var settings = NetSuiteConnectionSettings.FromDataSource(dataSource);
                if (settings == null) continue;
                list.Add(_shared.GetOrAdd(settings.GetIdentityKey(), _ => new NetSuiteConnection(settings)));
            }
            return list;
        }

        string RestUrl => Settings.ServiceUrl + "/services/rest";

        // ---------------------------------------------------------------- レコードの定義 (メタ情報の更新のときだけ読む)

        //取り込みを飛ばす失敗 (そのレコードが無い・読めない・SuiteQL の表が無い)。通信の失敗・認証の失敗はそのまま上げる (更新ごと止める)
        static bool IsUnreadable(NetSuiteException e) => e.Kind is NetSuiteErrorKind.NotFound or NetSuiteErrorKind.InvalidRequest or NetSuiteErrorKind.PermissionDenied;

        /// <summary>
        /// アカウントの全部のレコード種別の定義を NetSuite から読む (メタ情報の更新用。要求の数はレコード種別の数の 2 倍ほど)。
        /// 取り込むのは、取引の対応表にある取引と、SuiteQL に同じ名前の表があるレコード (コネクターが一覧・検索で読めるもの)。
        /// 対応表に無い取引・SuiteQL の表が無いレコード・定義が読めないレコードは入れない。取引の後ろに、その明細の表を並べる。表名は小文字。
        /// </summary>
        internal async Task<List<NetSuiteRecordDefinition>> FetchRecordDefinitionsAsync()
        {
            var names = await FetchRecordTypesAsync();
            //止める失敗 (通信・認証) が出たら、まだ始めていないレコードは読まない (残りの全部が再試行をやり切るのを待たない)
            var fetched = new NetSuiteRecordDefinition?[names.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, names.Count), new ParallelOptions { MaxDegreeOfParallelism = Settings.MaxConcurrency },
                async (i, _) => fetched[i] = await FetchReadableRecordDefinitionAsync(names[i]));
            var records = fetched.OfType<NetSuiteRecordDefinition>()
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e => e.First()).ToList();
            //明細の表と同じ名前のレコードは入れない (その名前は読み取り専用の明細の表)
            var lineTables = records.SelectMany(e => e.CreateLineTables()).GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(e => e.Key, e => e.First(), StringComparer.OrdinalIgnoreCase);
            var list = new List<NetSuiteRecordDefinition>();
            foreach (var record in records.Where(e => !lineTables.ContainsKey(e.Name)).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                list.Add(record);
                foreach (var line in record.CreateLineTables())
                {
                    if (lineTables.Remove(line.Name, out var table)) list.Add(table);
                }
            }
            return list;
        }

        /// <summary>レコード種別の名前の一覧 (GET record/v1/metadata-catalog)。</summary>
        async Task<List<string>> FetchRecordTypesAsync()
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{RestUrl}/record/v1/metadata-catalog"), true);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var names = new List<string>();
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } text)
                        names.Add(text);
                }
            }
            return names;
        }

        //取り込むレコードなら定義 (表名は小文字)。取り込まないなら null
        async Task<NetSuiteRecordDefinition?> FetchReadableRecordDefinitionAsync(string recordType)
        {
            var table = recordType.ToLowerInvariant();
            if (!NetSuiteRecordDefinition.IsCatalogName(recordType) || !SuiteQlTranslator.IsIdentifier(table)) return null;
            try
            {
                //取引は対応表で transaction の表に読み替える。ほかは SuiteQL に同じ名前の表があるものだけ (対応表に無い取引は SuiteQL の表が無いので入らない)
                if (SuiteQlTranslator.GetTransactionType(table) == null) await QuerySuiteQlAsync($"select id from {table}", new(), 1, 0);
                return await FetchRecordDefinitionAsync(recordType, table);
            }
            catch (NetSuiteException e) when (IsUnreadable(e))
            {
                return null;
            }
        }

        /// <summary>レコード種別 1 つの定義を NetSuite から読む。</summary>
        /// <param name="recordType">metadata-catalog の名前。</param>
        /// <param name="table">定義の名前 (表名)。</param>
        internal async Task<NetSuiteRecordDefinition> FetchRecordDefinitionAsync(string recordType, string table)
        {
            using var json = await GetSchemaAsync(recordType);
            //明細の行の定義 ($ref の先) は同じ metadata-catalog から読む。無ければその明細は列にしない
            return await NetSuiteRecordDefinition.FromJsonSchemaAsync(table, json.RootElement, async name =>
            {
                try
                {
                    using var target = await GetSchemaAsync(name);
                    return target.RootElement.Clone();
                }
                catch (NetSuiteException e) when (IsUnreadable(e))
                {
                    //明細の定義が読めないだけで、レコードごと使えなくしない
                    return null;
                }
                catch (JsonException)
                {
                    //JSON でない本文も同じ
                    return null;
                }
            });
        }

        async Task<JsonDocument> GetSchemaAsync(string name)
        {
            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{RestUrl}/record/v1/metadata-catalog/{Uri.EscapeDataString(name)}");
                request.Headers.Accept.ParseAdd("application/schema+json");
                return request;
            }, true);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        // ---------------------------------------------------------------- SuiteQL

        internal class SuiteQlPage
        {
            public List<Dictionary<string, string?>> Items { get; } = new();
            public bool HasMore { get; set; }
        }

        internal async Task<SuiteQlPage> QuerySuiteQlAsync(string query, List<string> parameters, int limit, int offset)
        {
            var body = new MemoryStream();
            using (var writer = new Utf8JsonWriter(body))
            {
                writer.WriteStartObject();
                writer.WriteString("q", query);
                if (parameters.Count != 0)
                {
                    writer.WriteStartArray("params");
                    foreach (var p in parameters) writer.WriteStringValue(p);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            var bytes = body.ToArray();

            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{RestUrl}/query/v1/suiteql?limit={limit}&offset={offset}");
                request.Headers.TryAddWithoutValidation("Prefer", "transient");
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return request;
            }, true);

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var page = new SuiteQlPage();
            if (json.RootElement.TryGetProperty("hasMore", out var hasMore)) page.HasMore = hasMore.ValueKind == JsonValueKind.True;
            if (json.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in item.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Array || property.Value.ValueKind == JsonValueKind.Object) continue; //links
                        row[property.Name] = property.Value.ValueKind switch
                        {
                            JsonValueKind.Null => null,
                            JsonValueKind.String => property.Value.GetString(),
                            JsonValueKind.True => "T",
                            JsonValueKind.False => "F",
                            _ => property.Value.GetRawText(),
                        };
                    }
                    page.Items.Add(row);
                }
            }
            return page;
        }

        // ---------------------------------------------------------------- レコードの読み出し

        /// <summary>レコードを明細つきで読む (GET record/v1/{type}/{id}?expandSubResources=true)。対象が無ければ null。</summary>
        internal async Task<JsonDocument?> GetRecordAsync(string recordType, string id)
        {
            try
            {
                using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
                    $"{RestUrl}/record/v1/{Uri.EscapeDataString(recordType)}/{Uri.EscapeDataString(id)}?expandSubResources=true"), true);
                return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            }
            catch (NetSuiteException e) when (e.Kind == NetSuiteErrorKind.NotFound)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- レコードの書き込み

        /// <summary>レコードを作り、NetSuite が採番した internal ID を返す。</summary>
        internal async Task<string> CreateRecordAsync(string recordType, byte[] json)
        {
            using var response = await SendAsync(() => CreateWriteRequest(HttpMethod.Post, $"{RestUrl}/record/v1/{Uri.EscapeDataString(recordType)}", json), false);
            var location = response.Headers.Location?.ToString() ?? string.Empty;
            var id = location.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(id))
                throw new NetSuiteException($"NetSuite created a '{recordType}' record but did not return its id.", NetSuiteErrorKind.WriteResultUnknown);
            return Uri.UnescapeDataString(id);
        }

        /// <summary>レコードを更新する。対象が無ければ false。</summary>
        /// <param name="replace">要求に無い行を消す明細の REST 名 (?replace=)。空なら付けない。</param>
        internal async Task<bool> UpdateRecordAsync(string recordType, string id, byte[] json, IReadOnlyCollection<string>? replace = null)
        {
            var url = $"{RestUrl}/record/v1/{Uri.EscapeDataString(recordType)}/{Uri.EscapeDataString(id)}";
            if (replace != null && replace.Count != 0) url += "?replace=" + string.Join(",", replace.Select(Uri.EscapeDataString));
            try
            {
                using var response = await SendAsync(() => CreateWriteRequest(HttpMethod.Patch, url, json), false);
                return true;
            }
            catch (NetSuiteException e) when (e.Kind == NetSuiteErrorKind.NotFound)
            {
                return false;
            }
        }

        /// <summary>レコードを削除する。対象が無ければ false。</summary>
        internal async Task<bool> DeleteRecordAsync(string recordType, string id)
        {
            try
            {
                using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{RestUrl}/record/v1/{Uri.EscapeDataString(recordType)}/{Uri.EscapeDataString(id)}"), false);
                return true;
            }
            catch (NetSuiteException e) when (e.Kind == NetSuiteErrorKind.NotFound)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- 添付ファイル (文書サービスと !attach / !detach・保存検索)

        /// <summary>保存検索の結果を 1 回の要求で読む行数 (テストで小さくする)。</summary>
        internal int SavedSearchPageSize { get; set; } = 1000;

        //NetSuite のページ送りの上限 (1000 ページ)
        const int MaxSavedSearchPages = 1000;

        /// <summary>
        /// 接続の保存検索 (<see cref="NetSuiteConnectionSettings.AttachmentSavedSearchId"/>) を最後のページまで実行し、
        /// <paramref name="recordIds"/> のレコードに付いているファイルを返す (レコードの internal ID → ファイル。保存検索の結果の並び。同じ組は 1 つ)。
        /// 保存検索を設定していなければ空。読めない (HTTP の失敗・結果の形が違う・取引の ID の列が無い) ときは NetSuiteException (0 件として返さない)。
        /// </summary>
        internal async Task<Dictionary<string, List<NetSuiteAttachedFile>>> ReadAttachedFilesAsync(IReadOnlyCollection<string> recordIds)
        {
            var result = new Dictionary<string, List<NetSuiteAttachedFile>>(StringComparer.Ordinal);
            if (!Settings.HasAttachmentSavedSearch || recordIds.Count == 0) return result;
            var targets = recordIds.ToHashSet(StringComparer.Ordinal);
            var seen = new HashSet<(string, string)>();
            var search = Settings.AttachmentSavedSearchId;
            var limit = Math.Max(1, SavedSearchPageSize);
            for (int page = 0, offset = 0; ; page++, offset += limit)
            {
                if (MaxSavedSearchPages <= page)
                    throw new NetSuiteException($"Saved search '{search}' returned more than {MaxSavedSearchPages} pages. Narrow the criteria of the saved search.", NetSuiteErrorKind.InvalidRequest);
                JsonDocument json;
                try
                {
                    using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
                        $"{RestUrl}/query/v1/savedsearch/{Uri.EscapeDataString(search)}/result?limit={limit}&offset={offset}"), true);
                    json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                }
                catch (NetSuiteException e)
                {
                    throw new NetSuiteException($"The attached files could not be read with saved search '{search}'. {e.Message}", e.Kind, e);
                }
                catch (JsonException e)
                {
                    throw new NetSuiteException($"The attached files could not be read with saved search '{search}': the response is not JSON.", NetSuiteErrorKind.ServiceUnavailable, e);
                }
                using (json)
                {
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                        throw new NetSuiteException($"The attached files could not be read with saved search '{search}': the response has no items.", NetSuiteErrorKind.ServiceUnavailable);
                    var count = 0;
                    foreach (var item in items.EnumerateArray())
                    {
                        count++;
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var recordId = FindValue(item, Settings.AttachmentSearchRecordColumn, false);
                        //取引の行には必ず ID がある。無ければ列の名前の設定が違う (黙って 0 件にしない)
                        if (recordId == null)
                            throw new NetSuiteException($"Saved search '{search}' does not return the column '{Settings.AttachmentSearchRecordColumn}' " +
                                $"({nameof(NetSuiteConnectionSettings.AttachmentSearchRecordColumn)} of NetSuite data source '{Settings.Name}').", NetSuiteErrorKind.InvalidRequest);
                        if (!targets.Contains(recordId)) continue;
                        //ファイルの無い行 (保存検索で絞っていない) は飛ばす
                        var fileId = FindValue(item, Settings.AttachmentSearchFileColumn, false);
                        if (!NetSuiteAttachments.IsFileId(fileId) || !seen.Add((recordId, fileId!))) continue;
                        var name = FindValue(item, Settings.AttachmentSearchFileNameColumn, true);
                        var size = string.IsNullOrEmpty(Settings.AttachmentSearchFileSizeColumn) ? null : NetSuiteAttachments.ParseSize(FindValue(item, Settings.AttachmentSearchFileSizeColumn, false));
                        var type = string.IsNullOrEmpty(Settings.AttachmentSearchFileTypeColumn) ? null : FindValue(item, Settings.AttachmentSearchFileTypeColumn, true);
                        if (!result.TryGetValue(recordId, out var files)) result[recordId] = files = new();
                        files.Add(new NetSuiteAttachedFile(fileId!, string.IsNullOrEmpty(name) ? fileId! : name, size, type));
                    }
                    var hasMore = root.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.True;
                    if (!hasMore) return result;
                    //続きがあると言いながら行が無ければ、最後まで読めない
                    if (count == 0)
                        throw new NetSuiteException($"The attached files could not be read with saved search '{search}': the next page could not be read.", NetSuiteErrorKind.ServiceUnavailable);
                }
            }
        }

        //結果の行の列 (名前の大文字・小文字は区別しない)。無い・空なら null
        static string? FindValue(JsonElement item, string column, bool preferName)
        {
            if (string.IsNullOrEmpty(column)) return null;
            if (item.TryGetProperty(column, out var value)) return NetSuiteAttachments.ReadSearchValue(value, preferName);
            foreach (var property in item.EnumerateObject())
            {
                if (string.Equals(property.Name, column, StringComparison.OrdinalIgnoreCase)) return NetSuiteAttachments.ReadSearchValue(property.Value, preferName);
            }
            return null;
        }

        /// <summary>
        /// ファイルを File Cabinet に登録し (POST document/v1/file/ の multipart: attributes と data)、ファイルの internal ID を返す (204 の x-ns-link の末尾)。
        /// 書き込みなので、結果が分からない失敗はやり直さない (WriteResultUnknown)。10 MB を超えて断られたら (413) 分かるエラーにする。
        /// </summary>
        internal async Task<string> UploadFileAsync(string folderId, string name, string contentType, byte[] content)
        {
            var attributes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            {
                ["isLink"] = false,
                ["fileName"] = name,
                ["folder"] = new Dictionary<string, string> { ["id"] = folderId },
            });
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{RestUrl}/document/v1/file/");
                    var multipart = new MultipartFormDataContent();
                    var attributesPart = new ByteArrayContent(attributes);
                    attributesPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    multipart.Add(attributesPart, "attributes");
                    var dataPart = new ByteArrayContent(content);
                    dataPart.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type) ? type : new MediaTypeHeaderValue(NetSuiteAttachments.DefaultContentType);
                    //本当の名前は attributes の fileName。Content-Disposition に書けない文字 (" \ 制御文字) は置き換える (そのままだとヘッダーの組み立てで例外になる)
                    multipart.Add(dataPart, "data", ToHeaderFileName(name));
                    request.Content = multipart;
                    return request;
                }, false);
            }
            catch (NetSuiteException e) when (e.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            {
                throw new NetSuiteException($"NetSuite did not accept '{name}' because it is too large (NetSuite accepts up to 10 MB per file).",
                    NetSuiteErrorKind.InvalidRequest, HttpStatusCode.RequestEntityTooLarge, e.ErrorCode, e.ErrorPath);
            }
            using (response)
            {
                var link = response.Headers.TryGetValues("x-ns-link", out var values) ? values.FirstOrDefault() : null;
                var id = (link ?? string.Empty).Trim().TrimEnd('/').Split('/').LastOrDefault();
                if (!NetSuiteAttachments.IsFileId(id))
                    throw new NetSuiteException($"NetSuite accepted the file '{name}' but did not return its id. Whether it was registered is unknown. Check the File Cabinet before retrying.",
                        NetSuiteErrorKind.WriteResultUnknown);
                return id!;
            }
        }

        /// <summary>multipart の data のファイル名: " と \ と制御文字を _ にした名前。</summary>
        internal static string ToHeaderFileName(string name)
            => new(name.Select(c => c == '"' || c == '\\' || char.IsControl(c) ? '_' : c).ToArray());

        /// <summary>登録済みのファイルをレコードに添付する (POST record/v1/{type}/{id}/!attach/file/{fileId}・本文なし)。</summary>
        internal async Task AttachFileAsync(string recordType, string recordId, string fileId)
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post,
                $"{RestUrl}/record/v1/{Uri.EscapeDataString(recordType)}/{Uri.EscapeDataString(recordId)}/!attach/file/{Uri.EscapeDataString(fileId)}"), false);
        }

        /// <summary>ファイルをレコードから外す (POST record/v1/{type}/{id}/!detach/file/{fileId}・本文なし)。File Cabinet のファイルは残る。</summary>
        internal async Task DetachFileAsync(string recordType, string recordId, string fileId)
        {
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post,
                $"{RestUrl}/record/v1/{Uri.EscapeDataString(recordType)}/{Uri.EscapeDataString(recordId)}/!detach/file/{Uri.EscapeDataString(fileId)}"), false);
        }

        /// <summary>ファイルの中身を読む (GET document/v1/file/{id}/content)。読めなければ (403・404・10 MB 超の 413 など) NetSuiteException。</summary>
        internal async Task<byte[]> DownloadFileAsync(string fileId)
        {
            using var response = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{RestUrl}/document/v1/file/{Uri.EscapeDataString(fileId)}/content");
                request.Headers.Accept.ParseAdd("application/octet-stream");
                return request;
            }, true);
            return await response.Content.ReadAsByteArrayAsync();
        }

        static HttpRequestMessage CreateWriteRequest(HttpMethod method, string url, byte[] json)
        {
            var request = new HttpRequestMessage(method, url);
            //知らない項目名を黙って無視させない (無視されると「保存できたのに入っていない」になる)
            request.Headers.TryAddWithoutValidation("X-NetSuite-PropertyNameValidation", "Error");
            request.Headers.TryAddWithoutValidation("X-NetSuite-PropertyValueValidation", "Error");
            request.Content = new ByteArrayContent(json);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return request;
        }

        // ---------------------------------------------------------------- 送信

        const int MaxAttempts = 4;

        /// <summary>
        /// 認証を付けて送る。読み出し (isRead) は一時的な失敗をやり直す。書き込みは、実行されていないと分かる失敗 (401・429) だけやり直し、
        /// 送ったあと応答を受け取れなかった・サーバー側の失敗 (503 以外の 5xx) が返ったら「結果不明」にする (同じ作成をもう一度送らない)。
        /// </summary>
        async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, bool isRead)
        {
            await _concurrency.WaitAsync();
            try
            {
                var refreshedToken = false;
                for (var attempt = 1; ; attempt++)
                {
                    var token = await GetAccessTokenAsync();
                    HttpResponseMessage response;
                    using (var request = createRequest())
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        try
                        {
                            response = await _http.SendAsync(request);
                        }
                        catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
                        {
                            if (!isRead)
                                throw new NetSuiteException("The request was sent to NetSuite but no response was received. Whether it was processed is unknown. Check the record in NetSuite before retrying.", NetSuiteErrorKind.WriteResultUnknown, e);
                            if (attempt < MaxAttempts)
                            {
                                await Delay(GetBackoff(attempt, null));
                                continue;
                            }
                            throw new NetSuiteException("Could not reach NetSuite: " + e.Message, NetSuiteErrorKind.ServiceUnavailable, e);
                        }
                    }

                    if (response.IsSuccessStatusCode) return response;

                    using (response)
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshedToken)
                        {
                            //トークンが切れた・失効した。取り直して 1 回だけやり直す
                            refreshedToken = true;
                            InvalidateAccessToken(token);
                            continue;
                        }
                        var isBusy = response.StatusCode == HttpStatusCode.TooManyRequests;
                        var isTemporary = response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                        if ((isBusy || (isRead && isTemporary)) && attempt < MaxAttempts)
                        {
                            await Delay(GetBackoff(attempt, response.Headers.RetryAfter?.Delta));
                            continue;
                        }
                        var exception = await CreateExceptionAsync(response);
                        //書き込みへのサーバー側の失敗 (503 = 受け付けていない、を除く 5xx) は、実行されたかどうかが分からない
                        if (!isRead && 500 <= (int)response.StatusCode && response.StatusCode != HttpStatusCode.ServiceUnavailable)
                            throw new NetSuiteException(exception.Message + " Whether the write was processed is unknown. Check the record in NetSuite before retrying.",
                                NetSuiteErrorKind.WriteResultUnknown, response.StatusCode, exception.ErrorCode, exception.ErrorPath);
                        throw exception;
                    }
                }
            }
            finally
            {
                _concurrency.Release();
            }
        }

        static TimeSpan GetBackoff(int attempt, TimeSpan? retryAfter)
        {
            if (retryAfter != null && TimeSpan.Zero < retryAfter && retryAfter < TimeSpan.FromSeconds(60)) return retryAfter.Value;
            return TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        }

        static async Task<NetSuiteException> CreateExceptionAsync(HttpResponseMessage response)
        {
            var body = string.Empty;
            try { body = await response.Content.ReadAsStringAsync(); } catch { }

            string detail = string.Empty, code = string.Empty, path = string.Empty;
            try
            {
                using var json = JsonDocument.Parse(body);
                //ゲートウェイなどが返す、オブジェクトでない本文は読まない (ステータスだけで分類する)
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                if (json.RootElement.TryGetProperty("o:errorDetails", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    var messages = new List<string>();
                    foreach (var e in details.EnumerateArray())
                    {
                        if (e.TryGetProperty("detail", out var d)) messages.Add(d.GetString() ?? string.Empty);
                        if (string.IsNullOrEmpty(code) && e.TryGetProperty("o:errorCode", out var c)) code = c.GetString() ?? string.Empty;
                        if (string.IsNullOrEmpty(path) && e.TryGetProperty("o:errorPath", out var p)) path = p.GetString() ?? string.Empty;
                    }
                    detail = string.Join(" ", messages.Where(e => !string.IsNullOrEmpty(e)));
                }
                if (string.IsNullOrEmpty(detail) && json.RootElement.TryGetProperty("title", out var title)) detail = title.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(detail) && json.RootElement.TryGetProperty("error_description", out var description)) detail = description.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(detail) && json.RootElement.TryGetProperty("error", out var error)) detail = error.ToString();
            }
            catch (Exception e) when (e is JsonException || e is InvalidOperationException) { }

            var kind = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => NetSuiteErrorKind.Authentication,
                HttpStatusCode.Forbidden => NetSuiteErrorKind.PermissionDenied,
                HttpStatusCode.NotFound => NetSuiteErrorKind.NotFound,
                HttpStatusCode.TooManyRequests => NetSuiteErrorKind.RateLimited,
                _ => (int)response.StatusCode >= 500 ? NetSuiteErrorKind.ServiceUnavailable : NetSuiteErrorKind.InvalidRequest,
            };
            if (string.IsNullOrEmpty(detail)) detail = $"HTTP {(int)response.StatusCode}";
            return new NetSuiteException($"NetSuite: {detail}", kind, response.StatusCode, code, path);
        }

        // ---------------------------------------------------------------- 認証 (OAuth 2.0 Client Credentials)

        void InvalidateAccessToken(string token)
        {
            if (_accessToken == token) _accessToken = string.Empty;
        }

        async Task<string> GetAccessTokenAsync()
        {
            var token = _accessToken;
            if (!string.IsNullOrEmpty(token) && UtcNow() < _accessTokenExpiresAt) return token;

            await _tokenLock.WaitAsync();
            try
            {
                if (!string.IsNullOrEmpty(_accessToken) && UtcNow() < _accessTokenExpiresAt) return _accessToken;

                var tokenUrl = $"{RestUrl}/auth/oauth2/v1/token";
                using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl);
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                    ["client_assertion"] = CreateClientAssertion(tokenUrl),
                });

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request);
                }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
                {
                    throw new NetSuiteException("Could not reach the NetSuite token endpoint: " + e.Message, NetSuiteErrorKind.ServiceUnavailable, e);
                }
                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var inner = await CreateExceptionAsync(response);
                        throw new NetSuiteException("NetSuite authentication failed. " + inner.Message, NetSuiteErrorKind.Authentication, response.StatusCode, inner.ErrorCode, inner.ErrorPath);
                    }
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    var accessToken = json.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrEmpty(accessToken))
                        throw new NetSuiteException("NetSuite authentication failed. The token response has no access_token.", NetSuiteErrorKind.Authentication);
                    var expiresIn = 3600;
                    if (json.RootElement.TryGetProperty("expires_in", out var e))
                    {
                        if (e.ValueKind == JsonValueKind.Number) expiresIn = e.GetInt32();
                        else if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var parsed)) expiresIn = parsed;
                    }
                    //期限の少し前に取り直す
                    _accessTokenExpiresAt = UtcNow().AddSeconds(Math.Max(30, expiresIn - 120));
                    _accessToken = accessToken;
                    return accessToken;
                }
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        //署名した JWT (client assertion)。秘密鍵で署名し、NetSuite に登録した証明書 (kid) で検証される
        internal string CreateClientAssertion(string tokenUrl)
        {
            var algorithm = Settings.Algorithm.ToUpperInvariant();
            var now = new DateTimeOffset(DateTime.SpecifyKind(UtcNow(), DateTimeKind.Utc)).ToUnixTimeSeconds();

            var header = JsonSerializer.Serialize(new Dictionary<string, object> { ["typ"] = "JWT", ["alg"] = algorithm, ["kid"] = Settings.CertificateId });
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["iss"] = Settings.ClientId,
                ["scope"] = Settings.Scope.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries),
                ["aud"] = tokenUrl,
                ["iat"] = now,
                ["exp"] = now + 300,
            });
            var signingInput = Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
            var data = Encoding.ASCII.GetBytes(signingInput);

            var hash = (algorithm.Length < 3 ? string.Empty : algorithm[2..]) switch
            {
                "256" => HashAlgorithmName.SHA256,
                "384" => HashAlgorithmName.SHA384,
                "512" => HashAlgorithmName.SHA512,
                _ => throw new NetSuiteException($"NetSuite data source '{Settings.Name}': unsupported Algorithm '{Settings.Algorithm}'."),
            };
            var pem = GetPrivateKeyPem();
            byte[] signature;
            try
            {
                if (algorithm.StartsWith("PS"))
                {
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(pem);
                    signature = rsa.SignData(data, hash, RSASignaturePadding.Pss);
                }
                else if (algorithm.StartsWith("ES"))
                {
                    using var ecdsa = ECDsa.Create();
                    ecdsa.ImportFromPem(pem);
                    signature = ecdsa.SignData(data, hash);
                }
                else
                {
                    throw new NetSuiteException($"NetSuite data source '{Settings.Name}': unsupported Algorithm '{Settings.Algorithm}'.");
                }
            }
            catch (Exception e) when (e is CryptographicException || e is ArgumentException)
            {
                //鍵の中身は例外に出さない
                throw new NetSuiteException($"NetSuite data source '{Settings.Name}': the private key could not be used with {algorithm}.", NetSuiteErrorKind.Authentication);
            }
            return signingInput + "." + Base64Url(signature);
        }

        string GetPrivateKeyPem()
        {
            if (!string.IsNullOrWhiteSpace(Settings.PrivateKey)) return Settings.PrivateKey.Replace("\\n", "\n");
            try
            {
                return File.ReadAllText(Settings.PrivateKeyPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new NetSuiteException($"NetSuite data source '{Settings.Name}': the private key file could not be read.", NetSuiteErrorKind.Authentication);
            }
        }

        static string Base64Url(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
