using NetSuite.Mock;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

//NetSuite のモック。
//  dotnet run --project Source/NetSuite.Mock [--urls http://localhost:5080] [--data <mock-data.json>]
var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://localhost:5080");

var dataPath = builder.Configuration["data"];
dataPath = string.IsNullOrEmpty(dataPath) ? Path.Combine(AppContext.BaseDirectory, "mock-data.json") : Path.GetFullPath(dataPath);
using var mock = MockNetSuite.FromFile(dataPath);

//コネクターが JWT の署名に使う鍵。モックは署名を確かめないので、その場で作った使い捨ての鍵でよい
var keyPath = Path.Combine(AppContext.BaseDirectory, "mock-key.pem");
if (!File.Exists(keyPath))
{
    using var rsa = RSA.Create(3072);
    File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
}

var app = builder.Build();
app.MapMockNetSuite(mock);

app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = app.Urls.FirstOrDefault(e => e.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? app.Urls.FirstOrDefault() ?? "http://localhost:5080";
    var uri = new UriBuilder(url);
    if (uri.Host is "0.0.0.0" or "[::]" or "::" or "+" or "*") uri.Host = "localhost";
    var serviceUrl = uri.Uri.GetLeftPart(UriPartial.Authority);

    var dataSource = new Dictionary<string, string>
    {
        ["Name"] = "NetSuite",
        ["DataSourceType"] = "Oracle",
        ["ConnectionString"] = $"Provider=NetSuite;ServiceUrl={serviceUrl};ClientId=mock-client;CertificateId=mock-cert;PrivateKeyPath={keyPath};Records={string.Join(",", mock.Records.Select(e => e.Name))}",
    };
    var json = JsonSerializer.Serialize(dataSource, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    Console.WriteLine();
    Console.WriteLine("NetSuite のモックを起動しました: " + serviceUrl);
    Console.WriteLine("データ: " + dataPath);
    Console.WriteLine("アプリ (appsettings.json) とデザイナの DataSources に、次のデータソースを足してください:");
    Console.WriteLine();
    Console.WriteLine(json);
    Console.WriteLine();
    Console.WriteLine("データはこのプロセスの中だけにあります。止めると初期データに戻ります。終了は Ctrl+C。");
    Console.WriteLine();
});

app.Run();

/// <summary>テストから WebApplicationFactory などで立てるための宣言。</summary>
public partial class Program { }
