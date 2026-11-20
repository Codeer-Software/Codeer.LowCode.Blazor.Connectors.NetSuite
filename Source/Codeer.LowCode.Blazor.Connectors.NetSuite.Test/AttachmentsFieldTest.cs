using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;
using Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Json;
using Codeer.LowCode.Blazor.OperatingModel;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.Repository.Design;
using System.Text;
using System.Text.Json.Nodes;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test
{
    /// <summary>添付のフィールド (クライアント用パッケージ) のうち、モジュールを作らずに確かめられるところ。</summary>
    public class AttachmentsFieldTest
    {
        const string Stored = "[{\"contentType\":\"text/plain\",\"id\":\"500\",\"name\":\"a.txt\",\"size\":\"3\"},{\"id\":\"501\",\"name\":\"b.txt\",\"size\":4}]";

        [Test]
        public void サーバーが名前で見る型の名前と一致する()
            => Assert.That(typeof(NetSuiteAttachmentsFieldDesign).FullName, Is.EqualTo(NetSuiteDbAccessor.AttachmentsFieldDesignTypeFullName));

        [Test]
        public void デザインとデータはJSONで型ごと往復する()
        {
            NetSuiteFieldsInitializer.Initialize();
            var design = new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files", MaxFileSizeMB = 3 };
            var restored = (NetSuiteAttachmentsFieldDesign)JsonConverterEx.DeserializeObject<FieldDesignBase>(JsonConverterEx.SerializeObject(design, typeof(FieldDesignBase)))!;
            Assert.That((restored.DbColumn, restored.MaxFileSizeMB), Is.EqualTo(("files", 3)));

            var data = new NetSuiteAttachmentsFieldData { Value = Stored };
            var restoredData = (NetSuiteAttachmentsFieldData)JsonConverterEx.DeserializeObject<FieldDataBase>(JsonConverterEx.SerializeObject(data, typeof(FieldDataBase)))!;
            Assert.That(restoredData.Value, Is.EqualTo(Stored));
        }

        [Test]
        public void 既定の上限は10MBで検索の部品を持たない()
        {
            var design = new NetSuiteAttachmentsFieldDesign();
            Assert.That(design.MaxFileSizeMB, Is.EqualTo(10));
            Assert.That(design.GetSearchWebComponentTypeFullName(), Is.Empty);
            Assert.That(design.GetSearchControlTypeFullName(), Is.Empty);
        }

        [Test]
        public void レコードのコピーでは写さない()
            => Assert.That(((IConfirmCopyFieldDesign)new NetSuiteAttachmentsFieldDesign()).NeedCopy, Is.False);

        [Test]
        public async Task 読んだ値は変更なしでファイルの並びになる()
        {
            var field = new NetSuiteAttachmentsField(new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files" });
            await field.InitializeDataAsync(new NetSuiteAttachmentsFieldData { Value = Stored });
            Assert.That(field.IsModified, Is.False);
            Assert.That(field.GetSubmitData().FieldData, Is.Null);
            //大きさは文字でも数値でも読む
            Assert.That(field.Entries.Select(e => (e.FileId, e.Name, e.Size, e.ContentType, e.IsAdded, e.Deleted)),
                Is.EqualTo(new[] { ("500", "a.txt", (long?)3, "text/plain", false, false), ("501", "b.txt", (long?)4, (string?)null, false, false) }));
        }

        [Test]
        public async Task 値が無ければファイルは無い()
        {
            var field = new NetSuiteAttachmentsField(new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files" });
            await field.InitializeDataAsync(new NetSuiteAttachmentsFieldData { Value = null });
            Assert.That(field.Entries, Is.Empty);
            await field.InitializeDataAsync(new NetSuiteAttachmentsFieldData { Value = "not json" });
            Assert.That(field.Entries, Is.Empty);
        }

        [Test]
        public async Task データありの後にデータなしで初期化すると空で変更なし()
        {
            //ファイルの無いレコードは本体がフィールドのキーを入れないので null が来る (最後の 1 個を外して保存した後の読み直し)
            var field = new NetSuiteAttachmentsField(new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files" });
            await field.InitializeDataAsync(new NetSuiteAttachmentsFieldData { Value = Stored });
            await field.InitializeDataAsync(null);
            Assert.That(field.Entries, Is.Empty);
            Assert.That(field.IsModified, Is.False);
            Assert.That(((NetSuiteAttachmentsFieldData)field.GetData()!).Value, Is.Null);
        }

        [Test]
        public async Task 外せるのはIDの分かる読んだファイルだけ()
        {
            var field = new NetSuiteAttachmentsField(new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files" });
            //読み直さない保存の後の足したファイル (id が無い) と、保存前の足したファイル (base64)
            await field.InitializeDataAsync(new NetSuiteAttachmentsFieldData
            {
                Value = "[{\"id\":\"500\",\"name\":\"a.txt\"},{\"name\":\"saved.txt\",\"size\":\"1\"},{\"name\":\"new.txt\",\"base64\":\"YQ==\"}]",
            });
            Assert.That(field.Entries.Select(NetSuiteAttachmentsField.CanDelete), Is.EqualTo(new[] { true, false, false }));
            Assert.That(field.Entries[2].Size, Is.EqualTo(1));
        }

        [Test]
        public async Task IDは数値でも読む()
        {
            var field = new NetSuiteAttachmentsField(new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files" });
            await field.InitializeDataAsync(new NetSuiteAttachmentsFieldData { Value = "[{\"id\":500,\"name\":\"a.txt\",\"size\":3,\"type\":\"PLAINTEXT\"}]" });
            Assert.That(field.Entries.Select(e => (e.FileId, e.Name, e.Size)), Is.EqualTo(new[] { ("500", "a.txt", (long?)3) }));
        }

        [Test]
        public async Task 空のファイルと上限を超えるファイルは入力エラーで足さない()
        {
            var field = new NetSuiteAttachmentsField(new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files", MaxFileSizeMB = 1 });
            await field.InitializeDataAsync(new NetSuiteAttachmentsFieldData { Value = null });
            Assert.That(await field.AddFileAsync("empty.txt", null, Array.Empty<byte>()), Is.False);
            Assert.That(field.ErrorText, Does.Contain("empty.txt"));
            Assert.That(await field.AddFileAsync("big.bin", null, new byte[1024 * 1024 + 1]), Is.False);
            Assert.That(field.ErrorText, Does.Contain("big.bin"));
            Assert.That(field.Entries, Is.Empty);
            Assert.That(field.IsModified, Is.False);
        }
    }

    /// <summary>
    /// 自動保存 (本体の AutoSubmitField。レコードを読み直さない軽い保存) の後の添付のフィールド。本体の Module を画面側のサービス (Harness/ClientServices) で作り、
    /// 保存を送信の途中で止めて、送信の間の操作を再現する。保存の後は、送った分だけが保存済みになり、送信の間の操作は変更として残る。
    /// </summary>
    public class AttachmentsFieldAutoSubmitTest
    {
        const string Stored = "[{\"contentType\":\"text/plain\",\"id\":\"500\",\"name\":\"a.txt\",\"size\":3},{\"id\":\"501\",\"name\":\"b.txt\",\"size\":4}]";

        ClientServices _services = null!;
        Module _module = null!;

        NetSuiteAttachmentsField Files => _module.GetField<NetSuiteAttachmentsField>("Files")!;
        TextField Title => _module.GetField<TextField>("Title")!;

        [SetUp]
        public async Task SetUp()
        {
            var design = new DesignData();
            var order = new ModuleDesign { Name = "Order", DataSourceName = "NetSuite", DbTable = "salesorder", CanCreate = true, CanUpdate = true };
            order.Fields.Add(new IdFieldDesign { Name = "Id", DbColumn = "id" });
            order.Fields.Add(new NetSuiteAttachmentsFieldDesign { Name = "Files", DbColumn = "files" });
            order.Fields.Add(new AutoSubmitFieldDesign { Name = "AutoSubmit", DelayMilliseconds = 0 });
            //保存の後処理が終わった目印 (本体はフィールドの並びの順に保存済みにするので、添付より後ろに置く)
            order.Fields.Add(new TextFieldDesign { Name = "Title", DbColumn = "memo" });
            ((IEditableModuleDesign)design.Modules).Add(order);
            _services = new ClientServices(design);

            var data = new ModuleData { Name = "Order" };
            data.Fields["Id"] = new IdFieldData { Value = "1" };
            data.Fields["Files"] = new NetSuiteAttachmentsFieldData { Value = Stored };
            data.Fields["Title"] = new TextFieldData { Value = "A" };
            _module = await ModuleCreationService.CreateModuleAsync(_services.Core, data);
        }

        static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        static string Base64(string text) => Convert.ToBase64String(Bytes(text));

        //自動保存を 1 回: 送信の間に during を行い、保存の後処理が終わるまで待つ
        async Task AutoSubmitAsync(Func<Task>? during = null)
        {
            await Title.SetValueAsync(Title.Value + "+");
            _services.HoldSubmit();
            _module.GetField<AutoSubmitField>("AutoSubmit")!.ScheduleSubmit();
            await _services.WaitSubmitEnteredAsync();
            if (during != null) await during();
            _services.ReleaseSubmit();
            var limit = DateTime.UtcNow.AddSeconds(10);
            while (Title.IsModified)
            {
                if (limit < DateTime.UtcNow) Assert.Fail("自動保存が終わらない");
                await Task.Delay(10);
            }
        }

        //送ったデータの添付の値
        string? SentValue(int index = 0)
            => ((NetSuiteAttachmentsFieldData)_services.Submits[index].Single().Update.Single().Fields["Files"]).Value;

        string? SubmitValue => (Files.GetSubmitData().FieldData as NetSuiteAttachmentsFieldData)?.Value;

        NetSuiteAttachmentsField.Entry Entry(string name) => Files.Entries.Single(e => e.Name == name);

        [Test]
        public async Task 送った値のままなら足した分は名前と大きさだけになり外した分は無くなり変更なし()
        {
            Assert.That(await Files.AddFileAsync("x.txt", "text/plain", Bytes("xyz12")), Is.True);
            await Files.SetDeletedAsync(Entry("b.txt"), true);

            await AutoSubmitAsync();

            Assert.That(SentValue(), Does.Contain(Base64("xyz12")).And.Contain("\"deleted\":true"));
            Assert.That(Files.Entries.Select(e => (e.FileId, e.Name, e.Size, e.Base64, e.Deleted)),
                Is.EqualTo(new[] { ("500", "a.txt", (long?)3, (string?)null, false), ((string?)null, "x.txt", (long?)5, (string?)null, false) }));
            Assert.That(Files.IsModified, Is.False);
            Assert.That(Files.GetSubmitData().FieldData, Is.Null);
            //足したファイルは読み直すまで ID が分からないので外せない・ダウンロードできない (名前だけ)
            Assert.That(NetSuiteAttachmentsField.CanDelete(Entry("x.txt")), Is.False);
            Assert.That(Files.GetDownloadUrl(Entry("x.txt")), Is.Null);
        }

        [Test]
        public async Task ダウンロードのURLは読んだファイルだけでモジュールとIdとフィールドとファイルのIDが付く()
        {
            Assert.That(Files.GetDownloadUrl(Entry("a.txt")), Is.EqualTo("api/netsuite/attachments/download?module=Order&id=1&field=Files&fileId=500"));
            await Files.AddFileAsync("x.txt", null, Bytes("x"));
            Assert.That(Files.GetDownloadUrl(Entry("x.txt")), Is.Null);
        }

        [Test]
        public async Task 送信の間に足したファイルだけが変更として残る()
        {
            await Files.AddFileAsync("x.txt", null, Bytes("first"));

            await AutoSubmitAsync(() => Files.AddFileAsync("y.txt", null, Bytes("second")));

            Assert.That(SentValue(), Does.Contain(Base64("first")).And.Not.Contain(Base64("second")));
            Assert.That(Entry("x.txt").IsAdded, Is.False);
            Assert.That(Entry("y.txt").Base64, Is.EqualTo(Base64("second")));
            Assert.That(Files.IsModified, Is.True);
            //次の保存で送るのは後から足した分だけ (先の分をもう一度足さない)
            Assert.That(SubmitValue, Does.Contain(Base64("second")).And.Not.Contain(Base64("first")));

            await AutoSubmitAsync();
            Assert.That(SentValue(1), Does.Contain(Base64("second")).And.Not.Contain(Base64("first")));
            Assert.That(Files.IsModified, Is.False);
            Assert.That(Files.Entries.Select(e => (e.Name, e.IsAdded)), Is.EqualTo(new[] { ("a.txt", false), ("b.txt", false), ("x.txt", false), ("y.txt", false) }));
        }

        [Test]
        public async Task 送信の間に付けた外す印は残り送った追加は保存済みになる()
        {
            await Files.AddFileAsync("x.txt", null, Bytes("first"));

            await AutoSubmitAsync(() => Files.SetDeletedAsync(Entry("a.txt"), true));

            Assert.That(SentValue(), Does.Not.Contain("deleted"));
            Assert.That(Entry("x.txt").IsAdded, Is.False);
            Assert.That(Entry("a.txt").Deleted, Is.True);
            Assert.That(Files.IsModified, Is.True);
            Assert.That(SubmitValue, Does.Contain("\"id\":\"500\"").And.Contain("\"deleted\":true").And.Not.Contain("base64"));
            //印を外せば変更なしに戻る (保存済みの状態は x.txt を含む)
            await Files.SetDeletedAsync(Entry("a.txt"), false);
            Assert.That(Files.IsModified, Is.False);
        }

        [Test]
        public async Task 送った追加を送信の間に取り消しても名前だけの保存済みとして出る()
        {
            await Files.AddFileAsync("x.txt", null, Bytes("first"));

            await AutoSubmitAsync(() => Files.RemoveAddedAsync(Entry("x.txt")));

            Assert.That(SentValue(), Does.Contain(Base64("first")));
            //サーバーには付いたので、名前だけの保存済みの行として出る (変更なし = もう一度送らない)
            Assert.That(Files.Entries.Select(e => (e.FileId, e.Name, e.Size, e.IsAdded)),
                Is.EqualTo(new[] { ("500", "a.txt", (long?)3, false), ("501", "b.txt", (long?)4, false), ((string?)null, "x.txt", (long?)5, false) }));
            Assert.That(Files.IsModified, Is.False);
            Assert.That(Files.GetSubmitData().FieldData, Is.Null);
        }

        [Test]
        public async Task 同じ名前と中身を2つ足して1つだけ送っていたら1つだけ保存済み()
        {
            await Files.AddFileAsync("x.txt", null, Bytes("same"));

            await AutoSubmitAsync(() => Files.AddFileAsync("x.txt", null, Bytes("same")));

            Assert.That(Files.Entries.Where(e => e.Name == "x.txt").Select(e => e.IsAdded), Is.EqualTo(new[] { false, true }));
            Assert.That(Files.IsModified, Is.True);
            var resent = JsonNode.Parse(SubmitValue!)!.AsArray();
            Assert.That(resent.Count(e => e!["base64"] != null), Is.EqualTo(1));
        }

        [Test]
        public async Task 送った分に追加も削除も無ければ何も変えない()
        {
            //並びだけ変えた値 (足すものも消すものも無い)
            const string reordered = "[{\"id\":\"501\",\"name\":\"b.txt\",\"size\":4},{\"id\":\"500\",\"name\":\"a.txt\",\"size\":3}]";
            await Files.SetDataAsync(new NetSuiteAttachmentsFieldData { Value = reordered });
            Assert.That(Files.IsModified, Is.True);

            await AutoSubmitAsync();

            Assert.That(SentValue(), Is.EqualTo(reordered));
            Assert.That(Files.IsModified, Is.True);
            Assert.That(((NetSuiteAttachmentsFieldData)Files.GetData()!).Value, Is.EqualTo(reordered));
            Assert.That(Files.Entries.Select(e => e.FileId), Is.EqualTo(new[] { "501", "500" }));
        }
    }
}
