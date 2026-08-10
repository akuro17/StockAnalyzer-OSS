using System.Text.Json.Serialization;

namespace StockAnalyzer.Core.Models;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract class CoreIndicatorParameterBase : System.ComponentModel.INotifyPropertyChanged, System.ComponentModel.INotifyDataErrorInfo
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    public abstract string GetDisplayName(string indicatorType);
    public abstract void Validate();

    public CoreIndicatorParameterBase Clone()
    {
        return (CoreIndicatorParameterBase)this.MemberwiseClone();
    }

    // INotifyDataErrorInfo Implementation
    public event EventHandler<System.ComponentModel.DataErrorsChangedEventArgs>? ErrorsChanged;

    public System.Collections.IEnumerable GetErrors(string? propertyName)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(this);
        
        // Validate all properties
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(this, context, results, true);

        if (string.IsNullOrEmpty(propertyName))
        {
            return results;
        }

        return results.Where(r => r.MemberNames.Contains(propertyName));
    }

    [JsonIgnore]
    public bool HasErrors
    {
        get
        {
            var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            var context = new System.ComponentModel.DataAnnotations.ValidationContext(this);
            return !System.ComponentModel.DataAnnotations.Validator.TryValidateObject(this, context, results, true);
        }
    }

    protected void OnErrorsChanged(string propertyName)
    {
        ErrorsChanged?.Invoke(this, new System.ComponentModel.DataErrorsChangedEventArgs(propertyName));
    }
}
