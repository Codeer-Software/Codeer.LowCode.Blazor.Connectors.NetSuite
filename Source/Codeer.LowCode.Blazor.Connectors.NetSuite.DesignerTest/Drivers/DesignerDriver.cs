using Codeer.Friendly;
using Codeer.Friendly.Dynamic;
using Codeer.Friendly.Windows;
using Codeer.Friendly.Windows.Grasp;
using RM.Friendly.WPFStandardControls;
using System.Diagnostics;
using System.Text.Json;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.DesignerTest.Drivers
{
    /// <summary>
    /// デザイナ (Example/LowCodeApp.Designer) を起動して Friendly でプロセス越しに操作する。
    /// 起動の引数にプロジェクト (app.clprj) を渡して開く。待ちは全部タイムアウト付き。
    /// 1 回の起動に時間の上限を付け、超えたら起動したプロセスだけを止める (UI スレッドが止まって Friendly の呼び出しが返らないときに抜けるため)。
    /// WebView (Blazor) の中は WPF のラッパーで触れないので、モジュールの作成・デザインチェックは対象プロセスの中の API を直接呼ぶ。
    /// </summary>
    public sealed class DesignerDriver : IDisposable
    {
        const string MainWindowTypeFullName = "Codeer.LowCode.Blazor.Designer.Views.Windows.MainWindow";
        const string MessageWindowTypeFullName = "Codeer.LowCode.Blazor.Designer.Views.Windows.MessageWindow";
        const string ToastWindowTypeFullName = "Codeer.LowCode.Blazor.Designer.Views.Windows.ToastWindow";

        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        readonly Process _process;
        readonly Timer _watchdog;
        AppVar? _fileWatcherSuspender;

        public WindowsAppFriend App { get; }
        public WindowControl MainWindow { get; }

        DesignerDriver(Process process, Timer watchdog, WindowsAppFriend app, WindowControl mainWindow)
        {
            _process = process;
            _watchdog = watchdog;
            App = app;
            MainWindow = mainWindow;
        }

        /// <summary>デザイナの exe (このテストと同じ構成でビルドしたもの)。</summary>
        public static string ExePath
        {
            get
            {
                //Source/<テスト>/bin/<構成>/<TFM>/
                var tfmDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
                var configuration = Path.GetFileName(Path.GetDirectoryName(tfmDir))!;
                var source = Path.GetFullPath(Path.Combine(tfmDir, "..", "..", "..", ".."));
                return Path.Combine(source, "Example", "LowCodeApp.Designer", "bin", configuration, "net8.0-windows", "LowCodeApp.Designer.exe");
            }
        }

        /// <summary>デザイナを起動して、プロジェクトが開くまで待つ。</summary>
        /// <param name="limit">この起動でデザイナを動かしておく上限。超えたら止める。</param>
        public static DesignerDriver Launch(string projectFile, TimeSpan limit)
        {
            var exe = ExePath;
            if (!File.Exists(exe)) throw new FileNotFoundException("The designer is not built.", exe);

            //前回のドッキングレイアウトを復元しない
            var layout = Path.Combine(Path.GetDirectoryName(exe)!, "IDELayout.config");
            if (File.Exists(layout)) File.Delete(layout);

            var process = Process.Start(new ProcessStartInfo(exe, $"\"{projectFile}\"") { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false })!;
            var watchdog = new Timer(_ => KillQuietly(process), null, limit, Timeout.InfiniteTimeSpan);
            try
            {
                var app = new WindowsAppFriend(process);
                var main = app.WaitForIdentifyFromTypeFullName(MainWindowTypeFullName);
                var driver = new DesignerDriver(process, watchdog, app, main);
                var folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(projectFile)))!;
                driver.WaitFor(() => driver.Title.EndsWith(": " + folder, StringComparison.Ordinal), TimeSpan.FromSeconds(60), $"project '{folder}' opened");
                return driver;
            }
            catch
            {
                watchdog.Dispose();
                KillQuietly(process);
                throw;
            }
        }

        public bool HasExited
        {
            get
            {
                _process.Refresh();
                return _process.HasExited;
            }
        }

        public void Dispose()
        {
            _watchdog.Dispose();
            try
            {
                App.Dispose();
            }
            catch { }
            KillQuietly(_process);
            _process.Dispose();
        }

        static void KillQuietly(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(10000);
                }
            }
            catch { }
        }

        /// <summary>条件が成り立つまで待つ。デザイナが終了したら待たずに失敗にする。</summary>
        public void WaitFor(Func<bool> condition, TimeSpan timeout, string description)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                if (HasExited) throw new InvalidOperationException($"The designer exited while waiting for: {description}");
                if (condition()) return;
                if (timeout < watch.Elapsed) throw new TimeoutException($"Timed out ({timeout.TotalSeconds} s) waiting for: {description}");
                Thread.Sleep(100);
            }
        }

        public string Title => (string)MainWindow.Dynamic().Title;

        dynamic Manager => MainWindow.Dynamic()._solutionTreeControl._mainControlManager;
        dynamic ViewModel => Manager.Vm;
        dynamic Environment => App.Type("System.Windows.Application").Current.DesignerEnvironment;

        bool IsNull(AppVar value) => (bool)App.Type<object>().ReferenceEquals(value, null);
        bool AreSame(AppVar a, AppVar b) => (bool)App.Type<object>().ReferenceEquals(a, b);

        /// <summary>
        /// ファイルの監視を止める (このドライバが生きている間)。デザイナ自身の書き込みを外部の変更と見て、
        /// 「再読み込みしますか」のモーダルが出るのを防ぐ。
        /// </summary>
        public void SuspendFileWatcher() => _fileWatcherSuspender ??= (AppVar)Manager.SuspendFileWatcher();

        // ---------------------------------------------------------------- メニュー

        /// <summary>拡張のメニュー (DesignerEnvironment.AddMainMenu で足されたもの) の見出し。</summary>
        public List<string> GetExtensionMenuHeaders()
        {
            var menus = MainWindow.Dynamic()._extensionMenus;
            var count = (int)menus.Count;
            var list = new List<string>();
            for (var i = 0; i < count; i++) list.Add((string)menus[i].Header.ToString());
            return list;
        }

        /// <summary>
        /// 拡張のメニューをクリックする (見出しのどれかに一致するもの)。モーダルが出てもよいように完了を待たない。
        /// </summary>
        public void ClickExtensionMenu(params string[] headers)
        {
            var menus = MainWindow.Dynamic()._extensionMenus;
            var count = (int)menus.Count;
            for (var i = 0; i < count; i++)
            {
                var header = (string)menus[i].Header.ToString();
                if (!headers.Contains(header)) continue;
                new WPFMenuItem((AppVar)menus[i]).EmulateClick(new Async());
                return;
            }
            throw new InvalidOperationException($"The menu is not found: {string.Join(" / ", headers)} (menus: {string.Join(", ", GetExtensionMenuHeaders())})");
        }

        // ---------------------------------------------------------------- トースト・モーダル

        /// <summary>いま (最後に) 出したトースト。無ければ null。</summary>
        public AppVar? CurrentToast
        {
            get
            {
                var toast = (AppVar)MainWindow.Dynamic()._toast;
                return IsNull(toast) ? null : toast;
            }
        }

        public sealed record Toast(string Message, bool IsSuccess);

        /// <summary><paramref name="previous"/> の後に出たトーストを待つ。</summary>
        public Toast WaitForNewToast(AppVar? previous, TimeSpan timeout)
        {
            AppVar? toast = null;
            WaitFor(() =>
            {
                var current = CurrentToast;
                if (current == null || (previous != null && AreSame(current, previous))) return false;
                toast = current;
                return true;
            }, timeout, "a new toast");
            return ReadToast(toast!);
        }

        Toast ReadToast(AppVar toast)
        {
            var message = (string)toast.Dynamic()._message.Text;
            //成功は LimeGreen、失敗は Orange
            var color = (string)toast.Dynamic().Background.Color.ToString();
            return new Toast(message, color == "#FF32CD32");
        }

        /// <summary>メインウィンドウとトースト以外に見えているウィンドウの型名 (モーダルが出ていないことの確認)。</summary>
        public List<string> GetOtherVisibleWindows()
        {
            var windows = App.Type("System.Windows.Application").Current.Windows;
            var count = (int)windows.Count;
            var list = new List<string>();
            for (var i = 0; i < count; i++)
            {
                if (!(bool)windows[i].IsVisible) continue;
                var type = (string)windows[i].GetType().FullName;
                if (type == MainWindowTypeFullName || type == ToastWindowTypeFullName) continue;
                list.Add(type);
            }
            return list;
        }

        /// <summary>MessageWindow (デザイナのメッセージのモーダル) が出るのを待って、文言を返して閉じる。</summary>
        public string WaitForMessageWindowAndClose(TimeSpan timeout)
        {
            AppVar? found = null;
            WaitFor(() =>
            {
                var windows = App.Type("System.Windows.Application").Current.Windows;
                var count = (int)windows.Count;
                for (var i = 0; i < count; i++)
                {
                    if ((string)windows[i].GetType().FullName != MessageWindowTypeFullName || !(bool)windows[i].IsVisible) continue;
                    found = (AppVar)windows[i];
                    return true;
                }
                return false;
            }, timeout, "MessageWindow");
            var message = (string)found!.Dynamic().DataContext.Message;
            found.Dynamic().Close();
            WaitFor(() => !GetOtherVisibleWindows().Contains(MessageWindowTypeFullName), timeout, "MessageWindow closed");
            return message;
        }

        // ---------------------------------------------------------------- データソース・テーブル

        public sealed class ColumnInfo
        {
            public string Name { get; set; } = string.Empty;
            public string NetTypeFullName { get; set; } = string.Empty;
            public string RawDbTypeName { get; set; } = string.Empty;
            public bool IsNullable { get; set; }
        }

        public sealed class TableInfo
        {
            public string Name { get; set; } = string.Empty;
            public List<ColumnInfo> Columns { get; set; } = new();
        }

        static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

        /// <summary>デザイナが持っているテーブルの一覧 (DesignerEnvironment.GetDbInfo)。</summary>
        public List<TableInfo> GetTables(string dataSourceName)
        {
            var tables = Environment.GetDbInfo(dataSourceName);
            var json = (string)App.Type("Codeer.LowCode.Blazor.Json.JsonConverterEx").SerializeObject(tables, true);
            return JsonSerializer.Deserialize<List<TableInfo>>(json, _jsonOptions)!;
        }

        // ---------------------------------------------------------------- モジュール

        /// <summary>
        /// テーブルからモジュールを作って保存する。DB から取り込むときと同じ変換
        /// (DbColumnFieldConverter.ConvertForModule) で列をフィールドにし、デザイナの保存 (SaveOne) で書く。
        /// </summary>
        public void CreateModuleFromTable(string moduleName, string dataSourceName, string tableName)
        {
            var vm = ViewModel;
            var error = (string?)vm.CreateModule(moduleName, "");
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException($"CreateModule failed: {error}");

            var module = vm.EditableDesignData.Modules.Find(moduleName);
            module.DataSourceName = dataSourceName;
            module.DbTable = tableName;

            var env = Environment;
            AppVar? dataSource = null;
            var dataSources = env.GetDesignerSettings().DataSources;
            for (var i = 0; i < (int)dataSources.Length; i++)
            {
                if ((string)dataSources[i].Name == dataSourceName) dataSource = (AppVar)dataSources[i];
            }
            if (dataSource == null) throw new InvalidOperationException($"No data source '{dataSourceName}'.");

            AppVar? table = null;
            var tables = env.GetDbInfo(dataSourceName);
            for (var i = 0; i < (int)tables.Count; i++)
            {
                if ((string)tables[i].Name == tableName) table = (AppVar)tables[i];
            }
            if (table == null) throw new InvalidOperationException($"No table '{tableName}' in '{dataSourceName}'.");

            var converter = App.Type("Codeer.LowCode.Blazor.Designer.Extensibility.DbColumnFieldConverter");
            var columns = table.Dynamic().Columns;
            for (var i = 0; i < (int)columns.Count; i++)
            {
                module.Fields.Add(converter.ConvertForModule(module, dataSource, table, columns[i]));
            }

            var projectDataType = App.Type("System.Type").GetType("Codeer.LowCode.Blazor.DesignLogic.DesignDataFileManager+ProjectDataType, Codeer.LowCode.Blazor");
            if (!(bool)vm.SaveOne(App.Type("System.Enum").Parse(projectDataType, "Module"), module, ""))
                throw new InvalidOperationException("SaveOne failed.");
            MainWindow.Dynamic()._solutionTreeControl.Reload();
        }

        public string[] GetModuleNames() => (string[])ViewModel.EditableDesignData.Modules.GetModuleNames().ToArray();

        /// <summary>モジュールの設計 (JSON)。</summary>
        public JsonDocument GetModuleDesign(string moduleName)
        {
            var module = ViewModel.EditableDesignData.Modules.Find(moduleName);
            //Friendly はオプション引数を補わないので全部渡す
            return JsonDocument.Parse((string)App.Type("Codeer.LowCode.Blazor.Json.JsonConverterEx").SerializeObject(module, true));
        }

        /// <summary>モジュールをエディタのタブで開く (ソリューションツリーのダブルクリックと同じ入口)。</summary>
        public void OpenModule(string moduleName)
        {
            var itemTypeEnum = App.Type("System.Type").GetType("Codeer.LowCode.Blazor.Designer.Views.Panes.SolutionTreeItemType, Codeer.LowCode.Blazor.Designer");
            Manager.OpenDocument(App.Type("System.Enum").Parse(itemTypeEnum, "Module"), moduleName, true);
        }

        /// <summary>モジュールのエディタ (全体設定) が開いているか。</summary>
        public bool IsModuleEditorOpen(string moduleName) => (bool)Manager.ModuleOverallSettingsDesigners.ContainsKey(moduleName);

        /// <summary>デザインチェック (メニューの Design Check と同じチェック) の指摘。"種類 位置: 内容"。</summary>
        public List<string> RunDesignCheck()
        {
            var checker = App.Type("Codeer.LowCode.Blazor.Designer.Models.DesignChecker")();
            var result = checker.DesignCheck();
            var list = new List<string>();
            for (var i = 0; i < (int)result.Count; i++)
            {
                list.Add($"{(string)result[i].GetType().Name} {(string)result[i].GetPositionText()}: {(string)result[i].Message}");
            }
            return list;
        }
    }
}
