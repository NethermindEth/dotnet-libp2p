using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace Multiformats.Address.Net
{
    public static class MultiaddressTools
    {
        public static IEnumerable<Multiaddress> GetInterfaceMultiaddresses()
        {
#if __MonoCS__
            return Array.Empty<Multiaddress>();
#else
            return NetworkInterface
                .GetAllNetworkInterfaces()
                .SelectMany(MultiaddressExtensions.GetMultiaddresses);
#endif
        }
    }
}
