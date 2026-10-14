using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using PlainClip.Core.Localization;
using PlainClip.Services;

namespace PlainClip;

/// <summary>
/// 起動・多重起動防止・未処理例外の扱い。
/// 通常のウィンドウは出さず、タスクトレイに常駐する（ShutdownMode = OnExplicitShutdown。トレイの「終了」でだけ終わる）。
/// </summary>
public partial class App : Application
{
    /// <summary>表示言語の変更で再起動したときの引数。前のプロセスが終わるまで多重起動防止の解放を待つ。</summary>
    public const string RestartArgument = "--restart";

    /// <summary>再起動のとき、前のプロセスの終了を待つ時間。</summary>
    private static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(15);

    private SingleInstance? _instance;
    private AppHost? _host;
    private bool _started;
    private bool _sessionEnding;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        // Windows のサインアウト・再起動中はエラー画面を出さない（出すと再起動を止めてしまう）。
        SessionEnding += (_, _) => _sessionEnding = true;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Write("未処理の例外（終了）", args.ExceptionObject as Exception);

        // 表示言語を決める前の Windows の表示言語（AppHost が設定の言語を適用すると変わるため、先に取っておく）。
        var windowsUiCulture = CultureInfo.CurrentUICulture;
        var restarting = e.Args.Contains(RestartArgument, StringComparer.OrdinalIgnoreCase);
        _instance = SingleInstance.TryAcquire(restarting ? RestartWait : TimeSpan.Zero);
        if (_instance is null)
        {
            // 既に起動済み: 既存側が使い方の画面を表示する。こちらは終了する。
            Shutdown(0);
            return;
        }

        _host = new AppHost(this, windowsUiCulture);
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(() => _host?.NotifyAlreadyRunning()));
        _host.Start();
        _started = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // フック・ショートカットを解放してから多重起動防止を解放する（再起動後のプロセスが登録できるように）。
        _host?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ErrorLog.Write("未処理の例外", e.Exception);
        if (_sessionEnding)
        {
            Shutdown(0);
            return;
        }
        try
        {
            // 常駐アプリなので、操作中の想定外の例外ではプロセスを終了しない（その操作だけ失敗させる）。
            _host?.RecoverFromError();
            MessageBox.Show(
                string.Format(Strings.AppUnexpectedErrorFormat, e.Exception.GetType().Name, e.Exception.Message, ErrorLog.FilePath) +
                (_started ? "" : "\n\n" + Strings.AppWillExit),
                "PlainClip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("エラー表示に失敗", ex);
        }
        if (!_started)
        {
            // 起動途中の失敗ではプロセスを残さない（local/C# の既知の落とし穴）。
            _host?.Dispose();
            Shutdown(1);
        }
    }
}
