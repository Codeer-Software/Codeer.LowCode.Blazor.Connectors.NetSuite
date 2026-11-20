# サンプルアプリ (Source/Example) を手元で動かす

`Source/Example` は、コネクターを組み込んだ Web アプリ (Cookie 認証のテンプレート) とデザイナ、そのデザインのプロジェクトです。
NetSuite のモックにつないで、顧客・子会社・カスタムレコード・品目・受注 (明細つき) の一覧・検索・詳細・保存と、受注の明細を横断した一覧・検索、受注の添付ファイルを試せます。

| フォルダ | 内容 |
|---|---|
| `LowCodeApp.Server` / `LowCodeApp.Client` / `LowCodeApp.Client.Shared` | Web アプリ。コネクターの組み込みは `LowCodeApp.Server/Infrastructure/DataService.cs` の 2 行。添付ファイルは下の「受注の添付ファイル」の 3 か所 |
| `LowCodeApp.Designer` | コネクターを組み込んだデザイナ |
| `Design` | デザインのプロジェクト (`app.clprj`)。モジュール `Customer` / `Subsidiary` / `CustomRecordSample` / `Item` / `SalesOrder` / `SalesOrderItem` / `SalesOrderLine` / `Employee` (NetSuite) と `AppUser` (ログインするユーザー・SQLite) |
| `Data/main.sql` | ログインするユーザーの表と、最初のユーザー (admin / admin) と、NetSuite でだけログインするユーザー (モックの従業員 `ichiro.sato@example.com`。パスワードなし) |

.NET 10 の SDK が要ります (デザイナとモックは .NET 8)。CLB のライセンスを登録したマシンで動かします。
以下のパスは例です。変えるときは、それぞれの設定を同じ値にそろえます。

## 1. モックを起動する

```
dotnet run --project Source/NetSuite.Mock
```

コンソールに出る `ConnectionString` (`Provider=NetSuite;ServiceUrl=http://localhost:5080;...`) を、このあと 2 か所で使います。
モックを止めるとデータは最初に戻ります。

## 2. デザイナの設定を作る

`Source/Example/Design/designer.settings.Development.json` を作ります (リポジトリには入れません)。

```json
{
  "ConnectionStrings": {
    "Main": "Data Source=C:/Codeer.LowCode.Blazor.Local/Data/netsuite_example.db;",
    "NetSuite": "<モックのコンソールに出た ConnectionString>"
  },
  "CurrentDeployInfoName": "local",
  "DeployInfo": {
    "local": {
      "DeployMethod": "FileSystem",
      "AppName": "App",
      "Directory": "C:\\Codeer.LowCode.Blazor.Local\\Designs\\NetSuite"
    }
  }
}
```

レコードの定義は `Design/ServerFiles/netsuite.NetSuite.metadata.json` (モックから書き出したメタ情報) から読みます。デザインと一緒に App.zip に入り、アプリも同じものを読みます。
モックのデータのファイルを変えたときは、デザイナで Tools > NetSuite のメタ情報を更新 を実行してから、デザインを送り直します。

## 3. ログインするユーザーの DB を作る

`C:\Codeer.LowCode.Blazor.Local\Data` のフォルダを作ってから、デザイナをビルドして (WPF なので VS の MSBuild)、`sql` コマンドで `Data/main.sql` を流します。

```
LowCodeApp.Designer.exe sql Source/Example/Design --datasource Main --file Source/Example/Data/main.sql --out sql-result.json
```

## 4. デザインを送る

デザイナで `Source/Example/Design/app.clprj` を開き、デプロイします。`C:\Codeer.LowCode.Blazor.Local\Designs\NetSuite\App.zip` ができます。

## 5. アプリを起動する

NetSuite の接続文字列は `appsettings` に書かず、環境変数で渡します (PowerShell の例)。

```
$env:ConnectionStrings__NetSuite = "<モックのコンソールに出た ConnectionString>"
dotnet run --project Source/Example/LowCodeApp.Server --launch-profile http
```

`http://localhost:5085` を開き、admin / admin でログインします。メニューの「顧客」から一覧・検索・詳細・保存を試せます。保存はモックのデータに入ります。

受注のモジュール:

| モジュール | 内容 |
|---|---|
| `Item` (メニューの「品目」) | NetSuite の `item` |
| `SalesOrder` (「受注」) | NetSuite の `salesorder`。顧客へのリンク・日付・メモと、明細 (`item`) の一覧・添付ファイル (`Files`)。一覧の画面には明細を出しません (一覧で明細を読まない)。添付ファイルは一覧にも出します (一覧の 1 ページで保存検索を 1 回) |
| `SalesOrderItem` | 受注の明細の行 (表を持たない行のモジュール)。品目は `Item` を候補にした SelectField で選びます |
| `SalesOrderLine` (「受注明細」) | 受注の明細の表 `salesorder_item` (読み取り専用)。品目・数量で検索でき、受注と品目へのリンクを持ちます |

受注の添付ファイル: 受注の詳細でファイルの一覧・ダウンロード・追加・外すを試せます (モックの受注 1 `First order` にファイルが 2 つ付いています)。
モックのコンソールに出る接続文字列には、ファイルを置くフォルダと保存検索 (`AttachmentFolderId` など) が入っています。アプリの結線は次の 3 か所です。

| 場所 | 内容 |
|---|---|
| `LowCodeApp.Client.Shared/Services/AppInfoService.cs`・`LowCodeApp.Server/Program.cs` | `NetSuiteFieldsInitializer.Initialize()` (画面用のパッケージ `Codeer.LowCode.Blazor.Connectors.NetSuite` は `LowCodeApp.Client.Shared` が参照) |
| `LowCodeApp.Server/Infrastructure/DataService.cs` | `NetSuite` プロパティ (`NetSuiteDbAccessor`) |
| `LowCodeApp.Server/Controllers/NetSuiteAttachmentsController.cs` | ダウンロードの口。添付の途中で失敗したときの文言は `Infrastructure/ExceptionHandlerUtils.cs` |

DB とデザインの置き場所は `LowCodeApp.Server/appsettings.Development.json` (`ConnectionStrings:Main`・`DesignFileDirectory`) です。環境変数 `ConnectionStrings__Main`・`DesignFileDirectory` でも上書きできます。

## NetSuite のアカウントでログインする

モックを https つきで起動していれば (開発用証明書を信頼済みにしてから起動。[NetSuiteMock.md](NetSuiteMock.md))、ログイン画面の「NetSuite」のボタンからモックの従業員でログインできます。
外部ログインの設定はリポジトリに入れず、環境変数か `appsettings.Development.json` で渡します (PowerShell の例。`ClientId`・`ClientSecret` はモックなら空でなければ何でもよい)。

```
$env:OidcLogins__0__Name = "NetSuite"
$env:OidcLogins__0__DisplayName = "NetSuite"
$env:OidcLogins__0__ClientId = "mock-client"
$env:OidcLogins__0__ClientSecret = "mock-secret"
$env:OidcLogins__0__Authority = "https://localhost:5081"
$env:OidcLogins__0__Scopes__0 = "openid"
$env:OidcLogins__0__Scopes__1 = "email"
$env:OidcLogins__0__LoginNameClaim = "email"
```

ユーザーモジュールは `AppUser` のままで、NetSuite のメールを `AppUser` のメールの列 (契約の `ExternalLoginName`) と突き合わせます ([NetSuite.md](NetSuite.md) の方式 1)。
モックの画面で「Ichiro Sato」を選ぶと「佐藤 一郎」でログインします。ほかの従業員は `AppUser` に行が無いので「このアカウントは登録されていません」で戻ります。admin / admin のログインもそのまま使えます。

ユーザーモジュールを `Employee` にする方式 2 も、このサンプルで動きます (デザインの `app.clprj` の `CurrentUserModuleDesignName` を `Employee` にしてデザインを配り直す)。
ログインの仕組みには `DataService.ModuleDbAccess` (NetSuite で包んだもの。`ModuleDataIO` に渡すのと同じ) を渡しているので、NetSuite の従業員をユーザーとして読めます。
このときモックの画面で選んだ従業員が、そのままアプリのユーザーになります (admin / admin のログインは使えなくなります)。

## ブラウザのテスト

`Source/Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest` は、ここまでの手順を一時フォルダで自動で行い (モック・鍵・DB・App.zip・アプリの起動)、ヘッドレスの Chrome で操作します。
上の手順で作った設定や DB は使いません。NetSuite のアカウントでのログインのテストは、モックを https で立てるので、ASP.NET Core の開発用証明書を信頼済みにしたマシンで動かします (`dotnet dev-certs https --trust`)。

```
cd Source
MSBUILDDISABLENODEREUSE=1 dotnet build Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest -m:1
dotnet test Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest --no-build
```
