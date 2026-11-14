using Codeer.LowCode.Blazor.SeleniumDrivers;
using OpenQA.Selenium;
using Selenium.StandardControls;
using Selenium.StandardControls.PageObjectUtility;
using Selenium.StandardControls.TestAssistant.GeneratorToolKit;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject
{
    public class CustomerListLayout : ListLayoutBase
    {
        public TextFieldDriver CompanyName => this.GetField("CompanyName").Wait();
        public LinkFieldDriver<SubsidiaryListLayout, SubsidiarySearchLayout> Subsidiary => this.GetField("Subsidiary").Wait();
        public TextFieldDriver Email => this.GetField("Email").Wait();

        public CustomerListLayout(IWebElement element) : base(element) { }

        public static implicit operator CustomerListLayout(ElementFinder finder) => finder.Find<CustomerListLayout>();
    }

    public class CustomerListPage : ListPage<CustomerListLayout, CustomerSearchLayout>
    {
        public CustomerListPage(IWebDriver driver) : base(driver) { }
    }

    public static class CustomerListPageExtensions
    {
        [PageObjectIdentify(UrlCompareType.IgnoreQueryEndsWith, "/Customer")]
        public static CustomerListPage AttachCustomerListPage(this IWebDriver driver)
        {
            driver.WaitForUrl(UrlCompareType.IgnoreQueryEndsWith, "/Customer");
            return new CustomerListPage(driver);
        }
    }
}
