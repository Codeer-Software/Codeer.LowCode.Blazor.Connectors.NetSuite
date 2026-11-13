using Codeer.LowCode.Blazor.Connectors.NetSuite.Designer;
using Codeer.LowCode.Blazor.Designer;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;

namespace LowCodeApp.Designer;

public partial class App : DesignerApp
{
    protected override void OnStartup(StartupEventArgs e)
    {
        //DB への接続 (NetSuite のデータソースは DbAccessorFactory で包む)
        Services.AddSingleton<IDbAccessorFactory, DbAccessorFactory>();

        //NetSuite: AI 用の仕様ドキュメントの登録と、メタ情報をファイルから読む設定。base.OnStartup より前に呼ぶ
        NetSuiteDesignerInitializer.Initialize(DesignerEnvironment);

        base.OnStartup(e);

        //NetSuite: メニュー Tools > NetSuite のメタ情報を更新。base.OnStartup の後に呼ぶ
        NetSuiteDesignerInitializer.Setup(DesignerEnvironment);
    }
}
