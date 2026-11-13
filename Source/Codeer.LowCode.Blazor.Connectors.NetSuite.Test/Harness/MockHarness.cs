using NetSuite.Mock;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness
{
    /// <summary>
    /// モック (Mock プロジェクト) をプロセスの中で立て (実ポートは開かない)、本物のコネクターと本体の ModuleDataIO をつなぐ。
    /// データは mock-data.json (既定の架空のサンプル)。モジュール: Subsidiary / Customer / Sample (customrecord_sample)。データソース名は "NS"。
    /// </summary>
    public sealed class MockHarness : IAsyncDisposable
    {
        public const string ServiceUrl = "http://localhost";

        WebApplication _app = null!;
        public MockNetSuite Mock { get; private set; } = null!;
        /// <summary>モックへの HTTP (コネクターを通さずに要求を送るとき)。</summary>
        public HttpMessageHandler Handler { get; private set; } = null!;
        internal NetSuiteConnection Connection { get; private set; } = null!;
        public RecordingDbAccessor Inner { get; } = new();
        public NetSuiteDbAccessor Accessor { get; private set; } = null!;
        public DesignData Design { get; private set; } = null!;
        public ModuleDataIO IO { get; private set; } = null!;

        MockHarness() { }

        class Auth : IAuthenticationContext
        {
            public Task<string> GetCurrentUserIdAsync() => Task.FromResult("U1");
        }

        public static string DataPath => Path.Combine(AppContext.BaseDirectory, "mock-data.json");

        /// <summary>コネクターが送った要求 (トークンを含む)。</summary>
        public RecordingHandler Recorder { get; private set; } = null!;

        /// <param name="mock">既定は mock-data.json。</param>
        /// <param name="editDesign">モジュールを足す・変えるとき (ModuleDataIO を作る前に呼ぶ)。</param>
        /// <param name="metadataDirectory">NetSuiteDbAccessor に渡すメタ情報のファイルのフォルダ。</param>
        public static async Task<MockHarness> StartAsync(MockNetSuite? mock = null, Action<NetSuiteConnectionSettings>? edit = null,
            Action<DesignData>? editDesign = null, string? metadataDirectory = null)
        {
            var h = new MockHarness { Mock = mock ?? MockNetSuite.FromFile(DataPath) };

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            h._app = builder.Build();
            h._app.MapMockNetSuite(h.Mock);
            await h._app.StartAsync();
            h.Handler = h._app.GetTestServer().CreateHandler();

            var settings = new NetSuiteConnectionSettings
            {
                Name = TestConnections.DataSourceName,
                ServiceUrl = ServiceUrl,
                ClientId = "mock-client",
                CertificateId = "mock-cert",
                PrivateKey = TestConnections.RsaPrivateKeyPem,
                Records = new() { "subsidiary", "customer", "customrecord_sample" },
            };
            edit?.Invoke(settings);
            h.Recorder = new RecordingHandler(h.Handler);
            h.Connection = h.CreateConnection(settings);
            h.Accessor = new NetSuiteDbAccessor(h.Inner, new[] { h.Connection }, metadataDirectory);
            h.Design = CreateDesign();
            editDesign?.Invoke(h.Design);
            h.IO = new ModuleDataIO(h.Design, new Auth(), h.Accessor, null!);
            return h;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            Mock.Dispose();
        }

        /// <summary>このモックへの、新しい接続 (トークン・定義のキャッシュを持たない)。要求は <see cref="Recorder"/> に記録される。</summary>
        internal NetSuiteConnection CreateConnection(NetSuiteConnectionSettings settings)
            => new(settings, Recorder) { Delay = _ => Task.CompletedTask };

        /// <summary>コネクターを通さずにモックへ送る HttpClient (BaseAddress は http://localhost)。</summary>
        public HttpClient CreateClient() => new(Handler, false) { BaseAddress = new Uri(ServiceUrl) };

        static DesignData CreateDesign()
        {
            var d = new DesignData();
            var modules = (IEditableModuleDesign)d.Modules;
            var ds = TestConnections.DataSourceName;

            var subsidiary = new ModuleDesign { Name = "Subsidiary", DataSourceName = ds, DbTable = "subsidiary", CanCreate = true, CanUpdate = true, CanDelete = true };
            subsidiary.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            subsidiary.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "name" });
            subsidiary.Fields.Add(new TextFieldDesign { Name = "Country", DbColumn = "country" });
            modules.Add(subsidiary);

            var customer = new ModuleDesign { Name = "Customer", DataSourceName = ds, DbTable = "customer", CanCreate = true, CanUpdate = true, CanDelete = true };
            customer.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            customer.Fields.Add(new TextFieldDesign { Name = "CompanyName", DbColumn = "companyname" });
            customer.Fields.Add(new TextFieldDesign { Name = "Email", DbColumn = "email" });
            customer.Fields.Add(new BooleanFieldDesign { Name = "IsInactive", DbColumn = "isinactive" });
            customer.Fields.Add(new NumberFieldDesign { Name = "CreditLimit", DbColumn = "creditlimit" });
            customer.Fields.Add(new NumberFieldDesign { Name = "Priority", DbColumn = "priority" });
            customer.Fields.Add(new DateFieldDesign { Name = "StartDate", DbColumn = "startdate" });
            customer.Fields.Add(new DateTimeFieldDesign { Name = "LastModified", DbColumn = "lastmodifieddate" });
            customer.Fields.Add(new LinkFieldDesign
            {
                Name = "Subsidiary",
                DbColumn = "subsidiary",
                SearchCondition = new SearchCondition("Subsidiary"),
                ValueVariable = "Id.Value",
                DisplayTextVariable = "Name.Value",
            });
            //リンク越しの項目 (一覧の表示・検索に使う)
            customer.Fields.Add(new TextFieldDesign { Name = "Subsidiary.Name", DbColumn = "name" });
            modules.Add(customer);

            var sample = new ModuleDesign { Name = "Sample", DataSourceName = ds, DbTable = "customrecord_sample", CanCreate = true, CanUpdate = true, CanDelete = true };
            sample.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            sample.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "name" });
            sample.Fields.Add(new NumberFieldDesign { Name = "Quantity", DbColumn = "custrecord_quantity" });
            sample.Fields.Add(new NumberFieldDesign { Name = "Amount", DbColumn = "custrecord_amount" });
            sample.Fields.Add(new DateFieldDesign { Name = "OrderDate", DbColumn = "custrecord_date" });
            sample.Fields.Add(new SelectFieldDesign { Name = "Status", DbColumn = "custrecord_status", Candidates = new() { "Open,open", "Won,won", "Lost,lost" } });
            sample.Fields.Add(new BooleanFieldDesign { Name = "Done", DbColumn = "custrecord_done" });
            sample.Fields.Add(new DateTimeFieldDesign { Name = "Recorded", DbColumn = "custrecord_recorded", SaveAsUtc = true });
            sample.Fields.Add(new LinkFieldDesign
            {
                Name = "Customer",
                DbColumn = "custrecord_customer",
                SearchCondition = new SearchCondition("Customer"),
                ValueVariable = "Id.Value",
                DisplayTextVariable = "CompanyName.Value",
            });
            sample.Fields.Add(new TextFieldDesign { Name = "Customer.CompanyName", DbColumn = "companyname" });
            modules.Add(sample);
            return d;
        }

        // ---------------------------------------------------------------- 期待値の計算用 (mock-data.json の初期の行)

        /// <summary>mock-data.json のレコードの初期の行。</summary>
        public static List<JsonObject> Rows(string record)
        {
            var root = JsonNode.Parse(File.ReadAllText(DataPath))!;
            return root["records"]![record]!["rows"]!.AsArray().Select(e => e!.AsObject()).ToList();
        }
    }

    /// <summary>送った要求を記録して、モックへ渡す。</summary>
    public sealed class RecordingHandler : DelegatingHandler
    {
        readonly object _lock = new();
        readonly List<(HttpMethod Method, string Path, string Body)> _requests = new();

        public RecordingHandler(HttpMessageHandler inner) : base(inner) { }

        public List<(HttpMethod Method, string Path, string Body)> Requests
        {
            get { lock (_lock) return _requests.ToList(); }
        }

        public void Clear()
        {
            lock (_lock) _requests.Clear();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_lock) _requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>mock-data.json の行の値の読み出し。</summary>
    public static class MockRowExtensions
    {
        public static string Id(this JsonObject row) => row["id"]!.ToString();
        public static string? Text(this JsonObject row, string name) => row[name]?.GetValue<string>();
        public static decimal? Number(this JsonObject row, string name) => row[name]?.GetValue<decimal>();
        public static bool? Bool(this JsonObject row, string name) => row[name]?.GetValue<bool>();
        public static DateOnly? Date(this JsonObject row, string name) => row[name] == null ? null : DateOnly.Parse(row[name]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        public static DateTime? DateTimeUtc(this JsonObject row, string name)
            => row[name] == null ? null : DateTimeOffset.Parse(row[name]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
        public static string? Ref(this JsonObject row, string name) => row[name]?["id"]?.GetValue<string>();
    }
}
