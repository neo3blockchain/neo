using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace Neo.Network
{
    public class UPnP
    {
        private static string _serviceUrl;

        public static TimeSpan TimeOut { get; set; } = TimeSpan.FromSeconds(3);

        public static bool Discover()
        {
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.ReceiveTimeout = (int)TimeOut.TotalMilliseconds;
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, 1);
                string req = "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "ST:upnp:rootdevice\r\n" +
                "MAN:\"ssdp:discover\"\r\n" +
                "MX:3\r\n\r\n";
                byte[] data = Encoding.ASCII.GetBytes(req);
                IPEndPoint ipe = new IPEndPoint(IPAddress.Broadcast, 1900);

                DateTime start = DateTime.Now;

                try
                {
                    s.SendTo(data, ipe);
                    s.SendTo(data, ipe);
                    s.SendTo(data, ipe);
                }
                catch
                {
                    return false;
                }

                byte[] buffer = new byte[0x1000];

                do
                {
                    int length;
                    try
                    {
                        length = s.Receive(buffer);

                        string resp = Encoding.ASCII.GetString(buffer, 0, length).ToLowerInvariant();
                        if (resp.Contains("upnp:rootdevice"))
                        {
                            resp = resp.Substring(resp.IndexOf("location:") + 9);
                            resp = resp.Substring(0, resp.IndexOf("\r")).Trim();
                            if (!string.IsNullOrEmpty(_serviceUrl = GetServiceUrl(resp)))
                            {
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
                while (DateTime.Now - start < TimeOut);

                return false;
            }
        }

        private static string GetServiceUrl(string resp)
        {
            try
            {
                XmlDocument desc = new XmlDocument();
                HttpWebRequest request = WebRequest.CreateHttp(resp);
                using (WebResponse response = request.GetResponse())
                {
                    desc.Load(response.GetResponseStream());
                }
                XmlNamespaceManager nsMgr = new XmlNamespaceManager(desc.NameTable);
                nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
                XmlNode typen = desc.SelectSingleNode("//tns:device/tns:deviceType/text()", nsMgr);
                if (!typen.Value.Contains("InternetGatewayDevice"))
                    return null;
                XmlNode node = desc.SelectSingleNode("//tns:service[contains(tns:serviceType,\"WANIPConnection\")]/tns:controlURL/text()", nsMgr);
                if (node == null)
                    return null;
                XmlNode eventnode = desc.SelectSingleNode("//tns:service[contains(tns:serviceType,\"WANIPConnection\")]/tns:eventSubURL/text()", nsMgr);
                return CombineUrls(resp, node.Value);
            }
            catch { return null; }
        }

        private static string CombineUrls(string resp, string p)
        {
            int n = resp.IndexOf("://");
            n = resp.IndexOf('/', n + 3);
            return resp.Substring(0, n) + p;
        }

        public static void ForwardPort(int port, ProtocolType protocol, string description)
        {
            if (string.IsNullOrEmpty(_serviceUrl))
                throw new Exception("No UPnP service available or Discover() has not been called");
            XmlDocument xdoc = SOAPRequest(_serviceUrl, "<u:AddPortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + port.ToString() + "</NewExternalPort><NewProtocol>" + protocol.ToString().ToUpper() + "</NewProtocol>" +
                "<NewInternalPort>" + port.ToString() + "</NewInternalPort><NewInternalClient>" + Dns.GetHostAddresses(Dns.GetHostName()).First(p => p.AddressFamily == AddressFamily.InterNetwork).ToString() +
                "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>" + description +
            "</NewPortMappingDescription><NewLeaseDuration>0</NewLeaseDuration></u:AddPortMapping>", "AddPortMapping");
        }

        public static void DeleteForwardingRule(int port, ProtocolType protocol)
        {
            if (string.IsNullOrEmpty(_serviceUrl))
                throw new Exception("No UPnP service available or Discover() has not been called");
            XmlDocument xdoc = SOAPRequest(_serviceUrl,
            "<u:DeletePortMapping xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
            "<NewRemoteHost>" +
            "</NewRemoteHost>" +
            "<NewExternalPort>" + port + "</NewExternalPort>" +
            "<NewProtocol>" + protocol.ToString().ToUpper() + "</NewProtocol>" +
            "</u:DeletePortMapping>", "DeletePortMapping");
        }

        public static IPAddress GetExternalIP()
        {
            if (string.IsNullOrEmpty(_serviceUrl))
                throw new Exception("No UPnP service available or Discover() has not been called");
            XmlDocument xdoc = SOAPRequest(_serviceUrl, "<u:GetExternalIPAddress xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
            "</u:GetExternalIPAddress>", "GetExternalIPAddress");
            XmlNamespaceManager nsMgr = new XmlNamespaceManager(xdoc.NameTable);
            nsMgr.AddNamespace("tns", "urn:schemas-upnp-org:device-1-0");
            string IP = xdoc.SelectSingleNode("//NewExternalIPAddress/text()", nsMgr).Value;
            return IPAddress.Parse(IP);
        }

        private static XmlDocument SOAPRequest(string url, string soap, string function)
        {
            string req = "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            soap +
            "</s:Body>" +
            "</s:Envelope>";
            HttpWebRequest r = WebRequest.CreateHttp(url);
            r.Method = "POST";
            byte[] b = Encoding.UTF8.GetBytes(req);
            r.Headers["SOAPACTION"] = "\"urn:schemas-upnp-org:service:WANIPConnection:1#" + function + "\"";
            r.ContentType = "text/xml; charset=\"utf-8\"";
            using (Stream reqs = r.GetRequestStream())
            {
                reqs.Write(b, 0, b.Length);
                XmlDocument resp = new XmlDocument();
                WebResponse wres = r.GetResponse();
                using (Stream ress = wres.GetResponseStream())
                {
                    resp.Load(ress);
                    return resp;
                }
            }
        }
    }
}
