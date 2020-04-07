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
    }
}
