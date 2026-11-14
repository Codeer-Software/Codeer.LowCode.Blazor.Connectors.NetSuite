# サンプルアプリ (Source/Example) を手元で動かす

`Source/Example` は、コネクターを組み込んだ Web アプリ (Cookie 認証のテンプレート) とデザイナ、そのデザインのプロジェクトです。
NetSuite のモックにつないで、顧客・子会社・カスタムレコードの一覧・検索・詳細・保存を試せます。

| フォルダ | 内容 |
|---|---|
| `LowCodeApp.Server` / `LowCodeApp.Client` / `LowCodeApp.Client.Shared` | Web アプリ。コネクターの組み込みは `LowCodeApp.Server/Infrastructure/DataService.cs` の 2 行 |
| `LowCodeApp.Designer` | コネクターを組み込んだデザイナ |
| `Design` | デザインのプロジェクト (`app.clprj`)。モジュール `Customer` / `Subsidiary` / `CustomRecordSample` (NetSuite) と `AppUser` (ログインするユーザー・SQLite) |
| `Data/main.sql` | ログインするユーザーの表と、最初のユーザー (admin / admin) |

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

レコードの定義は `Design/netsuite.NetSuite.metadata.json` (モックの定義から作ったもの) から読みます。モックのデータのファイルを変えたときは、デザイナで Tools > NetSuite のメタ情報を更新 を実行します。

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

DB とデザインの置き場所は `LowCodeApp.Server/appsettings.Development.json` (`ConnectionStrings:Main`・`DesignFileDirectory`) です。環境変数 `ConnectionStrings__Main`・`DesignFileDirectory` でも上書きできます。

## ブラウザのテスト

`Source/Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest` は、ここまでの手順を一時フォルダで自動で行い (モック・鍵・DB・App.zip・アプリの起動)、ヘッドレスの Chrome で操作します。
上の手順で作った設定や DB は使いません。

```
cd Source
MSBUILDDISABLENODEREUSE=1 dotnet build Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest -m:1
dotnet test Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest --no-build
```
