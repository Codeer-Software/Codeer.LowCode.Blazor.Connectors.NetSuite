using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NetSuite.Mock
{
    /// <summary>File Cabinet のファイル 1 つ。</summary>
    public sealed class MockFile
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string FolderId { get; init; } = string.Empty;
        public string FileType { get; init; } = string.Empty;
        public string ContentType { get; init; } = "application/octet-stream";
        public byte[] Content { get; init; } = Array.Empty<byte>();
        /// <summary>中身を持たない大きなファイルの大きさ (10 MB を超える既存のファイルの代わり)。null なら中身の大きさ。</summary>
        public long? DeclaredSize { get; init; }
        public long Size => DeclaredSize ?? Content.LongLength;
    }

    /// <summary>File Cabinet (文書サービス)・ファイルの添付 (!attach / !detach)・保存検索の実行。</summary>
    public sealed partial class MockNetSuite
    {
        /// <summary>文書サービスの 1 回の要求で送れる大きさの上限 (10 MB)。</summary>
        public const long MaxDocumentSize = 10L * 1024 * 1024;

        sealed class MockFolder
        {
            public string Id { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            /// <summary>連携用のロールが読めないフォルダ (中身の読み出しが 403)。</summary>
            public bool Restricted { get; init; }
        }

        /// <summary>保存検索 (添付ファイルの一覧)。結果の列の名前を持つ。</summary>
        sealed class MockSavedSearch
        {
            public string RecordColumn { get; init; } = "internalid";
            public string FileColumn { get; init; } = string.Empty;
            public string NameColumn { get; init; } = string.Empty;
            public string SizeColumn { get; init; } = string.Empty;
            public string TypeColumn { get; init; } = string.Empty;
        }

        readonly Dictionary<string, MockFolder> _folders = new(StringComparer.Ordinal);
        readonly Dictionary<string, MockFile> _files = new(StringComparer.Ordinal);
        //添付 (レコード名の小文字, レコードの internal ID, ファイルの internal ID)。添付した順
        readonly List<(string Record, string Id, string File)> _attachments = new();
        readonly Dictionary<string, MockSavedSearch> _savedSearches = new(StringComparer.OrdinalIgnoreCase);
        long _lastFileId;

        /// <summary>mock-data.json の fileCabinet と savedSearches を読む。</summary>
        void LoadFileCabinet(JsonElement root)
        {
            if (root.TryGetProperty("fileCabinet", out var cabinet) && cabinet.ValueKind == JsonValueKind.Object)
            {
                if (cabinet.TryGetProperty("folders", out var folders) && folders.ValueKind == JsonValueKind.Array)
                {
                    foreach (var folder in folders.EnumerateArray())
                    {
                        var id = Text(folder, "id") ?? throw new InvalidDataException("mock data: a folder of fileCabinet has no id.");
                        _folders[id] = new MockFolder
                        {
                            Id = id,
                            Name = Text(folder, "name") ?? id,
                            Restricted = folder.TryGetProperty("restricted", out var r) && r.ValueKind == JsonValueKind.True,
                        };
                    }
                }
                if (cabinet.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var file in files.EnumerateArray())
                    {
                        var id = Text(file, "id") ?? throw new InvalidDataException("mock data: a file of fileCabinet has no id.");
                        var folder = Text(file, "folder") ?? string.Empty;
                        if (!_folders.ContainsKey(folder)) throw new InvalidDataException($"mock data: the folder of file {id} is not in fileCabinet.folders.");
                        long? size = file.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
                        AddFileCore(id, Text(file, "name") ?? id, folder, Encoding.UTF8.GetBytes(Text(file, "text") ?? string.Empty),
                            Text(file, "fileType") ?? "PLAINTEXT", Text(file, "contentType") ?? "text/plain", size);
                    }
                }
                if (cabinet.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
                {
                    foreach (var attachment in attachments.EnumerateArray())
                    {
                        var record = Text(attachment, "record") ?? string.Empty;
                        var id = Text(attachment, "id") ?? string.Empty;
                        var file = Text(attachment, "file") ?? string.Empty;
                        var type = FindRecord(record) ?? throw new InvalidDataException($"mock data: the record '{record}' of an attachment is not defined.");
                        if (!Exists(type, id)) throw new InvalidDataException($"mock data: the record {record} {id} of an attachment does not exist.");
                        if (!_files.ContainsKey(file)) throw new InvalidDataException($"mock data: the file {file} of an attachment is not in fileCabinet.files.");
                        _attachments.Add((type.Name.ToLowerInvariant(), id, file));
                    }
                }
            }
            if (root.TryGetProperty("savedSearches", out var searches) && searches.ValueKind == JsonValueKind.Object)
            {
                foreach (var search in searches.EnumerateObject())
                {
                    if (!search.Value.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException($"mock data: saved search '{search.Name}' has no columns.");
                    _savedSearches[search.Name] = new MockSavedSearch
                    {
                        RecordColumn = Text(columns, "record") ?? "internalid",
                        FileColumn = Text(columns, "file") ?? throw new InvalidDataException($"mock data: saved search '{search.Name}' has no file column."),
                        NameColumn = Text(columns, "name") ?? throw new InvalidDataException($"mock data: saved search '{search.Name}' has no name column."),
                        SizeColumn = Text(columns, "size") ?? string.Empty,
                        TypeColumn = Text(columns, "type") ?? string.Empty,
                    };
                }
            }
        }

        static string? Text(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
        }

        MockFile AddFileCore(string id, string name, string folderId, byte[] content, string fileType, string contentType, long? declaredSize)
        {
            var file = new MockFile { Id = id, Name = name, FolderId = folderId, Content = content, FileType = fileType, ContentType = contentType, DeclaredSize = declaredSize };
            _files[id] = file;
            if (long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && _lastFileId < n) _lastFileId = n;
            return file;
        }

        // ---------------------------------------------------------------- テスト・手元の確かめ用

        /// <summary>フォルダを足す (restricted = 連携用のロールが中身を読めない)。</summary>
        public void AddFolder(string id, string name, bool restricted = false)
        {
            lock (_lock) _folders[id] = new MockFolder { Id = id, Name = name, Restricted = restricted };
        }

        /// <summary>File Cabinet にファイルを足して internal ID を返す (declaredSize = 中身を持たない大きなファイルの大きさ)。</summary>
        public string AddFile(string name, string folderId, byte[] content, string fileType = "PLAINTEXT", long? declaredSize = null)
        {
            lock (_lock)
            {
                if (!_folders.ContainsKey(folderId)) throw new InvalidOperationException($"Folder {folderId} does not exist.");
                return AddFileCore((++_lastFileId).ToString(CultureInfo.InvariantCulture), name, folderId, content, fileType, "application/octet-stream", declaredSize).Id;
            }
        }

        /// <summary>レコードにファイルを添付する (NetSuite の画面から添付したときの代わり)。</summary>
        public void Attach(string recordType, string id, string fileId)
        {
            lock (_lock)
            {
                var record = FindRecord(recordType) ?? throw new InvalidOperationException($"Record type '{recordType}' does not exist.");
                if (!_files.ContainsKey(fileId)) throw new InvalidOperationException($"File {fileId} does not exist.");
                var key = (record.Name.ToLowerInvariant(), id, fileId);
                if (!_attachments.Contains(key)) _attachments.Add(key);
            }
        }

        /// <summary>レコードに付いているファイルの internal ID (添付した順)。</summary>
        public List<string> GetAttachedFileIds(string recordType, string id)
        {
            lock (_lock)
            {
                var name = (FindRecord(recordType)?.Name ?? recordType).ToLowerInvariant();
                return _attachments.Where(e => e.Record == name && e.Id == id).Select(e => e.File).ToList();
            }
        }

        /// <summary>File Cabinet のファイル (無ければ null)。</summary>
        public MockFile? FindFile(string id)
        {
            lock (_lock) return _files.TryGetValue(id, out var file) ? file : null;
        }

        /// <summary>File Cabinet のファイルの数。</summary>
        public int FileCount
        {
            get { lock (_lock) return _files.Count; }
        }

        //レコードを消したら、そのレコードへの添付も無くなる (ファイルは残る)
        void RemoveAttachments(string recordName, string id)
            => _attachments.RemoveAll(e => e.Record == recordName.ToLowerInvariant() && e.Id == id);

        // ---------------------------------------------------------------- 文書サービス (document/v1/file)

        static MockResponse FileNotFound(string id) => Error(404, "NONEXISTENT_ID", $"The file {id} does not exist.");

        /// <summary>
        /// ファイルの登録 (POST document/v1/file/。multipart の attributes (JSON) と data)。204 と x-ns-link (末尾がファイルの internal ID)。
        /// </summary>
        public MockResponse CreateFile(string? attributes, string? dataFileName, string? dataContentType, byte[]? data, string baseUrl)
        {
            if (attributes == null) return Error(400, "INVALID_CONTENT", "Only multipart requests with the parts 'attributes' and 'data' are supported.");
            string? fileName;
            string? folderId;
            try
            {
                using var json = JsonDocument.Parse(attributes);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Error(400, "INVALID_CONTENT", "The attributes must be a JSON object.");
                if (root.TryGetProperty("isLink", out var isLink) && isLink.ValueKind == JsonValueKind.True)
                    return Error(400, "INVALID_CONTENT", "This mock does not support link files (isLink: true).");
                fileName = Text(root, "fileName");
                folderId = root.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.Object ? Text(folder, "id") : null;
            }
            catch (JsonException)
            {
                return Error(400, "INVALID_CONTENT", "The attributes are not valid JSON.");
            }
            if (string.IsNullOrEmpty(fileName)) return Error(400, "USER_ERROR", "Please enter value(s) for: Name", "fileName");
            if (string.IsNullOrEmpty(folderId)) return Error(400, "USER_ERROR", "Please enter value(s) for: Folder", "folder");
            if (data == null) return Error(400, "INVALID_CONTENT", "The part 'data' (the content of the file) is missing.");
            if (MaxDocumentSize < data.LongLength) return Error(413, "CONTENT_TOO_LARGE", "The file is larger than 10 MB.");
            lock (_lock)
            {
                if (!_folders.TryGetValue(folderId, out var target)) return Error(400, "INVALID_KEY_OR_REF", $"Invalid folder reference key {folderId}.", "folder");
                if (target.Restricted) return Error(403, "INSUFFICIENT_PERMISSION", "You do not have permission to add files to this folder.");
                var file = AddFileCore((++_lastFileId).ToString(CultureInfo.InvariantCulture), fileName, folderId, data, FileTypeOf(fileName),
                    string.IsNullOrEmpty(dataContentType) ? "application/octet-stream" : dataContentType, null);
                var response = NoContent();
                response.Headers["x-ns-link"] = $"{baseUrl}/services/rest/document/v1/file/{file.Id}";
                return response;
            }
        }

        static string FileTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".pdf" => "PDF",
            ".txt" => "PLAINTEXT",
            ".csv" => "CSV",
            ".png" => "PNGIMAGE",
            ".jpg" or ".jpeg" => "JPGIMAGE",
            ".zip" => "ZIP",
            ".xlsx" or ".xls" => "EXCEL",
            ".docx" or ".doc" => "WORD",
            _ => "MISCBINARY",
        };

        /// <summary>ファイルの属性 (GET document/v1/file/{id})。中身は含まない (content のリンクだけ)。</summary>
        public MockResponse GetFile(string id, string baseUrl)
        {
            lock (_lock)
            {
                if (!_files.TryGetValue(id, out var file)) return FileNotFound(id);
                if (_folders[file.FolderId].Restricted) return Error(403, "INSUFFICIENT_PERMISSION", "You do not have permission to view this file.");
                return Json(200, w =>
                {
                    w.WriteStartObject();
                    w.WriteString("id", file.Id);
                    w.WriteString("name", file.Name);
                    w.WriteBoolean("isLink", false);
                    w.WriteString("fileType", file.FileType);
                    w.WriteNumber("fileSize", file.Size);
                    w.WriteStartObject("folder");
                    w.WriteStartArray("links");
                    w.WriteStartObject();
                    w.WriteString("rel", "self");
                    w.WriteString("href", $"{baseUrl}/services/rest/document/v1/folder/{file.FolderId}");
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteString("id", file.FolderId);
                    w.WriteEndObject();
                    w.WriteStartObject("content");
                    w.WriteStartArray("links");
                    w.WriteStartObject();
                    w.WriteString("rel", "self");
                    w.WriteString("href", $"{baseUrl}/services/rest/document/v1/file/{file.Id}/content");
                    w.WriteString("mediaType", "application/octet-stream");
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                    w.WriteEndObject();
                });
            }
        }

        /// <summary>ファイルの中身 (GET document/v1/file/{id}/content)。無い・中身が無い = 404、10 MB 超 = 413、読めないフォルダ = 403。</summary>
        public MockResponse GetFileContent(string id)
        {
            lock (_lock)
            {
                if (!_files.TryGetValue(id, out var file)) return FileNotFound(id);
                if (_folders[file.FolderId].Restricted) return Error(403, "INSUFFICIENT_PERMISSION", "You do not have permission to view this file.");
                if (MaxDocumentSize < file.Size) return Error(413, "CONTENT_TOO_LARGE", "The file is larger than 10 MB.");
                if (file.Content.Length == 0) return Error(404, "NONEXISTENT_ID", $"The file {id} has no content.");
                return new MockResponse { Status = 200, Data = file.Content, ContentType = "application/octet-stream" };
            }
        }

        // ---------------------------------------------------------------- 添付 (record/v1/{type}/{id}/!attach|!detach/file/{fileId})

        /// <summary>ファイルの添付・外す。成功は 204。対象のレコード・ファイルが無ければ 404。ファイル以外の添付は扱わない。</summary>
        public MockResponse AttachOrDetach(string recordType, string id, string target, string fileId, bool attach)
        {
            var record = FindRecord(recordType);
            if (record == null) return RecordNotFound(recordType);
            if (!string.Equals(target, "file", StringComparison.OrdinalIgnoreCase))
                return Error(400, "INVALID_REQUEST", $"This mock attaches and detaches files only (not '{target}').");
            lock (_lock)
            {
                if (!IsId(id) || !Exists(record, id)) return IdNotFound(record.Name, id);
                if (!_files.ContainsKey(fileId)) return FileNotFound(fileId);
                var key = (record.Name.ToLowerInvariant(), id, fileId);
                if (attach)
                {
                    if (!_attachments.Contains(key)) _attachments.Add(key);
                }
                else
                {
                    _attachments.Remove(key);
                }
                return NoContent();
            }
        }

        // ---------------------------------------------------------------- 保存検索 (query/v1/savedsearch/{id}/result)

        /// <summary>
        /// 保存検索の実行。モックの保存検索は「取引に付いているファイル」の 1 種類だけ (1 行 = 取引とファイルの組。ファイルの無い取引は出さない)。
        /// 列の名前は mock-data.json の savedSearches の columns。ページ送りは limit / offset (offset は limit の倍数)。
        /// </summary>
        public MockResponse RunSavedSearch(string searchId, string? limitText, string? offsetText, string baseUrl)
        {
            var limit = MaxLimit;
            if (!string.IsNullOrEmpty(limitText) && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || MaxLimit < limit))
                return Error(400, "INVALID_PARAMETER", $"Invalid value for the parameter 'limit'. The value must be between 1 and {MaxLimit}.", "limit");
            var offset = 0;
            if (!string.IsNullOrEmpty(offsetText) && !int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
                return Error(400, "INVALID_PARAMETER", "Invalid value for the parameter 'offset'.", "offset");
            if (offset % limit != 0)
                return Error(400, "INVALID_PARAMETER", "Invalid value for the parameter 'offset'. The offset must be a multiple of the limit.", "offset");

            lock (_lock)
            {
                if (!_savedSearches.TryGetValue(searchId, out var search)) return Error(404, "NONEXISTENT_ID", $"The saved search {searchId} does not exist.");
                var transactions = _records.Where(e => e.IsTransaction).Select(e => e.Name.ToLowerInvariant()).ToHashSet();
                var rows = _attachments.Where(e => transactions.Contains(e.Record))
                    .OrderBy(e => long.Parse(e.Id, CultureInfo.InvariantCulture)).ThenBy(e => long.Parse(e.File, CultureInfo.InvariantCulture)).ToList();
                var page = rows.Skip(offset).Take(limit).ToList();
                var self = $"{baseUrl}/services/rest/query/v1/savedsearch/{searchId}/result";
                var hasMore = offset + page.Count < rows.Count;
                return Json(200, w =>
                {
                    w.WriteStartObject();
                    w.WriteStartArray("links");
                    w.WriteStartObject();
                    w.WriteString("rel", "self");
                    w.WriteString("href", $"{self}?limit={limit}&offset={offset}");
                    w.WriteString("method", "GET");
                    w.WriteEndObject();
                    if (hasMore)
                    {
                        w.WriteStartObject();
                        w.WriteString("rel", "next");
                        w.WriteString("href", $"{self}?limit={limit}&offset={offset + limit}");
                        w.WriteString("method", "GET");
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteNumber("count", page.Count);
                    w.WriteBoolean("hasMore", hasMore);
                    w.WriteStartArray("items");
                    foreach (var row in page)
                    {
                        var file = _files[row.File];
                        w.WriteStartObject();
                        w.WriteStartArray("links");
                        w.WriteEndArray();
                        w.WriteString(search.RecordColumn, row.Id);
                        w.WriteString(search.FileColumn, file.Id);
                        w.WriteString(search.NameColumn, file.Name);
                        if (!string.IsNullOrEmpty(search.SizeColumn)) w.WriteNumber(search.SizeColumn, file.Size);
                        if (!string.IsNullOrEmpty(search.TypeColumn))
                        {
                            //種類は選択の値の形 ({id, refName})
                            w.WriteStartObject(search.TypeColumn);
                            w.WriteStartArray("links");
                            w.WriteEndArray();
                            w.WriteString("id", file.FileType);
                            w.WriteString("refName", file.FileType);
                            w.WriteEndObject();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteNumber("offset", offset);
                    w.WriteNumber("totalResults", rows.Count);
                    w.WriteEndObject();
                });
            }
        }
    }
}
