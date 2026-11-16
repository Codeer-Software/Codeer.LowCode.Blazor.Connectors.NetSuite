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
4. デザイナで、DB のテーブルと同じ手順で NetSuite のレコードからモジュールを作る

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
      "ConnectionString": "Provider=NetSuite;ServiceUrl=https://1234567.suitetalk.api.netsuite.com;ClientId=xxxxxxxx;CertificateId=xxxxxxxx;PrivateKeyPath=C:\\keys\\netsuite.pem;Records=customer,customrecord_sample"
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
| `Records` | ○ | 扱うレコードの種別をカンマ区切りで。ここに書いたレコードだけを読み書きする |
| `Algorithm` | | トークン要求の署名方式。`PS256` (既定) / `PS384` / `PS512` / `ES256` / `ES384` / `ES512`。鍵の種類に合わせる |
| `Scope` | | トークンの scope。既定 `rest_webservices` |
| `TimeoutSeconds` | | 1 回の要求のタイムアウト (秒)。既定 100 |
| `MaxConcurrency` | | NetSuite へ同時に出す要求の上限。既定 4。NetSuite の同時実行の枠はほかの連携と共有なので控えめにする |
| `MaxRows` | | 件数を絞らない読み出しで受け取る行の上限。既定 10000。超えるとエラーになる |

秘密鍵は DB の接続文字列と同じ扱いにしてください (デザインのファイルやリポジトリに入れない。運用環境では環境変数やシークレットストアから渡す)。

`Records` に書くのは REST のレコード名です (`customer`、`vendor`、カスタムレコードなら `customrecord_xxx`)。

## 3. 組み込み

| プロジェクト | パッケージ |
|---|---|
| アプリ (サーバー) | `Codeer.LowCode.Blazor.Connectors.NetSuite.Server` |
| デザイナ | `Codeer.LowCode.Blazor.Connectors.NetSuite.Designer` |

### アプリ (サーバー)

`ModuleDataIO` に渡す `IDbAccessor` を `NetSuiteDbAccessor` で包みます。

```csharp
using Codeer.LowCode.Blazor.Connectors.NetSuite.Server;

DbAccess = new DbAccessor(SystemConfig.Instance.DataSources);
var netSuite = new NetSuiteDbAccessor(DbAccess, NetSuiteConnection.FromDataSources(SystemConfig.Instance.DataSources));
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

### デザイナはメタ情報をファイルから読む

デザイナは、NetSuite のレコードの定義 (メタ情報) をプロジェクトのフォルダのファイル `netsuite.<データソース名>.metadata.json` から読みます。
プロジェクトを開くたびに NetSuite へ取りに行くことはありません。

- **取り込み・更新**: メニュー **Tools > NetSuite のメタ情報を更新**。`Records` に書いたレコードの定義を NetSuite から取り直してファイルに保存します。
  最初の 1 回と、`Records` を変えたとき・NetSuite 側で項目を変えたときに実行してください。ファイルが無いうちは、NetSuite のレコードはデザイナに出ません
- **中身**: レコードと項目の名前・型だけです。接続の情報や鍵は入りません。デザインのファイルと一緒にリポジトリへ入れておけば、
  NetSuite に接続できない環境 (鍵を持たない人・オフライン) でもデザインできます
- アプリ (サーバー) はこのファイルを使いません。実行時は NetSuite から定義を取ります

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

- 参照の項目の値は相手の internal ID です。相手のレコードのモジュールを作って LinkField で結べば、表示名での表示・検索ができます (相手のレコードも `Records` に書く)。
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
- 行の番号 `line` は結び付けなくてかまいません。読んだ行に入っていて、保存のときに行の突き合わせに使われます
- 日付は Date のノード + DateField、日時は DateTime のノード + DateTimeField (`SaveAsUtc` をオンにする) にしてください
- 新規では行をレコードの作成と一緒に送ります。更新では、変えた行・足した行・消した行だけを送ります (どの行も変わらなければ明細は送りません)
- 行の中のサブレコード (在庫の詳細など) は扱えません

制限:

- **一覧の列に明細を出すと、行ごとに NetSuite へレコードの読み出しが 1 回ずつ出ます。** 明細は詳細の画面で見せ、一覧のレイアウトには置かないでください
- **後勝ちです。** 画面で読んでから保存するまでに NetSuite で明細が変わっていても、保存した内容で上書きされます (行を消したときは、その間に NetSuite で足された行も消えます)
- 親の明細の列 (`item`) を検索・並べ替え・集計には使えません (明細の列を検索条件に使うとエラー)。明細の中の項目で探す・集計するときは、下の「明細の表」を使います

### 取引のレコード

受注 (`salesorder`)・請求書 (`invoice`) などの取引も、ほかのレコードと同じに `Records` と `DbTable` へ REST のレコード名を書きます。
一覧・検索・集計で SuiteQL の取引の表 (`transaction`) を読むための読み替えは、コネクターがします。
読み替えるのは次の取引です: salesorder・invoice・purchaseorder・estimate・journalentry・creditmemo・vendorbill・cashsale・customerpayment・vendorpayment・
itemfulfillment・itemreceipt・returnauthorization・vendorcredit・customerdeposit・opportunity。

### 明細の表 (読み取り専用)

取引の明細の行を、表 1 つとして一覧・検索・並べ替え・集計できます (品目ごとの数量の合計、ある品目を含む受注の明細など)。
表の名前は `<取引のレコード名>_<明細の列名>` です (受注の `item` なら `salesorder_item`)。
親の取引を `Records` に書けば、その取引の明細ごとに自動でできます (`Records` には書きません)。メタ情報のファイルにも入ります。
SuiteQL の取引の明細の表 (`transactionline`) を読みます。

| 列 | 内容 |
|---|---|
| `uniquekey` | 行のキー (文字列)。モジュールの Id フィールドの `DbColumn` にします |
| `transaction` | 親の取引の internal ID (参照)。親のモジュールへの LinkField にすれば、親の項目 (メモ・日付など) で検索できます |
| `linesequencenumber` | 取引の中での行の順 (整数) |
| 行の項目 | 明細の行の項目 (`item`・`quantity`・`rate`・`amount`・`expectedshipdate` など。小文字。行の番号 `line` は除く) |

- 読むだけです。モジュールは CanCreate・CanUpdate・CanDelete をオフにします。書き込むとエラーになります。明細を変えるときは、親の取引の明細の列 (上の「明細 (サブリスト)」) で保存してください
- 親の取引を `Records` から外すと、明細の表も無くなります
- 明細の表は取引のレコードだけです (取引でないレコードの明細には表ができません)
- 一覧で並べる項目が同じ値の行は、`uniquekey` の順に並べます (ページングで行が抜けたり重なったりしないように)

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
- `Records` に `employee` を足します。NetSuite のデータソースのロールに従業員を読む権限が要ります (ログインする人のロールではありません)
- パスワードのログインはできません。管理者もパスワードでは入れないので、最初から NetSuite でログインできる人をそろえておきます
- セッションの失効 (DbColumnSessionStamp) は使えません (ログインの仕組みがセッションの印の列を DB に直接つないで扱うため)

**ホストの結線**: ログインの仕組み (Extras の `LoginAccountStore`) は、ユーザーの行をモジュールの読み書きを通さずに、ホストから渡された `IDbAccessor` で読みます。
方式 2 では、そこに `NetSuiteDbAccessor` で包んだもの (`ModuleDataIO` に渡すのと同じもの) を渡してください。包む前の `DbAccessor` を渡すと、NetSuite のデータソースを DB として開こうとしてログインが失敗します。
今の `Source/Example` はテンプレートどおり包む前の `DbAccessor` を渡しているので、方式 2 はそのままでは動きません。

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
- 一括の追加は 1 件ずつ作ります。途中で失敗したときは、エラーのメッセージに作成済みの件数と internal ID が入ります
  (作成済みのレコードは残ります)。照合してから続きを保存してください。
- 書き込みを送ったあと応答を受け取れなかった場合と、NetSuite がサーバー側の失敗 (503 以外の 5xx) を返した場合は、実行されたかどうかが分かりません。
  コネクターはやり直さず (二重に作らないため)、`WriteResultUnknown` のエラーにします。NetSuite 側で結果を確かめてください。
  書き込みへの 503 は実行されていないものとして `ServiceUnavailable` のエラーにします (これもやり直しません)。

## エラー

コネクターの失敗は `NetSuiteException` で、`Kind` で種類が分かります。

| Kind | 内容 |
|---|---|
| `NotSupported` | 設定の誤り・コネクターが扱えない操作 |
| `Authentication` | 認証の失敗 (鍵・証明書・Client ID・M2M の設定) |
| `PermissionDenied` | NetSuite 側のロールの権限不足 |
| `InvalidRequest` | 入力・業務ルールのエラー。`ErrorCode` / `ErrorPath` に NetSuite のコードと項目 |
| `NotFound` | 対象が無い |
| `RateLimited` | 同時実行・呼び出し量の制限 (やり直しても解消しなかった) |
| `ServiceUnavailable` | NetSuite 側・通信の障害 |
| `WriteResultUnknown` | 書き込みを送ったが、実行されたかどうかが分からない (応答を受け取れなかった・503 以外のサーバー側の失敗が返った) |

読み出しは一時的な失敗 (制限超過・障害) を時間を置いて 3 回までやり直します。書き込みは、実行されていないことが確かな失敗だけをやり直します。

## 扱えないもの

- サブレコード・複数選択の項目・明細の行の中のサブレコード
- 取引でないレコードの明細の中の項目での検索・並べ替え・集計 (取引の明細は「明細の表」で読めます)
- 上の一覧に無い取引のレコード
- ExecuteSqlField・QueryField などの SQL を直接書く機能
- 複合キー、Id の手入力
- OptimisticLockingField による同時更新の検出 (NetSuite には条件つきの更新が無く、確実には検出できない)
- 外部 ID を鍵にした作成・更新 (Upsert)。モジュールの Id は常に internal ID
- 保存検索・Dataset・レコードのアクション・変換・ファイル
- NetSuite の従業員をユーザーにしたとき (ログインの方式 2) のパスワードのログイン・セッションの失効
