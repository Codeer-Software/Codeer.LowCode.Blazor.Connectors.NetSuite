using Codeer.LowCode.Blazor.DesignLogic;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    /// <summary>
    /// レコードの定義 (メタ情報) を保存したファイル (netsuite.&lt;データソース名&gt;.metadata.json)。デザインのプロジェクトのサーバー専用ファイルに置き、
    /// デザインと一緒に配られる (App.zip に入る)。デザイナも Web アプリもこのファイルから表の定義を読む (NetSuite に取りに行かない)。
    /// 中身はアカウントの全部のレコード (コネクターが読めるもの) と取引の明細の表。接続の情報は入らない。
    /// </summary>
    public sealed class NetSuiteMetadataFile
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
            /// <summary>読み取り専用 (メタデータの readOnly)。false なら書かない。</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public bool ReadOnly { get; set; }
            /// <summary>固定の選択肢 (メタデータの enum)。無ければ null (書かない)。</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<ChoiceData>? Choices { get; set; }
            /// <summary>明細 (Sublist) の行の項目。明細でなければ null (書かない)。</summary>
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public List<FieldData>? Fields { get; set; }
        }

        class ChoiceData
        {
            /// <summary>NetSuite の内部の値 (保存される値)。</summary>
            public string Value { get; set; } = string.Empty;
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

        static readonly ConcurrentDictionary<string, SemaphoreSlim> _updateLocks = new(StringComparer.OrdinalIgnoreCase);

        //読んだ定義 (場所とデータソースごと)。プロジェクトはファイルの中身、配られたデザインは App.zip の更新日時と大きさが変わるまで使い回す。
        //値は差し替えるだけで中を変えないので、読む側はロックなしで使える。同じ場所を同時に読み直すのは 1 つだけ (Entry のロック)
        sealed class Entry
        {
            public object Sync { get; } = new();
            public (object Stamp, NetSuiteMetadata Metadata)? Loaded { get; set; }
        }

        static readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

        readonly string _directory;
        readonly bool _isDeployedDesign;

        NetSuiteMetadataFile(string directory, bool isDeployedDesign)
        {
            //空は今のフォルダ (本体が App.zip を探す場所と同じ)
            _directory = string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : Path.GetFullPath(directory);
            _isDeployedDesign = isDeployedDesign;
        }

        /// <summary>デザインのプロジェクトのファイル (デザイナ用)。</summary>
        /// <param name="projectDirectory">デザインのプロジェクトのフォルダ。</param>
        public static NetSuiteMetadataFile FromProject(string projectDirectory) => new(projectDirectory, false);

        /// <summary>配られたデザイン (App.zip) の中のファイル (Web アプリ用)。App.zip が差し替わると次の読み出しから新しい内容になる。</summary>
        /// <param name="designFileDirectory">App.zip のあるフォルダ。</param>
        public static NetSuiteMetadataFile FromDeployedDesign(string designFileDirectory) => new(designFileDirectory, true);

        //データソース名の文字 (日本語なども含む)・数字・_・-・. 以外は _ にする (OS に依らない決まり。デザイナと Web アプリで同じ名前になる)
        internal static string GetFileName(string dataSourceName)
            => $"netsuite.{string.Concat(dataSourceName.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_'))}.metadata.json";

        /// <summary>
        /// アカウントの全部のレコード種別の定義を NetSuite から読み直して、プロジェクトのサーバー専用ファイルに保存する (次のデプロイでデザインと一緒に配られる)。
        /// レコード種別の数だけ要求を送るので時間がかかる。読めなければファイルを変えない。書き終えてから置き換える (途中で失敗しても前のファイルが残る)。
        /// 同じファイルの更新は直列にする (プロセスの中だけ)。
        /// </summary>
        /// <param name="connection">NetSuite への接続。</param>
        /// <param name="projectDirectory">デザインのプロジェクトのフォルダ。</param>
        public static async Task UpdateAsync(NetSuiteConnection connection, string projectDirectory)
        {
            var fileName = GetFileName(connection.Settings.Name);
            var updateLock = _updateLocks.GetOrAdd(Path.Combine(Path.GetFullPath(projectDirectory), fileName), _ => new SemaphoreSlim(1, 1));
            await updateLock.WaitAsync();
            try
            {
                var records = await connection.FetchRecordDefinitionsAsync();
                var bytes = Serialize(records);
                //書く内容を読み直して、同じ定義になることを確かめてから書く
                var written = Parse(bytes, fileName);
                static int CountFields(IEnumerable<NetSuiteRecordDefinition> list) => list.Sum(e => e.Fields.Count(f => f.Kind != NetSuiteFieldKind.Attachments));
                if (written.Records.Count != records.Count || CountFields(written.Records) != CountFields(records))
                    throw new NetSuiteException($"The NetSuite metadata file '{fileName}' could not be written correctly.");
                DesignDataFileManager.WriteProjectServerFile(projectDirectory, fileName, bytes);
            }
            finally
            {
                updateLock.Release();
            }
        }

        static byte[] Serialize(List<NetSuiteRecordDefinition> records)
        {
            var data = new FileData
            {
                Records = records.Select(record => new RecordData
                {
                    Name = record.Name,
                    Parent = record.LineParent,
                    Sublist = record.LineSublist,
                    Fields = record.Fields.Select(ToFieldData).ToList(),
                }).ToList(),
            };
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data, _jsonOptions));
        }

        static FieldData ToFieldData(NetSuiteFieldDefinition field) => new()
        {
            Name = field.RestName,
            Kind = field.Kind,
            Required = !field.IsNullable,
            ReadOnly = field.IsReadOnly,
            Choices = field.Choices?.Select(e => new ChoiceData { Value = e }).ToList(),
            Fields = field.Kind == NetSuiteFieldKind.Sublist ? field.SublistFields.Select(ToFieldData).ToList() : null,
        };

        static NetSuiteFieldDefinition FromFieldData(FieldData field)
        {
            var choices = field.Choices?.Where(e => !string.IsNullOrEmpty(e?.Value)).Select(e => e.Value).Distinct().ToList();
            return new()
            {
                RestName = field.Name,
                Column = field.Name.ToLowerInvariant(),
                Kind = field.Kind,
                IsNullable = !field.Required,
                IsReadOnly = field.ReadOnly,
                Choices = choices is { Count: > 0 } ? choices : null,
                SublistFields = field.Fields?.Where(e => !string.IsNullOrEmpty(e?.Name)).Select(FromFieldData).ToList() ?? new(),
            };
        }

        /// <summary>このファイルの定義。ファイルが無ければ空。</summary>
        internal NetSuiteMetadata Load(string dataSourceName)
        {
            var fileName = GetFileName(dataSourceName);
            var entry = _cache.GetOrAdd((_isDeployedDesign ? "zip|" : "project|") + _directory + "|" + fileName, _ => new Entry());
            lock (entry.Sync)
            {
                if (_isDeployedDesign)
                {
                    //App.zip の更新日時と大きさで差し替えを知る (読む前に取る。読む間に差し替わっても、次の読み出しで日時が違うので読み直す)
                    var stamp = ZipStamp();
                    if (entry.Loaded is { } loaded && loaded.Stamp.Equals(stamp)) return loaded.Metadata;
                    using var stream = DesignDataFileManager.GetServerFile(_directory, fileName);
                    var metadata = stream == null ? NetSuiteMetadata.Empty : Parse(stream.ToArray(), fileName);
                    entry.Loaded = (stamp, metadata);
                    return metadata;
                }
                else
                {
                    //プロジェクトのファイルは中身で比べる (読むのは毎回。解釈は変わったときだけ)
                    var bytes = DesignDataFileManager.ReadProjectServerFile(_directory, fileName) ?? Array.Empty<byte>();
                    if (entry.Loaded is { Stamp: byte[] previous } loaded && previous.AsSpan().SequenceEqual(bytes)) return loaded.Metadata;
                    var metadata = bytes.Length == 0 ? NetSuiteMetadata.Empty : Parse(bytes, fileName);
                    entry.Loaded = (bytes, metadata);
                    return metadata;
                }
            }
        }

        //App.zip の名前は本体の配置の決まり (DesignDataFileManager が読む <フォルダ>/App.zip)
        (bool Exists, DateTime LastWriteTimeUtc, long Length) ZipStamp()
        {
            var zip = new FileInfo(Path.Combine(_directory, "App.zip"));
            return zip.Exists ? (true, zip.LastWriteTimeUtc, zip.Length) : (false, default, 0);
        }

        /// <summary>レコード (表) がファイルに無いときのエラー (どこを直せばよいかを入れる)。</summary>
        internal NetSuiteException RecordNotFound(string dataSourceName, string table)
            => new(_isDeployedDesign
                ? $"Record type '{table}' is not in the NetSuite metadata ({GetFileName(dataSourceName)}) of the deployed design. Update the NetSuite metadata in the designer and deploy the design again."
                : $"Record type '{table}' is not in the NetSuite metadata ({GetFileName(dataSourceName)}) of the design project. Update the NetSuite metadata (Tools menu of the designer).",
                NetSuiteErrorKind.NotFound);

        static NetSuiteMetadata Parse(byte[] bytes, string fileName)
        {
            FileData? data;
            try
            {
                data = JsonSerializer.Deserialize<FileData>(bytes, _jsonOptions);
            }
            catch (JsonException e)
            {
                throw new NetSuiteException($"The NetSuite metadata file '{fileName}' cannot be read. Update the NetSuite metadata again.", NetSuiteErrorKind.NotSupported, e);
            }
            var records = new List<NetSuiteRecordDefinition>();
            foreach (var record in data?.Records ?? new())
            {
                //手で直したファイルの null (要素・Fields) は無いものとして読む
                if (string.IsNullOrEmpty(record?.Name)) continue;
                //明細の表は、transactionline の行がその明細の行になるものだけ (前の版が書いたほかの明細の表は読まない)
                if (record.Parent != null && !string.Equals(SuiteQlTranslator.GetLineSublist(record.Parent), record.Sublist, StringComparison.OrdinalIgnoreCase)) continue;
                var definition = new NetSuiteRecordDefinition { Name = record.Name, LineParent = record.Parent, LineSublist = record.Sublist };
                //添付の仮想の列はファイルに持たない (書かれていても読まず、読むたびに取引に足す)
                foreach (var field in (record.Fields ?? new()).Where(e => !string.IsNullOrEmpty(e?.Name) && e.Kind != NetSuiteFieldKind.Attachments)) definition.Fields.Add(FromFieldData(field));
                definition.AddAttachmentsField();
                records.Add(definition);
            }
            return new NetSuiteMetadata(records);
        }
    }

    /// <summary>メタ情報のファイル 1 つ分の定義。作った後は変えない (読む側はロックなしで使う)。</summary>
    sealed class NetSuiteMetadata
    {
        public static NetSuiteMetadata Empty { get; } = new(new());

        readonly Dictionary<string, NetSuiteRecordDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>レコードと明細の表 (ファイルの並び)。</summary>
        public IReadOnlyList<NetSuiteRecordDefinition> Records { get; }

        public NetSuiteMetadata(List<NetSuiteRecordDefinition> records)
        {
            Records = records;
            //同じ名前が 2 つあれば先のもの
            foreach (var record in records) _byName.TryAdd(record.Name, record);
        }

        public NetSuiteRecordDefinition? Find(string name) => _byName.TryGetValue(name, out var record) ? record : null;
    }
}
