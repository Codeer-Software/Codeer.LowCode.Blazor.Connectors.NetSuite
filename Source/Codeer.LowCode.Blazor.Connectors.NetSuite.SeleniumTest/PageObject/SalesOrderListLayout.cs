using Codeer.LowCode.Blazor.SeleniumDrivers;
using OpenQA.Selenium;
using Selenium.StandardControls;
using Selenium.StandardControls.PageObjectUtility;
using Selenium.StandardControls.TestAssistant.GeneratorToolKit;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject
{
    public class SalesOrderListLayout : ListLayoutBase
    {
        public LinkFieldDriver<CustomerListLayout, CustomerSearchLayout> Customer => this.GetField("Customer").Wait();
        public DateFieldDriver TranDate => this.GetField("TranDate").Wait();
        public TextFieldDriver Memo => this.GetField("Memo").Wait();

        public SalesOrderListLayout(IWebElement element) : base(element) { }

        public static implicit operator SalesOrderListLayout(ElementFinder finder) => finder.Find<SalesOrderListLayout>();
    }

    public class SalesOrderSearchLayout : ComponentBase
    {
        public LinkFieldSearchDriver<CustomerListLayout, CustomerSearchLayout> Customer => this.GetField("Customer").Wait();
        public TextFieldSearchDriver Memo => this.GetField("Memo").Wait();

        public SalesOrderSearchLayout(IWebElement element) : base(element) { }

        public static implicit operator SalesOrderSearchLayout(ElementFinder finder) => finder.Find<SalesOrderSearchLayout>();
    }

    public class SalesOrderListPage : ListPage<SalesOrderListLayout, SalesOrderSearchLayout>
    {
        public SalesOrderListPage(IWebDriver driver) : base(driver) { }
    }

    public static class SalesOrderListPageExtensions
    {
        [PageObjectIdentify(UrlCompareType.IgnoreQueryEndsWith, "/SalesOrder")]
        public static SalesOrderListPage AttachSalesOrderListPage(this IWebDriver driver)
        {
            driver.WaitForUrl(UrlCompareType.IgnoreQueryEndsWith, "/SalesOrder");
            return new SalesOrderListPage(driver);
        }
    }
}
