using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using System.Security.Cryptography;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness
{
    /// <summary>テスト用の設定・接続を作る。鍵はテストの中で作ったものだけ使う。</summary>
    public static class TestConnections
    {
        public const string DataSourceName = "NS";

        static readonly Lazy<RSA> _rsa = new(() => RSA.Create(2048));
        public static RSA RsaKey => _rsa.Value;
        public static string RsaPrivateKeyPem => RsaKey.ExportPkcs8PrivateKeyPem();

        internal static NetSuiteConnectionSettings CreateSettings(Action<NetSuiteConnectionSettings>? edit = null)
        {
            var settings = new NetSuiteConnectionSettings
            {
                Name = DataSourceName,
                ServiceUrl = FakeNetSuite.ServiceUrl,
                ClientId = "client-1",
                CertificateId = "cert-1",
                PrivateKey = RsaPrivateKeyPem,
            };
            edit?.Invoke(settings);
            return settings;
        }

        /// <summary>待ち時間を記録だけして待たない接続。</summary>
        internal static NetSuiteConnection Create(FakeNetSuite fake, List<TimeSpan>? delays = null, Action<NetSuiteConnectionSettings>? edit = null)
        {
            var connection = new NetSuiteConnection(CreateSettings(edit), fake);
            connection.Delay = e =>
            {
                delays?.Add(e);
                return Task.CompletedTask;
            };
            return connection;
        }
    }
}
