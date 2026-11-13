using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.SystemSettings;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    public class NetSuiteConnectionSettingsTest
    {
        static DataSource Source(string connectionString)
            => new() { Name = "NS", DataSourceType = DataSourceType.Oracle, ConnectionString = connectionString };

        [TestCase("")]
        [TestCase("Data Source=localhost;User Id=a;Password=b")]
        [TestCase("Provider=OraOLEDB;ServiceUrl=https://x")]
        [TestCase("this is not a connection string")]
        public void NetSuiteでなければnull(string connectionString)
        {
            Assert.That(NetSuiteConnectionSettings.FromDataSource(Source(connectionString)), Is.Null);
        }

        [Test]
        public void 各キーを読む()
        {
            var settings = NetSuiteConnectionSettings.FromDataSource(Source(
                "provider=netsuite;ServiceUrl=https://1234567.suitetalk.api.netsuite.com/;ClientId=cid;CertificateId=kid;PrivateKeyPath=C:\\keys\\ns.pem;" +
                "PrivateKey=\"-----BEGIN PRIVATE KEY-----\\nAB+/=\\n-----END PRIVATE KEY-----\";Algorithm=ES256;Scope=rest_webservices,restlets;" +
                "Records= customer , customrecord_x ,,salesorder;TimeoutSeconds=30;MaxConcurrency=2;MaxRows=500"))!;
            Assert.That(settings, Is.Not.Null);
            Assert.That(settings.Name, Is.EqualTo("NS"));
            Assert.That(settings.ServiceUrl, Is.EqualTo("https://1234567.suitetalk.api.netsuite.com"));
            Assert.That(settings.ClientId, Is.EqualTo("cid"));
            Assert.That(settings.CertificateId, Is.EqualTo("kid"));
            Assert.That(settings.PrivateKeyPath, Is.EqualTo("C:\\keys\\ns.pem"));
            Assert.That(settings.PrivateKey, Is.EqualTo("-----BEGIN PRIVATE KEY-----\\nAB+/=\\n-----END PRIVATE KEY-----"));
            Assert.That(settings.Algorithm, Is.EqualTo("ES256"));
            Assert.That(settings.Scope, Is.EqualTo("rest_webservices,restlets"));
            Assert.That(settings.Records, Is.EqualTo(new[] { "customer", "customrecord_x", "salesorder" }));
            Assert.That(settings.TimeoutSeconds, Is.EqualTo(30));
            Assert.That(settings.MaxConcurrency, Is.EqualTo(2));
            Assert.That(settings.MaxRows, Is.EqualTo(500));
        }

        [Test]
        public void 書かなければ既定値()
        {
            var settings = NetSuiteConnectionSettings.FromDataSource(Source("Provider=NetSuite;ServiceUrl=https://x.suitetalk.api.netsuite.com;TimeoutSeconds=abc;MaxConcurrency=0;MaxRows=-1"))!;
            Assert.That(settings.Algorithm, Is.EqualTo("PS256"));
            Assert.That(settings.Scope, Is.EqualTo("rest_webservices"));
            Assert.That(settings.TimeoutSeconds, Is.EqualTo(100));
            Assert.That(settings.MaxConcurrency, Is.EqualTo(4));
            Assert.That(settings.MaxRows, Is.EqualTo(10000));
            Assert.That(settings.Records, Is.Empty);
            Assert.That(settings.ClientId, Is.Empty);
            Assert.That(settings.PrivateKey, Is.Empty);
        }

        [Test]
        public void Validate_揃っていれば通る()
        {
            Assert.DoesNotThrow(() => TestConnections.CreateSettings().Validate());
            Assert.DoesNotThrow(() => TestConnections.CreateSettings(e => { e.PrivateKey = ""; e.PrivateKeyPath = "C:\\keys\\ns.pem"; }).Validate());
        }

        [TestCase(nameof(NetSuiteConnectionSettings.ServiceUrl))]
        [TestCase(nameof(NetSuiteConnectionSettings.ClientId))]
        [TestCase(nameof(NetSuiteConnectionSettings.CertificateId))]
        public void Validate_必須の欠落(string name)
        {
            var settings = TestConnections.CreateSettings(e => typeof(NetSuiteConnectionSettings).GetProperty(name)!.SetValue(e, " "));
            var ex = Assert.Throws<NetSuiteException>(() => settings.Validate());
            Assert.That(ex!.Message, Does.Contain(name));
        }

        [Test]
        public void Validate_秘密鍵がどちらも無い()
        {
            var settings = TestConnections.CreateSettings(e => { e.PrivateKey = ""; e.PrivateKeyPath = ""; });
            var ex = Assert.Throws<NetSuiteException>(() => settings.Validate());
            Assert.That(ex!.Message, Does.Contain("PrivateKeyPath").And.Contain("PrivateKey"));
        }

        [TestCase("http://1234567.suitetalk.api.netsuite.com")]
        [TestCase("http://example.com:5080")]
        [TestCase("http://192.168.0.10:5080")]
        [TestCase("1234567.suitetalk.api.netsuite.com")]
        [TestCase("ftp://1234567.suitetalk.api.netsuite.com")]
        [TestCase("ftp://localhost")]
        public void Validate_httpsでない(string url)
        {
            var settings = TestConnections.CreateSettings(e => e.ServiceUrl = url);
            var ex = Assert.Throws<NetSuiteException>(() => settings.Validate());
            Assert.That(ex!.Message, Does.Contain("https"));
        }

        //このマシンの中で動かすモックに限り http を許す
        [TestCase("http://localhost:5080")]
        [TestCase("http://localhost")]
        [TestCase("http://127.0.0.1:5080")]
        [TestCase("http://[::1]:5080")]
        public void Validate_ループバックのhttpは通る(string url)
        {
            Assert.DoesNotThrow(() => TestConnections.CreateSettings(e => e.ServiceUrl = url).Validate());
        }

        [Test]
        public void Validate_Recordsが空()
        {
            var settings = TestConnections.CreateSettings(e => e.Records = new());
            var ex = Assert.Throws<NetSuiteException>(() => settings.Validate());
            Assert.That(ex!.Message, Does.Contain("Records"));
        }

        [Test]
        public void 接続のコンストラクタも検証する()
        {
            Assert.Throws<NetSuiteException>(() => new NetSuiteConnection(TestConnections.CreateSettings(e => e.Records = new()), new FakeNetSuite()));
        }
    }
}
