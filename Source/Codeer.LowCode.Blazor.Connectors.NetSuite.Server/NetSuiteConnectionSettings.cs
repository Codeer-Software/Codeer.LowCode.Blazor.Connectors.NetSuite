using Codeer.LowCode.Blazor.SystemSettings;
using System.Data.Common;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Server
{
    /// <summary>
    /// NetSuite への接続 1 つ分の設定。データソース (DataSources) の ConnectionString に "Provider=NetSuite;..." の形で書く。
    /// </summary>
    public class NetSuiteConnectionSettings
    {
        /// <summary>ConnectionString の Provider の値。これがあるデータソースを NetSuite として扱う。</summary>
        public const string ProviderName = "NetSuite";

        /// <summary>データソース名 (モジュールの DataSourceName)。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>アカウント固有の SuiteTalk の URL (例: https://1234567.suitetalk.api.netsuite.com)。NetSuite の Company Information › Company URLs で確かめる。</summary>
        public string ServiceUrl { get; set; } = string.Empty;

        /// <summary>インテグレーションの Client ID (Consumer Key)。</summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>OAuth 2.0 Client Credentials (M2M) Setup で証明書を登録したときの Certificate ID。</summary>
        public string CertificateId { get; set; } = string.Empty;

        /// <summary>秘密鍵 (PEM) のファイルのパス。<see cref="PrivateKey"/> とどちらか。</summary>
        public string PrivateKeyPath { get; set; } = string.Empty;

        /// <summary>秘密鍵 (PEM) の本文。改行は "\n" と書いてもよい。<see cref="PrivateKeyPath"/> とどちらか。</summary>
        public string PrivateKey { get; set; } = string.Empty;

        /// <summary>トークン要求の署名方式。PS256 / PS384 / PS512 (RSA) か ES256 / ES384 / ES512 (EC)。</summary>
        public string Algorithm { get; set; } = "PS256";

        /// <summary>トークンの scope。</summary>
        public string Scope { get; set; } = "rest_webservices";

        /// <summary>1 回の要求のタイムアウト (秒)。</summary>
        public int TimeoutSeconds { get; set; } = 100;

        /// <summary>この接続から NetSuite へ同時に出す要求の上限。アカウントの同時実行枠はほかの連携と共有なので控えめにする。</summary>
        public int MaxConcurrency { get; set; } = 4;

        /// <summary>1 回の読み出しで受け取る行の上限。件数を絞らない読み出しがこれを超えたらエラーにする (黙って切り捨てない)。</summary>
        public int MaxRows { get; set; } = 10000;

        /// <summary>
        /// 添付ファイルのフィールドで足したファイルを置く File Cabinet のフォルダの internal ID。空ならファイルを足せない (足そうとするとエラー)。
        /// </summary>
        public string AttachmentFolderId { get; set; } = string.Empty;

        /// <summary>
        /// 取引に今付いているファイルの一覧を読む保存検索の ID (internal ID か script ID)。空なら一覧は読まない
        /// (添付ファイルのフィールドは足すだけになる。既存の添付の表示・ダウンロード・外すはできない)。
        /// </summary>
        public string AttachmentSavedSearchId { get; set; } = string.Empty;

        /// <summary>保存検索の結果で、取引の internal ID を持つ列の名前。</summary>
        public string AttachmentSearchRecordColumn { get; set; } = "internalid";

        /// <summary>保存検索の結果で、ファイルの internal ID を持つ列の名前。<see cref="AttachmentSavedSearchId"/> を書いたら必須。</summary>
        public string AttachmentSearchFileColumn { get; set; } = string.Empty;

        /// <summary>保存検索の結果で、ファイル名を持つ列の名前。<see cref="AttachmentSavedSearchId"/> を書いたら必須。</summary>
        public string AttachmentSearchFileNameColumn { get; set; } = string.Empty;

        /// <summary>保存検索の結果で、ファイルの大きさを持つ列の名前 (任意。値はバイトとして表示する)。</summary>
        public string AttachmentSearchFileSizeColumn { get; set; } = string.Empty;

        /// <summary>保存検索の結果で、ファイルの種類を持つ列の名前 (任意)。</summary>
        public string AttachmentSearchFileTypeColumn { get; set; } = string.Empty;

        internal bool HasAttachmentSavedSearch => !string.IsNullOrWhiteSpace(AttachmentSavedSearchId);

        /// <summary>
        /// データソースが NetSuite 用 (ConnectionString が Provider=NetSuite) なら設定を読む。違えば null。
        /// </summary>
        public static NetSuiteConnectionSettings? FromDataSource(DataSource dataSource)
        {
            DbConnectionStringBuilder builder;
            try
            {
                builder = new DbConnectionStringBuilder { ConnectionString = dataSource.ConnectionString };
            }
            catch (ArgumentException)
            {
                return null;
            }
            if (!builder.TryGetValue("Provider", out var provider) ||
                !string.Equals(provider?.ToString(), ProviderName, StringComparison.OrdinalIgnoreCase)) return null;

            string Get(string key) => builder.TryGetValue(key, out var v) ? v?.ToString() ?? string.Empty : string.Empty;
            int GetInt(string key, int defaultValue) => int.TryParse(Get(key), out var v) && 0 < v ? v : defaultValue;

            var defaults = new NetSuiteConnectionSettings();
            var settings = new NetSuiteConnectionSettings
            {
                Name = dataSource.Name,
                ServiceUrl = Get(nameof(ServiceUrl)).TrimEnd('/'),
                ClientId = Get(nameof(ClientId)),
                CertificateId = Get(nameof(CertificateId)),
                PrivateKeyPath = Get(nameof(PrivateKeyPath)),
                PrivateKey = Get(nameof(PrivateKey)),
                TimeoutSeconds = GetInt(nameof(TimeoutSeconds), defaults.TimeoutSeconds),
                MaxConcurrency = GetInt(nameof(MaxConcurrency), defaults.MaxConcurrency),
                MaxRows = GetInt(nameof(MaxRows), defaults.MaxRows),
                AttachmentFolderId = Get(nameof(AttachmentFolderId)).Trim(),
                AttachmentSavedSearchId = Get(nameof(AttachmentSavedSearchId)).Trim(),
                AttachmentSearchFileColumn = Get(nameof(AttachmentSearchFileColumn)).Trim(),
                AttachmentSearchFileNameColumn = Get(nameof(AttachmentSearchFileNameColumn)).Trim(),
                AttachmentSearchFileSizeColumn = Get(nameof(AttachmentSearchFileSizeColumn)).Trim(),
                AttachmentSearchFileTypeColumn = Get(nameof(AttachmentSearchFileTypeColumn)).Trim(),
            };
            if (!string.IsNullOrWhiteSpace(Get(nameof(AttachmentSearchRecordColumn)))) settings.AttachmentSearchRecordColumn = Get(nameof(AttachmentSearchRecordColumn)).Trim();
            if (!string.IsNullOrEmpty(Get(nameof(Algorithm)))) settings.Algorithm = Get(nameof(Algorithm));
            if (!string.IsNullOrEmpty(Get(nameof(Scope)))) settings.Scope = Get(nameof(Scope));
            return settings;
        }

        internal void Validate()
        {
            void Require(string value, string name)
            {
                if (string.IsNullOrWhiteSpace(value)) throw new NetSuiteException($"NetSuite data source '{Name}': {name} is required.");
            }
            Require(ServiceUrl, nameof(ServiceUrl));
            Require(ClientId, nameof(ClientId));
            Require(CertificateId, nameof(CertificateId));
            if (string.IsNullOrWhiteSpace(PrivateKeyPath) && string.IsNullOrWhiteSpace(PrivateKey))
                throw new NetSuiteException($"NetSuite data source '{Name}': {nameof(PrivateKeyPath)} or {nameof(PrivateKey)} is required.");
            //https だけ。このマシン自身 (localhost) で動かすモックに限り http を許す
            if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) ||
                !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                throw new NetSuiteException($"NetSuite data source '{Name}': {nameof(ServiceUrl)} must be an https URL.");
            //URL に入れる ID は形を確かめる (internal ID は数字、保存検索は数字か script ID)
            if (!string.IsNullOrEmpty(AttachmentFolderId) && !AttachmentFolderId.All(char.IsAsciiDigit))
                throw new NetSuiteException($"NetSuite data source '{Name}': {nameof(AttachmentFolderId)} must be the internal ID (digits) of a File Cabinet folder.");
            if (HasAttachmentSavedSearch)
            {
                if (!AttachmentSavedSearchId.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                    throw new NetSuiteException($"NetSuite data source '{Name}': {nameof(AttachmentSavedSearchId)} must be the internal ID or the script ID of a saved search.");
                Require(AttachmentSearchRecordColumn, nameof(AttachmentSearchRecordColumn));
                Require(AttachmentSearchFileColumn, nameof(AttachmentSearchFileColumn) + " (required with " + nameof(AttachmentSavedSearchId) + ")");
                Require(AttachmentSearchFileNameColumn, nameof(AttachmentSearchFileNameColumn) + " (required with " + nameof(AttachmentSavedSearchId) + ")");
            }
        }

        //同じ接続先・同じ認証主体なら同じ接続 (トークン・同時実行の枠) を使う。秘密鍵は鍵を差し替えたら別の接続になるように含める
        internal string GetIdentityKey()
            => string.Join("\n", Name, ServiceUrl, ClientId, CertificateId, PrivateKeyPath, PrivateKey, Algorithm, Scope, TimeoutSeconds, MaxConcurrency, MaxRows,
                AttachmentFolderId, AttachmentSavedSearchId, AttachmentSearchRecordColumn, AttachmentSearchFileColumn, AttachmentSearchFileNameColumn,
                AttachmentSearchFileSizeColumn, AttachmentSearchFileTypeColumn);
    }
}
