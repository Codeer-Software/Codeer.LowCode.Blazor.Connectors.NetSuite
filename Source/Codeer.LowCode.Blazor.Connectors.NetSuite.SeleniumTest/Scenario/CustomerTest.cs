using Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject;
using OpenQA.Selenium;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.Scenario;

/// <summary>
/// 顧客 (NetSuite の customer) の一覧・検索・作成・更新・削除。保存の結果はモックのデータで確かめる。
/// 一覧と検索はモックの初期データ (mock-data.json) を前提にする。
/// </summary>
public class CustomerTest
{
    IWebDriver _driver = default!;

    [SetUp]
    public void SetUp()
    {
        _driver = WebDriverManager.Driver;
        WebDriverManager.Navigate("/Main/Customer");
    }

    [TearDown]
    public void TearDown() => WebDriverManager.FailedCleanup();

    [Test]
    public void 一覧にリンク先の子会社の名前が出る()
    {
        var page = _driver.AttachCustomerListPage();
        _driver.WaitUntil(() => page.List.Items.Count == 20);

        var row = FindRow(page, "Alpha Trading");
        row.Subsidiary.Element.Text.Is("Japan Sales");
    }

    [Test]
    public void 検索で絞れる()
    {
        var page = _driver.AttachCustomerListPage();
        _driver.WaitUntil(() => page.List.Items.Count == 20);

        page.Search.Fields.CompanyName.Text.Edit("Gamma");
        page.Search.Search.Click();
        _driver.WaitUntil(() => page.List.Items.Count == 1);

        var row = page.List.Items.GetItem(0);
        row.CompanyName.Element.Text.Is("Gamma Systems");
        row.Subsidiary.Element.Text.Is("US Sales");
    }

    [Test]
    public void 作成_更新_削除がNetSuiteに反映される()
    {
        var companyName = "Selenium " + Guid.NewGuid().ToString("N")[..8];

        //作成
        _driver.AttachCustomerListPage().Create.Click();
        var create = _driver.AttachCustomerDetailPage().Detail;
        create.CompanyName.Input.Edit(companyName);
        create.Subsidiary.Search.Click();
        _driver.WaitUntil(() => create.Subsidiary.LinkList.Items.Count == 3);
        FindLinkRow(create, "Headquarters").Name.Element.DoubleClick();
        _driver.WaitUntil(() => create.Subsidiary.Input.Text == "Headquarters");
        create.Email.Input.Edit("create@example.com");
        create.SubmitButton.Submit.Click();

        var created = _driver.WaitUntil(() => FindCustomer(companyName))!;
        created["subsidiary"].Is("1");
        created["email"].Is("create@example.com");

        //更新
        var update = OpenDetail(companyName);
        _driver.WaitUntil(() => update.CompanyName.Input.Text == companyName);
        update.Email.Input.Edit("update@example.com");
        update.SubmitButton.Submit.Click();
        _driver.WaitUntil(() => FindCustomer(companyName)?["email"] == "update@example.com");

        //削除
        WebDriverManager.Navigate("/Main/Customer");
        var list = SearchByCompanyName(companyName);
        list.List.Items.GetItem(0).Delete.Click();
        _driver.ClickMessageBoxButton(0);
        _driver.WaitUntil(() => FindCustomer(companyName) == null);
    }

    static CustomerListLayout FindRow(CustomerListPage page, string companyName)
    {
        var items = page.List.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var row = items.GetItem(i);
            if (row.CompanyName.Element.Text == companyName) return row;
        }
        throw new AssertionException($"No row '{companyName}'.");
    }

    static SubsidiaryListLayout FindLinkRow(CustomerDetailLayout detail, string name)
    {
        var items = detail.Subsidiary.LinkList.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var row = items.GetItem(i);
            if (row.Name.Element.Text == name) return row;
        }
        throw new AssertionException($"No subsidiary '{name}'.");
    }

    CustomerListPage SearchByCompanyName(string companyName)
    {
        var page = _driver.AttachCustomerListPage();
        page.Search.Fields.CompanyName.Text.Edit(companyName);
        page.Search.Search.Click();
        _driver.WaitUntil(() => page.List.Items.Count == 1 && page.List.Items.GetItem(0).CompanyName.Element.Text == companyName);
        return page;
    }

    CustomerDetailLayout OpenDetail(string companyName)
    {
        WebDriverManager.Navigate("/Main/Customer");
        SearchByCompanyName(companyName).List.Items.GetItem(0).NavigateToDetail.Click();
        return _driver.AttachCustomerDetailPage().Detail;
    }

    //モックの customer (見つからなければ null)
    static Dictionary<string, string>? FindCustomer(string companyName)
        => WebDriverManager.Mock.Query("SELECT id, companyname, email, subsidiary FROM customer WHERE companyname = ?", companyName).SingleOrDefault();
}
