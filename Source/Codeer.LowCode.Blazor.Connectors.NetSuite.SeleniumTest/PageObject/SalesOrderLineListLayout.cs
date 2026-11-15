using Codeer.LowCode.Blazor.SeleniumDrivers;
using OpenQA.Selenium;
using Selenium.StandardControls;
using Selenium.StandardControls.PageObjectUtility;
using Selenium.StandardControls.TestAssistant.GeneratorToolKit;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject
{
    public class SalesOrderLineListLayout : ListLayoutBase
    {
        public LinkFieldDriver<SalesOrderListLayout, SalesOrderSearchLayout> Order => this.GetField("Order").Wait();
        public LinkFieldDriver<ItemListLayout, ItemSearchLayout> Item => this.GetField("Item").Wait();
        public NumberFieldDriver Quantity => this.GetField("Quantity").Wait();
        public NumberFieldDriver Amount => this.GetField("Amount").Wait();

        public SalesOrderLineListLayout(IWebElement element) : base(element) { }

        public static implicit operator SalesOrderLineListLayout(ElementFinder finder) => finder.Find<SalesOrderLineListLayout>();
    }

    public class SalesOrderLineSearchLayout : ComponentBase
    {
        public LinkFieldSearchDriver<ItemListLayout, ItemSearchLayout> Item => this.GetField("Item").Wait();
        public NumberFieldSearchDriver Quantity => this.GetField("Quantity").Wait();

        public SalesOrderLineSearchLayout(IWebElement element) : base(element) { }

        public static implicit operator SalesOrderLineSearchLayout(ElementFinder finder) => finder.Find<SalesOrderLineSearchLayout>();
    }

    public class SalesOrderLineListPage : ListPage<SalesOrderLineListLayout, SalesOrderLineSearchLayout>
    {
        public SalesOrderLineListPage(IWebDriver driver) : base(driver) { }
    }

    public static class SalesOrderLineListPageExtensions
    {
        [PageObjectIdentify(UrlCompareType.IgnoreQueryEndsWith, "/SalesOrderLine")]
        public static SalesOrderLineListPage AttachSalesOrderLineListPage(this IWebDriver driver)
        {
            driver.WaitForUrl(UrlCompareType.IgnoreQueryEndsWith, "/SalesOrderLine");
            return new SalesOrderLineListPage(driver);
        }
    }
}
