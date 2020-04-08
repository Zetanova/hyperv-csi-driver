using HypervCsiDriver.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace HypervCsiDriver.UnitTests
{
    public sealed class HypervTests
    {
        [Theory]
        [InlineData(@"data/kvp.bin")]
        public async Task read_kvp_entries(string filePath)
        {
            var dic = new Dictionary<string, string>();

            await foreach (var entry in HypervUtils.ReadKvpPoolAsync(filePath))
            {
                dic[entry.Name] = entry.Value;
            }

            Assert.Contains("VirtualMachineId", (IDictionary<string,string>)dic);
            Assert.Contains("VirtualMachineName", (IDictionary<string, string>)dic);
            Assert.Contains("PhysicalHostNameFullyQualified", (IDictionary<string, string>)dic);
        }

        [Theory]
        [InlineData(@"C:\ClusterStorage\hv01\Disks\Lnx1510.vhdx", "Lnx1510")]
        public async Task can_parse_win_file_name(string filePath, string fileName)
        {
            var r = HypervUtils.GetFileNameWithoutExtension(filePath);

            Assert.Equal(fileName, r);
        }

        [Theory]
        [InlineData(@"C:\ClusterStorage\hv01\Volumes\Lnx1510.vhdx", "hv01")]
        public async Task can_parse_storage_name_from_win_file_name(string filePath, string storageName)
        {
            var r = HypervUtils.GetStorageNameFromPath(filePath);

            Assert.Equal(storageName, r);
        }
    }
}
