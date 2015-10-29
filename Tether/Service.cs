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
        private bool systemStatsSent = false;
        private List<ICheck> ICheckTypeList;
        private List<Type> sliceTypes;

        List<ICheck> sdCoreChecks;

        public Service()
        {
            logger.Trace("start ctor");
            timer = new Timer(ConfigurationSingleton.Instance.Config.CheckInterval*1000);
            timer.Elapsed += Timer_Elapsed;
            
            ICheckTypeList = new List<ICheck>();

            sliceTypes = new List<Type>();

            sdCoreChecks = new List<ICheck>();

            DetectPlugins();

            CreateBaseChecks();

            logger.Trace("end ctor");
        }

        private void CreateBaseChecks()
        {
            logger.Info("Creating Base Checks...");

            sdCoreChecks.Add(CreateCheck<NetworkTrafficCheck>());
            sdCoreChecks.Add(CreateCheck<DriveInfoBasedDiskUsageCheck>());
            sdCoreChecks.Add(CreateCheck<ProcessorCheck>());
            sdCoreChecks.Add(CreateCheck<ProcessCheck>());
            sdCoreChecks.Add(CreateCheck<PhysicalMemoryFreeCheck>());
            sdCoreChecks.Add(CreateCheck<PhysicalMemoryUsedCheck>());
            sdCoreChecks.Add(CreateCheck<PhysicalMemoryCachedCheck>());
            sdCoreChecks.Add(CreateCheck<SwapMemoryFreeCheck>());
            sdCoreChecks.Add(CreateCheck<SwapMemoryUsedCheck>());
            sdCoreChecks.Add(CreateCheck<IOCheck>());

            logger.Info("Base Check Creation Complete...");
        }

        private ICheck CreateCheck<T>() where T: ICheck, new()
        {
            logger.Trace("Creating " + typeof(T).Name);

            T item;
            try
            {
                item = new T();
            }
            catch (Exception e)
            {
                logger.Trace("Error when creating " + typeof(T).Name, e);
                throw;
            }

            logger.Trace("Finished Creating " + typeof(T).Name);

            return item;
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


                    var types = assembly.GetTypes().Where(e => e.GetCustomAttributes(typeof(PerformanceCounterGroupingAttribute), true).Any());
                    foreach (var type in types)
                    {
                        logger.Trace("Found slice " + type.FullName);
                        sliceTypes.Add(type);
                    }
            
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

                            

                            property.SetValue(item , changeType, null);
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
                    pluginCollection.Add("Slice[" + ((System.Type)(o.GetType())).GetGenericArguments()[0].Name + "]-[" + GetName(o, coll) +"]", coll);
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
                    return ((Type)coll.GetType()).GetProperties().FirstOrDefault(f => f.Name == "Name").GetValue(coll, null);
                }
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
            timer.Enabled = false;
            return true;
        }
    }
}