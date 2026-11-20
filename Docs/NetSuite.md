# NetSuite コネクター

Oracle NetSuite のレコードを、Codeer.LowCode.Blazor のデータソースとして扱うためのライブラリです。
モジュールを NetSuite のレコードに結び付けると、いつもの一覧・検索・詳細・保存の操作がそのまま NetSuite の読み書きになります。
URL・JSON・SQL を手で書く必要はありません。

- 一覧・検索・件数: SuiteQL (REST Query Service)
- 作成・更新・削除: REST Record Service
- 認証: OAuth 2.0 Client Credentials (証明書を使う M2M)

> この版は NetSuite の実アカウントでの動作確認がまだ済んでいません (HTTP の偽物を相手にしたテストだけ)。
> 実アカウントで使う前に、検証用のアカウントで動作を確かめてください。

実アカウントが無いうちは、手元で動く NetSuite のモックで、モジュールの作成から一覧・集計・保存までを試せます ([NetSuiteMock.md](NetSuiteMock.md))。

## 使い方の流れ

1. NetSuite 側で連携用の設定をする
2. アプリとデザイナに NetSuite のデータソースを書く
3. アプリとデザイナにコネクターを組み込む (1 行ずつ)
4. デザイナで NetSuite のメタ情報 (レコードの定義) を取り込む
5. デザイナで、DB のテーブルと同じ手順で NetSuite のレコードからモジュールを作る
6. デザインを配る (メタ情報のファイルもデザインと一緒に配られる)

アプリのログインに NetSuite のアカウントを使うこともできます (下の「NetSuite のアカウントでログインする」)。

## 1. NetSuite 側の準備

- **機能を有効にする**: REST Web Services、OAuth 2.0、SuiteAnalytics Workbook
- **インテグレーションレコードを作る**: Client Credentials (Machine to Machine) Grant を有効にし、Scope に REST Web Services を選ぶ。Client ID を控える
- **証明書を登録する**: 鍵のペアを作り、公開側の証明書を OAuth 2.0 Client Credentials (M2M) Setup に登録する (インテグレーション・Entity・ロールを選ぶ)。Certificate ID を控える
- **ロールの権限**: REST Web Services、Log in using OAuth 2.0 Access Tokens、SuiteAnalytics Workbook と、扱うレコードの権限。管理者ロールは使わず、必要な権限だけを持つロールを用意する

NetSuite で実行される主体は、ここで選んだ Entity とロールです。アプリにログインした利用者ごとの権限は、モジュールの権限設定で決めます。

Sandbox の M2M の設定は本番からコピーされず、Sandbox を更新すると消えます。更新のたびに設定し直してください。

## 2. データソース

アプリの `appsettings.json` とデザイナの設定の DataSources に、DB と同じ並びで書きます。`ConnectionString` を `Provider=NetSuite;` で始めたものが NetSuite のデータソースです。

```json
{
  "DataSources": [
    {
      "Name": "NetSuite",
      "DataSourceType": "Oracle",
      "ConnectionString": "Provider=NetSuite;ServiceUrl=https://1234567.suitetalk.api.netsuite.com;ClientId=xxxxxxxx;CertificateId=xxxxxxxx;PrivateKeyPath=C:\\keys\\netsuite.pem"
    }
  ]
}
```

`DataSourceType` は `Oracle` にします。

| キー | 必須 | 内容 |
|---|---|---|
| `Provider` | ○ | `NetSuite` |
| `ServiceUrl` | ○ | アカウント固有の SuiteTalk の URL。NetSuite の Setup › Company › Company Information › Company URLs で確かめる |
| `ClientId` | ○ | インテグレーションレコードの Client ID |
| `CertificateId` | ○ | 証明書を登録したときの Certificate ID |
| `PrivateKeyPath` | △ | 秘密鍵 (PEM) のファイルのパス |
| `PrivateKey` | △ | 秘密鍵 (PEM) の本文。改行は `\n` と書ける。`PrivateKeyPath` とどちらか |
| `Algorithm` | | トークン要求の署名方式。`PS256` (既定) / `PS384` / `PS512` / `ES256` / `ES384` / `ES512`。鍵の種類に合わせる |
| `Scope` | | トークンの scope。既定 `rest_webservices` |
| `TimeoutSeconds` | | 1 回の要求のタイムアウト (秒)。既定 100 |
| `MaxConcurrency` | | NetSuite へ同時に出す要求の上限。既定 4。NetSuite の同時実行の枠はほかの連携と共有なので控えめにする |
| `MaxRows` | | 件数を絞らない読み出しで受け取る行の上限。既定 10000。超えるとエラーになる |
| `AttachmentFolderId` | | 取引の添付ファイル: 足したファイルを登録する File Cabinet のフォルダの internal ID (数字)。無ければファイルを足せない |
| `AttachmentSavedSearchId` | | 取引の添付ファイル: 付いているファイルを読む保存検索の ID (`customsearch_...` か internal ID)。無ければ一覧・ダウンロード・外すができない (足すだけ) |
| `AttachmentSearchRecordColumn` | | 保存検索の結果で、取引の internal ID の列の名前。既定 `internalid` |
| `AttachmentSearchFileColumn` | △ | 保存検索の結果で、ファイルの internal ID の列の名前。保存検索を書いたら必須 |
| `AttachmentSearchFileNameColumn` | △ | 保存検索の結果で、ファイル名の列の名前。保存検索を書いたら必須 |
| `AttachmentSearchFileSizeColumn` | | 保存検索の結果で、ファイルの大きさの列の名前 (書けば画面に大きさが出る) |
| `AttachmentSearchFileTypeColumn` | | 保存検索の結果で、ファイルの種類の列の名前 |

秘密鍵は DB の接続文字列と同じ扱いにしてください (デザインのファイルやリポジトリに入れない。運用環境では環境変数やシークレットストアから渡す)。

## 3. 組み込み

| プロジェクト | パッケージ |
|---|---|
| アプリ (サーバー) | `Codeer.LowCode.Blazor.Connectors.NetSuite.Server` |
| デザイナ | `Codeer.LowCode.Blazor.Connectors.NetSuite.Designer` |
| アプリ (画面。取引の添付ファイルを使うときだけ) | `Codeer.LowCode.Blazor.Connectors.NetSuite` |

### アプリ (サーバー)

`ModuleDataIO` に渡す `IDbAccessor` を `NetSuiteDbAccessor` で包みます。

```csharp
using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;

DbAccess = new DbAccessor(SystemConfig.Instance.DataSources);
var netSuite = new NetSuiteDbAccessor(DbAccess, NetSuiteConnection.FromDataSources(SystemConfig.Instance.DataSources),
    NetSuiteMetadataFile.FromDeployedDesign(SystemConfig.Instance.DesignFileDirectory));
ModuleDataIO = new CustomizedModuleDataIO(Design.DesignData, this, netSuite, TemporaryFileManager);
```

### デザイナ

`IDbAccessorFactory` の実装で `IDbAccessor` を包みます。

```csharp
using Codeer.LowCode.Blazor.Connectors.NetSuite.Designer;

public class DbAccessorFactory : IDbAccessorFactory
{
    public IDbAccessor Create(DataSource[] dataSources)
        => NetSuiteDesignerInitializer.CreateDbAccessor(new DbAccessor(dataSources), dataSources);
}
```

デザイナのアプリの `OnStartup` に 2 行足します。

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    NetSuiteDesignerInitializer.Initialize(DesignerEnvironment);   //base.OnStartup より前
    base.OnStartup(e);
    NetSuiteDesignerInitializer.Setup(DesignerEnvironment);        //base.OnStartup の後
}
```

NetSuite 以外のデータソースは、包んだ `DbAccessor` がこれまでどおり処理します。

組み込んだデザイナの見本は [Source/Example/LowCodeApp.Designer](../Source/Example/LowCodeApp.Designer) にあります (`App.xaml.cs` と `DbAccessorFactory.cs`)。

`NetSuiteMetadataFile.FromDeployedDesign` には、デザインの配置先 (App.zip のあるフォルダ) を渡します。

### メタ情報はファイルから読む

デザイナもアプリも、NetSuite のレコードの定義 (メタ情報) を、デザインのプロジェクトの `ServerFiles/netsuite.<データソース名>.metadata.json` から読みます。
プロジェクトを開くたび・アプリの実行中に NetSuite へ定義を取りに行くことはありません。

- **取り込み・更新**: デザイナのメニュー **Tools > NetSuite のメタ情報を更新**。NetSuite のアカウントのレコードの定義を全部取り直してファイルに保存します (レコードが多いと時間がかかります)。
  最初の 1 回と、NetSuite 側でレコードや項目を足した・変えたときに実行してください。ファイルが無いうちは、NetSuite のレコードはデザイナに出ません
- **取り込むレコード**: SuiteQL で読めるレコード (データソースのロールで読めるもの) と、下の「取引のレコード」の一覧にある取引、その取引の明細の表です。
  ロールで読めないレコード・一覧に無い取引は入りません。名前は REST のレコード名の小文字です
- **中身**: レコードと項目の名前・型だけです。接続の情報や鍵は入りません。デザインのファイルと一緒にリポジトリへ入れておけば、
  NetSuite に接続できない環境 (鍵を持たない人・オフライン) でもデザインできます
- **アプリ**: ファイルはデザインと一緒に App.zip に入って配られ、アプリはそれを読みます (画面へ送るデザインには入りません)。
  メタ情報を更新したら、デザインを配り直してください。ファイルに無いレコードを使うモジュールは、「メタ情報を更新してデザインを配り直す」ように言うエラーになります
- このファイルは手で書き換えません

## 4. モジュールを作る

デザイナのデータソースに NetSuite のレコードが、DB のテーブルと同じように並びます。テーブルからモジュールを作るのと同じ手順で作れます。

| モジュールの設定 | NetSuite では |
|---|---|
| DataSourceName | NetSuite のデータソース名 |
| DbTable | レコードの種別 (`customer` など) |
| フィールドの DbColumn | 項目名を小文字にしたもの (`companyName` → `companyname`) |
| Id フィールド | DbColumn は `id` (NetSuite の internal ID)。手入力にしない |

### 項目とフィールドの対応

| NetSuite の項目 | 列の型 | フィールド |
|---|---|---|
| 文字列 | 文字列 | TextField |
| チェックボックス | 真偽 | BooleanField |
| 整数 | 整数 | NumberField |
| 小数・通貨 | 数値 | NumberField |
| 日付 | 日付 | DateField |
| 日時 | 日時 | DateTimeField (SaveAsUtc をオンにする) |
| ほかのレコード・選択肢への参照 | 文字列 (相手の internal ID) | LinkField / SelectField / IdField |
| 固定の選択肢 (選べる値がメタデータに並んでいる項目) | 文字列 (選択肢の値) | SelectField (候補は Enum) |

- 参照の項目の値は相手の internal ID です。相手のレコードのモジュールを作って LinkField で結べば、表示名での表示・検索ができます (相手のレコードもメタ情報に入っている必要があります)。
- 固定の選択肢の項目は、選べる値がメタ情報のファイルの項目の `Choices` に入ります (`[{"Value": "_pDF"}, ...]`)。
  メタデータには表示名が無いので、Enum を作って値ごとに表示名を書き、SelectField の候補にその Enum を選んでください。
  保存では選んだ値を送ります。値が NetSuite の選択肢に無ければ、保存は NetSuite のエラーになります
- 読み取り専用の項目 (作成日時など。メタデータで読み取り専用と書かれている項目) は、メタ情報のファイルの項目に `"ReadOnly": true` が入ります。
  新規では送りません (値があっても無視します)。更新でその項目を変えると、NetSuite に送る前にエラーになります。画面ではフィールドを読み取り専用にしてください
- 明細 (サブリスト) は親のレコードの列 1 つになります (下記)。住所などのサブレコード・複数選択の項目は列になりません。
- 使う項目だけをフィールドにしてください。フィールドにしていない項目は読みも書きもしません。
- NetSuite で必須の項目も、列としては空を許す形で見えます。必須かどうかは保存のときに NetSuite が確かめ、足りなければ保存がエラーになります。
  画面で先に止めたいときは、フィールドを必須にしてください。

### 明細 (サブリスト)

受注の明細 (`item`) のような明細は、親のレコードの列 (明細名の小文字・文字列) として見えます。
親のモジュールに一覧 (ListField) を置き、一覧の `DbColumn` にその列名を書きます (行を親の列に持つ一覧)。行のモジュールは DB の列に JSON 配列で持つ一覧と同じ作りです。

| 設定 | 書く値 |
|---|---|
| 親の一覧 (ListField) の `DbColumn` | 明細の列名 (`item`) |
| 一覧の `SearchCondition` | 行のモジュール。`LimitCount` は付けない |
| 行のモジュール | DataSource・DbTable・Id フィールドなし。ルートに JsonField を 1 つ置き、行の項目をノードにする |
| ノードのキー・行のフィールドの `DbColumn` | NetSuite の行の項目名そのまま (大文字・小文字も。`quantity`・`expectedShipDate`) |

- 参照の項目 (品目など) は、キーに相手の internal ID の文字列、`<キー>_refName` に表示名が入ります (`item` と `item_refName`)。表示名は読むだけで、保存では送りません
- 行で参照の項目を選ばせるときは、SelectField の候補を相手のモジュールから出します (`SearchCondition` に相手のモジュール、`ValueVariable` に `Id.Value`、`DisplayTextVariable` に表示する項目)。
  例は Example の `SalesOrderItem` (受注の明細の行。品目を品目のモジュールの候補から選ぶ) です
- 行の番号 `line` と、読んだときにあった行の番号の並び `_readLines` は結び付けなくてかまいません。読んだ行に入っていて、保存のときに行の突き合わせに使われます
- 日付は Date のノード + DateField、日時は DateTime のノード + DateTimeField (`SaveAsUtc` をオンにする) にしてください
- 新規では行をレコードの作成と一緒に送ります。更新では、変えた行・足した行・消した行だけを送ります (どの行も変わらなければ明細は送りません)
- 行の読み取り専用の項目 (金額など、メタデータで読み取り専用と書かれている項目) は、足した行でも今ある行でも送りません (エラーにもしません)。
  明細は行ごと全部の値が送られてくるので、値が違っても利用者が変えたのか、読んだ後に NetSuite の再計算やほかの人の保存で変わったのかを見分けられないためです
- 行の中のサブレコード (在庫の詳細など) は扱えません

ほかの人と同時に明細を変えたとき:

| 画面で読んだ後に NetSuite で起きたこと | 保存すると |
|---|---|
| 行が足された | 足された行は残ります (行を消す保存でも消しません) |
| 同じ行の項目が変えられた | 後から保存したほうの値になります (後勝ち) |
| 行が消された | その行を変えていた・残していた保存は、何も送らずにエラーになります。読み込み直してください |

- 同じ行 (同じ `line`) が 2 つある保存は、入力のエラーになります
- 読んだときに明細が空だった・行を全部消して保存したときは、読んだ後に足された行かどうかを見分けられないので、保存した内容が明細の全部になります

制限:

- **一覧の列に明細を出すと、行ごとに NetSuite へレコードの読み出しが 1 回ずつ出ます。** 明細は詳細の画面で見せ、一覧のレイアウトには置かないでください
- 親の明細の列 (`item`) を検索・並べ替え・集計には使えません (明細の列を検索条件に使うとエラー)。明細の中の項目で探す・集計するときは、下の「明細の表」を使います
- **明細の列 (ListField) のあるモジュールに AutoSubmitField (自動保存) を置かないでください。** 自動保存はレコードを読み直さないので、足した行は NetSuite が付けた行の番号 (`line`) を持たないまま画面に残ります。
  次の自動保存で明細の行が全部また送られ、その行は番号の無い新しい行としてもう一度足されます (保存のたびに同じ行が増えます)。
  番号の無い行は「前の保存で足した行をもう一度送ったもの」か「同じ内容の行をもう 1 つ足したもの」かを見分けられないので、コネクターでは防げません

### 取引のレコード

受注 (`salesorder`)・請求書 (`invoice`) などの取引も、ほかのレコードと同じに `DbTable` へ REST のレコード名 (小文字) を書きます。
一覧・検索・集計で SuiteQL の取引の表 (`transaction`) を読むための読み替えは、コネクターがします。
読み替えるのは次の取引です: salesorder・invoice・purchaseorder・estimate・journalentry・creditmemo・vendorbill・cashsale・customerpayment・vendorpayment・
itemfulfillment・itemreceipt・returnauthorization・vendorcredit・customerdeposit・opportunity・transferorder・workorder・workorderissue・workordercompletion・
workorderclose・assemblybuild・assemblyunbuild・inventoryadjustment・inventorytransfer・inventorycount・bintransfer・binworksheet・expensereport・customerrefund・
depositapplication・deposit・check・creditcardcharge・vendorreturnauthorization・purchaserequisition・blanketpurchaseorder・purchasecontract・requestforquote・
vendorprepayment・vendorprepaymentapplication。
この一覧に無い取引は、メタ情報の更新で取り込まれません (一覧・検索で読む表が分からないため)。

### 明細の表 (読み取り専用)

取引の明細の行を、表 1 つとして一覧・検索・並べ替え・集計できます (品目ごとの数量の合計、ある品目を含む受注の明細など)。
表の名前は `<取引のレコード名>_<明細の列名>` です (受注の `item` なら `salesorder_item`)。
メタ情報の更新で、次の取引の品目の明細 (`item`) について自動でメタ情報のファイルに入ります:
salesorder・invoice・estimate・creditmemo・cashsale・returnauthorization・opportunity・itemfulfillment・itemreceipt・transferorder。
SuiteQL の取引の明細の表 (`transactionline`) を読みます。
ほかの明細 (経費の明細など) と、品目と経費の両方の明細を持つ取引 (発注・仕入請求など)・仕訳には表ができません。
明細の表は取引の明細の行を明細の種類で分けずに読むので、明細の種類を 1 つに決められる取引だけにしています。

| 列 | 内容 |
|---|---|
| `uniquekey` | 行のキー (文字列)。モジュールの Id フィールドの `DbColumn` にします |
| `transaction` | 親の取引の internal ID (参照)。親のモジュールへの LinkField にすれば、親の項目 (メモ・日付など) で検索できます |
| `linesequencenumber` | 取引の中での行の順 (整数) |
| 行の項目 | 明細の行の項目 (`item`・`quantity`・`rate`・`amount`・`expectedshipdate` など。小文字。行の番号 `line` は除く) |

- 読むだけです。モジュールは CanCreate・CanUpdate・CanDelete をオフにします。書き込むとエラーになります。明細を変えるときは、親の取引の明細の列 (上の「明細 (サブリスト)」) で保存してください
- 明細の表は上の取引だけです (取引でないレコードの明細には表ができません)
- 一覧で並べる項目が同じ値の行は、`uniquekey` の順に並べます (ページングで行が抜けたり重なったりしないように)

### 取引の添付ファイル

受注などの取引に付いたファイルを、詳細の画面で一覧・ダウンロード・追加・外すことができます。
フィールドは「NetSuite の添付ファイル」(`NetSuiteAttachmentsFieldDesign`) で、`DbColumn` に `files` を選びます。
`files` はメタ情報の更新で取り込んだ取引 (上の「取引のレコード」の一覧のもの) に必ずある列で、NetSuite の項目ではありません (メタ情報のファイルにも書かれません)。
取引でないレコード・明細の表にはありません。

| 操作 | NetSuite で行うこと |
|---|---|
| 一覧 | 接続文字列の保存検索を最後のページまで実行し、表示している取引のファイルだけを取り出す。一覧の 1 ページで 1 回 (行ごとには実行しない)。結果は持っておかない |
| 足す | レコードを保存した後、ファイルを 1 つずつ File Cabinet のフォルダ (`AttachmentFolderId`) に登録し、取引に添付する |
| 外す | 保存の直前に保存検索で読んだ「その取引のファイル」に ID が一致するものだけを、取引から外す。File Cabinet のファイルは消さない |
| ダウンロード | 利用者がその取引を読めて、フィールドの読み取りの権限があり、保存検索でその取引にそのファイルが付いているときだけ中身を返す |

- 取引を削除しても、ファイルは File Cabinet に残ります (コネクターはファイルに何もしません)
- 保存検索が無いときは、ファイルを足すことだけができます (一覧は空・外す指定は無視・ダウンロードはできません)
- 足したファイルは、保存した後もレコードを読み直すまで名前だけが出ます (ダウンロード・外すは読み直してから)
- 1 ファイル 10 MB まで (NetSuite の上限。フィールドの「1 ファイルの上限 (MB)」の既定も 10)。空のファイル・一度に 100 個を超えるファイルは選べません
- 条件・並べ替え・集計には使えません (使うとエラー)。レコードのコピーでは写しません

#### NetSuite 側の準備

1. ファイルを置くフォルダを File Cabinet に作り、internal ID を `AttachmentFolderId` に書く
2. 保存検索を作る (Lists › Search › Saved Searches › New › Transaction)
   - 条件: ファイルの付いている行だけにする (例: File の Internal ID が空でない)。明細ごとに同じ組が出ないよう Main Line = True にする (重なってもコネクターが 1 つにまとめます)
   - 結果の列: Internal ID (取引)・File の Internal ID・File の Name。必要なら File の Size と Type
   - ID (`customsearch_...`) を付けて保存し、連携用のロールが実行できるようにする (公開するか、ロールに使わせる)
   - 保存検索の条件・並びは REST から変えられないので、保存検索で絞った範囲がそのまま一覧の対象になります。全部の取引を最後のページまで読むので、対象を絞り込んでおくと速くなります
3. REST で実行したときの結果の列の名前を確かめて、接続文字列の `AttachmentSearch...Column` に書く (結合した列 = File の列の名前は、実物の結果で確かめてください)
4. 連携用のロールに、File Cabinet (Documents and Files) の作成・参照、取引の編集 (添付・外す)、保存検索の実行の権限を付ける。トークンの scope は `rest_webservices` のままで、ほかの scope は要りません (実物では未確認)

```json
"ConnectionString": "Provider=NetSuite;...;AttachmentFolderId=100;AttachmentSavedSearchId=customsearch_transaction_files;AttachmentSearchFileColumn=file_internalid;AttachmentSearchFileNameColumn=file_name;AttachmentSearchFileSizeColumn=file_documentsize"
```

#### アプリへの組み込み (3 か所)

画面 (Client) のプロジェクトに `Codeer.LowCode.Blazor.Connectors.NetSuite` を追加し、

1. 起動時に `NetSuiteFieldsInitializer.Initialize()` を呼ぶ (クライアントとサーバーの両方。デザインを読む前。デザイナは `NetSuiteDesignerInitializer.Initialize` が呼ぶ)
2. コントローラーから `NetSuiteDbAccessor` を使えるようにする (例: `DataService` に `NetSuiteDbAccessor` のプロパティ)
3. ダウンロードの口を `NetSuiteAttachmentsFieldDesign.DownloadUrl` (`api/netsuite/attachments/download`) に置く。クエリは `module`・`id`・`field`・`fileId`

```csharp
[Authorize]
[HttpGet(NetSuiteAttachmentsFieldDesign.DownloadUrl)]
public async Task<IActionResult> DownloadAsync(string? module, string? id, string? field, string? fileId)
{
    //読めるかの判定 (レコード・フィールドの権限・その取引のファイルか) は GetAttachmentAsync が持つ。読めなければ null
    var file = await _dataService.NetSuite.GetAttachmentAsync(_dataService.ModuleDataIO, _dataService.Design.DesignData, module, id, field, fileId);
    if (file == null) return NotFound();
    Response.Headers.XContentTypeOptions = "nosniff";
    return File(file.Content, "application/octet-stream", file.FileName);
}
```

見本は Example の `NetSuiteAttachmentsController.cs` (ファイル名の制御文字を外す・監査ログ) にあります。

#### 保存が途中で失敗したとき

レコードの保存とファイルの登録・添付は NetSuite でそれぞれ確定します。途中で失敗すると、レコードは保存済みのまま `NetSuiteException` になり、
`RecordId` に取引の internal ID、`UnattachedFileIds` に File Cabinet に登録したが添付できなかった (または添付できたか分からない) ファイルの ID が入ります。
メッセージには、どこまで済んだか (添付できたファイル・送っていないファイルの数) が入ります。

コネクターは書き込みをやり直さず、登録したファイルも消しません。同じ保存をそのままやり直すと、済んだ分のファイルが二重に付きます。
利用者には、レコードを開き直して付いているファイルを確かめ、足りないファイルだけを足し直してもらってください (Example の文言はそうしています)。
添付できなかったファイルは File Cabinet に残るので、要らなければ NetSuite で消してください。

## NetSuite のアカウントでログインする

NetSuite は OpenID Connect のプロバイダになれます。アプリの外部ログイン (Extras の `OidcLogins`) に NetSuite を 1 件書くと、
ログイン画面に「NetSuite」のボタンが出て、NetSuite のアカウントでアプリにログインできます。コネクターに専用のクラスはありません (汎用の OpenID Connect の設定です)。

ログインした人をアプリのどのユーザーにするかで、2 つの方式があります。

| | 方式 1: ユーザーの表は DB | 方式 2: NetSuite の従業員をユーザーにする |
|---|---|---|
| ユーザーモジュール | DB の表 (`AppUser` など) | NetSuite の `employee` のモジュール |
| 突き合わせ | NetSuite のメールと、ユーザーの表の外部ログイン名の列 | NetSuite のメールと、従業員のメール |
| ユーザーの登録 | アプリの側で行を作る (行の無い人はログインできない) | 要らない (NetSuite の従業員がそのままユーザー) |
| パスワードのログイン | 併用できる | できない (パスワードの列を持たない) |
| セッションの失効 (DbColumnSessionStamp) | 使える | 使えない |

### NetSuite 側の準備

公開されている仕様に沿って書いています。実アカウントではまだ確かめていません。

- **機能を有効にする**: OAuth 2.0
- **インテグレーションレコード**: Authorization Code Grant を有効にし、Scope に `openid` と `email` を選ぶ。Redirect URI に `https://<アプリのホスト>/signin-netsuite` を書く
  (`signin-` の後ろは `OidcLogins` の `Name` を小文字にしたもの)。Client ID と Client Secret を控える
- **ロールの権限**: ログインする人のロールに Log in using OAuth 2.0 Access Tokens
- NetSuite の OpenID Connect の scope は `openid` と `email` だけです (`profile` は無いので、`Scopes` を省かずに書きます)。ID トークンに名前のクレームは無いので、アプリに出す名前はユーザーモジュールの表示名から取ります

### アプリの設定

`appsettings.json` の `OidcLogins` に書きます。Client Secret は `appsettings.Development.json` か環境変数に置き、リポジトリに入れないでください。

```json
{
  "OidcLogins": [
    {
      "Name": "NetSuite",
      "DisplayName": "NetSuite",
      "ClientId": "<インテグレーションレコードの Client ID>",
      "ClientSecret": "<インテグレーションレコードの Client Secret>",
      "Authority": "https://<アカウント ID>.suitetalk.api.netsuite.com",
      "Scopes": [ "openid", "email" ],
      "LoginNameClaim": "email"
    }
  ]
}
```

| キー | 書く値 |
|---|---|
| `Name` | プロバイダ名 (英数字と `-` `_`)。Redirect URI の `signin-<小文字>` になる |
| `Authority` | アカウント固有の SuiteTalk の URL (データソースの `ServiceUrl` と同じ)。`<Authority>/.well-known/openid-configuration` を読む |
| `Scopes` | `openid` と `email` |
| `LoginNameClaim` | `email`。ユーザーと突き合わせる値になる |
| `AllowedDomains` | (任意) ログインを許すメールのドメイン |

`Authority` は https でないと動きません (ASP.NET Core の OpenID Connect が https でしか設定を読まないため)。手元のモックで試すときも https で起動します ([NetSuiteMock.md](NetSuiteMock.md))。

### 方式 1: ユーザーの表は DB

ユーザーモジュール (`AppUser` など) にメールのフィールドを足し、ログインアカウントの契約 (`LoginAccount`) の `ExternalLoginName` にそのフィールドを選びます。
NetSuite でログインした人のメールが、どれかの行のそのフィールドと一致すればその行のユーザーになります。一致する行が無ければ、ログイン画面に「このアカウントは登録されていません」で戻ります。
パスワードのログイン (`LoginName` / `PasswordHash`) はそのまま使えます。パスワードを持たない行 (NetSuite でだけログインする人) も作れます。

### 方式 2: NetSuite の従業員をユーザーにする

デザインのユーザーモジュール (`CurrentUserModuleDesignName`) を、NetSuite の `employee` のモジュールにします。

| フィールド | DbColumn | |
|---|---|---|
| Id (IdField) | `id` | 従業員の internal ID がユーザーの Id になる |
| 表示名 (TextField) | `entityid` | 画面に出す名前 |
| メール (TextField) | `email` | NetSuite のログインのメール |
| LoginAccount (ログインアカウントの契約) | | `LoginName` = メール、`DisplayName` = 表示名。パスワード・有効フラグ・セッションの印は選ばない |

- モジュールは読むだけにします (CanCreate / CanUpdate / CanDelete をオフ)。従業員の登録・無効化は NetSuite でします
- NetSuite のデータソースのロールに従業員を読む権限が要ります (ログインする人のロールではありません)。権限が無いと、メタ情報に `employee` が入りません
- パスワードのログインはできません。管理者もパスワードでは入れないので、最初から NetSuite でログインできる人をそろえておきます
- セッションの失効 (DbColumnSessionStamp) は使えません (ログインの仕組みがセッションの印の列を DB に直接つないで扱うため)

**ホストの結線**: ログインの仕組み (Extras の `LoginAccountStore`) は、ユーザーの行をモジュールの読み書きを通さずに、ホストから渡された `IDbAccessor` で読みます。
方式 2 では、そこに `NetSuiteDbAccessor` で包んだもの (`ModuleDataIO` に渡すのと同じもの) を渡してください。包む前の `DbAccessor` を渡すと、NetSuite のデータソースを DB として開こうとしてログインが失敗します。
`Source/Example` では `DataService` に包んだもの (`ModuleDbAccess`) を持たせ、`ModuleDataIO`・ログイン (パスワード・外部ログイン・Cookie の照合)・Extras の機能に渡しています。
一時ファイルの管理など DB を直接扱うところには、包む前の `DbAccess` を渡したままにします。

ログインに使う従業員と、データソースの M2M の設定 (インテグレーションの Entity とロール) は別のものです。
NetSuite への読み書きは、ログインした従業員ではなく、データソースの M2M の Entity とロールの権限で行われます。

## 動き

### 一覧・検索

モジュールの検索条件・並び・ページングは SuiteQL になって NetSuite で実行されます。
ほかの NetSuite のモジュールへのリンク越しの項目 (LinkField の表示名など) は結合になります。

- ページをまたいでも順序が変わらないよう、並びの最後に internal ID が足されます。
- 件数を絞らない読み出しは 1000 行ずつ読み進め、`MaxRows` を超えたらエラーにします。一覧には表示件数を設定してください。
- 日付は日付のまま、日時は UTC として読み書きします。
- 集計 (件数・合計・平均・最小・最大、項目や日付の区切りごと) も SuiteQL になって NetSuite で実行されます。

### 保存

| 操作 | NetSuite への要求 |
|---|---|
| 新規 | レコードの作成。NetSuite が採番した internal ID が Id になる |
| 更新 | 変更したフィールドの項目だけを更新する。空にしたフィールドは項目を消す |
| 削除 | レコードの削除 |

- 項目名や値を NetSuite が受け付けなければ、保存は NetSuite のメッセージのエラーになります。
- **NetSuite への保存はレコード 1 件ごとに確定します。** 同じ保存の中のほかの処理が失敗しても戻りません。
  1 回の保存で NetSuite のレコードを複数書く画面や、NetSuite と DB を一緒に書く画面は、途中まで保存された状態が起こりえます
  (保存がデータソースごとに確定するのは、DB のデータソースを 2 つ使うときと同じです)。そろっていないと困る組み合わせは、画面を分けてください。
- 一括の追加は 1 件ずつ作ります。途中で失敗したときは、エラーのメッセージに作成済みの件数と internal ID・失敗した 1 件 (結果が分からないときはそう書きます)・送っていない件数が入り、
  `NetSuiteException.CreatedIds` に作成済みの internal ID が入ります (作成済みのレコードは残ります)。照合してから続きを保存してください。
  Example は、本番ではこれを定型の文言 (作成済みの internal ID と、照合用のリクエスト Id つき) にして画面に出します
- 読み取り専用の項目は、新規では送らず、更新で変えると NetSuite に送る前にエラーにします。明細の行の読み取り専用の項目は、新規でも更新でも送りません (エラーにしません)。
- **Id 以外の条件が付いた更新・削除 (データの書き込み条件など) は、先に読んで条件に合うかを確かめてから、Id を指定して書きます。**
  確かめてから書くまでの間に NetSuite でデータが変わると、条件に合わなくなったレコードにも書きます (保存の時点で条件に合うことは保証しません)。
  「承認の前だけ編集できる」のように保存の時点で条件に合うことが必要な画面は、NetSuite 側 (ワークフロー・スクリプト・ロールの権限) で書き込みを止めるか、編集できないようにしてください。
  Id だけを指定する普通の更新・削除は先に読みません
- 書き込みを送ったあと応答を受け取れなかった場合と、NetSuite がサーバー側の失敗 (503 以外の 5xx) を返した場合は、実行されたかどうかが分かりません。
  コネクターはやり直さず (二重に作らないため)、`WriteResultUnknown` のエラーにします。NetSuite 側で結果を確かめてください。
  書き込みへの 503 は `ServiceUnavailable` のエラーにします (これもやり直しません)。NetSuite が処理を始めていないと言い切れる資料は無いので、
  利用者にはやり直す前に NetSuite で保存されていないことを確かめてもらってください (Example の文言はそうしています)。

## エラー

コネクターの失敗は `NetSuiteException` で、`Kind` で種類が分かります。

| Kind | 内容 |
|---|---|
| `NotSupported` | 設定の誤り・コネクターが扱えない操作 |
| `Authentication` | 認証の失敗 (鍵・証明書・Client ID・M2M の設定) |
| `PermissionDenied` | NetSuite 側のロールの権限不足 |
| `InvalidRequest` | 入力・業務ルールのエラー。`ErrorCode` / `ErrorPath` に NetSuite のコードと項目 |
| `NotFound` | 対象が無い |
| `Conflict` | 読んだ後に NetSuite でデータが変わっていた (明細の行が消されていた)。何も送っていない。読み込み直してやり直す |
| `RateLimited` | 同時実行・呼び出し量の制限 (やり直しても解消しなかった) |
| `ServiceUnavailable` | NetSuite 側・通信の障害 |
| `WriteResultUnknown` | 書き込みを送ったが、実行されたかどうかが分からない (応答を受け取れなかった・503 以外のサーバー側の失敗が返った) |

読み出しは一時的な失敗 (制限超過・障害) を時間を置いて 3 回までやり直します。書き込みは、実行されていないことが確かな失敗だけをやり直します。
一括の追加の途中で失敗したときは、`CreatedIds` に作成済みの internal ID が入ります (`Kind` などは失敗した 1 件のもの)。
レコードは保存したがファイルの添付が途中で失敗したときは、`RecordId` と `UnattachedFileIds` が入ります (上の「取引の添付ファイル」)。一括の追加では、そのレコードも `CreatedIds` に入ります。

例外のメッセージは英語で、項目名やレコードの種別を含みます。本番の画面には出さず、`Kind` を見て利用者向けの文言にしてください (例は Example の `ExceptionHandlerUtils`)。

## 扱えないもの

- サブレコード (住所など)・複数選択の項目・明細の行の中のサブレコード
- 取引でないレコードの明細の中の項目での検索・並べ替え・集計、明細の表の無い取引・明細の中の項目での検索・並べ替え・集計 (表のある取引の品目の明細は「明細の表」で読めます)
- 上の一覧に無い取引のレコード
- 固定の選択肢の表示名の取り込み (値だけが入ります。表示名は Enum に書きます)
- ExecuteSqlField・QueryField などの SQL を直接書く機能
- 複合キー、Id の手入力
- OptimisticLockingField による同時更新の検出 (NetSuite には条件つきの更新が無く、確実には検出できない)
- 外部 ID を鍵にした作成・更新 (Upsert)。モジュールの Id は常に internal ID
- 保存検索 (添付ファイルの一覧に使う以外)・Dataset・レコードのアクション・変換
- 取引でないレコードの添付ファイル、File Cabinet のファイルの削除・差し替え
- NetSuite の従業員をユーザーにしたとき (ログインの方式 2) のパスワードのログイン・セッションの失効
