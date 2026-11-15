# Codeer.LowCode.Blazor.Connectors.NetSuite - Project Guide

## プロジェクト概要

Codeer.LowCode.Blazor (CLB) のモジュールを Oracle NetSuite のレコードに結び付けるコネクター。
NuGet パッケージ `Codeer.LowCode.Blazor.Connectors.NetSuite.Server` / `Codeer.LowCode.Blazor.Connectors.NetSuite.Designer` (MIT) として配布する。

- **ターゲット**: .NET 8.0 / C# (Nullable 有効, ImplicitUsings 有効)
- **本体リポジトリ**: `C:\tfs\codeer\Codeer.LowCode.Blazor` (参照用、直接編集しない)。本体は NuGet パッケージで参照する
- **作りの手本**: `C:\GitHub\Codeer.LowCode.Blazor.Extras`
- **利用者向けドキュメント**: `Docs/NetSuite.md`
- **調査資料**: `Docs/01_netsuite_api_inventory.md` (API 調査) / `Docs/02_netsuite_specification_draft.md` (仕様案) / `Docs/03_research_request.md` (追加調査の依頼) / `Docs/04_research_request_design.md` (作りの分岐を決めるための調査の依頼) / `Docs/05_design_research_results.md` (04 の調査の回答) / `Docs/06_sandbox_verification_plan.md` (Sandbox で確かめることの計画)

## リポジトリ構成

```
Source/
├── Codeer.LowCode.Blazor.Connectors.NetSuite.slnx
├── Codeer.LowCode.Blazor.Connectors.NetSuite.Server/ # サーバー側ライブラリ (WPF・ASP.NET に依存しない)
│   ├── NetSuiteDbAccessor.cs          # IDbAccessor のデコレータ (公開の入口)
│   ├── NetSuiteConnection.cs          # 接続 1 つ: 認証・送信 (再試行・エラー分類)・定義・SuiteQL・レコード操作
│   ├── NetSuiteConnectionSettings.cs  # ConnectionString "Provider=NetSuite;..." の読み取り
│   ├── NetSuiteRecordDefinition.cs    # REST メタデータ (JSON Schema) → 列の定義
│   ├── SuiteQlTranslator.cs           # 本体の SQL → SuiteQL / レコード操作
│   ├── NetSuiteSublistJson.cs         # 明細の列の値 (行の JSON 配列) と REST の明細の本文の変換・更新の差分
│   └── NetSuiteException.cs
├── Codeer.LowCode.Blazor.Connectors.NetSuite.Designer/   # デザイナ用 (WPF)。AI 用の仕様ドキュメントの登録・メタ情報の更新メニュー・デザイナ用の IDbAccessor
│   ├── NetSuiteDesignerInitializer.cs # Initialize(env) = SpecDocs の登録 + ファイルから読むモード / Setup(env) = Tools のメニュー / CreateDbAccessor
│   └── SpecDocs/NetSuite.md           # AI が NetSuite のモジュールを設計するための仕様 (デザイン側のことだけを書く)
├── NetSuite.Mock/   # NetSuite のモック (ASP.NET Core・SQLite のインメモリ)。利用者が手元で起動して試す用。パッケージにしない
│   ├── MockNetSuite.cs                # 定義・データ・トークンを持ち、REST の要求に応える
│   ├── MockNetSuiteEndpoints.cs       # /services/rest/... の結び付け (MapMockNetSuite。テストは TestServer で同じものを立てる)
│   ├── MockSuiteQl.cs                 # SuiteQL を SQLite で動かす (文の確認・ROWNUM / NUMTODSINTERVAL の書き換え・Oracle の関数)
│   ├── MockRecordType.cs              # mock-data.json の読み取りと値の変換
│   ├── MockOidc.cs                    # アプリのログイン用の OpenID Connect のプロバイダ (discovery・認可の画面・認可コード + PKCE・JWKS・PS256 の ID トークン)。相手は employee の行
│   └── mock-data.json                 # 既定の定義と初期データ (架空のサンプル)。利用者向けの説明は Docs/NetSuiteMock.md
├── Codeer.LowCode.Blazor.Connectors.NetSuite.Test/   # NUnit。HTTP は偽物 (Harness/FakeNetSuite) とモック (Harness/MockHarness = モック + 本物のコネクター + 本体の ModuleDataIO)
│                                    # Extras.Server を参照するのはテストだけ (MockLoginAccountTest = Extras の LoginAccountStore の SQL がコネクターを通るか)
├── Example/                         # サンプル (パッケージにしない)。動かし方は Docs/Example.md
│   ├── LowCodeApp.Designer/         # コネクターを組み込んだデザイナ (WPF・net8)。結線の見本 (App.xaml.cs / DbAccessorFactory.cs) 兼 GUI テストの対象。Design の AppUser のため Extras.Designer も組み込む
│   ├── LowCodeApp.Server/ LowCodeApp.Client/ LowCodeApp.Client.Shared/   # Web アプリ (net10)。Starter の Cookie ホストを StarterTool export-app で書き出したもの
│   │                                # テンプレートとの違い: DataService.cs の NetSuiteDbAccessor の 2 行・.Server の ProjectReference・appsettings (Main = SQLite / NetSuite)・パッケージの版 (公開済みの版)
│   │                                # NetSuite の接続文字列は appsettings に書かない (環境変数 ConnectionStrings__NetSuite)
│   ├── Design/                      # デザインのプロジェクト。Customer / Subsidiary / CustomRecordSample / Item / SalesOrder (+ 行 SalesOrderItem) / SalesOrderLine (明細の表) / Employee (NetSuite)・AppUser (SQLite。メールの列 = 外部ログイン名)・netsuite.NetSuite.metadata.json (モックの定義)
│   └── Data/main.sql                # ログインするユーザーの表と admin / admin・NetSuite でだけ入るユーザー (モックの従業員 1 のメール)
├── Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest/   # NUnit のブラウザテスト (ヘッドレス Chrome)。Example の Web アプリを別プロセスで起動して操作する
│   ├── Harness/MockServer.cs          # モックをこのプロセスの Kestrel で 127.0.0.1 の空きポートに立てる・モックのデータを SuiteQL で確かめる
│   ├── Harness/AppServer.cs           # 一時フォルダに鍵・SQLite・App.zip を作り、Example の Server を環境変数で設定を上書きして起動 (止めるのは自分が起動したプロセスだけ)
│   ├── PageObject/                    # 手書き (Example のデザイナには pageobject の CLI が無い)。モジュールのレイアウトを変えたら合わせる
│   └── Scenario/                      # 一覧 (リンク先の名前)・検索・作成/更新/削除・受注の明細の作成/変更/追加/削除と明細の表の検索 (SalesOrderTest)・NetSuite のアカウントでのログイン (NetSuiteLoginTest。モックを https で別に立てる)
└── Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest/   # NUnit の GUI テスト。Example のデザイナを起動して Friendly で操作する
    ├── Harness/MockServer.cs          # モックを Kestrel で 127.0.0.1 の空きポートに立てる (要求を数える・同じポートで立て直す)。DeadPort = 届かない NetSuite
    ├── Harness/DesignProject.cs       # テストごとの一時フォルダ: 空のデザインプロジェクト (NetSuite のデータソースだけ) とその場で作った鍵
    └── Drivers/DesignerDriver.cs      # 起動 (引数で app.clprj を開く)・メニュー・トースト・MessageWindow・テーブルの定義・モジュール作成・デザインチェック
```

パッケージは 2 つ (Extras.Server と Extras.Designer と同じ分け方): サーバーは `.Server`、デザイナは `.Designer` (`.Server` に依存) を参照する。
無印の `Codeer.LowCode.Blazor.Connectors.NetSuite` は、フロント用のフィールドを足すときのために空けてある (Extras の無印と同じ位置づけ)。

## ビルド・テスト

```bash
cd Source
MSBUILDDISABLENODEREUSE=1 dotnet build Codeer.LowCode.Blazor.Connectors.NetSuite.Server -m:1
MSBUILDDISABLENODEREUSE=1 dotnet test Codeer.LowCode.Blazor.Connectors.NetSuite.Test -m:1
```

`.Designer` は WPF なので dotnet CLI ではなく VS の MSBuild でビルドする (`MSBuild.exe <csproj> -restore -m:1`)。

### GUI テスト (DesignerTest)

`Example/LowCodeApp.Designer` と `DesignerTest` も WPF なので VS の MSBuild でビルドする (DesignerTest をビルドすると Example のデザイナも先にビルドされる)。
テストの実行は別コマンドで `dotnet test --no-build`。

```bash
cd Source
MSBUILDDISABLENODEREUSE=1 "C:/Program Files/Microsoft Visual Studio/2022/Professional/MSBuild/Current/Bin/MSBuild.exe" Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest/Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest.csproj -restore -m:1 -v:m -p:UseSharedCompilation=false
dotnet test Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest/bin/Debug/net8.0-windows/Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest.dll --no-build
```

- 実行するとデザイナの画面が実際に立ち上がる (テストごとに起動して閉じる)。操作中はマウス・キーボードに触らない
- CLB のライセンスを登録したマシンで動かす (起動時のライセンス確認を通る前提)
- モックはテストのプロセスの中で Kestrel に立てる (127.0.0.1 の空きポート・テストの間だけ)。デザインのプロジェクトと鍵はテストごとの一時フォルダ
- 1 回の起動に上限 (120 秒) があり、超えるとそのテストが起動したデザイナだけを止める (プロセス名で一括に止めない)
- WebView (Blazor) の中は WPF のラッパーで触れないので、テーブルからのモジュール作成とデザインチェックは対象プロセスの中の API を直接呼ぶ
  (作成は DB から取り込むときと同じ変換 `DbColumnFieldConverter.ConvertForModule` + デザイナの保存。Example は最小構成で、Designer.Standard の「DB から取り込む」メニューは無い)

### ブラウザのテスト (SeleniumTest)

```bash
cd Source
MSBUILDDISABLENODEREUSE=1 dotnet build Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest -m:1
dotnet test Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest --no-build
```

- ビルドすると Example の Server も同じ構成 (Debug / Release) でビルドされ、テストはその出力 (`bin/<構成>/net10.0`) を `dotnet` で起動する
- 一覧・検索はモックの初期データ (mock-data.json) を前提にしている
- 画面の変化は条件待ち (上限 30 秒 = `WebDriverManager.WaitTimeout`)。超えたらフリーズとして失敗にする。失敗するとスクリーンショットとサーバーの出力が残る
- ログインのテストはモックを https (ASP.NET Core の開発用証明書) で立てる。`dotnet dev-certs https --trust` 済みのマシンで動かす (OpenID Connect の discovery は https でしか読まれない)
- ログインの方式 2 (ユーザーモジュール = Employee) は `[Ignore]`。Example の `ExternalLoginUserResolver` などが `LoginAccountStore.Create(design, dataService.DbAccess)` に包む前の `DbAccessor` を渡すため
  (`DbAccess` の型を `IDbAccessor` にすると `ModuleDataController` の `DbAccess.ClearAsync()` がコンパイルできない)

## 設計: NetSuite を IDbAccessor として見せる

本体の `ModuleDataIO` は DB 処理 (`ModuleDataIOForDb`) を直接持っていて、データソースごとに処理を差し替える口は無い。
一方 `IDbAccessor` は公開のインターフェースで、ホストが実装を渡す。デザイナも `IDbAccessorFactory` 経由で同じものを使う。
そこで NetSuite 用のデータソースだけを横取りするデコレータ (`NetSuiteDbAccessor`) にした。**本体への変更は無い。**

```
ModuleDataIO (認可・インターセプタ・一覧/保存の組み立て = 本体のまま)
  └─ ModuleDataIOForDb が SQL を組み立てる (NetSuite 用データソースは Oracle 方言)
       └─ NetSuiteDbAccessor
            ├─ NetSuite のデータソース → SuiteQlTranslator → NetSuiteConnection (REST)
            └─ それ以外 → 包んだ IDbAccessor (DbAccessor)
```

この形で得られるもの:

- 認可 (UserRead / DataRead 条件・フィールド権限)・インターセプタ (監査・編集履歴)・サーバー側スクリプト・リンクの結合が、DB のモジュールと同じ経路で効く。
  ブラウザから来るのは通常の検索条件と保存データだけで、SuiteQL・URL・レコード種別を直接指定する口は無い
- デザイナの「テーブルからモジュール作成」・列のツリー・接続確認が、`GetCustomTableDefinitionsAsync` を返すだけで動く
- 設定は既存の DataSources に乗る (`ConnectionString` が `Provider=NetSuite;...`)。本体の設定の型もデザイナの UI も変えない

### SQL の読み替え (SuiteQlTranslator)

`GetDataSource` で `DataSourceType.Oracle` を返すので、本体は Oracle 方言で SQL を組む (SuiteQL は Oracle の書き方を受け付ける)。
本体が組む形だけを受け付け、それ以外 (`;` を含む文・知らない形の書き込み) は拒否する。

| 本体の SQL | NetSuite |
|---|---|
| `select ... from "t" left join ... where ... order by ... offset N rows fetch next M rows only` | SuiteQL。引用符を外す・ページング句は URL の limit / offset へ・日付と日時の列は `TO_CHAR` で決まった形に・ページングつきなら並びの最後に `主表.id` (明細の表は `主表.uniquekey`) |
| `BEGIN INSERT INTO "t"(...) VALUES (:p0,...) RETURNING "id" INTO :returning_id; END;` | `POST record/v1/t`。Location の末尾が Id (`args[":returning_id"]` にも入れる = 本体はそこから読む) |
| `update "t" set "c"=:p0,... where "id"=:pid0 [and ...]` | `PATCH record/v1/t/{id}`。追加条件があれば先に `select count(*)` で確かめる |
| `delete from "t" where "id"=:pid0 [and ...]` | `DELETE record/v1/t/{id}` |
| `BEGIN INSERT ...; INSERT ...; END;` (一括) | 1 件ずつ POST |

- パラメータ: 文字は `?` + `params` (値はすべて JSON の文字列)。数値・真偽 (`'T'` / `'F'`)・日付 (`TO_DATE(...)`) は決まった書式のリテラルにして埋める
  (利用者の文字列は埋めない)。LIKE のエスケープ文字は本体の制御文字 (U+0001) から `\` に置き換える
- 表は、どの階層 (副問い合わせ・結合) でも `Records` に書かれたレコードと、その取引の明細の表だけ
- 結果: SuiteQL は値を文字列で返し、null の列を省く。select の項目を読んで、本体が付けた別名をキーに、列の種類に合わせた型で返す (本体は別名の大文字・小文字まで一致で探す)
- 取引のレコード (salesorder など): from / join の表を `(select * from transaction where type = '<種別>') <別名>` に置き換える。
  種別は組み込みの対応表 (`SuiteQlTranslator._transactionTypes`。salesorder = SalesOrd ほか 16 種・大文字小文字を区別しない)。`Records` の確認・定義・書き込みは REST のレコード名のまま
- 明細の列 (`NetSuiteFieldKind.Sublist`。下記): SuiteQL の select から外し、主表の id を必ず読む (本体が select していなければ `主表.id as nsxid` を足して結果からは外す)。
  行ごとに `GET record/v1/{type}/{id}?expandSubResources=true` を同時実行の枠の中で並べて送り、明細を行の JSON 配列 (文字列) にして返す。
  主表の select の項目に素の列として書かれたときだけ受け付け、結合先の表・条件・並び・集計に出たら `NetSuiteException` (`SublistNotSupported`)
- 明細の表 (`<取引>_<明細の列>`。下記): from / join の表を
  `(select tl.* from transactionline tl inner join transaction t on t.id = tl.transaction where t.type = '<種別>' and tl.mainline = 'F' and tl.taxline = 'F') <別名>` に置き換える。
  書き込み (insert / update / delete の対象) は `NetSuiteException` (`NotSupported`。読み取り専用・親の明細の列で書けと言う)

明細の表 (明細をまたぐ検索・集計用。読み取り専用):

- 名前は `<Records の取引のレコード名>_<明細の列名>` の小文字 (`salesorder_item`)。親が `Records` にあって取引の対応表にあり、その明細が列 (`Sublist`) のとき自動でできる。`Records` には書かない
- 名前の突き合わせは「Records の名前 + `_` + 明細の列名」(`_` で割らない。`customrecord_x` のような名前があるため)。親の定義を読んで `CreateLineTables` の中に同じ名前があるかで決める
- 列: `uniquekey` (文字列。Id に結ぶ)・`transaction` (参照。親)・`linesequencenumber` (整数)・行の項目 (REST 名の小文字。`line` は除く)
- `GetCustomTableDefinitionsAsync` には親の後ろに並べる。メタ情報のファイルには `Parent` / `Sublist` つきで保存する

明細の列 (親の列として見せる明細):

| 本体 | NetSuite |
|---|---|
| 読み出し (select の項目) | 上記。値は `[{"line":1,"item":"5","item_refName":"...","quantity":2,...}, ...]` |
| 作成 (INSERT の値) | POST の本文に `"<明細>":{"items":[...]}`。定義にある行の項目だけを変換 (参照 → `{"id"}`・時差の無い日時は UTC として `Z`)。`_refName`・定義に無いキー・`line`・null は送らない。空の配列 / null なら明細ごと省く |
| 更新 (UPDATE の値) | 今のレコードを GET して `line` で突き合わせる。変わった行は `line` + 変わった項目 (空にした項目は null)、`line` の無い行は新しい行。消えた行があれば `?replace=<明細>` にして残す行を全部並べる (変わっていない行は `{"line":n}`)。差が無ければ明細を送らず、何も変わらなければ PATCH しない (1 件更新として返す)。本体の項目と明細は 1 回の PATCH |

- 行の JSON のキーは REST の項目名そのまま。日付は `yyyy-MM-dd`、日時は REST の値のまま (`Z` つき)、参照はキーに id の文字列 + `<キー>_refName` に表示名。`line` はそのまま。行の中の入れ子 (サブレコード・明細)・links は入れない
- 比べるときは揃えてから: 参照は id、数値は decimal、日時は同じ瞬間か、日付は日
- 今の明細に無い `line` の行・同じ `line` の 2 つ目は新しい行として送る (別の画面で消された行を戻す = 後勝ち)
- 後勝ち: 読んでから保存するまでに NetSuite で変わった明細は、送った内容で上書きされる (送らなかった行・項目はそのまま。消えた行があるときは replace なので、その間に足された行も消える)

本体が組む SQL の形が変わるとここが壊れる。`Test/NetSuiteDbAccessorTest` は本体の `ModuleDataIO` を実際に通しているので、本体のパッケージを上げたらこのテストで確かめる。

### 名前の対応

- 表名 (モジュールの DbTable) = REST のレコード名
- 列名 (DbColumn) = REST の項目名の小文字。SuiteQL にはそのまま使い、書き込みでは定義 (`NetSuiteFieldDefinition.RestName`) で REST の名前に戻す
- 取引のレコードも表名は REST のレコード名。SuiteQL の `transaction` 表と種別の条件はコネクターが読み替える (上記)。対応表に無い取引は SuiteQL で `<レコード名>` の表として送る
- 明細 = 親の列。列名は REST の明細名の小文字、.NET の型は string、`RawDbTypeName` は `CLOB`。行の項目は列にせず、行の JSON のキー (REST の名前のまま) にする
- 明細の表 = `<取引のレコード名>_<明細の列名>` (小文字)。列名は `transactionline` の列名 = 行の REST の項目名の小文字 (未検証。下記)
- 明細として扱うのは、行に `line` の項目がある明細だけ。メタデータはその場に書かれた形 (`{properties:{items:{type:array,items:{properties}}}}`) と `$ref` (…Collection → items → …Element) を受け、
  `$ref` は同じ metadata-catalog から読む (たどる回数に上限 4)。読めなければ列にしない。メタ情報のファイルにも行の項目を保存する (`Fields`。無い古いファイルも読める)

### 接続 (NetSuiteConnection)

- 長生きのオブジェクト (トークン・定義・同時実行の枠を持つ)。`FromDataSources` は設定が同じなら同じ接続を返す。`NetSuiteDbAccessor` は要求ごとに作ってよい
- 定義はプロセスの間持つ (本体も表の定義を static に持つので、短い期限を付けても本体側に古い定義が残る)。NetSuite の項目を変えたらアプリを再起動
- 再試行: 読み出しは 429 / 502 / 503 / 504 / 通信失敗を 3 回まで。書き込みは 429 だけ。401 はトークンを取り直して 1 回。
  書き込みを送ったあとの通信失敗と、書き込みへの 503 以外の 5xx は `WriteResultUnknown` (実行されたか分からないので、やり直さない)
- 例外に秘密鍵・トークン・接続文字列を入れない

## 決めてあること (2026-11-13)

仕様の自然さで決めた。蒸し返すときは理由ごと見直す。

- **保存はデータソースごとに確定する。1 回の保存に NetSuite のレコードが複数ある・DB と混ざる要求を、書く前に拒否しない。**
  本体の `DbAccessor` はデータソースごとに別のトランザクションを順にコミットするので、DB を 2 つ使う保存も全体では原子的でない。
  NetSuite は「1 件ごとに確定するデータソース」として同じ並びに置く。拒否すると、NetSuite のレコード + DB の編集履歴・監査のような普通の組み合わせが使えなくなる。
  補償 (失敗したら作ったレコードを消す・値を戻す) はしない (補償自体が失敗しうる・消したレコードは同じ ID で戻せない)
- **`Records` は必須。** データソース = そのアプリが扱うレコードの集まり。デザイナは全テーブルの定義を読むので、アカウントの全レコード種別を対象にしない
- **`DataSourceType` は `Oracle` と書かせる。** 本体の enum に NetSuite を足さない (本体は NetSuite を知らないまま = IDbAccessor の差し替えだけで成り立つ形を保つ)
- **モジュールの Id は常に internal ID。** 外部 ID を鍵にした Upsert は提供しない
- **集計は DB のモジュールと同じに動かす** (本体の `AggregateAsync` の SQL を読み替える)
- **OptimisticLockingField は対象外。** REST に条件つきの更新が無い。Id 以外の条件が付いた更新・削除は、先に読んで確かめるだけ (確かめてから書くまでの間は防げない)
- **定義はプロセスの間持つ。** 本体が表の定義を static に持つのに揃える
- **デザイナはメタ情報をファイルから読む** (ユーザー指示 2026-11-13)。プロジェクトのフォルダの `netsuite.<データソース名>.metadata.json` (`NetSuiteMetadataFile`)。
  NetSuite に取りに行くのは Tools > NetSuite のメタ情報を更新 のときだけ。ファイルが無ければ表は 0 件 (自動では取りに行かない)。
  狙いは「プロジェクトを開くたびの重い取得を無くす」「オフライン・鍵なしでもデザインできる」。headless の CLI も同じファイルを読む (更新の CLI は無い)。
  サーバー (実行時) はファイルを使わない (デザインのファイルはデプロイされない)
- **必須の項目も、列の定義は null 可で見せる。** NOT NULL にすると本体 (`DbDataConverter`) が空の値を型の既定値 (0・0001-01-01・false) に置き換えて書いてしまう。必須は NetSuite が保存時に確かめる
- **Starter (ホストのテンプレート) には組み込まない。** 特定の外部サービス向けなので、使うアプリが 2 行足す
- **取引のレコード: 利用者が書く表名は常に REST のレコード名。** SuiteQL の `transaction` 表・種別の条件との違いはコネクターが吸収する
- **明細 (サブリスト) は親の列として見せる。** 本体の列モードの一覧 (`ListFieldDesign.DbColumn` = 明細の列) で親のモジュールの中に並べ、行の JSON と REST の明細の本文の変換はコネクターがする。
  明細をまたぐ検索・集計は、読み取り専用の明細の表 `<取引>_<明細の列>` (`salesorder_item`。SuiteQL の `transactionline` を読む) を別に見せて行う。
  書き込みは親の明細の列だけ (明細の表に書くとエラー)。取引でないレコードの明細には表を作らない (SuiteQL で読む表が無い)
- **選択肢: 相手がレコードなら参照 (いまと同じ)。** 固定の選択肢の見せ方は未定
- **アプリのログインに NetSuite のアカウントを使うのは、Extras の汎用 OpenID Connect (`OidcLogins` に 1 件)。** コネクターに NetSuite 専用のログインのクラスを作らない・ホストのコードを変えない。
  方式 1 = ユーザーモジュールは DB のまま、メールを契約の `ExternalLoginName` と突き合わせる。方式 2 = ユーザーモジュールを NetSuite の `employee` にする (`LoginName` = メール。パスワード・有効フラグ・セッションの印なし。
  セッションの印は Extras が Id の列の型を DB に直接つないで調べるので使えない)。方式 2 はホストが `LoginAccountStore` に `NetSuiteDbAccessor` で包んだものを渡す必要がある

## 守ること

- 本体は core のリポで変える (このリポから直接編集しない)。足りない口はユーザーに相談
- ホスト (Starter) に置くべきもの (Controller・DI の要求) をライブラリに持ち込まない。組み込みは `IDbAccessor` を包む 1 行
- 実アカウントで確かめていないことを「確認済み」と書かない
- 公開リポジトリ。秘密鍵・Client ID・アカウント ID などの実物をコード・テスト・ドキュメントに書かない (テストの鍵はテストの中で生成する)

## 未検証・未実装 (2026-11-13 時点)

**NetSuite の実アカウントには一度もつないでいない。** テストは HTTP の偽物が相手。
実際の応答・構文について裏が取れていない前提は `Docs/03_research_request.md` に質問として並べてある (回答が来たらコードの前提と突き合わせる)。特に不確かなもの:

- JWT の `scope` クレームの形 (配列で送っている)
- メタデータの JSON Schema での参照・選択肢・明細の書かれ方 (`NetSuiteRecordDefinition.GetKind` の判定)
- SuiteQL の結果の形 (null の省略・値が文字列・キーの大文字小文字) と、`TO_CHAR` した日時の時刻帯 (UTC として扱っている)
- REST の項目名の小文字 = SuiteQL の列名、が成り立つ範囲
- 集計で本体が書く Oracle の式 (`TRUNC` / `ADD_MONTHS` / `NUMTODSINTERVAL`・派生表) を SuiteQL が受け付けるか
- 書き込みへの 5xx のうち、実行されていないと言えるもの (いまは 503 だけをそう扱っている)
- アプリのログイン (OpenID Connect。公開の仕様とモックだけで確かめた): ID トークンの `iss` (`https://system.netsuite.com` という情報) が discovery の `issuer` と違うと ASP.NET Core の検証で落ちる。
  PS256 の署名・`at_hash` の検証 (落ちるという未確認の報告あり)・返るクレーム (`email` が入るか・`email_verified` が無いか)・`response_mode=form_post` を受け付けるか。
  `iss` や `at_hash` で落ちたら、`OidcLoginProvider` を継承したクラスで `Configure` を上書きして検証を合わせる (そのときはコネクターかアプリのどちらに置くかを決める)

明細と取引のレコードは、モック (NetSuite.Mock) を相手にしか確かめていない。実アカウントで裏が取れていないもの:

- メタデータでの明細の書かれ方 (その場の形か `$ref` か・…Collection / …Element の名前・行に `line` が載るか)
- `?replace=<明細>` の動き (送らなかった行が消える・残す行を `{"line":n}` だけで送ってよいか)
- `line` だけの要素で行が変わらずに残るか
- SuiteQL が `(select * from transaction where type = '...') 別名` の派生表を受け付けるか (結合・集計・ページングの中でも)
- 取引の種別の文字列 (`SalesOrd` など対応表の 16 種)
- `expandSubResources=true` の応答の形 (行の参照が `{id, refName}`・日時が `Z` つき)
- 明細の表: 行の REST の項目名の小文字 = `transactionline` の列名 (`item`・`quantity`・`expectedshipdate` など)
- 明細の表: `mainline = 'F' and taxline = 'F'` で対象の明細の行だけになるか (取引に明細が複数あるとき・送料や小計などの行が混ざらないか)
- 明細の表: `transactionline` の数量・金額の符号 (取引の種別によって REST と逆になる行があるという情報がある)
- 明細の表: `uniquekey` が行ごとに一意で、行を足し消ししても変わらないか (Id とページングの並びの最後に使っている)

未実装:

- サブレコード・複数選択、固定の選択肢の候補、取引でないレコードの明細をまたぐ検索・集計
- 読み取り専用の項目の判別 (いまは NetSuite がエラーを返すのに任せている。メタデータで見分けられるかは調査の回答待ち)
- 対象外と決めたもの: 外部 ID の Upsert、保存検索・Dataset・Action・Transform・ファイル
