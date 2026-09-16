using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using StockAnalyzer.Core.Models;
using System;
using System.Globalization;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

// Message for validation changes
public class ParameterValidationChangedMessage : ValueChangedMessage<bool>
{
    public ParameterValidationChangedMessage(bool hasError) : base(hasError) { }
}
public partial class IndicatorParameterViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _value;

    private string? _validationError;
    public string? ValidationError
    {
        get => _validationError;
        private set
        {
            if (SetProperty(ref _validationError, value))
            {
                OnPropertyChanged(nameof(HasError));
                WeakReferenceMessenger.Default.Send(new ParameterValidationChangedMessage(HasError));
            }
        }
    }

    public string Name { get; set; } = string.Empty;
    public Type ParameterType { get; set; } = typeof(object);
    public bool IsColor => ParameterType == typeof(IndicatorColor);
    public object? MinValue { get; set; }
    public object? MaxValue { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool HasError => !string.IsNullOrEmpty(ValidationError);

    partial void OnValueChanged(object? value)
    {
        Validate();
    }

    public bool Validate()
    {
        ValidationError = null;
        if (Value == null && ParameterType.IsValueType && Nullable.GetUnderlyingType(ParameterType) == null)
        {
            ValidationError = "Value is required";
            return false;
        }

        try
        {
            var underlying = Nullable.GetUnderlyingType(ParameterType) ?? ParameterType;
            
            if (IsNumericType(underlying))
            {
                var stringVal = Value?.ToString();
                if (string.IsNullOrWhiteSpace(stringVal))
                {
                     return true;
                }

                if (underlying == typeof(decimal))
                {
                    if (!decimal.TryParse(stringVal, NumberStyles.Any, CultureInfo.CurrentCulture, out var decVal) &&
                        !decimal.TryParse(stringVal, NumberStyles.Any, CultureInfo.InvariantCulture, out decVal))
                    {
                        ValidationError = "Please enter a numeric value";
                        return false;
                    }

                    if (MinValue != null)
                    {
                         var minVal = Convert.ToDecimal(MinValue, CultureInfo.InvariantCulture);
                         if (decVal < minVal)
                         {
                             ValidationError = $"Minimum: {MinValue}"; return false;
                         }
                    }
                    if (MaxValue != null)
                    {
                        var maxVal = Convert.ToDecimal(MaxValue, CultureInfo.InvariantCulture);
                        if (decVal > maxVal)
                        {
                            ValidationError = $"Maximum: {MaxValue}"; return false;
                        }
                    }
                    // Update value to the parsed decimal to ensure consistency
                    if (Value is string) Value = decVal;
                }
                else
                {
                    if (!double.TryParse(stringVal, NumberStyles.Any, CultureInfo.CurrentCulture, out var dVal) &&
                        !double.TryParse(stringVal, NumberStyles.Any, CultureInfo.InvariantCulture, out dVal))
                    {
                         ValidationError = "Please enter a numeric value";
                         return false;
                    }

                    if (MinValue != null)
                    {
                        var minVal = Convert.ToDouble(MinValue, CultureInfo.InvariantCulture);
                        if (dVal < minVal)
                        {
                            ValidationError = $"Minimum: {MinValue}"; return false;
                        }
                    }
                    if (MaxValue != null)
                    {
                        var maxVal = Convert.ToDouble(MaxValue, CultureInfo.InvariantCulture);
                        if (dVal > maxVal)
                        {
                            ValidationError = $"Maximum: {MaxValue}"; return false;
                        }
                    }
                     // Update value to the parsed double to ensure consistency
                    if (Value is string) Value = Convert.ChangeType(dVal, underlying);
                }
            }
        }
        catch
        {
            ValidationError = "Invalid value";
            return false;
        }
        return true;
    }

    private bool IsNumericType(Type t)
    {
        return t == typeof(int) || t == typeof(double) || t == typeof(decimal) ||
               t == typeof(long) || t == typeof(short) || t == typeof(float) || t == typeof(byte);
    }
}
