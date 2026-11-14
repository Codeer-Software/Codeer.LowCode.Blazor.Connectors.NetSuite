using Codeer.LowCode.Blazor.SeleniumDrivers;
using OpenQA.Selenium;
using Selenium.StandardControls.PageObjectUtility;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject
{
    public class CustomerSearchLayout : ComponentBase
    {
        public TextFieldSearchDriver CompanyName => this.GetField("CompanyName").Wait();
        public LinkFieldSearchDriver<SubsidiaryListLayout, SubsidiarySearchLayout> Subsidiary => this.GetField("Subsidiary").Wait();

        public CustomerSearchLayout(IWebElement element) : base(element) { }

        public static implicit operator CustomerSearchLayout(ElementFinder finder) => finder.Find<CustomerSearchLayout>();
    }
}
