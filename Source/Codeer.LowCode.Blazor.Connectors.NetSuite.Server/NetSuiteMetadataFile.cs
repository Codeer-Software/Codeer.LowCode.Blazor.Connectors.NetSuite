using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    /// <summary>
    /// レコードの定義 (メタ情報) を保存したファイル。デザイナはこのファイルから表の定義を読む (NetSuite に取りに行かない)。
    /// データソースごとに 1 つ、デザインのプロジェクトのフォルダに置く (netsuite.&lt;データソース名&gt;.metadata.json)。接続の情報は入らない。
    /// </summary>
    public static class NetSuiteMetadataFile
    {
        class RecordData
        {
            public string Name { get; set; } = string.Empty;
            /// <summary>明細の表なら親のレコード名。レコードなら null (書かない)。</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Parent { get; set; }
            /// <summary>明細の表なら、親のレコードでの明細の REST 名。</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string? Sublist { get; set; }
            public List<FieldData> Fields { get; set; } = new();
        }

        class FieldData
        {
            public string Name { get; set; } = string.Empty;
            public NetSuiteFieldKind Kind { get; set; }
            public bool Required { get; set; }
            /// <summary>明細 (Sublist) の行の項目。明細でなければ null (書かない)。</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<FieldData>? Fields { get; set; }
        }

        class FileData
        {
            public List<RecordData> Records { get; set; } = new();
        }

        static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
        };

        //キーはどちらもフルパス
        static readonly ConcurrentDictionary<string, (DateTime LastWriteTimeUtc, List<NetSuiteRecordDefinition> Records)> _cache = new(StringComparer.OrdinalIgnoreCase);
        static readonly ConcurrentDictionary<string, SemaphoreSlim> _updateLocks = new(StringComparer.OrdinalIgnoreCase);

        public static string GetPath(string directory, string dataSourceName)
        {
            var name = string.Concat(dataSourceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(directory, $"netsuite.{name}.metadata.json");
        }

        /// <summary>
        /// NetSuite から Records に書かれたレコードの定義を取り直して、ファイルに保存する。
        /// 同じフォルダの一時ファイルに書いて読み直してから置き換える (途中で失敗しても前のファイルはそのまま)。同じファイルへの更新は順に行う (プロセスの中だけ)。
        /// </summary>
        public static async Task UpdateAsync(NetSuiteConnection connection, string directory)
        {
            var path = Path.GetFullPath(GetPath(directory, connection.Settings.Name));
            var gate = _updateLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                var records = await connection.ReloadRecordDefinitionsAsync();
                var data = new FileData
                {
                    Records = records.OrderBy(e => e.Name, StringComparer.Ordinal).Select(record => new RecordData
                    {
                        Name = record.Name,
                        Parent = record.LineParent,
                        Sublist = record.LineSublist,
                        Fields = record.Fields.Select(ToFieldData).ToList(),
                    }).ToList(),
                };
                var temp = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(data, _jsonOptions));
                    Read(temp);
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                //置き換えたファイルの最終更新時刻が前と同じになっても古い定義を返さないように
                _cache.TryRemove(path, out _);
            }
            finally
            {
                gate.Release();
            }
        }

        static FieldData ToFieldData(NetSuiteFieldDefinition field) => new()
        {
            Name = field.RestName,
            Kind = field.Kind,
            Required = !field.IsNullable,
            Fields = field.Kind == NetSuiteFieldKind.Sublist ? field.SublistFields.Select(ToFieldData).ToList() : null,
        };

        static NetSuiteFieldDefinition FromFieldData(FieldData field) => new()
        {
            RestName = field.Name,
            Column = field.Name.ToLowerInvariant(),
            Kind = field.Kind,
            IsNullable = !field.Required,
            SublistFields = field.Fields?.Select(FromFieldData).ToList() ?? new(),
        };

        /// <summary>保存してある定義。ファイルが無ければ null。</summary>
        internal static List<NetSuiteRecordDefinition>? Load(string directory, string dataSourceName)
        {
            var path = Path.GetFullPath(GetPath(directory, dataSourceName));
            if (!File.Exists(path)) return null;
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var cached) && cached.LastWriteTimeUtc == lastWriteTimeUtc) return cached.Records;

            var records = Read(path);
            _cache[path] = (lastWriteTimeUtc, records);
            return records;
        }

        static List<NetSuiteRecordDefinition> Read(string path)
        {
            FileData? data;
            try
            {
                //読んでいる間も更新の置き換え (File.Move) を止めない
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                data = JsonSerializer.Deserialize<FileData>(stream, _jsonOptions);
            }
            catch (JsonException e)
            {
                throw new NetSuiteException($"The NetSuite metadata file '{Path.GetFileName(path)}' cannot be read. Update the NetSuite metadata again.", NetSuiteErrorKind.NotSupported, e);
            }
            var records = new List<NetSuiteRecordDefinition>();
            foreach (var record in data?.Records ?? new())
            {
                var definition = new NetSuiteRecordDefinition { Name = record.Name, LineParent = record.Parent, LineSublist = record.Sublist };
                foreach (var field in record.Fields) definition.Fields.Add(FromFieldData(field));
                records.Add(definition);
            }
            return records;
        }
    }
}
