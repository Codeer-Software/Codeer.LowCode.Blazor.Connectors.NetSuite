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

        public static async Task<AppServer> StartAsync(MockServer mock)
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
            using (var zip = DesignDataFileManager.CreateZipAsync(Path.Combine(example, "Design")))
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
