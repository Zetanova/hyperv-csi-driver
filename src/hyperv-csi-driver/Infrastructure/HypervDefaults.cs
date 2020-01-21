using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HypervCsiDriver.Infrastructure
{
    public static class HypervDefaults
    {
        //todo query over Get-Cluster
        public static readonly string ClusterStoragePath = @"C:\ClusterStorage";
    }
}
