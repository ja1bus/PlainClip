using PlainClip.Core.Localization;
#if MSIX
using Windows.ApplicationModel;
#endif

namespace PlainClip.Services;

/// <summary>
/// MSIX 版の自動起動（PROJECT.md「Microsoft Store（MSIX）」）。マニフェスト（installer/msix/AppxManifest.xml）の
/// windows.startupTask "PlainClipStartup" を StartupTask API で有効・無効にする。
/// WinRT の API を使うため、MSIX 用のビルド（PlainClipMsix=true。TargetFramework に Windows の版を付け、定数 MSIX を定義）だけで実装する。
/// 通常のビルドはパッケージで動かないので、ここには来ない。
/// </summary>
public static partial class AutoStartService
{
    /// <summary>マニフェストの uap5:StartupTask の TaskId と同じ値。</summary>
    public const string StartupTaskId = "PlainClipStartup";

#if MSIX
    // StartupTask の非同期 API は UI スレッドで待つと止まることがあるため、スレッドプールで実行して待つ。
    private static T RunSync<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();

    private static StartupTask GetTask() => RunSync(() => StartupTask.GetAsync(StartupTaskId).AsTask());

    private static bool PackagedIsEnabled()
    {
        try
        {
            return GetTask().State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Failed to read the startup task state", ex);
            return false;
        }
    }

    private static string? PackagedSetEnabled(bool enabled)
    {
        try
        {
            var task = GetTask();
            if (!enabled)
            {
                if (task.State == StartupTaskState.EnabledByPolicy) return Strings.AutoStartDisabledByPolicy;
                task.Disable();
                return null;
            }
            var state = RunSync(() => task.RequestEnableAsync().AsTask());
            return state switch
            {
                StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => null,
                StartupTaskState.DisabledByUser => Strings.AutoStartDisabledByUser,
                StartupTaskState.DisabledByPolicy => Strings.AutoStartDisabledByPolicy,
                _ => state.ToString(),
            };
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Failed to change the startup task", ex);
            return ex.Message;
        }
    }
#else
    private static bool PackagedIsEnabled() => false;

    private static string? PackagedSetEnabled(bool enabled) => Strings.AutoStartNoPath;
#endif
}
