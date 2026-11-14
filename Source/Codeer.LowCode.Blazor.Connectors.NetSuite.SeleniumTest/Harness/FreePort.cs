using System.Net;
using System.Net.Sockets;

namespace Codeer.LowCode.Blazor.Connectors.NetSuite.SeleniumTest.Harness
{
    static class FreePort
    {
        public static int Get()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
