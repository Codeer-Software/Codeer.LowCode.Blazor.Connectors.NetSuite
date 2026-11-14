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
            public List<FieldData> Fields { get; set; } = new();
        }

        class FieldData
        {
            public string Name { get; set; } = string.Empty;
            public NetSuiteFieldKind Kind { get; set; }
            public bool Required { get; set; }
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

        static readonly ConcurrentDictionary<string, (DateTime LastWriteTimeUtc, List<NetSuiteRecordDefinition> Records)> _cache = new(StringComparer.OrdinalIgnoreCase);

        public static string GetPath(string directory, string dataSourceName)
        {
            var name = string.Concat(dataSourceName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            return Path.Combine(directory, $"netsuite.{name}.metadata.json");
        }

        /// <summary>
        /// NetSuite から Records に書かれたレコードの定義を取り直して、ファイルに保存する。
        /// </summary>
        public static async Task UpdateAsync(NetSuiteConnection connection, string directory)
        {
            var records = await connection.ReloadRecordDefinitionsAsync();
            var data = new FileData
            {
                Records = records.OrderBy(e => e.Name, StringComparer.Ordinal).Select(record => new RecordData
                {
                    Name = record.Name,
                    Fields = record.Fields.Select(field => new FieldData { Name = field.RestName, Kind = field.Kind, Required = !field.IsNullable }).ToList(),
                }).ToList(),
            };
            await File.WriteAllTextAsync(GetPath(directory, connection.Settings.Name), JsonSerializer.Serialize(data, _jsonOptions));
        }

        /// <summary>保存してある定義。ファイルが無ければ null。</summary>
        internal static List<NetSuiteRecordDefinition>? Load(string directory, string dataSourceName)
        {
            var path = GetPath(directory, dataSourceName);
            if (!File.Exists(path)) return null;
            var lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var cached) && cached.LastWriteTimeUtc == lastWriteTimeUtc) return cached.Records;

            FileData? data;
            try
            {
                data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(path), _jsonOptions);
            }
            catch (JsonException e)
            {
                throw new NetSuiteException($"The NetSuite metadata file '{Path.GetFileName(path)}' cannot be read. Update the NetSuite metadata again.", NetSuiteErrorKind.NotSupported, e);
            }
            var records = new List<NetSuiteRecordDefinition>();
            foreach (var record in data?.Records ?? new())
            {
                var definition = new NetSuiteRecordDefinition { Name = record.Name };
                foreach (var field in record.Fields)
                {
                    definition.Fields.Add(new NetSuiteFieldDefinition
                    {
                        RestName = field.Name,
                        Column = field.Name.ToLowerInvariant(),
                        Kind = field.Kind,
                        IsNullable = !field.Required,
                    });
                }
                records.Add(definition);
            }
            _cache[path] = (lastWriteTimeUtc, records);
            return records;
        }
    }
}
