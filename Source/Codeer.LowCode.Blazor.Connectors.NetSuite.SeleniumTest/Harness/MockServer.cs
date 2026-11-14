using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NetSuite.Mock;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.Harness
{
    /// <summary>
    /// モックを Kestrel で 127.0.0.1 の空きポートに立てる (Web アプリは別プロセスなので実ポートで待ち受ける)。
    /// データはテストのプロセスの中 (Mock) にあり、テストは画面の操作の結果をここで確かめる。
    /// </summary>
    public sealed class MockServer : IAsyncDisposable
    {
        readonly WebApplication _app;

        public MockNetSuite Mock { get; }
        public string ServiceUrl { get; }

        public const string Records = "subsidiary,customer,customrecord_sample";

        MockServer(WebApplication app, MockNetSuite mock, string serviceUrl)
        {
            _app = app;
            Mock = mock;
            ServiceUrl = serviceUrl;
        }

        /// <summary>付いている mock-data.json の初期データで起動する。</summary>
        public static async Task<MockServer> StartAsync()
        {
            var mock = MockNetSuite.FromFile(Path.Combine(AppContext.BaseDirectory, "mock-data.json"));
            var serviceUrl = $"http://127.0.0.1:{FreePort.Get()}";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseUrls(serviceUrl);
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.MapMockNetSuite(mock);
            await app.StartAsync();
            return new MockServer(app, mock, serviceUrl);
        }

        /// <summary>Web アプリの NetSuite のデータソースの接続文字列 (鍵はテストがその場で作ったもの)。</summary>
        public string GetConnectionString(string privateKeyPath)
            => $"Provider=NetSuite;ServiceUrl={ServiceUrl};ClientId=mock-client;CertificateId=mock-cert;PrivateKeyPath={privateKeyPath};Records={Records}";

        /// <summary>モックのデータを SuiteQL で読む。値はすべて文字列 (空の項目は無い)、キーは小文字。</summary>
        public List<Dictionary<string, string>> Query(string suiteQl, params string[] parameters)
        {
            var body = new JsonObject { ["q"] = suiteQl, ["params"] = new JsonArray(parameters.Select(e => (JsonNode?)e).ToArray()) };
            var response = Mock.QuerySuiteQl(null, null, "transient", body.ToJsonString());
            if (response.Status != 200) throw new InvalidOperationException($"SuiteQL failed ({response.Status}): {response.Body}");
            var items = JsonNode.Parse(response.Body!)!["items"]!.AsArray();
            return items.Select(item => item!.AsObject().Where(e => e.Key != "links").ToDictionary(e => e.Key, e => e.Value!.GetValue<string>())).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            Mock.Dispose();
        }
    }
}
