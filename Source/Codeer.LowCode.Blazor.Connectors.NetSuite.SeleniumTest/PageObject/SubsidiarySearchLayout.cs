using Codeer.LowCode.Blazor.SeleniumDrivers;
using OpenQA.Selenium;
using Selenium.StandardControls.PageObjectUtility;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject
{
    public class SubsidiarySearchLayout : ComponentBase
    {
        public TextFieldSearchDriver Name => this.GetField("Name").Wait();

        public SubsidiarySearchLayout(IWebElement element) : base(element) { }

        public static implicit operator SubsidiarySearchLayout(ElementFinder finder) => finder.Find<SubsidiarySearchLayout>();
    }
}
