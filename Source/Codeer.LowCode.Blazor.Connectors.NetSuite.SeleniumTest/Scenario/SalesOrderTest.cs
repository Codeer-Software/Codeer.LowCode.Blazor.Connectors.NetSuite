using Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject;
using OpenQA.Selenium;
using System.Globalization;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.Scenario;

/// <summary>
/// 受注 (NetSuite の salesorder) と明細 (item。列モードの一覧) の作成・更新、明細の表 (salesorder_item) の一覧・検索。
/// 保存の結果はモックのデータ (SuiteQL の transaction / transactionline) で確かめる。明細の品目は SelectField (候補 = 品目のモジュール) で選ぶ。
/// </summary>
public class SalesOrderTest
{
    IWebDriver _driver = default!;

    [SetUp]
    public void SetUp() => _driver = WebDriverManager.Driver;

    [TearDown]
    public void TearDown() => WebDriverManager.FailedCleanup();

    [Test]
    public void 明細つきの作成_数量の変更_行の追加_行の削除がNetSuiteに反映される()
    {
        var memo = "Selenium " + Guid.NewGuid().ToString("N")[..8];

        //作成 (明細 2 行)
        WebDriverManager.Navigate("/Main/SalesOrder");
        _driver.AttachSalesOrderListPage().Create.Click();
        var create = _driver.AttachSalesOrderDetailPage().Detail;
        create.Customer.Search.Click();
        _driver.WaitUntil(() => create.Customer.LinkList.Items.Count == 20);
        FindCustomerRow(create, "Beta Foods").CompanyName.Element.DoubleClick();
        _driver.WaitUntil(() => create.Customer.Input.Text == "Beta Foods");
        create.TranDate.Input.Edit(2026, 6, 1);
        create.Memo.Input.Edit(memo);
        create.Items.Create.Click();
        _driver.WaitUntil(() => create.Items.Items.Count == 1);
        EditLine(create.Items.Items.GetItem(0), "WIDGET-S", "2", "100");
        create.Items.Create.Click();
        _driver.WaitUntil(() => create.Items.Items.Count == 2);
        EditLine(create.Items.Items.GetItem(1), "GADGET", "1", "900");
        create.SubmitButton.Submit.Click();

        var order = _driver.WaitUntil(() => FindOrder(memo))!;
        order["entity"].Is("2");
        order["trandate"].Is("2026-06-01");
        _driver.WaitUntil(() => Lines(order["id"]).Count == 2);
        Lines(order["id"]).Is(("1", 2m, 100m), ("3", 1m, 900m));

        //数量の変更
        var update = OpenDetail(memo);
        _driver.WaitUntil(() => update.Items.Items.Count == 2);
        update.Items.Items.GetItem(0).Quantity.Input.Edit("5");
        update.SubmitButton.Submit.Click();
        _driver.WaitUntil(() => Lines(order["id"]).FirstOrDefault().Quantity == 5m);
        Lines(order["id"]).Is(("1", 5m, 100m), ("3", 1m, 900m));

        //行の追加
        var add = OpenDetail(memo);
        _driver.WaitUntil(() => add.Items.Items.Count == 2);
        add.Items.Create.Click();
        _driver.WaitUntil(() => add.Items.Items.Count == 3);
        EditLine(add.Items.Items.GetItem(2), "WIDGET-L", "4", "300");
        add.SubmitButton.Submit.Click();
        _driver.WaitUntil(() => Lines(order["id"]).Count == 3);
        Lines(order["id"]).Is(("1", 5m, 100m), ("3", 1m, 900m), ("2", 4m, 300m));

        //行の削除 (GADGET の行)
        var delete = OpenDetail(memo);
        _driver.WaitUntil(() => delete.Items.Items.Count == 3);
        delete.Items.Items.GetItem(1).Delete.Click();
        //確認が出る設定なら はい を押す
        _driver.WaitUntil(() => delete.Items.Items.Count == 2 || HasMessageBox());
        if (HasMessageBox()) _driver.ClickMessageBoxButton(0);
        _driver.WaitUntil(() => delete.Items.Items.Count == 2);
        delete.SubmitButton.Submit.Click();
        _driver.WaitUntil(() => Lines(order["id"]).Count == 2);
        Lines(order["id"]).Is(("1", 5m, 100m), ("2", 4m, 300m));

        //明細の表の一覧にも出る (受注のメモで表示)
        WebDriverManager.Navigate("/Main/SalesOrderLine");
        var lines = _driver.AttachSalesOrderLineListPage();
        _driver.WaitUntil(() => FindLineRows(lines, memo).Count == 2);
        FindLineRows(lines, memo).Select(e => e.Item.Element.Text).OrderBy(e => e).Is("WIDGET-L", "WIDGET-S");
    }

    [Test]
    public void 明細の表の一覧を品目と数量で検索できる()
    {
        WebDriverManager.Navigate("/Main/SalesOrderLine");
        var page = _driver.AttachSalesOrderLineListPage();
        _driver.WaitUntil(() => page.List.Items.Count > 0);

        //品目 CABLE-1M (id 4) で数量 5 以上の明細 (初期データでは First order の 5 と Spare parts の 20)
        var expected = WebDriverManager.Mock.Query("SELECT uniquekey, quantity FROM transactionline WHERE item = ? AND quantity >= 5", "4");
        var item = page.Search.Fields.Item;
        item.Search.Click();
        _driver.WaitUntil(() => item.LinkList.Items.Count == 5);
        var cable = Enumerable.Range(0, item.LinkList.Items.Count).Select(i => item.LinkList.Items.GetItem(i)).Single(e => e.ItemId.Element.Text == "CABLE-1M");
        cable.ItemId.Element.DoubleClick();
        _driver.WaitUntil(() => item.Text.Text == "CABLE-1M");
        page.Search.Fields.Quantity.Min.Edit("5");
        page.Search.Search.Click();
        _driver.WaitUntil(() => page.List.Items.Count == expected.Count);

        var rows = Enumerable.Range(0, page.List.Items.Count).Select(i => page.List.Items.GetItem(i)).ToList();
        rows.Select(e => e.Item.Element.Text).Distinct().Is("CABLE-1M");
        rows.Select(e => decimal.Parse(e.Quantity.Element.Text, CultureInfo.InvariantCulture)).OrderBy(e => e)
            .Is(expected.Select(e => decimal.Parse(e["quantity"], CultureInfo.InvariantCulture)).OrderBy(e => e));
        rows.Select(e => e.Order.Element.Text).Contains("Spare parts").IsTrue();
    }

    //明細の行に品目 (候補から選ぶ)・数量・単価を入れる
    void EditLine(SalesOrderItemListLayout line, string itemId, string quantity, string rate)
    {
        line.Item.Select.Edit(itemId);
        line.Quantity.Input.Edit(quantity);
        line.Rate.Input.Edit(rate);
    }

    bool HasMessageBox() => _driver.FindElements(By.CssSelector(".modal.show[data-system='message-box']")).Count != 0;

    static CustomerListLayout FindCustomerRow(SalesOrderDetailLayout detail, string companyName)
    {
        var items = detail.Customer.LinkList.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var row = items.GetItem(i);
            if (row.CompanyName.Element.Text == companyName) return row;
        }
        throw new AssertionException($"No customer '{companyName}'.");
    }

    static List<SalesOrderLineListLayout> FindLineRows(SalesOrderLineListPage page, string memo)
    {
        var items = page.List.Items;
        return Enumerable.Range(0, items.Count).Select(i => items.GetItem(i)).Where(e => e.Order.Element.Text == memo).ToList();
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

    //モックの受注 (見つからなければ null)
    static Dictionary<string, string>? FindOrder(string memo)
        => WebDriverManager.Mock.Query("SELECT id, entity, trandate, memo FROM transaction WHERE memo = ?", memo).SingleOrDefault();

    //モックの受注の明細 (行の順)。品目の id・数量・単価
    static List<(string Item, decimal Quantity, decimal Rate)> Lines(string orderId)
        => WebDriverManager.Mock.Query("SELECT item, quantity, rate FROM transactionline WHERE transaction = ? ORDER BY linesequencenumber", orderId)
            .Select(e => (e["item"], decimal.Parse(e["quantity"], CultureInfo.InvariantCulture), decimal.Parse(e["rate"], CultureInfo.InvariantCulture))).ToList();
}
