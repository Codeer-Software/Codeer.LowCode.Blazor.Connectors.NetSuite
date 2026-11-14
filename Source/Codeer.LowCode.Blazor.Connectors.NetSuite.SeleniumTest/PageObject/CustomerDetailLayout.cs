using Codeer.LowCode.Blazor.SeleniumDrivers;
using OpenQA.Selenium;
using Selenium.StandardControls;
using Selenium.StandardControls.PageObjectUtility;
using Selenium.StandardControls.TestAssistant.GeneratorToolKit;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject
{
    public class CustomerDetailLayout : ComponentBase
    {
        public TextFieldDriver CompanyName => this.GetField("CompanyName").Wait();
        public LinkFieldDriver<SubsidiaryListLayout, SubsidiarySearchLayout> Subsidiary => this.GetField("Subsidiary").Wait();
        public TextFieldDriver Email => this.GetField("Email").Wait();
        public NumberFieldDriver CreditLimit => this.GetField("CreditLimit").Wait();
        public SubmitButtonFieldDriver SubmitButton => this.GetField("SubmitButton").Wait();

        public CustomerDetailLayout(IWebElement element) : base(element) { }

        public static implicit operator CustomerDetailLayout(ElementFinder finder) => finder.Find<CustomerDetailLayout>();
    }

    public class CustomerDetailPage : DetailPage<CustomerDetailLayout>
    {
        public CustomerDetailPage(IWebDriver driver) : base(driver) { }
    }

    public static class CustomerDetailPageExtensions
    {
        [PageObjectIdentify(UrlCompareType.Contains, "/Customer/")]
        public static CustomerDetailPage AttachCustomerDetailPage(this IWebDriver driver)
        {
            driver.WaitForUrl(UrlCompareType.Contains, "/Customer/");
            return new CustomerDetailPage(driver);
        }
    }
}
