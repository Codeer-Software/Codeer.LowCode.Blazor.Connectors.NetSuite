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
| アプリ (サーバー) | `Codeer.LowCode.Blazor.Connectors.NetSuite` |
| デザイナ | `Codeer.LowCode.Blazor.Connectors.NetSuite.Designer` |

### アプリ (サーバー)

`ModuleDataIO` に渡す `IDbAccessor` を `NetSuiteDbAccessor` で包みます。

```csharp
using Codeer.LowCode.Blazor.Connectors.NetSuite;

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
- 明細 (サブリスト)・住所などのサブレコード・複数選択の項目は列になりません。
- 使う項目だけをフィールドにしてください。フィールドにしていない項目は読みも書きもしません。
- NetSuite で必須の項目も、列としては空を許す形で見えます。必須かどうかは保存のときに NetSuite が確かめ、足りなければ保存がエラーになります。
  画面で先に止めたいときは、フィールドを必須にしてください。

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
- 書き込みを送ったあと応答を受け取れなかった場合と、NetSuite がサーバー側の失敗を返した場合は、実行されたかどうかが分かりません。
  コネクターはやり直さず (二重に作らないため)、`WriteResultUnknown` のエラーにします。NetSuite 側で結果を確かめてください。

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
| `WriteResultUnknown` | 書き込みを送ったが、実行されたかどうかが分からない (応答を受け取れなかった・サーバー側の失敗が返った) |

読み出しは一時的な失敗 (制限超過・障害) を時間を置いて 3 回までやり直します。書き込みは、実行されていないことが確かな失敗だけをやり直します。

## 扱えないもの

- 明細 (サブリスト)・サブレコード・複数選択の項目
- SuiteQL での表の名前がレコード名と違うレコード (受注・請求書などの取引)
- ExecuteSqlField・QueryField などの SQL を直接書く機能
- 複合キー、Id の手入力
- OptimisticLockingField による同時更新の検出 (NetSuite には条件つきの更新が無く、確実には検出できない)
- 外部 ID を鍵にした作成・更新 (Upsert)。モジュールの Id は常に internal ID
- 保存検索・Dataset・レコードのアクション・変換・ファイル
