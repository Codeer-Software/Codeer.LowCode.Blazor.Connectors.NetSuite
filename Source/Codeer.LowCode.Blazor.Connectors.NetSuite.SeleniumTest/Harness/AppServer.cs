using Codeer.LowCode.Blazor.DesignLogic;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.Harness
{
    /// <summary>
    /// Example の Web アプリ (LowCodeApp.Server のビルド出力) を別プロセスで 127.0.0.1 の空きポートに起動する。
    /// 一時フォルダに鍵・SQLite (Example/Data/main.sql)・App.zip (Example/Design) を作り、appsettings の値は環境変数で上書きして渡す。
    /// デザインを変えて起動するときは、Example/Design を一時フォルダに写してから変える (リポジトリのデザインは変えない)。
    /// 止めるときは、ここで起動したプロセスだけを止める。
    /// </summary>
    public sealed class AppServer : IDisposable
    {
        static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(120);

        readonly Process _process;
        readonly StringBuilder _log = new();

        public string Root { get; }
        public string BaseUrl { get; }

        /// <summary>サーバーの標準出力・標準エラー (失敗の原因を調べる用)。</summary>
        public string Log
        {
            get
            {
                lock (_log) return _log.ToString();
            }
        }

        AppServer(string root, string baseUrl, Process process)
        {
            Root = root;
            BaseUrl = baseUrl;
            _process = process;
        }

        /// <param name="editDesign">デザインのフォルダ (写し) を変える。null なら Example/Design のまま。</param>
        /// <param name="settings">足す設定 (環境変数の名前 → 値。appsettings のキーを __ 区切りで)。</param>
        public static async Task<AppServer> StartAsync(MockServer mock, Action<string>? editDesign = null, IReadOnlyDictionary<string, string>? settings = null)
        {
            var source = FindSourceDirectory();
            var example = Path.Combine(source, "Example");
            var serverDll = Path.Combine(example, "LowCodeApp.Server", "bin", Configuration, "net10.0", "LowCodeApp.Server.dll");
            if (!File.Exists(serverDll)) throw new FileNotFoundException("Build Example/LowCodeApp.Server first.", serverDll);

            var root = Path.Combine(Path.GetTempPath(), "NetSuiteSeleniumTest", Guid.NewGuid().ToString("N"));
            var designDirectory = Path.Combine(root, "Design");
            Directory.CreateDirectory(designDirectory);
            Directory.CreateDirectory(Path.Combine(root, "Font"));
            Directory.CreateDirectory(Path.Combine(root, "Storages"));

            //コネクターが JWT の署名に使う鍵 (モックは署名を確かめない)
            var keyPath = Path.Combine(root, "test-key.pem");
            using (var rsa = RSA.Create(2048)) File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());

            //ログインするユーザー (admin / admin) の SQLite
            var dbPath = Path.Combine(root, "main.db");
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = File.ReadAllText(Path.Combine(example, "Data", "main.sql"));
                command.ExecuteNonQuery();
            }

            //デザイナの送信 (デプロイ) と同じ App.zip
            var designSource = Path.Combine(example, "Design");
            if (editDesign != null)
            {
                var copy = Path.Combine(root, "DesignSource");
                CopyDirectory(designSource, copy);
                editDesign(copy);
                designSource = copy;
            }
            using (var zip = DesignDataFileManager.CreateZipAsync(designSource))
            using (var file = File.Create(Path.Combine(designDirectory, "App.zip")))
            {
                zip.CopyTo(file);
            }

            var baseUrl = $"http://127.0.0.1:{FreePort.Get()}";
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(serverDll)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            startInfo.ArgumentList.Add(serverDll);
            //appsettings.json / appsettings.Development.json の値を上書きする (ConnectionStrings:<Name> と同じキーを __ 区切りで)
            var environment = startInfo.Environment;
            environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            environment["ASPNETCORE_URLS"] = baseUrl;
            environment["ConnectionStrings__Main"] = $"Data Source={dbPath};";
            environment["ConnectionStrings__NetSuite"] = mock.GetConnectionString(keyPath);
            environment["DesignFileDirectory"] = designDirectory;
            environment["FontFileDirectory"] = Path.Combine(root, "Font");
            environment["FileSystemStorages__0__Directory"] = Path.Combine(root, "Storages");
            foreach (var (key, value) in settings ?? new Dictionary<string, string>()) environment[key] = value;

            var process = new Process { StartInfo = startInfo };
            var server = new AppServer(root, baseUrl, process);
            process.OutputDataReceived += (_, e) => server.Append(e.Data);
            process.ErrorDataReceived += (_, e) => server.Append(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await server.WaitReadyAsync();
            }
            catch
            {
                server.Dispose();
                throw;
            }
            return server;
        }

        void Append(string? line)
        {
            if (line == null) return;
            lock (_log) _log.AppendLine(line);
        }

        async Task WaitReadyAsync()
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < StartTimeout)
            {
                if (_process.HasExited) throw new InvalidOperationException($"The server exited ({_process.ExitCode}).\n{Log}");
                try
                {
                    using var response = await http.GetAsync(BaseUrl + "/login.html");
                    if (response.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(500);
            }
            throw new TimeoutException($"The server did not start within {StartTimeout.TotalSeconds} seconds.\n{Log}");
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(10000);
                }
            }
            catch (InvalidOperationException) { }
            _process.Dispose();
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }

        //このテストと同じ構成 (Debug / Release) のビルド出力を使う: bin/<Configuration>/net10.0/
        static string Configuration => new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;

        static string FindSourceDirectory()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "Example", "Design"))) return dir.FullName;
            }
            throw new DirectoryNotFoundException("Source/Example/Design was not found above the test output folder.");
        }
    }
}
