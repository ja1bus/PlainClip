using System.Runtime.InteropServices;

namespace PlainClip.Services;

/// <summary>
/// MSIX パッケージ（Microsoft Store 版）として動いているか（PROJECT.md「Microsoft Store（MSIX）」）。
/// パッケージ版はレジストリ（HKCU）への書き込みが仮想化され、Run キーでの自動起動が効かないため、自動起動の方法を切り替える。
/// </summary>
public static class PackageInfo
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;

    public static bool IsPackaged { get; } = DetectPackaged();

    /// <summary>パッケージのファミリー名（V2.1.0。診断ログの実際の保存先に使う）。パッケージ版でなければ null。</summary>
    public static string? FamilyName { get; } = IsPackaged ? ReadFamilyName() : null;

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

    private static string? ReadFamilyName()
    {
        try
        {
            var length = 0;
            if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer || length <= 0) return null;
            var buffer = new char[length];
            return GetCurrentPackageFamilyName(ref length, buffer) == 0 ? new string(buffer, 0, length - 1) : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int packageFamilyNameLength, char[]? packageFamilyName);
}
