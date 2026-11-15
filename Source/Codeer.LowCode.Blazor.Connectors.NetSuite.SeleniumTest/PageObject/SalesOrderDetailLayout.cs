using Codeer.LowCode.Blazor.SeleniumDrivers;
using OpenQA.Selenium;
using Selenium.StandardControls;
using Selenium.StandardControls.PageObjectUtility;
using Selenium.StandardControls.TestAssistant.GeneratorToolKit;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject
{
    public class SalesOrderDetailLayout : ComponentBase
    {
        public LinkFieldDriver<CustomerListLayout, CustomerSearchLayout> Customer => this.GetField("Customer").Wait();
        public DateFieldDriver TranDate => this.GetField("TranDate").Wait();
        public TextFieldDriver Memo => this.GetField("Memo").Wait();
        public ListFieldDriver<SalesOrderItemListLayout> Items => this.GetField("Items").Wait();
        public SubmitButtonFieldDriver SubmitButton => this.GetField("SubmitButton").Wait();

        public SalesOrderDetailLayout(IWebElement element) : base(element) { }

        public static implicit operator SalesOrderDetailLayout(ElementFinder finder) => finder.Find<SalesOrderDetailLayout>();
    }

    //明細の行 (列モードの一覧の行)
    public class SalesOrderItemListLayout : ListLayoutBase
    {
        public SelectFieldDriver Item => this.GetField("Item").Wait();
        public NumberFieldDriver Quantity => this.GetField("Quantity").Wait();
        public NumberFieldDriver Rate => this.GetField("Rate").Wait();
        public TextFieldDriver Description => this.GetField("Description").Wait();

        public SalesOrderItemListLayout(IWebElement element) : base(element) { }

        public static implicit operator SalesOrderItemListLayout(ElementFinder finder) => finder.Find<SalesOrderItemListLayout>();
    }

    public class SalesOrderDetailPage : DetailPage<SalesOrderDetailLayout>
    {
        public SalesOrderDetailPage(IWebDriver driver) : base(driver) { }
    }

    public static class SalesOrderDetailPageExtensions
    {
        [PageObjectIdentify(UrlCompareType.Contains, "/SalesOrder/")]
        public static SalesOrderDetailPage AttachSalesOrderDetailPage(this IWebDriver driver)
        {
            driver.WaitForUrl(UrlCompareType.Contains, "/SalesOrder/");
            return new SalesOrderDetailPage(driver);
        }
    }
}
