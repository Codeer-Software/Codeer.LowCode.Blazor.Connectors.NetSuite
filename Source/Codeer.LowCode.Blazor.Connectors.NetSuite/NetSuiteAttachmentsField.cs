using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.OperatingModel;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Script;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite
{
    /// <summary>
    /// NetSuite の添付ファイルのフィールド。値 (JSON の文字) を、読んだファイル・足すファイル・外す印に読み解いて画面に出す。
    /// 変更が無ければ値は読んだ文字のまま (保存で送らない)。変更があれば、読んだファイル (外すものは deleted: true) と足すファイル (base64) の並びにする。
    /// </summary>
    public class NetSuiteAttachmentsField(NetSuiteAttachmentsFieldDesign design) : FieldBase<NetSuiteAttachmentsFieldDesign>(design)
    {
        /// <summary>画面に出すファイル 1 つ。</summary>
        internal sealed class Entry
        {
            /// <summary>読んだファイルの internal ID (サーバーが保存検索で引き当てたもの)。</summary>
            public string? FileId { get; init; }
            public string Name { get; init; } = string.Empty;
            public long? Size { get; init; }
            public string? ContentType { get; init; }
            /// <summary>足すファイルの中身 (保存前)。null なら読んだファイル。</summary>
            public string? Base64 { get; init; }
            /// <summary>外す印 (読んだファイルだけ)。</summary>
            public bool Deleted { get; set; }
            public bool IsAdded => Base64 != null;
        }

        NetSuiteAttachmentsFieldData _source = new();
        NetSuiteAttachmentsFieldData _current = new();
        List<Entry> _entries = new();
        bool _initialized;

        public override bool IsModified => !IgnoreModification && _source.Value != _current.Value;

        internal IReadOnlyList<Entry> Entries => _entries;

        [ScriptHide]
        public override FieldDataBase? GetData() => _current;

        [ScriptHide]
        public override FieldSubmitData GetSubmitData() => new() { FieldData = IsModified ? _current : null };

        [ScriptHide]
        public override async Task InitializeDataAsync(FieldDataBase? data)
        {
            await Task.CompletedTask;
            if (data is not NetSuiteAttachmentsFieldData src)
            {
                if (!_initialized)
                {
                    _initialized = true;
                    return;
                }
                _source = new();
                _current = new();
                _entries = new();
                return;
            }
            //データありで初期化した後の null (ファイルの無いレコード) は値を消す
            _initialized = true;
            _source = src;
            _current = src.Clone();
            _entries = Parse(_current.Value);
        }

        [ScriptHide]
        public override async Task SetDataAsync(FieldDataBase? data)
        {
            if (data == _current) return;
            SetCurrent(data as NetSuiteAttachmentsFieldData);
            ClearError();
            await InvokeOnDataChangedAsync();
        }

        [ScriptHide]
        public override async Task SetDataWithoutInteractionAsync(FieldDataBase? data)
        {
            await Task.CompletedTask;
            if (data == _current) return;
            SetCurrent(data as NetSuiteAttachmentsFieldData);
        }

        void SetCurrent(NetSuiteAttachmentsFieldData? data)
        {
            _current = data?.Clone() ?? new();
            _entries = Parse(_current.Value);
        }

        /// <summary>
        /// 保存で送った分を保存した状態にする (読み直さない保存の後のため): 送って消したファイルを外し、送って足したファイルは中身を捨てて名前だけ残す
        /// (FileId は読み直すまで分からないのでダウンロード・外すはできない)。送信の間に足した・印を付けた分は変更として残す
        /// (送った分まで残すと、次の保存で同じファイルがもう一度足される)。送った追加を送信の間に取り消していても、サーバーには付いたので名前だけで出す。
        /// </summary>
        [ScriptHide]
        public override void AcceptChanges(SubmitAcceptInfo info)
        {
            if (!info.TryGetSubmittedData<NetSuiteAttachmentsFieldData>(this, out var submitted)) return;
            var sent = Parse(submitted!.Value);
            var sentAdded = sent.Where(e => e.IsAdded).ToList();
            var sentDeleted = sent.Where(e => e.Deleted && e.FileId != null).Select(e => e.FileId!).ToHashSet(StringComparer.Ordinal);
            if (sentAdded.Count == 0 && sentDeleted.Count == 0) return;
            var entries = new List<Entry>();
            foreach (var entry in _entries)
            {
                if (entry.IsAdded)
                {
                    var index = sentAdded.FindIndex(e => e.Name == entry.Name && e.Base64 == entry.Base64);
                    if (0 <= index)
                    {
                        sentAdded.RemoveAt(index);
                        entries.Add(new Entry { Name = entry.Name, Size = entry.Size });
                        continue;
                    }
                }
                else if (entry.FileId != null && sentDeleted.Contains(entry.FileId))
                {
                    continue;
                }
                entries.Add(entry);
            }
            //送信の間に画面で取り消した追加も、サーバーには付いているので保存済み (名前だけ) として出す
            foreach (var added in sentAdded) entries.Add(new Entry { Name = added.Name, Size = added.Size });
            _entries = entries;
            //保存済みの状態 = 足す前のファイル (外す印なし)
            var saved = entries.Where(e => !e.IsAdded).Select(e => e.Deleted ? new Entry { FileId = e.FileId, Name = e.Name, Size = e.Size, ContentType = e.ContentType } : e).ToList();
            _source = new NetSuiteAttachmentsFieldData { Value = saved.Count == 0 ? null : Serialize(saved, true) };
            _current = entries.Any(e => e.IsAdded || e.Deleted) ? new NetSuiteAttachmentsFieldData { Value = Serialize(entries, false) } : _source.Clone();
            //表示が変わる (未保存の印・取り消しのボタンが消える) ので描き直す。本体の軽い保存はほかのフィールドの描き直しを呼ばない
            NotifyStateChanged();
        }

        [ScriptHide]
        public override async Task<bool> ValidateInput()
        {
            var tooLarge = _entries.FirstOrDefault(e => e.IsAdded && Design.MaxFileSizeBytes < (e.Size ?? 0));
            if (tooLarge != null)
            {
                SetError(TooLargeMessage(tooLarge.Name));
                return false;
            }
            if (Design.IsRequired && !_entries.Any(e => !e.Deleted))
            {
                SetError(IsJapanese ? "ファイルを添付してください。" : "Attach a file.");
                return false;
            }
            return await base.ValidateInput();
        }

        /// <summary>足すファイルを入れる。上限を超えていれば入力エラーにして入れない (false)。</summary>
        internal async Task<bool> AddFileAsync(string name, string? contentType, byte[] content)
        {
            if (content.LongLength == 0)
            {
                RejectEmpty(name);
                return false;
            }
            if (Design.MaxFileSizeBytes < content.LongLength)
            {
                SetError(TooLargeMessage(name));
                return false;
            }
            ClearError();
            _entries.Add(new Entry { Name = name, ContentType = string.IsNullOrEmpty(contentType) ? null : contentType, Size = content.LongLength, Base64 = Convert.ToBase64String(content) });
            await ApplyEntriesAsync();
            return true;
        }

        /// <summary>上限を超えたファイルを選んだことを知らせる (中身を読まずに止めるとき)。</summary>
        internal void RejectTooLarge(string name) => SetError(TooLargeMessage(name));

        /// <summary>空のファイル (0 バイト) を選んだことを知らせる。</summary>
        internal void RejectEmpty(string name) => SetError(IsJapanese ? $"「{name}」は空のファイルです。" : $"'{name}' is empty.");

        /// <summary>一度に選べる数を超えたことを知らせる。</summary>
        internal void RejectTooMany(int max) => SetError(IsJapanese ? $"一度に選べるファイルは {max} 個までです。" : $"Up to {max} files can be selected at once.");

        /// <summary>足したファイルを取り消す。</summary>
        internal async Task RemoveAddedAsync(Entry entry)
        {
            if (!entry.IsAdded || !_entries.Remove(entry)) return;
            await ApplyEntriesAsync();
        }

        /// <summary>外す印を付けられるか (読んだファイルで ID が分かるものだけ。読み直さない保存の直後の足したファイルは、読み直すまで外せない)。</summary>
        internal static bool CanDelete(Entry entry) => !entry.IsAdded && !string.IsNullOrEmpty(entry.FileId);

        /// <summary>読んだファイルの外す印を付ける / 取り消す。</summary>
        internal async Task SetDeletedAsync(Entry entry, bool deleted)
        {
            if (!CanDelete(entry) || entry.Deleted == deleted || !_entries.Contains(entry)) return;
            entry.Deleted = deleted;
            await ApplyEntriesAsync();
        }

        /// <summary>
        /// ダウンロードの URL (アプリのルートからの相対)。保存済みのレコードの、読んだときからあるファイルだけ (足したファイル・新しいレコードには無い)。
        /// </summary>
        internal string? GetDownloadUrl(Entry entry)
        {
            if (entry.IsAdded || string.IsNullOrEmpty(entry.FileId) || Module.IsNewData) return null;
            var id = Module.GetIdText();
            if (string.IsNullOrEmpty(id)) return null;
            if (!Parse(_source.Value).Any(e => !e.IsAdded && string.Equals(e.FileId, entry.FileId, StringComparison.Ordinal))) return null;
            return NetSuiteAttachmentsFieldDesign.DownloadUrl +
                "?module=" + Uri.EscapeDataString(Module.Design.Name) +
                "&id=" + Uri.EscapeDataString(id) +
                "&field=" + Uri.EscapeDataString(Design.Name) +
                "&fileId=" + Uri.EscapeDataString(entry.FileId);
        }

        async Task ApplyEntriesAsync()
        {
            //足すものも外すものも無ければ、読んだ値に戻す (変更なし = 保存で送らない)
            _current = new NetSuiteAttachmentsFieldData
            {
                Value = _entries.Any(e => e.IsAdded || e.Deleted) ? Serialize(_entries, false) : _source.Value,
            };
            await InvokeOnDataChangedAsync();
        }

        async Task InvokeOnDataChangedAsync()
        {
            if (Module.IsInitializing) return;
            await Module.ExecuteScriptAsync(Design.OnDataChanged);
            await NotifyDataChangedAsync();
        }

        static List<Entry> Parse(string? value)
        {
            var list = new List<Entry>();
            if (string.IsNullOrWhiteSpace(value)) return list;
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(value);
            }
            catch (JsonException)
            {
                return list;
            }
            if (root is not JsonArray array) return list;
            foreach (var item in array.OfType<JsonObject>())
            {
                var base64 = GetString(item, "base64");
                list.Add(new Entry
                {
                    FileId = base64 == null ? GetText(item["id"]) : null,
                    Name = GetString(item, "name") ?? string.Empty,
                    Size = ReadSize(item["size"]) ?? (base64 == null ? null : Base64Length(base64)),
                    ContentType = GetString(item, "contentType"),
                    Base64 = base64,
                    Deleted = base64 == null && item["deleted"] is JsonValue d && d.TryGetValue<bool>(out var deleted) && deleted,
                });
            }
            return list;
        }

        //大きさは数値。数字の文字でも読む
        static long? ReadSize(JsonNode? node)
        {
            if (node is not JsonValue value) return null;
            if (value.TryGetValue<long>(out var number)) return number;
            if (value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return null;
        }

        //足すファイルは {name, contentType, base64}、外すファイルは {id, name, size, deleted: true}、そのままのファイルは {id, name, size}
        static string Serialize(IEnumerable<Entry> entries, bool savedOnly)
        {
            var array = new JsonArray();
            foreach (var e in entries)
            {
                var item = new JsonObject();
                if (e.IsAdded && !savedOnly)
                {
                    item["name"] = e.Name;
                    if (e.ContentType != null) item["contentType"] = e.ContentType;
                    item["base64"] = e.Base64;
                }
                else
                {
                    if (e.FileId != null) item["id"] = e.FileId;
                    item["name"] = e.Name;
                    if (e.Size != null) item["size"] = e.Size.Value;
                    if (e.Deleted) item["deleted"] = true;
                }
                array.Add(item);
            }
            return array.ToJsonString();
        }

        //ID は文字でも数値でも読む
        static string? GetText(JsonNode? node)
        {
            if (node is not JsonValue value) return null;
            if (value.TryGetValue<string>(out var text)) return text;
            if (value.TryGetValue<long>(out var number)) return number.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        static string? GetString(JsonObject item, string name)
            => item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        static long Base64Length(string base64)
            => base64.Length / 4 * 3 - (base64.EndsWith("==", StringComparison.Ordinal) ? 2 : base64.EndsWith('=') ? 1 : 0);

        internal static bool IsJapanese => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja";

        string TooLargeMessage(string name)
            => IsJapanese
                ? $"「{name}」は {Design.MaxFileSizeMB} MB を超えています。"
                : $"'{name}' exceeds {Design.MaxFileSizeMB} MB.";
    }
}
