using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace RdpTabs
{
    /// <summary>Two sources of failure detail: our TCP pre-flight, and the control's extended disconnect reason.</summary>
    internal static class RdpErrors
    {
        /// <summary>
        /// Probe TCP ourselves before connecting so the most common failures (DNS, closed port, unreachable
        /// host) get an accurate message. Late binding cannot receive OnDisconnected, so the control can only
        /// give us an extended reason code, which carries little detail.
        /// Returns null when reachable -- and also when the probe itself fails, so a probe problem never blocks
        /// a connection attempt.
        /// </summary>
        public static string Preflight(string host, int port, int timeoutMs)
        {
            if (string.IsNullOrEmpty(host)) return "No host name was given.";

            IPAddress[] addresses;
            try
            {
                IPAddress literal;
                addresses = IPAddress.TryParse(host, out literal)
                    ? new[] { literal }
                    : Dns.GetHostAddresses(host);
            }
            catch (SocketException)
            {
                return "Cannot resolve host name \"" + host + "\". Check the spelling and DNS, or use an IP address.";
            }
            catch (ArgumentException)
            {
                return "Host name \"" + host + "\" is not valid.";
            }

            if (addresses == null || addresses.Length == 0)
                return "Cannot resolve host name \"" + host + "\" (no addresses returned).";

            try
            {
                using (TcpClient client = new TcpClient())
                {
                    IAsyncResult pending = client.BeginConnect(addresses, port, null, null);
                    if (!pending.AsyncWaitHandle.WaitOne(timeoutMs))
                    {
                        return string.Format(CultureInfo.CurrentCulture,
                            "Timed out connecting to {0}:{1} (no response within {2:0.#} s). Check that the machine is on, reachable, and that the firewall allows this port.",
                            host, port, timeoutMs / 1000.0);
                    }
                    client.EndConnect(pending);
                }
                return null;
            }
            catch (SocketException ex)
            {
                return DescribeSocketError(ex, host, port);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string DescribeSocketError(SocketException ex, string host, int port)
        {
            string target = host + ":" + port.ToString(CultureInfo.InvariantCulture);
            switch (ex.SocketErrorCode)
            {
                case SocketError.ConnectionRefused:
                    return target + " refused the connection. Remote Desktop may be disabled, or the port is wrong.";
                case SocketError.TimedOut:
                    return "Timed out connecting to " + target + ". Check that the machine is on and the firewall allows this port.";
                case SocketError.HostUnreachable:
                case SocketError.NetworkUnreachable:
                    return "Cannot reach " + target + " (network unreachable).";
                case SocketError.HostNotFound:
                case SocketError.NoData:
                    return "Cannot resolve host name \"" + host + "\".";
                case SocketError.AddressFamilyNotSupported:
                    return "This computer does not support that address family (" + target + ").";
                default:
                    return "Cannot connect to " + target + ": " + ex.Message +
                           " (socket error " + ex.SocketErrorCode + ").";
            }
        }

        /// <summary>Turns the control's ExtendedDisconnectReason into words. Unknown codes are reported as-is.</summary>
        public static string DescribeExtendedReason(int code)
        {
            string known = KnownExtendedReason(code);
            if (known != null) return known;
            if (code == 0) return "The connection was closed.";
            return "The connection was closed (extended reason code " + code.ToString(CultureInfo.InvariantCulture) + ").";
        }

        private static string KnownExtendedReason(int code)
        {
            switch (code)
            {
                case 0: return null; // the control gave no detail
                case 1: return "The connection was closed locally.";
                case 2: return "You signed out of the remote session.";
                case 3: return "The server disconnected the session after an idle timeout.";
                case 4: return "Sign-in timed out and the server closed the connection.";
                case 5: return "The session was taken over by another connection (the same account signed in elsewhere).";
                case 6: return "The server ran out of memory and closed the connection.";
                case 7: return "The server denied this connection.";
                case 8: return "The server denied this connection (FIPS encryption policy mismatch).";
                case 9: return "The account lacks permission to sign in remotely (it must be in the Remote Desktop Users group).";
                case 10: return "The server requires fresh credentials.";
                case 11: return "The connection was closed from the server side.";
                case 12: return "The user signed out.";

                // Remote Desktop licensing
                case 256: return "Remote Desktop licensing failed (internal error).";
                case 257: return "No Remote Desktop license server could be found.";
                case 258: return "No Remote Desktop licenses are available.";
                case 259: return "The licensing protocol failed (invalid client message).";
                case 260: return "The license does not match this machine's hardware ID.";
                case 261: return "The client license is invalid.";
                case 262: return "The licensing protocol could not be completed.";
                case 263: return "The client ended the licensing protocol.";
                case 264: return "Encryption failed during the licensing protocol.";
                case 265: return "The client license could not be upgraded.";
                case 266: return "The server does not allow remote connections (Remote Desktop licensing is not configured).";
                default: return null;
            }
        }
    }
}
