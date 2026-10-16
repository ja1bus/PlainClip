using PlainClip.Core.Licensing;
using PlainClip.Core.Localization;

namespace PlainClip.Tests;

/// <summary>同梱のライセンス表記と Web の URL（V2.0.0。PROJECT.md「ライセンス表記」）。</summary>
public class LicenseTests
{
    [Fact]
    public void License_and_notices_are_copied_next_to_the_executable()
    {
        var license = LicenseFiles.Read(LicenseFiles.LicenseName, out var problem);
        Assert.True(license is not null, problem);
        Assert.StartsWith("MIT License", license);
        Assert.Contains("Copyright (c) 2026 ja1bus", license);

        var notices = LicenseFiles.Read(LicenseFiles.NoticesName, out problem);
        Assert.True(notices is not null, problem);
        // MSIX は .NET のランタイムを内包するため、その著作権・許諾表示を含める。
        Assert.Contains("Copyright (c) .NET Foundation and Contributors", notices);
        Assert.DoesNotContain("AreaShot", notices);

        // 拡張子の無い LICENSE（暗号化される拡張子を避ける）。
        Assert.Equal("", Path.GetExtension(LicenseFiles.PathOf(LicenseFiles.LicenseName)));
    }

    [Fact]
    public void Unreadable_license_reports_the_full_path()
    {
        Assert.Null(LicenseFiles.Read("no-such-file", out var problem));
        Assert.Contains(LicenseFiles.PathOf("no-such-file"), problem);
    }

    [Fact]
    public void Links_point_to_the_plainclip_pages()
    {
        Assert.Equal("https://github.com/ja1bus/PlainClip", AppLinks.Repository);
        Assert.Equal("https://www.ja1bus.com/plainclip/privacy/", AppLinks.Privacy);
        Assert.Equal("https://www.ja1bus.com/plainclip/", AppLinks.WebsiteFor(System.Globalization.CultureInfo.GetCultureInfo("ja-JP")));
        Assert.Equal("https://www.ja1bus.com/plainclip/en/", AppLinks.WebsiteFor(System.Globalization.CultureInfo.GetCultureInfo("en-US")));
    }
}
