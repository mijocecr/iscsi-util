using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ISCSI_Util.Helpers
{
    public static class NetworkHelper
    {
        public static List<string> ObtenerRedesLocales()
        {
            var redes = new List<string>();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                var ipProps = nic.GetIPProperties();

                foreach (var unicast in ipProps.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        string ip = unicast.Address.ToString();

                        // Extraer los primeros 3 octetos → "192.168.10"
                        int lastDot = ip.LastIndexOf('.');
                        if (lastDot > 0)
                        {
                            string red = ip.Substring(0, lastDot);
                            if (!redes.Contains(red))
                                redes.Add(red);
                        }
                    }
                }
            }

            return redes;
        }
    }
}