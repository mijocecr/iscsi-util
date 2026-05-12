using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ISCSI_Util.Services;

namespace ISCSI_Util.Helpers
{
    public static class NetworkHelper
    {
        public static List<string> ObtenerRedesLocales()
        {
            LogService.Debug("NetworkHelper → ObtenerRedesLocales()");

            var redes = new List<string>();

            try
            {
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

                            int lastDot = ip.LastIndexOf('.');
                            if (lastDot > 0)
                            {
                                string red = ip.Substring(0, lastDot);

                                if (!redes.Contains(red))
                                {
                                    redes.Add(red);
                                    LogService.Debug($"NetworkHelper: red detectada → {red}");
                                }
                            }
                        }
                    }
                }

                LogService.Debug($"NetworkHelper ← Total redes detectadas: {redes.Count}");
            }
            catch (System.Exception ex)
            {
                LogService.Error($"NetworkHelper error: {ex.Message}");
            }

            return redes;
        }
    }
}