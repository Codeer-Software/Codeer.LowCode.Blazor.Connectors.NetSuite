using Codeer.LowCode.Blazor.SystemSettings;
using System.Data.Common;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite
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

        /// <summary>扱うレコード種別 (REST のレコード名。例: customer, customrecord_xxx)。ここに無いレコードは読み書きしない。</summary>
        public List<string> Records { get; set; } = new();

        /// <summary>1 回の要求のタイムアウト (秒)。</summary>
        public int TimeoutSeconds { get; set; } = 100;

        /// <summary>この接続から NetSuite へ同時に出す要求の上限。アカウントの同時実行枠はほかの連携と共有なので控えめにする。</summary>
        public int MaxConcurrency { get; set; } = 4;

        /// <summary>1 回の読み出しで受け取る行の上限。件数を絞らない読み出しがこれを超えたらエラーにする (黙って切り捨てない)。</summary>
        public int MaxRows { get; set; } = 10000;

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
                Records = Get(nameof(Records)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                TimeoutSeconds = GetInt(nameof(TimeoutSeconds), defaults.TimeoutSeconds),
                MaxConcurrency = GetInt(nameof(MaxConcurrency), defaults.MaxConcurrency),
                MaxRows = GetInt(nameof(MaxRows), defaults.MaxRows),
            };
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
            if (Records.Count == 0)
                throw new NetSuiteException($"NetSuite data source '{Name}': {nameof(Records)} is required (comma separated record types, e.g. Records=customer,customrecord_sample).");
        }

        //同じ接続先・同じ認証主体なら同じ接続 (トークン・定義のキャッシュ) を使う。秘密鍵は鍵を差し替えたら別の接続になるように含める
        internal string GetIdentityKey()
            => string.Join("\n", Name, ServiceUrl, ClientId, CertificateId, PrivateKeyPath, PrivateKey, Algorithm, Scope,
                string.Join(",", Records), TimeoutSeconds, MaxConcurrency, MaxRows);
    }
}
