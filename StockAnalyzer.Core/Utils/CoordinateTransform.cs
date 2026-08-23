namespace StockAnalyzer.Utils;

using StockAnalyzer.Utilities;

/// <summary>
/// Coordinate transformation class
/// Converts price to Y coordinate and vice versa
/// </summary>
public class CoordinateTransform
{
    /// <summary>
    /// Convert price to Y coordinate
    /// </summary>
    public double GetY(decimal price, decimal maxPrice, decimal minPrice, double canvasHeight, bool isLogScale = false, bool isInverted = false)
    {
        if (canvasHeight <= 0) return 0;
        if (maxPrice <= minPrice) return canvasHeight / 2; // Prevent invalid range

        // If inverted, we can just flip the price logic or the final Y
        // Standard: Y = Height * (1 - ratio)
        // Inverted: Y = Height * ratio
        
        if (isLogScale && minPrice > 0 && maxPrice > 0)
        {
            try
            {
                double y = LogScaleTransform.PriceToY_Log(price, minPrice, maxPrice, canvasHeight);
                return isInverted ? canvasHeight - y : y;
            }
            catch (ArgumentException)
            {
                return canvasHeight / 2;
            }
        }

        var range = maxPrice - minPrice;
        var ratio = (double)((price - minPrice) / range);

        // Y coordinate system: top is 0, bottom is max
        // Normal: subtract from 1 (1 - ratio)
        // Inverted: use ratio directly (0 at min price -> top) if min is top?
        // Wait: Inverted means Low Price is at TOP (0), High Price is at BOTTOM (Height).
        // Normal: High Price is at TOP (0), Low Price is at BOTTOM (Height).
        
        // Normal: Y = H * (1 - (Val - Min) / Range) -> Val=Max => Y=0, Val=Min => Y=H
        // Inverted: Y = H * ((Val - Min) / Range) -> Val=Min => Y=0, Val=Max => Y=H
        
        return isInverted 
            ? canvasHeight * ratio 
            : canvasHeight * (1 - ratio);
    }

    /// <summary>
    /// Convert Y coordinate to price
    /// </summary>
    public decimal GetPrice(double y, decimal maxPrice, decimal minPrice, double canvasHeight, bool isLogScale = false, bool isInverted = false)
    {
        if (canvasHeight == 0) return minPrice;
        if (maxPrice <= minPrice) return minPrice; // Prevent invalid range

        if (isLogScale && minPrice > 0 && maxPrice > 0)
        {
            // For log scale, if inverted, invert Y first
            double effectiveY = isInverted ? canvasHeight - y : y;
            try
            {
                return LogScaleTransform.YToPrice_Log(effectiveY, minPrice, maxPrice, canvasHeight);
            }
            catch (ArgumentException)
            {
                return minPrice;
            }
        }

        // Normal: ratio = 1 - (y / H)
        // Inverted: ratio = y / H
        var ratio = isInverted 
            ? (y / canvasHeight)
            : 1 - (y / canvasHeight);
            
        var range = maxPrice - minPrice;

        return minPrice + range * (decimal)ratio;
    }

    /// <summary>
    /// Get X coordinate for candle center
    /// </summary>
    public double GetX(int index, int visibleCount, double canvasWidth)
    {
        if (visibleCount == 0) return 0;

        double spacing = canvasWidth / visibleCount;
        return index * spacing + spacing / 2;
    }

    /// <summary>
    /// Get candle width based on visible count
    /// </summary>
    public double GetCandleWidth(int visibleCount, double canvasWidth)
    {
        if (visibleCount == 0) return 0;
        return canvasWidth / visibleCount * 0.8; // 80% for candle, 20% for spacing
    }
}
