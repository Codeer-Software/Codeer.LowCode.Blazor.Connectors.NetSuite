using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository;
using Codeer.LowCode.Blazor.Repository.Design;
using Codeer.LowCode.Blazor.Repository.Match;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness
{
    /// <summary>
    /// 本体の ModuleDataIO を NetSuiteDbAccessor (中身は FakeNetSuite) で動かす。
    /// モジュール: Subsidiary / Customer / CustomerLocked (lastmodifieddate で楽観ロック)。データソース名は "NS"。
    /// </summary>
    public sealed class NetSuiteHarness : IDisposable
    {
        public FakeNetSuite Fake { get; } = new();
        public RecordingDbAccessor Inner { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        internal NetSuiteConnection Connection { get; }
        public NetSuiteDbAccessor Accessor { get; }
        public DesignData Design { get; }
        public ModuleDataIO IO { get; }

        class Auth : IAuthenticationContext
        {
            public Task<string> GetCurrentUserIdAsync() => Task.FromResult("U1");
        }

        /// <param name="editDesign">モジュールを足す・変えるとき (ModuleDataIO を作る前に呼ぶ)。</param>
        public NetSuiteHarness(Action<NetSuiteConnectionSettings>? edit = null, Action<DesignData>? editDesign = null)
        {
            Connection = TestConnections.Create(Fake, Delays, edit);
            Accessor = new NetSuiteDbAccessor(Inner, new[] { Connection });
            Design = CreateDesign();
            editDesign?.Invoke(Design);
            IO = new ModuleDataIO(Design, new Auth(), Accessor, null!);
        }

        public void Dispose() => Fake.Dispose();

        static DesignData CreateDesign()
        {
            var d = new DesignData();
            var modules = (IEditableModuleDesign)d.Modules;

            var subsidiary = new ModuleDesign { Name = "Subsidiary", DataSourceName = TestConnections.DataSourceName, DbTable = "subsidiary" };
            subsidiary.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            subsidiary.Fields.Add(new TextFieldDesign { Name = "Name", DbColumn = "name" });
            modules.Add(subsidiary);

            var customer = new ModuleDesign { Name = "Customer", DataSourceName = TestConnections.DataSourceName, DbTable = "customer", CanCreate = true, CanUpdate = true, CanDelete = true };
            customer.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            customer.Fields.Add(new TextFieldDesign { Name = "CompanyName", DbColumn = "companyname" });
            customer.Fields.Add(new BooleanFieldDesign { Name = "IsInactive", DbColumn = "isinactive" });
            customer.Fields.Add(new NumberFieldDesign { Name = "CreditLimit", DbColumn = "creditlimit" });
            customer.Fields.Add(new DateFieldDesign { Name = "StartDate", DbColumn = "startdate" });
            customer.Fields.Add(new DateTimeFieldDesign { Name = "LastModified", DbColumn = "lastmodifieddate" });
            customer.Fields.Add(new LinkFieldDesign
            {
                Name = "Subsidiary",
                DbColumn = "subsidiary",
                SearchCondition = new SearchCondition("Subsidiary"),
                ValueVariable = "Id.Value",
                DisplayTextVariable = "Name.Value",
            });
            modules.Add(customer);

            var locked = new ModuleDesign { Name = "CustomerLocked", DataSourceName = TestConnections.DataSourceName, DbTable = "customer", CanCreate = true, CanUpdate = true, CanDelete = true };
            locked.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            locked.Fields.Add(new TextFieldDesign { Name = "CompanyName", DbColumn = "companyname" });
            locked.Fields.Add(new OptimisticLockingFieldDesign { Name = SystemFieldNames.OptimisticLocking, DbColumn = "lastmodifieddate" });
            modules.Add(locked);
            return d;
        }

        /// <summary>
        /// SuiteQL の応答を決める: select count(*) には count を、それ以外は rows を limit / offset で切り出して返す。
        /// </summary>
        public void RespondRows(List<Dictionary<string, string>> rows, int? count = null)
        {
            Fake.SuiteQl = call =>
            {
                if (call.Q.StartsWith("select count(*)"))
                {
                    return new SuiteQlResult { Items = new() { new() { ["nsx0"] = (count ?? rows.Count).ToString() } } };
                }
                return new SuiteQlResult
                {
                    Items = rows.Skip(call.Offset).Take(call.Limit).ToList(),
                    HasMore = call.Offset + call.Limit < rows.Count,
                };
            };
        }

        /// <summary>customer の一覧の 1 行 (SuiteQL の結果の形。値は文字列)。</summary>
        public static Dictionary<string, string> CustomerRow(int id, string name)
            => new() { ["customer_id"] = id.ToString(), ["customer_companyname"] = name };
    }
}
