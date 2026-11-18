using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlainClip.Core.Settings;

/// <summary>
/// 設定ファイルの列挙値の読み書き（V2.3.0。仕様書 §13「列挙値の読み込み」）。
/// 読み: 名前（大文字小文字を区別しない）ならその値、整数ならその値（範囲外は <see cref="SettingsNormalizer"/> が補正する）、
/// それ以外（未知の名前・数字の文字列・カンマ区切り・null・真偽値・小数・オブジェクト・配列）は <c>default(T)</c>。
/// 1 つの項目の未知の値でファイル全体を壊れた扱いにしない（将来の版で値を増やした後に旧版へ戻しても、他の設定が消えないように）。
/// 書き: 名前の文字列（V2.2.1 までの JsonStringEnumConverter と同じ形式）。
/// </summary>
public sealed class LenientEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    private static readonly string[] Names = Enum.GetNames<T>();

    /// <summary>null も受け取り、既定値として読む。</summary>
    public override bool HandleNull => true;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var text = reader.GetString();
                foreach (var name in Names)
                {
                    if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) return Enum.Parse<T>(name);
                }
                return default;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) ? (T)Enum.ToObject(typeof(T), number) : default;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip();
                return default;
            default:
                return default; // null・true・false
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
