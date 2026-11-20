namespace Codeer.LowCode.Blazor.Connectors.NetSuite
{
    /// <summary>
    /// このパッケージのフィールドを使えるようにする。本体はデザインのフィールドの型を読み込み済みのアセンブリから探すので、
    /// デザインを読む前にこのアセンブリを読み込んでおく。クライアント (Blazor) とサーバーの起動時に 1 回呼ぶ (デザイナは .Designer の Initialize が呼ぶ)。
    /// </summary>
    public static class NetSuiteFieldsInitializer
    {
        public static void Initialize() => typeof(NetSuiteAttachmentsFieldDesign).ToString();
    }
}
