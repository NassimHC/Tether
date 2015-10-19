﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using NLog;
using NLog.Fluent;
using Tether.CoreChecks;
using Tether.CoreSlices;
using Tether.Plugins;
using Topshelf;
using Utilities.DataTypes.ExtensionMethods;
using Timer = System.Timers.Timer;

namespace Tether
{
    public class Service : ServiceControl
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private Timer timer;
        Thread pluginDetectionThread;
        private bool systemStatsSent = false;
        private List<ICheck> ICheckTypeList;
        private List<Type> sliceTypes;

        List<ICheck> sdCoreChecks;

        public Service()
        {
            logger.Trace("start ctor");
            timer = new Timer(ConfigurationSingleton.Instance.Config.CheckInterval*1000);
            timer.Elapsed += Timer_Elapsed;
            
            pluginDetectionThread = new Thread(DetectPlugins);
            pluginDetectionThread.Start();

            ICheckTypeList = new List<ICheck>();

            sliceTypes = new List<Type>();

            sdCoreChecks = new List<ICheck>();

            CreateBaseChecks();

            logger.Trace("end ctor");
        }

        private void CreateBaseChecks()
        {
            sdCoreChecks.Add(new NetworkTrafficCheck());
            sdCoreChecks.Add(new DriveInfoBasedDiskUsageCheck());
            sdCoreChecks.Add(new ProcessorCheck());
            sdCoreChecks.Add(new ProcessCheck());
            sdCoreChecks.Add(new PhysicalMemoryFreeCheck());
            sdCoreChecks.Add(new PhysicalMemoryUsedCheck());
            sdCoreChecks.Add(new PhysicalMemoryCachedCheck());
            sdCoreChecks.Add(new SwapMemoryFreeCheck());
            sdCoreChecks.Add(new SwapMemoryUsedCheck());
            sdCoreChecks.Add(new IOCheck());
        }

        private string basePath = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        private void DetectPlugins()
        {
            var pluginPath = Path.Combine(basePath, "plugins");
            if (!Directory.Exists(pluginPath))
            {
                return;
            }
            logger.Trace("Finding plugins");
            DirectoryInfo di = new DirectoryInfo(pluginPath);
            FileInfo[] fileInfo = di.GetFiles("*.dll");
            foreach (var info in fileInfo)
            {
                try
                {
                    var assembly = Assembly.LoadFile(info.FullName);

                    var enumerable = assembly.Types(typeof(ICheck));
                    
                    foreach (var type in enumerable)
                    {
                        ICheckTypeList.Add(Activator.CreateInstance(type) as ICheck);
                    }


                    var types = assembly.GetTypes().Where(e => e.GetCustomAttribute<PerformanceCounterGroupingAttribute>() != null);
                    foreach (var type in types)
                    {
                        logger.Trace("Found slice " + type.FullName);
                    }
                    sliceTypes.AddRange(types);


                }
                catch (Exception e)
                {
                    logger.Warn("Unable to load " + info.FullName, e);
                }
            }

            logger.Trace("Plugins found!");
        }

        private static List<T> PopulateMultiple<T>() where T : new()
        {
            var t = new List<T>();
            var pcga = typeof(T).Attribute<PerformanceCounterGroupingAttribute>();

            if (pcga != null)
            {
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
                        PropertyInfo property = typeof(T).GetProperties()
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

                            

                            property.SetValue(item, changeType);
                        }
                        catch (Exception e)
                        {
                            logger.ErrorException("Error on property " + name, e);
                        }

                    }
                    t.Add(item);
                }


            }


            return t;
        }





        private void Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            var results = new Dictionary<string, object>();
            List<dynamic> objList = new List<dynamic>();

            

            if (systemStatsSent)
            {
                sdCoreChecks.RemoveAll(f => f.Key == "systemStats");
            }

            systemStatsSent = true;
            logger.Info("Polling Checks");
            Parallel.ForEach(
                sdCoreChecks,
                check =>
                {

                    logger.Debug("{0}: start", check.GetType());
                    try
                    {

                        var result = check.DoCheck();

                        if (result == null)
                        {
                            return;
                        }

                        results.Add(check.Key, result);

                        logger.Debug("{0}: end", check.GetType());
                    }
                    catch (Exception ex)
                    {
                        logger.Error(string.Format("Error on {0}", check.GetType()), ex);
                    }

                });

            var pluginCollection = new Dictionary<string, object>();
            logger.Info("Polling Slices");
            Parallel.ForEach(
                ICheckTypeList,
                check =>
                {

                    logger.Debug("{0}: start", check.GetType());
                    try
                    {

                        var result = check.DoCheck();

                        if (result == null)
                        {
                            return;
                        }

                        pluginCollection.Add(check.Key, result);

                        logger.Debug("{0}: end", check.GetType());
                    }
                    catch (Exception ex)
                    {
                        logger.Error(string.Format("Error on {0}", check.GetType()), ex);
                    }

                });

            logger.Info("Generating SD compatible names for slices.");
            Parallel.ForEach(
                sliceTypes,
                type =>
                {
                    try
                    {
                        MethodInfo method = GetType().GetMethod("PopulateMultiple", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).MakeGenericMethod(new Type[] { type });
                        var invoke = method.Invoke(this, null) as dynamic;
                        objList.Add(invoke);
                    }
                    catch (Exception exception)
                    {
                        logger.ErrorException("Error during slice " + type.FullName, exception);
                    }

                });

            foreach (dynamic o in objList)
            {
                foreach (var coll in o)
                {
                    pluginCollection.Add("Slice[" + ((System.Type)(o.GetType())).GenericTypeArguments[0].Name + "]-[" + GetName(o, coll) +"]", coll);
                }
                
            }

            results.Add("plugins", pluginCollection);

            try
            {
                var poster = new PayloadPoster(results);
                poster.Post();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error with sending data to SD servers");
            }
        }

        private static dynamic GetName(dynamic o, dynamic coll)
        {
            try
            {
                if (((Type)coll.GetType()).GetProperties().Any(f=> f.Name == "Name" ))
                {
                    return ((Type)coll.GetType()).GetProperties().FirstOrDefault(f => f.Name == "Name").GetValue(coll);
                } //o.GetType().GetProperty("Name") != null ? o.Name : o.IndexOf(coll);
                return o.IndexOf(coll);
            }
            catch (Exception e)
            {
                logger.ErrorException("GetName", e);
                throw;
            }
        }

        public bool Start(HostControl hostControl)
        {
            try
            {
                timer.Enabled = true;
                return true;
            }
            catch (Exception e)
            {
                logger.Error(e);
                throw;
            }
        }

        public bool Stop(HostControl hostControl)
        {
            if (pluginDetectionThread.IsAlive)
            {
                pluginDetectionThread.Abort();
            }

            timer.Enabled = false;
            return true;
        }
    }

    public static class Helpers
    {

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
                returnList = returnList.Where(e => e["Name"].ToString() == new ManagementObjectSearcher("root\\cimv2", "select Description from Win32_NetworkAdapterConfiguration where IPEnabled=True").Get().Cast<ManagementObject>().FirstOrDefault().Properties.Cast<PropertyData>().FirstOrDefault().Value);
            }

            return returnList;
        }
    }


}