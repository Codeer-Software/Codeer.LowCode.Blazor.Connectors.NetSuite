using NetSuite.Mock;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Text.Json;

//NetSuite のモック。
//  dotnet run --project Source/NetSuite.Mock [--urls http://localhost:5080] [--data <mock-data.json>]
//OpenID Connect (アプリのログイン) は https が要る (ASP.NET Core の OpenID Connect はメタデータを https でしか読まない)。
//--urls を書かなければ、ASP.NET Core の開発用証明書 (dotnet dev-certs https) があるときだけ https://localhost:5081 でも待ち受ける
var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls(HasDevelopmentCertificate() ? new[] { "http://localhost:5080", "https://localhost:5081" } : new[] { "http://localhost:5080" });

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
        ["ConnectionString"] = $"Provider=NetSuite;ServiceUrl={serviceUrl};ClientId=mock-client;CertificateId=mock-cert;PrivateKeyPath={keyPath}" +
            //添付ファイル (mock-data.json のフォルダと保存検索)
            ";AttachmentFolderId=100;AttachmentSavedSearchId=customsearch_clb_transaction_files;AttachmentSearchFileColumn=file_internalid;AttachmentSearchFileNameColumn=file_name;AttachmentSearchFileSizeColumn=file_documentsize;AttachmentSearchFileTypeColumn=file_filetype",
    };
    var json = JsonSerializer.Serialize(dataSource, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    Console.WriteLine();
    Console.WriteLine("NetSuite のモックを起動しました: " + serviceUrl);
    Console.WriteLine("データ: " + dataPath);
    Console.WriteLine("アプリ (appsettings.json) とデザイナの DataSources に、次のデータソースを足してください:");
    Console.WriteLine();
    Console.WriteLine(json);
    Console.WriteLine();
    var https = app.Urls.FirstOrDefault(e => e.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
    if (https != null)
    {
        var authority = new UriBuilder(https);
        if (authority.Host is "0.0.0.0" or "[::]" or "::" or "+" or "*") authority.Host = "localhost";
        Console.WriteLine("NetSuite のアカウントでのログイン (OpenID Connect) の Authority: " + authority.Uri.GetLeftPart(UriPartial.Authority));
        Console.WriteLine("ログインできる従業員: " + string.Join(", ", mock.GetLoginEmployees().Select(e => e.Email)));
    }
    else
    {
        Console.WriteLine("NetSuite のアカウントでのログイン (OpenID Connect) を試すには https で待ち受けます (dotnet dev-certs https --trust の後に起動し直すか、--urls に https の URL を足す)。");
    }
    Console.WriteLine();
    Console.WriteLine("データはこのプロセスの中だけにあります。止めると初期データに戻ります。終了は Ctrl+C。");
    Console.WriteLine();
});

app.Run();

//ASP.NET Core の開発用証明書 (Kestrel が https に既定で使う) があるか
static bool HasDevelopmentCertificate()
{
    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
    store.Open(OpenFlags.ReadOnly);
    var now = DateTime.Now;
    return store.Certificates.Any(c => c.HasPrivateKey && c.NotBefore <= now && now <= c.NotAfter &&
        c.Extensions.Any(e => e.Oid?.Value == "1.3.6.1.4.1.311.84.1.1"));
}

/// <summary>テストから WebApplicationFactory などで立てるための宣言。</summary>
public partial class Program { }
