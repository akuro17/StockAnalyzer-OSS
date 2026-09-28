using System.Collections.Generic;

namespace StockAnalyzer.Generators
{
    public class EnumMemberModel
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Value { get; set; }
    }

    public class IndicatorModel
    {
        public string TypeName { get; set; }
        public string ClassName { get; set; }
        public string Namespace { get; set; }
        public string ParameterClassName { get; set; }
        public string ParameterFullTypeName { get; set; }
        public List<IndicatorParameterModel> Parameters { get; set; } = new List<IndicatorParameterModel>();
    }

    public class IndicatorParameterModel
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }

    public class EnumComparer : IEqualityComparer<EnumMemberModel>
    {
        public bool Equals(EnumMemberModel x, EnumMemberModel y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (ReferenceEquals(x, null)) return false;
            if (ReferenceEquals(y, null)) return false;
            if (x.GetType() != y.GetType()) return false;
            return x.Name == y.Name;
        }

        public int GetHashCode(EnumMemberModel obj)
        {
            return obj.Name.GetHashCode();
        }
    }

    public class IndicatorComparer : IEqualityComparer<IndicatorModel>
    {
        public bool Equals(IndicatorModel x, IndicatorModel y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (ReferenceEquals(x, null)) return false;
            if (ReferenceEquals(y, null)) return false;
            if (x.GetType() != y.GetType()) return false;
            return x.TypeName == y.TypeName && x.ClassName == y.ClassName;
        }

        public int GetHashCode(IndicatorModel obj)
        {
            unchecked
            {
                return ((obj.TypeName != null ? obj.TypeName.GetHashCode() : 0) * 397) ^ (obj.ClassName != null ? obj.ClassName.GetHashCode() : 0);
            }
        }
    }
}
