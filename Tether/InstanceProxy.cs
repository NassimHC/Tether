﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Reflection;
using Newtonsoft.Json;
using Tether.Plugins;
using Utilities.DataTypes.ExtensionMethods;

namespace Tether
{
    internal class InstanceProxy : MarshalByRefObject
    {
        private Dictionary<string, ICheck> CheckTypes;
        private Dictionary<string, Type> slices;

        public InstanceProxy()
        {
            CheckTypes = new Dictionary<string, ICheck>();
            slices = new Dictionary<string, Type>();
        }

        public Dictionary<string, string> GetSlice(string Name)
        {
            var type = slices[Name];
            MethodInfo method = GetType().GetMethod("PopulateMultiple", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).MakeGenericMethod(new Type[] { type  });


            var retr = new Dictionary<string, string>();
            // pluginCollection.Add($"Slice[{((Type) o.GetType()).GetGenericArguments()[0].Name}]-[" + GetName(o, coll) +"]", coll);
            var invoke = method.Invoke(this, null) as dynamic;
            

            foreach (dynamic o in invoke)
            {
                //foreach (var coll in o)
                //{
                    string str = JsonConvert.SerializeObject(o);
                    retr.Add($"Slice[{type.Name}]-[" + GetName(o, invoke) + "]", str);
                //}

            }



            return retr;
        }


        private static dynamic GetName(dynamic o, dynamic coll)
        {
            return ((Type)coll.GetType()).GetProperties().Any(f => f.Name == "Name") ? ((Type)coll.GetType()).GetProperties().FirstOrDefault(f => f.Name == "Name").GetValue(coll, null) : coll.IndexOf(o);
        }


        private static List<T> PopulateMultiple<T>() where T : new()
        {
            var t = new List<T>();
            var pcga = typeof(T).Attribute<PerformanceCounterGroupingAttribute>();

            if (pcga != null)
            {

                if (pcga.UsePerformanceCounter)
                {
                    var PerfCounter = new PerformanceCounterCategory(pcga.WMIClassName);

                    var instances = PerfCounter.GetInstanceNames().PerformCounterFiltering(pcga.Selector, pcga.SelectorValue, pcga.ExclusionContains);

                    foreach (var instance in instances)
                    {
                        var item = new T();

                        IEnumerable<string> names = typeof(T).GetProperties()
                            .Where(f => ReflectionExtensions.Attribute<PerformanceCounterValueExcludeAttribute>(f) == null)
                            .Select(
                                delegate (PropertyInfo info)
                                {
                                    if (info.Attribute<PerformanceCounterValueAttribute>() != null && info.Attribute<PerformanceCounterValueAttribute>().PropertyName != null)
                                    {
                                        return info.Attribute<PerformanceCounterValueAttribute>().PropertyName;
                                    }
                                    return info.Name;
                                });

                        foreach (var name in names)
                        {
                            PropertyInfo property = typeof(T).GetProperties()
                                .FirstOrDefault(
                                    f => (f.Attribute<PerformanceCounterValueAttribute>() != null && f.Attribute<PerformanceCounterValueAttribute>().PropertyName == name) ||
                                         f.Name == name && f.Attribute<PerformanceCounterValueExcludeAttribute>() == null);


                            if (property.Attribute<PerformanceCounterInstanceNameAttribute>() != null)
                            {
                                property.SetValue(item, instance, null);
                            }
                            else
                            {
                                var performanceCounters = PerfCounter.GetCounters(instance);
                                try
                                {

                                    var changeType = Convert.ChangeType(performanceCounters.FirstOrDefault(e => e.CounterName == name).NextValue(), property.PropertyType);

                                    if (property.Attribute<PerformanceCounterValueAttribute>() != null && property.Attribute<PerformanceCounterValueAttribute>().Divisor > 0)
                                    {
                                        if (property.PropertyType == typeof(long))
                                        {
                                            changeType = (long)changeType / property.Attribute<PerformanceCounterValueAttribute>().Divisor;
                                        }
                                        else if (property.PropertyType == typeof(int))
                                        {
                                            changeType = (int)changeType / property.Attribute<PerformanceCounterValueAttribute>().Divisor;
                                        }
                                        else if (property.PropertyType == typeof(short))
                                        {
                                            changeType = (short)changeType / property.Attribute<PerformanceCounterValueAttribute>().Divisor;
                                        }
                                    }

                                    property.SetValue(item, changeType, null);
                                }
                                catch (Exception e)
                                {
                                    //logger.ErrorException("Error on property " + name, e);
                                }
                                finally
                                {
                                    //DisposeAll(performanceCounters);

                                }
                            }

                        }
                        t.Add(item);

                    }
                    return t;

                }


                var searcher = new ManagementObjectSearcher(pcga.WMIRoot, "SELECT * FROM " + pcga.WMIClassName);

                foreach (ManagementObject var in searcher.Get().Cast<ManagementObject>().PerformFiltering(pcga.Selector, pcga.SelectorValue, pcga.ExclusionContains, pcga.Subquery))
                {
                    var item = new T();

                    IEnumerable<string> names = typeof(T).GetProperties()
                        .Where(f => f.Attribute<PerformanceCounterValueExcludeAttribute>() == null)
                        .Select(
                            delegate (PropertyInfo info)
                            {
                                if (info.Attribute<PerformanceCounterValueAttribute>() != null && info.Attribute<PerformanceCounterValueAttribute>().PropertyName != null)
                                {
                                    return info.Attribute<PerformanceCounterValueAttribute>().PropertyName;
                                }
                                return info.Name;
                            });

                    foreach (var name in names)
                    {
                        var property = typeof(T).GetProperties()
                            .FirstOrDefault(
                                f => (f.Attribute<PerformanceCounterValueAttribute>() != null && f.Attribute<PerformanceCounterValueAttribute>().PropertyName == name) || f.Name == name && f.Attribute<PerformanceCounterValueExcludeAttribute>() == null);

                        try
                        {

                            var changeType = Convert.ChangeType(var[name], property.PropertyType);

                            if (property.Attribute<PerformanceCounterValueAttribute>() != null && property.Attribute<PerformanceCounterValueAttribute>().Divisor > 0)
                            {
                                if (property.PropertyType == typeof(long))
                                {
                                    changeType = (long)changeType / property.Attribute<PerformanceCounterValueAttribute>().Divisor;
                                }
                                else if (property.PropertyType == typeof(int))
                                {
                                    changeType = (int)changeType / property.Attribute<PerformanceCounterValueAttribute>().Divisor;
                                }
                                else if (property.PropertyType == typeof(short))
                                {
                                    changeType = (short)changeType / property.Attribute<PerformanceCounterValueAttribute>().Divisor;
                                }
                            }



                            property.SetValue(item, changeType, null);
                        }
                        catch (Exception e)
                        {
                            //logger.Error(e, $"Error on property {name}");
                        }

                    }
                    t.Add(item);
                }


            }


            return t;
        }


        public dynamic PerformCheck(string checkName)
        {
            if (string.IsNullOrWhiteSpace(checkName))
            {
                throw new ArgumentException("message", nameof(checkName));
            }

            return CheckTypes[checkName].DoCheck() as dynamic;
        }

        public List<string> LoadSlices(string path)
        {
            var asm = Assembly.LoadFrom(path);

            var types = asm.GetTypes().Where(e => e.GetCustomAttributes(typeof(PerformanceCounterGroupingAttribute), true).Any()).ToList();

            if (!types.Any())
            {
                return null;
            }

            var retr = new List<string>();

            foreach (var type in types)
            {
                slices.Add(type.FullName, type);
                retr.Add(type.FullName);
            }

           return retr;
        }

        public List<String> LoadLibrary(string path)
        {
            var asm = Assembly.LoadFrom(path);

            var enumerable = asm.GetTypes().Where(r=> r.GetInterfaces().Any(e=>e.FullName == typeof(ICheck).FullName)  ).ToList();

            if (enumerable.Any())
            {
                foreach (var type in enumerable)
                {
                    if (Activator.CreateInstance(type) is ICheck check)
                    {
                        CheckTypes.Add(check.Key, check);
                    }
                }

                var items = CheckTypes.Select(f => f.Key).ToList();

                return items;
            }
            else
            {
                return null;
            }
        }
    }
}