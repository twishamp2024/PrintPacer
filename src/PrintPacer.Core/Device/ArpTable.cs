using System.Net;
using System.Runtime.InteropServices;

namespace PrintPacer.Core.Device;

/// <summary>
/// Reads the Windows IPv4 neighbour (ARP) cache.
///
/// Used to turn a printer's hardware address into the IPv4 address it is currently using.
/// This only reads entries Windows has already learned from talking to the device; it sends
/// nothing and scans nothing.
///
/// The older GetIpNetTable is used in preference to GetIpNetTable2 because its row layout is
/// a handful of fixed-size fields with no unions, which makes the marshalling unambiguous.
/// </summary>
internal static class ArpTable
{
    private const int MaxPhysAddrLength = 8;
    private const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPNETROW
    {
        public uint Index;
        public uint PhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxPhysAddrLength)]
        public byte[] PhysAddr;
        public uint Addr;
        public uint Type;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddress, ref uint macLength);

    /// <summary>
    /// Asks the network stack for the hardware address behind an IPv4 address, resolving it
    /// if it is not already cached. Used to tell two discovered devices apart when we know
    /// which hardware address the printer should have.
    /// </summary>
    public static byte[]? ResolveMac(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;

        try
        {
            var destination = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
            var mac = new byte[6];
            var length = (uint)mac.Length;

            return SendARP(destination, 0, mac, ref length) == 0 && length == 6 ? mac : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool SameMac(byte[]? a, byte[]? b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Returns the IPv4 address currently associated with a hardware address, if any.</summary>
    public static IPAddress? FindIPv4(byte[] macAddress)
    {
        if (macAddress.Length != 6) return null;

        var size = 0;
        var result = GetIpNetTable(IntPtr.Zero, ref size, false);
        if (result != ErrorInsufficientBuffer || size <= 0) return null;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0) return null;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MIB_IPNETROW>();

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_IPNETROW>(buffer + 4 + i * rowSize);
                if (row.PhysAddrLen != 6 || row.PhysAddr is null) continue;

                var matches = true;
                for (var b = 0; b < 6; b++)
                {
                    if (row.PhysAddr[b] == macAddress[b]) continue;
                    matches = false;
                    break;
                }

                if (matches) return new IPAddress(BitConverter.GetBytes(row.Addr));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return null;
    }

    /// <summary>
    /// Recovers the hardware address embedded in an IPv6 link-local address formed by the
    /// modified EUI-64 rule: the 48-bit MAC is split, <c>FF FE</c> is inserted in the
    /// middle, and bit 1 of the first byte is inverted. Reversing that yields the MAC.
    ///
    /// This matters because Windows records a WSD printer's location as exactly such an
    /// address, and the printer's management interface is reachable only over IPv4.
    /// </summary>
    public static byte[]? MacFromLinkLocalIPv6(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return null;
        if (!address.IsIPv6LinkLocal) return null;

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 16) return null;

        // The interface identifier occupies the low 64 bits; FF FE must sit in its middle.
        if (bytes[11] != 0xFF || bytes[12] != 0xFE) return null;

        return new[]
        {
            (byte)(bytes[8] ^ 0x02),
            bytes[9],
            bytes[10],
            bytes[13],
            bytes[14],
            bytes[15],
        };
    }
}
