using System.Text;
using System.Text.Json;

namespace PlainClip.Core.Settings;

public enum SettingsLoadStatus
{
    Loaded,
    /// <summary>ファイルが無い（初回起動）。既定値を使う。</summary>
    Missing,
    /// <summary>壊れていて読めない。退避して既定値を使う。</summary>
    Corrupt,
}

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status, IReadOnlyList<string> Fixes, string? BackupPath = null, string? Error = null);

/// <summary>
/// 設定ファイル（UTF-8 JSON）の読み書き（PROJECT.md「設定保存」）。
/// 壊れたファイルは settings.json.broken-yyyyMMddHHmmss へ退避してから既定値で続行する。
/// 書き込みは一時ファイル経由で置き換え、途中で失敗しても元のファイルを壊さない。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        // "Ctrl+Alt+P" の "+" を + にせず、そのまま読める形で保存する（設定値に HTML は含まない）。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlainClip");

    public static SettingsStore CreateDefault() => new(Path.Combine(DefaultDirectory, "settings.json"));

    public string FilePath { get; }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath))
            return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Missing, []);

        try
        {
            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                           ?? throw new JsonException("設定ファイルが空です。");
            var fixes = SettingsNormalizer.Normalize(settings);
            return new SettingsLoadResult(settings, SettingsLoadStatus.Loaded, fixes);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            string? backup = null;
            try
            {
                backup = FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            {
                backup = null;
            }
            return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Corrupt, [], backup, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めない（ロック・権限）。既定値で続行するが、退避はしない（ファイルは壊れていない可能性がある）。
            return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Corrupt, [], null, ex.Message);
        }
    }

    /// <summary>保存する。失敗時は例外（IOException / UnauthorizedAccessException）を呼び出し側へ返す。</summary>
    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
    }
}
