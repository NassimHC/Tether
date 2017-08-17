using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Tether.Plugins;

namespace Tether.CoreChecks
{
    /// <summary>
    /// Class for checking IO stats on disks
    /// </summary>
    public class IOCheck : ICheck
    {
        private const string PhsicalDiskCategoryName = "PhysicalDisk";

        /// <summary>
        /// List of the physical drives to check
        /// </summary>
        private List<Drive> drivesToCheck;
        private static Logger logger = LogManager.GetCurrentClassLogger();
        Thread counterThread;
        /// <summary>
        /// Initializes a new instance of the IOCheck class and set up the performance monitors we need
        /// </summary>
        public IOCheck()
        {
            this.drivesToCheck = new List<Drive>();

            var perfCategory = new PerformanceCounterCategory(PhsicalDiskCategoryName);

            logger.Trace("Getting instance Names");
            // string[] instanceNames = perfCategory.GetInstanceNames();

            var searcher = new ManagementObjectSearcher("root\\cimv2", "SELECT * FROM Win32_PerfFormattedData_PerfDisk_PhysicalDisk");
            var instanceNames = searcher.Get().Cast<ManagementObject>().Select(e => e["Name"].ToString()).ToArray();

            logger.Trace("Instance Names populated");

            foreach (var instance in instanceNames)
            {
                // ignore _Total and other system categories
                if (instance.StartsWith("_", StringComparison.Ordinal))
                {
                    continue;
                }
                logger.Trace("Instance = " + instance);

                var drive = new Drive();

                drive.DriveName = GetDriveNameForMountPoint(instance);
                
                drive.InstanceName = instance;
                drive.Metrics = new List<DriveMetric>
                {
                    new DriveMetric
                    {
                        MetricName = "rkB/s",
                        Counter = new PerformanceCounter(PhsicalDiskCategoryName, "Disk Read Bytes/sec", instance),
                        Divisor = 1024
                    },
                    new DriveMetric
                    {
                        MetricName = "wkB/s",
                        Counter = new PerformanceCounter(PhsicalDiskCategoryName, "Disk Write Bytes/sec", instance),
                        Divisor = 1024
                    },
                    new DriveMetric
                    {
                        MetricName = "%util",
                        Counter = new PerformanceCounter(PhsicalDiskCategoryName, "% Disk Time", instance),
                        Divisor = 1
                    },
                    new DriveMetric
                    {
                        MetricName = "avgqu-sz",
                        Counter = new PerformanceCounter(PhsicalDiskCategoryName, "Avg. Disk Queue Length", instance),
                        Divisor = 1
                    },
                    new DriveMetric
                    {
                        MetricName = "r/s",
                        Counter = new PerformanceCounter(PhsicalDiskCategoryName, "Disk Reads/sec", instance),
                        Divisor = 1
                    },
                    new DriveMetric
                    {
                        MetricName = "w/s",
                        Counter = new PerformanceCounter(PhsicalDiskCategoryName, "Disk Writes/sec", instance),
                        Divisor = 1
                    },
                    new DriveMetric
                    {
                        MetricName = "svctm",
                        Counter = new PerformanceCounter(PhsicalDiskCategoryName, "Avg. Disk sec/Transfer", instance),
                        Divisor = 1
                    }
                };


                this.drivesToCheck.Add(drive);
            }

            counterThread = new Thread(GetNextCounterValueToIgnore);
            counterThread.Start();
        }

        private void GetNextCounterValueToIgnore()
        {
            logger.Trace("GetNextCounterValueToIgnore Start");
            foreach (Drive drive in drivesToCheck)
            {
                foreach (DriveMetric metric in drive.Metrics)
                {
                    metric.Counter.NextValue();
                }
            }
            logger.Trace("GetNextCounterValueToIgnore Stop");
        }

        private string GetDriveNameForMountPoint(string DriveID)
        {
            try
            {

                if (DriveID.Contains(" "))
                {
                    return DriveID.Split(new char[1] { ' ' }, 2)[1];
                }

                var searcher = new ManagementObjectSearcher(@"Root\Microsoft\Windows\Storage", $@"SELECT * FROM MSFT_Partition WHERE DiskNumber='{DriveID}'");

                foreach (string[] wibble in from ManagementObject ob in searcher.Get() where ob["AccessPaths"] != null select ob["AccessPaths"] as string[])
                {
                    return wibble.FirstOrDefault(f => !f.Contains(@"\\?\"));
                }
            }
            catch (Exception e)
            {
                logger.Debug(e, "Error with MSFT");
            }
            return DriveID.ToString();
        }

        /// <summary>
        /// Gets the name of the check
        /// </summary>
        public string Key => "ioStats";

        /// <summary>
        /// Run the check
        /// </summary>
        /// <returns>An object (usually a Dictionary) containing the check results</returns>
        public object DoCheck()
        {
            var results = new Dictionary<string, object>();

            foreach (var drive in this.drivesToCheck)
            {
                var driveResults = new Dictionary<string, object>();

                foreach (var metric in drive.Metrics)
                {
                    driveResults[metric.MetricName] = metric.Counter.NextValue() / metric.Divisor;
                }


                var read = (float)driveResults["r/s"];
                var write = (float)driveResults["w/s"];

                var total = read + write;
                float ratio = (read / total) * 100;

                if (!float.IsNaN(ratio))
                {
                    driveResults["rwratio"] = ratio;
                }
                else
                {
                    driveResults["rwratio"] = 0.0;
                }


                results[drive.DriveName] = driveResults;
            }

            return results;
        }

        /// <summary>
        /// A single metric to measure
        /// </summary>
        private class DriveMetric
        {
            /// <summary>
            /// Gets or sets the Performance Counter to retrieve the metric from
            /// </summary>
            public PerformanceCounter Counter { get; set; }

            /// <summary>
            /// Gets or sets the name of the metric to send to SD
            /// </summary>
            public string MetricName { get; set; }

            /// <summary>
            /// Gets or sets the number to divide result by (to convert bytes to kilobytes, etc)
            /// </summary>
            public int Divisor { get; set; }
        }

        /// <summary>
        /// Represents a physical drive to get metrics on
        /// </summary>
        private class Drive
        {
            /// <summary>
            /// Gets or sets the name that performance monitor uses for the drive
            /// </summary>
            public string InstanceName { get; set; }

            /// <summary>
            /// Gets or sets the friendly name to display in SD
            /// </summary>
            public string DriveName { get; set; }

            /// <summary>
            /// Gets or sets the list of metrics to fetch each run
            /// </summary>
            public List<DriveMetric> Metrics { get; set; }
        }
    }
}