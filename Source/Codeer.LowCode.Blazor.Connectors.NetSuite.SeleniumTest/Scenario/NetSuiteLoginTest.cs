using Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.Harness;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.Scenario;

/// <summary>
/// NetSuite のアカウントでアプリにログインする (Extras の外部ログイン = appsettings の OidcLogins に NetSuite を 1 件)。相手はモックの OpenID Connect。
/// 方式 1: ユーザーモジュールは AppUser (SQLite)。NetSuite のメールが AppUser のメールの列と一致すればログインできる。
/// 方式 2: ユーザーモジュールを Employee (NetSuite の employee) にしたデザイン。
/// ほかのテストと分けるため、モック (https つき)・アプリ・Chrome をこのクラスで別に立てる。
/// モックの https は ASP.NET Core の開発用証明書を使う (dotnet dev-certs https --trust 済みのマシンで動かす)。
/// </summary>
[NonParallelizable]
public class NetSuiteLoginTest
{
#pragma warning disable NUnit1032
    MockServer _mock = default!;
    AppServer _app = default!;
    IWebDriver _driver = default!;
#pragma warning restore NUnit1032
    AppServer? _failedServer;

    //モックの従業員 (mock-data.json の employee)。1 は Example/Data/main.sql の AppUser にメールがある、2 は無い
    const string RegisteredEmployee = "1";
    const string UnregisteredEmployee = "2";

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _mock = await MockServer.StartAsync(https: true);
        _app = await AppServer.StartAsync(_mock, settings: OidcSettings(_mock));
        var options = new ChromeOptions();
        options.AddArgument("--headless=new");
        options.AddArgument("--window-size=1600,1000");
        _driver = new ChromeDriver(options);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        _driver?.Quit();
        _driver?.Dispose();
        _app?.Dispose();
        if (_mock != null) await _mock.DisposeAsync();
    }

    [TearDown]
    public void TearDown()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Failed) return;
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "TestResults");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{TestContext.CurrentContext.Test.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            ((ITakesScreenshot)_driver).GetScreenshot().SaveAsFile(path);
            TestContext.AddTestAttachment(path);
        }
        catch (WebDriverException) { }
        TestContext.Out.WriteLine((_failedServer ?? _app).Log);
    }

    //外部ログインの設定 (シークレットはテストの中で作る使い捨て。モックは中身を確かめない)
    static Dictionary<string, string> OidcSettings(MockServer mock) => new()
    {
        ["OidcLogins__0__Name"] = "NetSuite",
        ["OidcLogins__0__DisplayName"] = "NetSuite",
        ["OidcLogins__0__ClientId"] = "selenium-test-client",
        ["OidcLogins__0__ClientSecret"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        ["OidcLogins__0__Authority"] = mock.OidcAuthority,
        ["OidcLogins__0__Scopes__0"] = "openid",
        ["OidcLogins__0__Scopes__1"] = "email",
        ["OidcLogins__0__LoginNameClaim"] = "email",
    };

    //ログアウトした状態でログイン画面を開く (同じホストのアプリは Cookie を共有するので全部消す)
    LoginForm OpenLoginPage(AppServer app)
    {
        _driver.Url = app.BaseUrl + "/login.html";
        _driver.Manage().Cookies.DeleteAllCookies();
        _driver.Url = app.BaseUrl + "/login.html";
        return _driver.AttachLoginForm();
    }

    //ログイン画面の「NetSuite でログイン」→ モックの画面で従業員を選ぶ
    void LoginWithNetSuite(AppServer app, string employeeId)
    {
        OpenLoginPage(app);
        var button = _driver.WaitUntil(() => _driver.FindElements(By.CssSelector("#Providers button")).FirstOrDefault(e => e.Text.Contains("NetSuite")))!;
        button.Click();
        _driver.WaitUntil(() => _driver.Url.StartsWith(_mock.OidcAuthority, StringComparison.OrdinalIgnoreCase));
        _driver.WaitUntil(() => _driver.FindElement(By.Id("employee-" + employeeId))).Click();
    }

    //Home のログイン中のユーザーの表示
    string WaitCurrentUserName(AppServer app)
    {
        _driver.WaitUntil(() => _driver.Url.StartsWith(app.BaseUrl, StringComparison.OrdinalIgnoreCase) && !_driver.Url.Contains("login.html", StringComparison.OrdinalIgnoreCase));
        return _driver.WaitUntil(() =>
        {
            var text = _driver.FindElement(By.CssSelector("[data-name='CurrentUserName']")).Text;
            return string.IsNullOrEmpty(text) ? null : text;
        })!;
    }

    [Test]
    public void 方式1_AppUserにメールがある従業員はNetSuiteでログインできる()
    {
        LoginWithNetSuite(_app, RegisteredEmployee);
        //表示名はユーザー行 (AppUser) のもの。NetSuite の ID トークンに表示名は無い
        WaitCurrentUserName(_app).Is("ログイン中: 佐藤 一郎");
    }

    [Test]
    public void 方式1_AppUserに行が無い従業員はuser_not_registeredでログイン画面に戻る()
    {
        LoginWithNetSuite(_app, UnregisteredEmployee);
        _driver.WaitUntil(() => _driver.Url.Contains("login.html?error=user_not_registered", StringComparison.OrdinalIgnoreCase));
        var form = _driver.AttachLoginForm();
        _driver.WaitUntil(() => form.ErrorMessage.Text == "このアカウントは登録されていません");
    }

    [Test]
    public void 方式1_パスワードのログインも使える()
    {
        var form = OpenLoginPage(_app);
        form.Login("admin", "admin");
        WaitCurrentUserName(_app).Is("ログイン中: 管理者");
    }

    [Test]
    [Ignore("ホストの結線待ち: Example の ExternalLoginUserResolver などはユーザー行を DataService.DbAccess (NetSuite を包む前の DbAccessor) で読むので、NetSuite のユーザーモジュールを読めない。DataService.DbAccess を IDbAccessor にすると ModuleDataController の DbAccess.ClearAsync() がコンパイルできない")]
    public async Task 方式2_ユーザーモジュールをEmployeeにするとNetSuiteの従業員がそのまま現在のユーザーになる()
    {
        //デザインの写しで、ユーザーモジュールを Employee に替える
        using var app = await AppServer.StartAsync(_mock, editDesign: dir =>
        {
            var path = Path.Combine(dir, "app.clprj");
            var project = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            project["CurrentUserModuleDesignName"] = "Employee";
            File.WriteAllText(path, project.ToJsonString());
        }, settings: OidcSettings(_mock));
        _failedServer = app;
        try
        {
            LoginWithNetSuite(app, "3");
            //表示名は NetSuite の employee の名前 (entityId)
            WaitCurrentUserName(app).Is("ログイン中: Emily Brown");
            _driver.Url = app.BaseUrl + "/api/account/current_user";
            _driver.WaitUntil(() => _driver.PageSource.Contains("\"3\""));
            _failedServer = null;
        }
        finally
        {
            //失敗したときの出力は TearDown で書く (ここで止めるとプロセスの出力は残る)
            if (_failedServer != null) TestContext.Out.WriteLine(app.Log);
            _failedServer = null;
        }
    }
}
