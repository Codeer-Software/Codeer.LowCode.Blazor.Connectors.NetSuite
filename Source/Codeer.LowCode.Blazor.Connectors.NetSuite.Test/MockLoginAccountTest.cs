using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.Extras.Designs;
using Codeer.LowCode.Blazor.Extras.Server.Auth;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository.Design;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>
    /// ユーザーモジュールを NetSuite の employee にしたとき、Extras のログイン (LoginAccountStore) がコネクター経由で行を読めるか。
    /// LoginAccountStore はモジュールの読み書きを通さず、自分で SQL を組んで IDbAccessor に渡す (表の別名・列の別名なし)。
    /// </summary>
    public class MockLoginAccountTest
    {
        MockHarness _h = null!;

        [SetUp]
        public async Task SetUp()
            => _h = await MockHarness.StartAsync(edit: s => s.Records.Add("employee"));

        [TearDown]
        public async Task TearDown() => await _h.DisposeAsync();

        //Example の Employee と同じ形 (ログイン ID = メール、表示名 = 名前。パスワード・有効フラグ・セッションの印なし)
        static (ModuleDesign Module, LoginAccountContractFieldDesign Contract) Employee()
        {
            var module = new ModuleDesign { Name = "Employee", DataSourceName = TestConnections.DataSourceName, DbTable = "employee" };
            module.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            module.Fields.Add(new TextFieldDesign { Name = "表示名", DbColumn = "entityid" });
            module.Fields.Add(new TextFieldDesign { Name = "メール", DbColumn = "email" });
            var contract = new LoginAccountContractFieldDesign { Name = "LoginAccount", LoginName = "メール", DisplayName = "表示名" };
            module.Fields.Add(contract);
            return (module, contract);
        }

        LoginAccountStore CreateStore()
        {
            var (module, contract) = Employee();
            return new LoginAccountStore(module, contract, _h.Accessor);
        }

        [Test]
        public async Task 外部ログインのメールで従業員の行に解決できる()
        {
            var store = CreateStore();
            Assert.That((store.HasPassword, store.HasSessionStamp), Is.EqualTo((false, false)));

            var account = await store.FindByExternalLoginNameAsync("ichiro.sato@example.com");
            Assert.That(account, Is.Not.Null);
            Assert.That((account!.UserId, account.LoginName, account.DisplayName, account.SessionStamp), Is.EqualTo(("1", "ichiro.sato@example.com", "Ichiro Sato", (string?)null)));

            //送った SuiteQL: 引用符を外し、値はパラメータ
            var query = _h.Recorder.Requests.Last(e => e.Path.EndsWith("/query/v1/suiteql"));
            Assert.That(query.Body, Does.Contain("from employee where email = ?"));
            Assert.That(query.Body, Does.Contain("\"params\":[\"ichiro.sato@example.com\"]"));
        }

        [Test]
        public async Task 無いメールはnull()
        {
            var store = CreateStore();
            Assert.That(await store.FindByExternalLoginNameAsync("nobody@example.com"), Is.Null);
            Assert.That(await store.FindByLoginNameAsync("nobody@example.com"), Is.Null);
            Assert.That(await store.FindByExternalLoginNameAsync(""), Is.Null);
        }

        [Test]
        public async Task ログインIDでも探せる()
        {
            var account = await CreateStore().FindByLoginNameAsync("emily.brown@example.com");
            Assert.That((account?.UserId, account?.DisplayName), Is.EqualTo(("3", "Emily Brown")));
        }

        [Test]
        public async Task ユーザーがいるかを数えられる()
            => Assert.That(await CreateStore().AnyAsync(), Is.True);

        [Test]
        public void デザインからも作れる()
        {
            var (module, _) = Employee();
            ((IEditableModuleDesign)_h.Design.Modules).Add(module);
            _h.Design.AppSettings.CurrentUserModuleDesignName = "Employee";
            Assert.That(LoginAccountStore.Create(_h.Design, _h.Accessor), Is.Not.Null);
        }
    }
}
