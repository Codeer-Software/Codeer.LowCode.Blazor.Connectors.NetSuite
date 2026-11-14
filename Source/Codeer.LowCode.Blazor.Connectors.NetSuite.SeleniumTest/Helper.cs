using Codeer.LowCode.Blazor.SeleniumDrivers;
using OpenQA.Selenium;
using OpenQA.Selenium.Interactions;
using OpenQA.Selenium.Support.UI;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest;

public static class Helper
{
    public static void DoubleClick(this IWebElement element)
    {
        var driver = (element as IWrapsDriver)?.WrappedDriver;
        if (driver is IJavaScriptExecutor js)
        {
            js.ExecuteScript("arguments[0].scrollIntoView({block:'center', inline:'center'});", element);
        }
        new Actions(driver).MoveToElement(element).DoubleClick(element).Perform();
    }

    /// <summary>MessageBox (確認ダイアログ) のボタンを押して、閉じるまで待つ。0 = 左端 (OK / はい)。</summary>
    public static void ClickMessageBoxButton(this IWebDriver driver, int index = 0)
    {
        driver.AttachMessageBox().Buttons.GetItem(index).Click();
        driver.WaitUntil(() => driver.FindElements(By.CssSelector(".modal.show[data-system='message-box']")).Count == 0);
        WebDriverManager.WaitLoading();
    }

    /// <summary>
    /// 条件が成り立つ (true / null 以外を返す) まで待つ。描き直しの途中で要素が差し替わったときは、もう一度見る。
    /// <see cref="WebDriverManager.WaitTimeout"/> を超えたら失敗にする。
    /// </summary>
    public static T WaitUntil<T>(this IWebDriver driver, Func<T> condition)
    {
        var wait = new WebDriverWait(driver, WebDriverManager.WaitTimeout);
        wait.IgnoreExceptionTypes(typeof(NoSuchElementException), typeof(StaleElementReferenceException));
        return wait.Until(_ => condition());
    }
}
