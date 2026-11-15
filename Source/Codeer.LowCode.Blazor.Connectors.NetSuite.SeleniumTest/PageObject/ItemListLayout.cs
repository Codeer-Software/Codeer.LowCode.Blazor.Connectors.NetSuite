using Codeer.LowCode.Blazor.SeleniumDrivers;
using OpenQA.Selenium;
using Selenium.StandardControls.PageObjectUtility;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.PageObject
{
    public class ItemListLayout : ListLayoutBase
    {
        public TextFieldDriver ItemId => this.GetField("ItemId").Wait();
        public TextFieldDriver DisplayName => this.GetField("DisplayName").Wait();

        public ItemListLayout(IWebElement element) : base(element) { }

        public static implicit operator ItemListLayout(ElementFinder finder) => finder.Find<ItemListLayout>();
    }

    public class ItemSearchLayout : ComponentBase
    {
        public TextFieldSearchDriver ItemId => this.GetField("ItemId").Wait();
        public TextFieldSearchDriver DisplayName => this.GetField("DisplayName").Wait();

        public ItemSearchLayout(IWebElement element) : base(element) { }

        public static implicit operator ItemSearchLayout(ElementFinder finder) => finder.Find<ItemSearchLayout>();
    }
}
