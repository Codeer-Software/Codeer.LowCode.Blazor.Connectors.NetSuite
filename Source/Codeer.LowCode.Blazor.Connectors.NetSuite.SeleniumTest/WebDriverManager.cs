using Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.Harness;
using NUnit.Framework.Interfaces;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using Selenium.StandardControls;
using Selenium.StandardControls.PageObjectUtility;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest
{
    /// <summary>
    /// テスト全体で 1 つずつ: NetSuite のモック (このプロセスの中)・Example の Web アプリ (別プロセス)・Chrome (ヘッドレス)。
    /// 最初に admin / admin でログインまで済ませる。終わったら、ここで起動したものだけを止める。
    /// </summary>
    [SetUpFixture]
    public class WebDriverManager
    {
        /// <summary>画面の変化を待つ上限。超えたらフリーズとみなして失敗にする。</summary>
        public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

#pragma warning disable NUnit1032
        static MockServer? _mock;
        static AppServer? _server;
        static IWebDriver? _driver;
#pragma warning restore NUnit1032

        public static MockServer Mock => _mock ?? throw new InvalidOperationException("not started");
        public static IWebDriver Driver => _driver ?? throw new InvalidOperationException("not started");
        public static string BaseUrl => (_server ?? throw new InvalidOperationException("not started")).BaseUrl;

        [OneTimeSetUp]
        public async Task SetUp()
        {
            Settings.DefaultWaitTime = WaitTimeout;
            _mock = await MockServer.StartAsync();
            _server = await AppServer.StartAsync(_mock);

            var options = new ChromeOptions();
            options.AddArgument("--headless=new");
            options.AddArgument("--window-size=1600,1000");
            //Selenium Manager がインストール済み Chrome に合う chromedriver を自動取得する
            _driver = new ChromeDriver(options);

            _driver.Url = BaseUrl;
            _driver.AttachLoginForm().Login("admin", "admin");
            _driver.WaitUntil(() => !_driver.Url.Contains("login.html", StringComparison.OrdinalIgnoreCase));
        }

        [OneTimeTearDown]
        public async Task TearDown()
        {
            if (_driver != null)
            {
                _driver.Quit();
                _driver.Dispose();
                _driver = null;
            }
            _server?.Dispose();
            _server = null;
            if (_mock != null) await _mock.DisposeAsync();
            _mock = null;
        }

        /// <summary>ページ内の URL へ移動する ("/Main/Customer" のようにルート相対で渡す)。</summary>
        public static void Navigate(string relativePath)
        {
            Driver.Url = BaseUrl + "/" + relativePath.TrimStart('/');
            WaitLoading();
        }

        /// <summary>ローディング表示 (backdrop) が無くなるまで待つ。</summary>
        public static void WaitLoading()
            => Driver.WaitUntil(() => Driver.FindElements(By.ClassName("backdrop")).Count == 0);

        /// <summary>
        /// 各テストの [TearDown] から呼ぶ。失敗したらスクリーンショットとサーバーの出力を残す。
        /// </summary>
        public static void FailedCleanup()
        {
            if (TestContext.CurrentContext.Result.Outcome.Status != TestStatus.Failed) return;
            try
            {
                if (_driver is ITakesScreenshot shot)
                {
                    var dir = Path.Combine(AppContext.BaseDirectory, "TestResults");
                    Directory.CreateDirectory(dir);
                    var name = string.Join("_", TestContext.CurrentContext.Test.FullName.Split(Path.GetInvalidFileNameChars()));
                    var path = Path.Combine(dir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                    shot.GetScreenshot().SaveAsFile(path);
                    TestContext.AddTestAttachment(path);
                }
            }
            catch (WebDriverException) { }
            if (_server != null) TestContext.Out.WriteLine(_server.Log);
        }
    }
}
