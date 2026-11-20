using LowCodeApp.Server.Extras;
using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DataIO.Db;
using Codeer.LowCode.Blazor.Extras.Server.Auth;
using Codeer.LowCode.Blazor.Extras.Server.FileManagement;
using Codeer.LowCode.Blazor.DbAccess;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;

namespace LowCodeApp.Server.Infrastructure
{
    public class DataService : IAuthenticationContext, IAsyncDisposable
    {
        public DbAccessor DbAccess { get; }
        //モジュールとユーザーの表を読み書きするアクセサ (NetSuite のデータソースはコネクター、それ以外は DbAccess)。
        //ユーザーモジュールを NetSuite の employee にするときは、ログイン (LoginAccountStore など) にもこちらを渡す
        public IDbAccessor ModuleDbAccess => NetSuite;
        //NetSuite のコネクター (添付ファイルのダウンロードの口が使う)
        public NetSuiteDbAccessor NetSuite { get; }
        public TemporaryFileManager TemporaryFileManager { get; }
        public CustomizedModuleDataIO ModuleDataIO { get; }
        //このリクエストが使うデザイン (最初に参照した時点の版に固定)
        public RequestDesign Design { get; }
        readonly IHttpContextAccessor? _httpContextAccessor;
        readonly string? _fixedUserId;

        public DataService(IHttpContextAccessor? httpContextAccessor = null, RequestDesign? design = null)
        {
            _httpContextAccessor = httpContextAccessor;
            Design = design ?? new RequestDesign();
            DbAccess = new DbAccessor(SystemConfig.Instance.DataSources);
            TemporaryFileManager = new TemporaryFileManager(DbAccess, SystemConfig.Instance.TemporaryFileTableInfo, FileStorageTable.Storages, this);
            //NetSuite のデータソースはコネクターが、それ以外は DbAccess が受け持つ。表の定義は配られたデザインのメタ情報のファイルから読む
            NetSuite = new NetSuiteDbAccessor(DbAccess, NetSuiteConnection.FromDataSources(SystemConfig.Instance.DataSources),
                NetSuiteMetadataFile.FromDeployedDesign(SystemConfig.Instance.DesignFileDirectory));
            ModuleDataIO = new CustomizedModuleDataIO(Design.DesignData, this, ModuleDbAccess, TemporaryFileManager);
        }

        //リクエストの外 (ジョブなど) で、そのユーザーの権限で使う DataService
        public DataService(string userId) : this(httpContextAccessor: null)
            => _fixedUserId = userId;

        public Task<string> GetCurrentUserIdAsync()
            => Task.FromResult(_fixedUserId ?? LoginPrincipal.GetUserId(_httpContextAccessor?.HttpContext?.User));

        public async ValueTask DisposeAsync()
            => await DbAccess.DisposeAsync();
    }
}
