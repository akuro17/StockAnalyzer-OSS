using System;
using SkiaSharp;

namespace StockAnalyzer.Avalonia.Drawing;

/// <summary>
/// 複数行テキストのレイアウト・計測および決定論的アライメント描画エンジン。
/// SkiaSharp の SKCanvas.DrawText / SKPaint.MeasureText は単一行のみを解釈するため、
/// 複数行テキストおよび水平アライメント（Left / Center / Right）を決定論的に制御します。
/// </summary>
public static class MultilineTextRenderer
{
    private static readonly string[] s_emptyLines = new string[] { string.Empty };

    /// <summary>
    /// 文字列を改行コード（\r\n, \r, \n）で分割します。
    /// 空文字列または null の場合は要素数 1 の空行配列を返します。
    /// </summary>
    public static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return s_emptyLines;
        }

        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    /// <summary>
    /// 最長行の幅（Ink-width）とブロック全体の総高さ（行数 × FontSpacing）を計測します。
    /// </summary>
    public static SKSize MeasureBlock(SKPaint paint, string[]? lines)
    {
        if (paint == null) throw new ArgumentNullException(nameof(paint));

        if (lines == null || lines.Length == 0)
        {
            return new SKSize(0f, paint.FontSpacing);
        }

        float maxWidth = 0f;
        var bounds = new SKRect();
        for (int i = 0; i < lines.Length; i++)
        {
            float w = paint.MeasureText(lines[i], ref bounds);
            if (w > maxWidth) maxWidth = w;
        }

        return new SKSize(maxWidth, paint.FontSpacing * lines.Length);
    }

    /// <summary>
    /// 指定された水平アライメントに従い、垂直中心 Y を基準として各行を描画します。
    /// </summary>
    /// <param name="canvas">描画先キャンバス</param>
    /// <param name="lines">描画対象の各行テキスト</param>
    /// <param name="leftX">テキスト領域の左端 X 座標（パディング適用後）</param>
    /// <param name="centerY">テキスト領域の垂直中心 Y 座標</param>
    /// <param name="paint">描画ペイント</param>
    /// <param name="alignment">水平アライメント（Left / Center / Right）</param>
    /// <param name="blockWidth">ブロック最大幅（0 の場合は lines から自動計測）</param>
    public static void DrawBlock(
        SKCanvas canvas,
        string[]? lines,
        float leftX,
        float centerY,
        SKPaint paint,
        TextHorizontalAlignment alignment = TextHorizontalAlignment.Left,
        float blockWidth = 0f)
    {
        if (canvas == null || paint == null || lines == null || lines.Length == 0)
        {
            return;
        }

        float lineHeight = paint.FontSpacing;
        float totalHeight = lineHeight * lines.Length;
        float firstBaselineY = centerY - totalHeight / 2f - paint.FontMetrics.Ascent;

        float maxWidth = blockWidth > 0f ? blockWidth : MeasureBlock(paint, lines).Width;

        var bounds = new SKRect();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            float lineWidth = paint.MeasureText(line, ref bounds);

            float lineX = alignment switch
            {
                TextHorizontalAlignment.Center => leftX + (maxWidth - lineWidth) / 2f - bounds.Left,
                TextHorizontalAlignment.Right => leftX + (maxWidth - lineWidth) - bounds.Left,
                _ => leftX - bounds.Left // Left (default)
            };

            float lineY = firstBaselineY + i * lineHeight;
            canvas.DrawText(line, lineX, lineY, paint);
        }
    }
}
