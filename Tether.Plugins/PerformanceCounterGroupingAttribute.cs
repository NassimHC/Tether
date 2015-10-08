using System;

namespace Tether.Plugins
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class PerformanceCounterGroupingAttribute : Attribute
    {
        public PerformanceCounterGroupingAttribute(string wmiClassName, SelectorEnum selector)
        {
            WMIClassName = wmiClassName;
            Selector = selector;
        }

        public PerformanceCounterGroupingAttribute(string wmiClassName, SelectorEnum selector, string selectorValue)
        {
            WMIClassName = wmiClassName;
            Selector = selector;
            SelectorValue = selectorValue;
            WMIRoot = @"\\.\root\cimv2";
        }

        public string WMIRoot { get; set; }
        public string WMIClassName { get; set; }
        public SelectorEnum Selector { get; set; }
        public string SelectorValue { get; set; }
        public string[] ExclusionContains { get; set; }

        public string Subquery { get; set; }

    }
}