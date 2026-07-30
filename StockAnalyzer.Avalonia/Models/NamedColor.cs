using Avalonia.Media;
using SkiaSharp;
using System;

namespace StockAnalyzer.Avalonia.Models;

public class NamedColor
{
    public string Name { get; set; }
    public string Value { get; set; }
    public SKColor Color { get; set; }

    public NamedColor(string name, SKColor color)
    {
        Name = name;
        Color = color;
        Value = color.ToString();
    }

    public NamedColor(string name, string colorString)
    {
        Name = name;
        Value = colorString;
        
        if (global::Avalonia.Media.Color.TryParse(colorString, out var avaColor))
        {
             Color = new SKColor(avaColor.R, avaColor.G, avaColor.B, avaColor.A);
        }
        else
        {
             try 
             {
                 var c = SKColor.Parse(colorString);
                 Color = c;
             }
             catch
             {
                 Color = SKColors.Black;
             }
        }
    }
}
