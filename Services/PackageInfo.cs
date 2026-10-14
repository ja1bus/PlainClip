using System.Runtime.InteropServices;

namespace PlainClip.Services;

/// <summary>
/// MSIX パッケージ（Microsoft Store 版）として動いているか（PROJECT.md「Microsoft Store（MSIX）」）。
/// パッケージ版はレジストリ（HKCU）への書き込みが仮想化され、Run キーでの自動起動が効かないため、自動起動の方法を切り替える。
/// </summary>
public static class PackageInfo
{
    private const int AppModelErrorNoPackage = 15700;

    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);
}
