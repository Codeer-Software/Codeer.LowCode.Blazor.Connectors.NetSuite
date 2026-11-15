# NetSuite (Oracle NetSuite のレコードをモジュールにする)

「NetSuite の顧客を一覧したい」「NetSuite のレコードを画面から直したい」と言われたらこれ。
データソースが NetSuite のモジュールは、**DB のテーブルに結び付けたモジュールと同じ作り**で、読み書きの先が NetSuite のレコードになる。
フィールドもスクリプトも専用のものは無い。URL・JSON・SuiteQL を書くことも無い。

NetSuite のデータソースかどうかは、データソースの `ConnectionString` が `Provider=NetSuite;` で始まることで分かる (`DataSourceType` は `Oracle` と書かれている)。
扱えるレコードは接続文字列の `Records` に並んでいるものだけ。足りなければユーザーに `Records` へ足してもらう (デザインからは変えられない)。

レコードの定義 (テーブルの定義) は、プロジェクトのフォルダの `netsuite.<データソース名>.metadata.json` から読まれる (NetSuite には取りに行かない)。
NetSuite のテーブルが 1 つも出てこない・要るレコードや項目が無いときは、このファイルが無いか古い。
ユーザーにデザイナのメニュー **Tools > NetSuite のメタ情報を更新** を実行してもらう (このファイルを手で書き換えない)。

## モジュールの作り方

DB のテーブルからモジュールを作るのと同じ手順 (テーブルの定義を読んで、列からフィールドを作る)。NetSuite のレコードがテーブル、項目が列として見える。

| モジュールの設定 | 書く値 |
|---|---|
| `DataSourceName` | NetSuite のデータソース名 |
| `DbTable` | レコードの種別 (`customer`、`vendor`、`customrecord_xxx`) |
| フィールドの `DbColumn` | テーブルの定義に出てくる列名そのまま (NetSuite の項目名の小文字。`companyName` → `companyname`) |
| Id フィールド | `DbColumn` は `id` (NetSuite の internal ID)。NetSuite が採番するので手入力にしない |

列の型とフィールド:

| 列の型 | フィールド |
|---|---|
| 文字列 | TextField |
| 真偽 | BooleanField |
| 整数・数値 | NumberField |
| 日付 | DateField |
| 日時 | DateTimeField。**`SaveAsUtc: true` にする** |
| ほかのレコードへの参照 (文字列。値は相手の internal ID) | LinkField (相手のレコードのモジュールを作って結ぶ)。相手を見せなくてよければ TextField |

- 参照の相手のレコードも `Records` に入っている必要がある (入っていなければモジュールを作れない)
- 使う項目だけをフィールドにする。フィールドにしていない項目は読みも書きもしない
- 一覧には表示件数 (ページング) を付ける。件数を絞らない読み出しは上限 (データソースの `MaxRows`、既定 10000 行) を超えるとエラーになる
- 受注 (`salesorder`)・請求書 (`invoice`) などの取引も `DbTable` はレコードの種別そのまま (ほかのレコードと同じ)

### 明細 (サブリスト)

明細はテーブルの定義に、親のテーブルの文字列の列 (明細名の小文字。受注なら `item`) として出てくる。行は親のモジュールの一覧 (ListField) にする。
作りは「行データを親の列に JSON 配列で保存する一覧 (ListField の `DbColumn`)」と同じ。

| 設定 | 書く値 |
|---|---|
| 親の ListField の `DbColumn` | 明細の列名 (`item`) |
| ListField の `SearchCondition` | `ModuleName` = 行のモジュール、`LimitCount: null` |
| 行のモジュール | `DataSourceName`・`DbTable`・Id フィールドなし。ルートの JsonField 1 つに、行の項目をノードとして並べる |
| ノードの `Key`・行のフィールドの `DbColumn` | NetSuite の行の項目名そのまま (大文字・小文字も変えない。`quantity`・`expectedShipDate`)。親の列名と違い小文字にしない |

- 参照の項目 (品目 `item` など) は Text のノードで、値は相手の internal ID の文字列。表示名は `<キー>_refName` (`item_refName`) の Text のノードで読める (保存では送られない)
- 行で参照の相手を選ばせるときは、そのノードに SelectField を結ぶ (`SearchCondition.ModuleName` = 相手のモジュール、`ValueVariable: "Id.Value"`、`DisplayTextVariable` = 見せる項目)
- 行の番号 `line` はノードにもフィールドにもしなくてよい (結び付けていないキーとして行に残り、保存のときに使われる)
- 数値は Number、日付は Date のノード + DateField、日時は DateTime のノード + DateTimeField (`SaveAsUtc: true`)
- ノードにする項目は、行の項目のうち画面で使うものだけ
- 明細は詳細のレイアウトに置く。**一覧のレイアウト (一覧の列) に明細の ListField を置かない** (行ごとに NetSuite への読み出しが出る)
- 親の明細の列を検索条件・並び・集計に使わない (エラーになる)。明細の中の項目で探す・集計するなら、取引は次の「明細の表」を使う
- 保存は後勝ち (読んでから保存までに NetSuite で変わった明細は上書きされる)。同時編集を防ぐ仕組みは無い

### 明細の表 (取引の明細を横断して読む・読み取り専用)

「品目ごとの受注数量を集計したい」「ある品目を含む受注の明細を一覧したい」と言われたらこれ。
取引 (`salesorder` など) の明細ごとに、明細の行を 1 行とするテーブル `<取引のレコード名>_<明細の列名>` (小文字。受注の `item` なら `salesorder_item`) がテーブルの定義に出てくる。
`Records` に書く必要は無い (親の取引が `Records` にあれば出てくる)。取引でないレコードの明細には無い。

| 列 | 内容・フィールド |
|---|---|
| `uniquekey` | 行のキー (文字列)。**Id フィールドの `DbColumn` にする** (`id` ではない) |
| `transaction` | 親の取引の internal ID (参照)。親のモジュールへの LinkField にする |
| `linesequencenumber` | 取引の中での行の順 (整数) |
| 行の項目 | 明細の行の項目の小文字 (`item`・`quantity`・`expectedshipdate`)。親の明細のノードのキーと違い小文字 |

- モジュールは `CanCreate` / `CanUpdate` / `CanDelete` を false にする。このテーブルには書けない (書くとエラー)。明細を変える画面は親の取引の明細の一覧で作る
- 検索・並び・ページング・集計・LinkField 越しの親や品目の項目での検索は、ほかのテーブルと同じに使える

### ユーザーモジュールを NetSuite の従業員にする

「NetSuite のアカウントでログインして、その従業員をそのままアプリのユーザーにしたい」と言われたらこれ (ログインそのものはアプリの外部ログインの設定で、デザインには書かない)。
NetSuite の `employee` のモジュールを作り、プロジェクトの `CurrentUserModuleDesignName` にする。`Records` に `employee` が要る。

| フィールド | 書く値 |
|---|---|
| Id (IdField) | `DbColumn: "id"` |
| 表示名 (TextField) | `DbColumn: "entityid"` |
| メール (TextField) | `DbColumn: "email"` |
| ログインアカウントの契約 (LoginAccountContractFieldDesign) | `LoginName` = メールのフィールド、`DisplayName` = 表示名のフィールド。パスワード・有効フラグ・セッションの印のフィールドは選ばない (NetSuite に列が無い) |

- `CanCreate` / `CanUpdate` / `CanDelete` は false (従業員の登録・無効化は NetSuite でする)
- パスワードのログインはできない (管理者も含めて NetSuite でログインする)。DbColumnSessionStamp (セッションの失効) は置かない
- ユーザーの表を DB に置いたまま NetSuite でログインさせるだけなら、この形にしない (DB のユーザーモジュールにメールのフィールドを足し、契約の `ExternalLoginName` に選ぶ)

## 動くもの

検索条件・並び・ページング・件数、LinkField 越しの項目の表示と検索 (NetSuite のモジュール同士)、集計、詳細の表示、新規・更新・削除、
モジュールの権限 (UserRead / DataRead などの条件・フィールド権限) は、DB のモジュールと同じに動く。

## やってはいけないこと

- **NetSuite のモジュールに ExecuteSqlField・QueryField (SQL を書くモジュール) を使わない**。NetSuite のデータソースでは SQL を実行できない
- **Id フィールドを複合キー・手入力にしない**。レコードは internal ID 1 つで決まる
- **OptimisticLockingField を置かない**。NetSuite には条件つきの更新が無く、同時更新を確実には検出できない
- **1 回の保存で NetSuite のレコードと DB のテーブルの両方がそろって保存される前提にしない**。NetSuite への保存はレコード 1 件ごとに確定し、
  同じ保存の中のほかの処理が失敗しても戻らない (データソースごとに確定するのは DB が 2 つあるときと同じ)。
  そろっていないと困る組み合わせは、画面 (保存の単位) を分ける
- **NetSuite のモジュールと DB のモジュールを LinkField の結合で結ばない**。結合できるのは同じデータソースのモジュール同士だけ

## 扱えないもの

- 住所などのサブレコード・複数選択の項目・行に `line` を持たない明細 (テーブルの定義に列として出てこない)
- 明細の行の中のサブレコード (在庫の詳細など)
- 取引でないレコードの明細の中の項目での検索・並び・集計
- 保存検索・Dataset・レコードのアクション・変換・ファイル
