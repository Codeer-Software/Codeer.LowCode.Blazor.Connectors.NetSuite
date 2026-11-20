using Codeer.LowCode.Blazor.DataIO;
using Codeer.LowCode.Blazor.DesignLogic;
using Codeer.LowCode.Blazor.Repository.Data;
using Codeer.LowCode.Blazor.RequestInterfaces;
using Codeer.LowCode.Blazor.Script;
using System.Reflection;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.Test.Harness
{
    /// <summary>
    /// 画面側のモジュール (本体の Module) を作るための最小のサービス。サーバーにはつながない。
    /// 保存 (SubmitAsync) は送られたデータを残して成功を返す。<see cref="HoldSubmit"/> で送信の途中で止められる (自動保存の送信中の操作を再現する)。
    /// </summary>
    public class ClientServices : IAppInfoService, IModuleDataService
    {
        readonly DesignData _design;
        readonly ScriptRuntimeTypeManager _scriptRuntimeTypeManager = new();
        TaskCompletionSource? _hold;
        TaskCompletionSource _entered = new();

        public ClientServices(DesignData design)
        {
            _design = design;
            Core = new Services(new NullServiceProvider(), this, this, NullProxy<INavigationService>.Create(), NullProxy<IUIService>.Create(), NullProxy<ILogger>.Create());
        }

        public Services Core { get; }

        /// <summary>送られた保存のデータ。</summary>
        public List<List<ModuleSubmitData>> Submits { get; } = new();

        /// <summary>次の保存を、<see cref="ReleaseSubmit"/> まで返さない。</summary>
        public void HoldSubmit()
        {
            _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>止めた保存が送られてくるまで待つ。</summary>
        public Task WaitSubmitEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void ReleaseSubmit() => _hold?.SetResult();

        //IAppInfoService
        public string CurrentUserId => string.Empty;
        public ModuleData? CurrentUserData => null;
        public ScriptRuntimeTypeManager GetScriptRuntimeTypeManager() => _scriptRuntimeTypeManager;
        public Task<MemoryStream?> GetResourceAsync(string resourcePath) => Task.FromResult<MemoryStream?>(null);
        public DesignData GetDesignData() => _design;

        //IModuleDataService
        public async Task<List<ModuleSubmitResult>?> SubmitAsync(List<ModuleSubmitData> data)
        {
            Submits.Add(data);
            _entered.TrySetResult();
            if (_hold != null) await _hold.Task;
            _hold = null;
            return data.Select(_ => new ModuleSubmitResult()).ToList();
        }

        public Task<Codeer.LowCode.Blazor.DataIO.FileInfo?> UploadFile(string moduleName, string fieldName, string fileName, StreamContent content) => throw new NotSupportedException();
        public Task<MemoryStream?> DownloadFile(string moduleName, string fieldName, string id) => throw new NotSupportedException();
        public Task<MemoryStream?> GetListFileAsync(Codeer.LowCode.Blazor.Repository.Match.SearchCondition condition) => throw new NotSupportedException();
        public Task<List<ModuleSubmitResult>?> SubmitByFileAsync(string moduleName, StreamContent content) => throw new NotSupportedException();

        class NullServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }

    /// <summary>何もしないインターフェースの実装 (戻り値は既定値。Task は完了したもの)。</summary>
    public class NullProxy<T> : DispatchProxy where T : class
    {
        public static T Create() => DispatchProxy.Create<T, NullProxy<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod?.ReturnType;
            if (type == null || type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var value = Default(type.GetGenericArguments()[0]);
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, new[] { value });
            }
            return Default(type);
        }

        static object? Default(Type type)
            => type == typeof(string) ? string.Empty : type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
