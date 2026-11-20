using Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject;
using OpenQA.Selenium;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.Scenario;

/// <summary>
/// 受注の添付ファイル (NetSuite の添付ファイルのフィールド): 一覧・ダウンロード (ホストの口)・足す・外す。
/// 受注 1 (First order) には mock-data.json でファイル 500 (見積書.txt) と 501 (spec.csv) が付いている。結果はモックの File Cabinet と添付で確かめる。
/// </summary>
public class SalesOrderAttachmentsTest
{
    IWebDriver _driver = default!;
    string? _tempFile;

    [SetUp]
    public void SetUp() => _driver = WebDriverManager.Driver;

    [TearDown]
    public void TearDown()
    {
        WebDriverManager.FailedCleanup();
        if (_tempFile != null) File.Delete(_tempFile);
    }

    IWebElement Files => _driver.FindElement(By.CssSelector("[data-name='Files']"));

    List<string> LinkNames() => Files.FindElements(By.TagName("a")).Select(e => e.Text).ToList();

    //ブラウザのログインのまま GET して、状態と本文を返す
    (long Status, string Body) Fetch(string url)
    {
        var result = (IReadOnlyCollection<object>)((IJavaScriptExecutor)_driver).ExecuteAsyncScript(
            "const done = arguments[arguments.length - 1]; fetch(arguments[0]).then(r => r.text().then(t => done([r.status, t]))).catch(e => done([0, String(e)]));", url);
        var values = result.ToList();
        return ((long)values[0], (string)values[1]);
    }

    [Test]
    public void 付いているファイルを一覧してダウンロードでき_ほかのレコードのファイルは取れない()
    {
        OpenDetail("First order");
        _driver.WaitUntil(() => LinkNames().Count == 2);
        LinkNames().Is("見積書.txt", "spec.csv");

        var link = Files.FindElements(By.TagName("a")).Single(e => e.Text == "見積書.txt");
        var href = link.GetAttribute("href");
        href.EndsWith("api/netsuite/attachments/download?module=SalesOrder&id=1&field=Files&fileId=500", StringComparison.Ordinal).IsTrue();
        Fetch(href).Is((200L, "Quotation for sales order 1 (sample)."));

        //受注 2 にはファイル 500 は付いていない
        Fetch(href.Replace("&id=1&", "&id=2&")).Item1.Is(404L);
    }

    [Test]
    public void ファイルを足して保存すると添付され_外して保存するとレコードから外れてFileCabinetには残る()
    {
        var mock = WebDriverManager.Mock.Mock;
        var before = mock.GetAttachedFileIds("salesOrder", "4");
        //ファイルの無いレコードで始める (外すと 1 個も無くなる場合を確かめる)
        before.Count.Is(0);
        _tempFile = Path.Combine(Path.GetTempPath(), $"clb-netsuite-{Guid.NewGuid():N}.txt");
        File.WriteAllText(_tempFile, "added from selenium");
        var name = Path.GetFileName(_tempFile);

        //足す
        var detail = OpenDetail("Spare parts");
        Files.FindElement(By.CssSelector("input[type='file']")).SendKeys(_tempFile);
        _driver.WaitUntil(() => Files.Text.Contains(name) && Files.Text.Contains("未保存"));
        detail.SubmitButton.Submit.Click();
        var added = _driver.WaitUntil(() => mock.GetAttachedFileIds("salesOrder", "4").Except(before).SingleOrDefault())!;
        mock.FindFile(added)!.Name.Is(name);
        mock.FindFile(added)!.FolderId.Is("100");

        //開き直すとダウンロードのリンクになる
        OpenDetail("Spare parts");
        _driver.WaitUntil(() => LinkNames().Contains(name));
        Fetch(Files.FindElements(By.TagName("a")).Single(e => e.Text == name).GetAttribute("href")).Is((200L, "added from selenium"));

        //外す
        var row = Files.FindElements(By.CssSelector("div.d-flex")).Single(e => e.Text.Contains(name));
        row.FindElement(By.CssSelector("button[title^='外す']")).Click();
        _driver.WaitUntil(() => Files.Text.Contains("外す"));
        _driver.AttachSalesOrderDetailPage().Detail.SubmitButton.Submit.Click();
        _driver.WaitUntil(() => !mock.GetAttachedFileIds("salesOrder", "4").Contains(added));
        mock.FindFile(added).IsNotNull();
        //最後の 1 個を外したので、読み直した画面にファイルは残らない (ファイルの無いレコードは値が来ない = 前の値を消す)
        _driver.WaitUntil(() => !Files.Text.Contains(name) && !Files.Text.Contains("未保存") && !Files.Text.Contains("外す"));
    }

    SalesOrderDetailLayout OpenDetail(string memo)
    {
        WebDriverManager.Navigate("/Main/SalesOrder");
        var page = _driver.AttachSalesOrderListPage();
        page.Search.Fields.Memo.Text.Edit(memo);
        page.Search.Search.Click();
        _driver.WaitUntil(() => page.List.Items.Count == 1 && page.List.Items.GetItem(0).Memo.Element.Text == memo);
        page.List.Items.GetItem(0).NavigateToDetail.Click();
        var detail = _driver.AttachSalesOrderDetailPage().Detail;
        _driver.WaitUntil(() => detail.Memo.Input.Text == memo);
        return detail;
    }
}
