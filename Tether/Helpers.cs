using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Tether.Plugins;

namespace Tether
{
    public static class Helpers
    {

        public static IEnumerable<string> PerformCounterFiltering(this IEnumerable<string> Instances, SelectorEnum selector, string selectorValue, string[] ExceptList)
        {
            IEnumerable<string> excepts = new List<string>();

            if (ExceptList != null)
            {
                excepts = ExceptList.Select(f => f.ToLowerInvariant());
            }

            IEnumerable<string> returnList = Instances;

            switch (selector)
            {
                case SelectorEnum.Single:
                    returnList = Instances.Take(1);
                    break;
                case SelectorEnum.Each:
                    returnList = Instances;
                    break;
                case SelectorEnum.Index:
                    returnList = Instances.Skip(Convert.ToInt32(selectorValue) - 1).Take(1);
                    break;
                case SelectorEnum.Name:
                    returnList = Instances.Where(f => f.ToLowerInvariant() == selectorValue.ToLowerInvariant());
                    break;
                case SelectorEnum.Total:
                    returnList = Instances.Where(f => f.ToLowerInvariant() == "_Total".ToLowerInvariant());
                    break;
                case SelectorEnum.Except:
                    returnList = Instances.Where(
                        delegate (string f)
                        {
                            return !excepts.Any(except => f.ToLowerInvariant().Contains(except));
                        });
                    break;
            }


            return returnList;
        }

        public static IEnumerable<ManagementObject> PerformFiltering(this IEnumerable<ManagementObject> obj, SelectorEnum selector, string selectorValue, string[] ExceptList, string subQuery = null)
        {
            IEnumerable<string> excepts = new List<string>();
            if (ExceptList != null)
            {
                excepts = ExceptList.Select(f => f.ToLowerInvariant());
            }

            IEnumerable<ManagementObject> returnList = obj;

            switch (selector)
            {
                case SelectorEnum.Single:
                    returnList = obj.Take(1);
                    break;
                case SelectorEnum.Each:
                    returnList = obj;
                    break;
                case SelectorEnum.Index:
                    returnList = obj.Skip(Convert.ToInt32(selectorValue) - 1).Take(1);
                    break;
                case SelectorEnum.Name:
                    returnList = obj.Where(f => f["Name"] == selectorValue);
                    break;
                case SelectorEnum.Total:
                    returnList = obj.Where(f => f["Name"].ToString().ToLowerInvariant() == "_Total".ToLowerInvariant());
                    break;
                case SelectorEnum.Except:
                    returnList = obj.Where(
                        delegate (ManagementObject f)
                        {
                            return !excepts.Any(except => f["Name"].ToString().ToLowerInvariant().Contains(except));
                        });
                    break;
            }

            if (!String.IsNullOrEmpty(subQuery))
            {
                returnList = returnList.Where(e => e["Name"].ToString() == new ManagementObjectSearcher("root\\cimv2", subQuery).Get().Cast<ManagementObject>().FirstOrDefault().Properties.Cast<PropertyData>().FirstOrDefault().Value);
            }

            return returnList;
        }
    }
}