using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace HypervCsiDriver.Utils
{
    public static class HypervUtils
    {
        public static async IAsyncEnumerable<(string Name, string Value)> ReadKvpPoolAsync(string poolFile = "/var/lib/hyperv/.kvp_pool_3",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            //info https://docs.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/dn798287(v%3Dws.11)
            using var file = File.OpenRead(poolFile);

            var buffer = ArrayPool<byte>.Shared.Rent(2560);
            var buffered = 0;

            try
            {
                while (true)
                {
                    var c = await file.ReadAsync(buffer, buffered, buffer.Length - buffered, cancellationToken);
                    if (c == 0)
                        break;

                    buffered += c;

                    if (buffered >= 2560)
                    {
                        var eof = Array.IndexOf(buffer, (byte)'\0', 0, 512);
                        if (eof < 0) eof = 512;

                        var name = eof > 0 ? Encoding.UTF8.GetString(buffer, 0, eof) : string.Empty;

                        eof = Array.IndexOf(buffer, (byte)'\0', 512, 2048) - 512;
                        if (eof < 0) eof = 2048;

                        var value = eof > 0 ? Encoding.UTF8.GetString(buffer, 512, eof) : string.Empty;

                        yield return (name, value);

                        buffered -= 2560;

                        if (buffered > 0)
                            Array.Copy(buffer, 2560, buffer, 0, buffered);
                    }
                }
            }
            finally
            {
                //maybe if buffered > 0 then fail 

                ArrayPool<byte>.Shared.Return(buffer);

                await file.DisposeAsync();
            }
        }

        public static string GetFileNameWithoutExtension(string filePath)
        {
            var slice = filePath.AsSpan();

            int i = slice.LastIndexOf('\\');
            if (i > 0)
                slice = slice.Slice(i + 1);

            i = slice.IndexOf('.');
            if (i > 0)
                slice = slice.Slice(0, i);

            return slice.ToString();
        }

        public static string GetStorageNameFromPath(string path)
        {
            //var path = $@"{HypervDefaults.ClusterStoragePath}\{storage}\Volumes\{name}.vhdx";

            var parts = path.Split('\\');
            for (int i = 1; i < parts.Length; i++)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(parts[i], "Volumes"))
                    return parts[i - 1];
            }
            return string.Empty;
        }

        public static string GetDiskFilter(Guid vhdId)
        {
            //scsi   360022480ef0695fc37490b1f60261042
            //scsi   360022480-ef0695fc-3749-0b1f60261042
            //vhdid  fc9506ef-4937-4123-a68e-0b1f60261042

            //Get-ChildItem -Path /dev/disk/by-id/scsi-*ef0695fc37490b1f60261042

            Span<byte> bytes = stackalloc byte[16];
            vhdId.TryWriteBytes(bytes);

            var sb = new StringBuilder(32);

            sb.Append("/dev/disk/by-id/scsi-*");

            var v = bytes.Slice(0, 4);
            //v.Reverse();
            sb.Append(Convert.ToHexString(v).ToLower());

            v = bytes.Slice(4, 2);
            //v.Reverse();
            sb.Append(Convert.ToHexString(v).ToLower());

            v = bytes.Slice(10);
            sb.Append(Convert.ToHexString(v).ToLower());

            return sb.ToString();
        }
    }
}
