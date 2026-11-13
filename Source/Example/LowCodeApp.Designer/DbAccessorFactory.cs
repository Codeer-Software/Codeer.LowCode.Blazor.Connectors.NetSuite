using Codeer.LowCode.Blazor.Connectors.NetSuite.Designer;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.DbAccess;
using Codeer.LowCode.Blazor.Designer;
using Codeer.LowCode.Blazor.SystemSettings;

namespace LowCodeApp.Designer
{
    public class DbAccessorFactory : IDbAccessorFactory
    {
        //NetSuite のデータソースはコネクターが、それ以外は DbAccessor が受け持つ
        public IDbAccessor Create(DataSource[] dataSources)
            => NetSuiteDesignerInitializer.CreateDbAccessor(new DbAccessor(dataSources), dataSources);
    }
}
