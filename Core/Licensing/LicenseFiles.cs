namespace PlainClip.Core.Licensing;

/// <summary>
/// 配布物に同梱するライセンス表記（V2.0.0。PROJECT.md「ライセンス表記」）。exe と同じフォルダーにある <c>LICENSE</c>（MIT License の全文）と
/// <c>THIRD-PARTY-NOTICES.md</c>（第三者のソフトウェアの表記）を、設定画面から確かめられるようにする。
/// <c>LICENSE</c> に拡張子を付けないのは、暗号化される拡張子（.txt など）を避けるため。読めなければフルパスを示す。
/// </summary>
public static class LicenseFiles
{
    public const string LicenseName = "LICENSE";
    public const string NoticesName = "THIRD-PARTY-NOTICES.md";

    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, name);

    /// <summary>ファイルの内容。読めなければ null と、フルパスを含む理由。</summary>
    public static string? Read(string name, out string problem)
    {
        var path = PathOf(name);
        problem = "";
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"{path}{Environment.NewLine}{ex.Message}";
            return null;
        }
    }
}
