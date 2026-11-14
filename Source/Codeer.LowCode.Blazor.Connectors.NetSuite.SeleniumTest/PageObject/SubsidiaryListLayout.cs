using Codeer.LowCode.Blazor.SeleniumDrivers;
using OpenQA.Selenium;
using Selenium.StandardControls.PageObjectUtility;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject
{
    public class SubsidiaryListLayout : ListLayoutBase
    {
        public TextFieldDriver Name => this.GetField("Name").Wait();

        public SubsidiaryListLayout(IWebElement element) : base(element) { }

        public static implicit operator SubsidiaryListLayout(ElementFinder finder) => finder.Find<SubsidiaryListLayout>();
    }
}
