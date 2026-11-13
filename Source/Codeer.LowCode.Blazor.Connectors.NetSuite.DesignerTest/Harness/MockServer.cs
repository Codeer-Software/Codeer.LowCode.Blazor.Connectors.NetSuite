using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using NetSuite.Mock;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest.Harness
{
    /// <summary>
    /// モックを Kestrel で localhost (http) に立てる。デザイナは別プロセスなので実ポートで待ち受ける。
    /// 受けた HTTP 要求を数える。同じポートで止めて立て直せる (設定の ServiceUrl を変えずに済む)。
    /// </summary>
    public sealed class MockServer : IAsyncDisposable
    {
        WebApplication? _app;
        MockNetSuite? _mock;
        int _requestCount;

        public int Port { get; }
        public string ServiceUrl => $"http://127.0.0.1:{Port}";
        public int RequestCount => Volatile.Read(ref _requestCount);
        public bool IsRunning => _app != null;

        MockServer(int port) => Port = port;

        /// <summary>付いている mock-data.json。</summary>
        public static string DefaultDataPath => Path.Combine(AppContext.BaseDirectory, "mock-data.json");

        public static string DefaultRecords => "subsidiary,customer,customrecord_sample";

        public static async Task<MockServer> StartAsync(string? dataJson = null)
        {
            var server = new MockServer(GetFreePort());
            await server.RestartAsync(dataJson);
            return server;
        }

        /// <summary>止めてから、同じポートで (データを差し替えて) 立て直す。要求の数は 0 に戻す。</summary>
        public async Task RestartAsync(string? dataJson = null)
        {
            await StopAsync();
            _mock = MockNetSuite.FromJson(dataJson ?? File.ReadAllText(DefaultDataPath));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseUrls(ServiceUrl);
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Use(async (context, next) =>
            {
                Interlocked.Increment(ref _requestCount);
                await next();
            });
            app.MapMockNetSuite(_mock);
            Interlocked.Exchange(ref _requestCount, 0);
            await app.StartAsync();
            _app = app;
        }

        public async Task StopAsync()
        {
            if (_app != null)
            {
                await _app.StopAsync();
                await _app.DisposeAsync();
                _app = null;
            }
            _mock?.Dispose();
            _mock = null;
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        /// <summary>mock-data.json に項目を足した JSON。</summary>
        public static string DataWithField(string record, string field, string type, string title)
        {
            var root = JsonNode.Parse(File.ReadAllText(DefaultDataPath))!;
            root["records"]![record]!["fields"]!.AsObject()[field] = new JsonObject { ["type"] = type, ["title"] = title };
            return root.ToJsonString();
        }

        static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    /// <summary>
    /// 止まっている NetSuite の代わりに、ポートで接続を受けてすぐ切る (応答しない)。接続が来たかを数える。
    /// </summary>
    public sealed class DeadPort : IDisposable
    {
        readonly TcpListener _listener;
        readonly CancellationTokenSource _cancel = new();
        int _connectionCount;

        public int ConnectionCount => Volatile.Read(ref _connectionCount);

        public DeadPort(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_cancel.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_cancel.Token);
                        Interlocked.Increment(ref _connectionCount);
                    }
                    catch
                    {
                        if (_cancel.IsCancellationRequested) return;
                    }
                }
            });
        }

        public void Dispose()
        {
            _cancel.Cancel();
            _listener.Stop();
        }
    }
}
