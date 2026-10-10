using System.Net;
using RestaurantWiFiNetworking;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RestaurantWiFiGateway;

internal sealed class NetworkAdapterSnapshot
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public int InterfaceIndex { get; init; }
    public string Type { get; init; } = "";
    public string Ipv4 { get; init; } = "";
    public string Mask { get; init; } = "";
    public string Subnet { get; init; } = "";
    public string Gateway { get; init; } = "";
    public long SpeedMbps { get; init; }
    public bool IsUp { get; init; }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(Ipv4, out var local) ||
            !IPAddress.TryParse(Mask, out var mask))
            return false;

        var a = address.GetAddressBytes();
        var l = local.GetAddressBytes();
        var m = mask.GetAddressBytes();
        for (var i = 0; i < 4; i++)
            if ((a[i] & m[i]) != (l[i] & m[i]))
                return false;
        return true;
    }
}

internal sealed class NetworkTopologySnapshot
{
    public NetworkAdapterSnapshot? Wan { get; init; }
    public List<NetworkAdapterSnapshot> AccessPoints { get; init; } = new();
    public DateTimeOffset DetectedAt { get; init; } = DateTimeOffset.UtcNow;
}

internal static class NetworkDiscovery
{
    public static NetworkTopologySnapshot Discover()
    {
        var all = new List<NetworkAdapterSnapshot>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch { continue; }

            var ipv4 = props.UnicastAddresses
                .FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork &&
                                     !IPAddress.IsLoopback(x.Address));
            if (ipv4 is null) continue;

            var index = props.GetIPv4Properties()?.Index ?? -1;
            if (index <= 0) continue;

            var mask = ipv4.IPv4Mask?.ToString() ?? "255.255.255.0";
            var gateway = props.GatewayAddresses
                .Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork &&
                                     !x.Equals(IPAddress.Any))?.ToString() ?? "";

            all.Add(new NetworkAdapterSnapshot
            {
                Name = nic.Name,
                Description = nic.Description,
                InterfaceIndex = index,
                Type = nic.NetworkInterfaceType.ToString(),
                Ipv4 = ipv4.Address.ToString(),
                Mask = mask,
                Subnet = BuildSubnet(ipv4.Address, ipv4.IPv4Mask),
                Gateway = gateway,
                SpeedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000 : 0,
                IsUp = nic.OperationalStatus == OperationalStatus.Up
            });
        }

        var bestIndex = GetBestInternetInterface();
        var wan = all.FirstOrDefault(x => x.InterfaceIndex == bestIndex && x.IsUp)
                  ?? all.FirstOrDefault(x => x.IsUp && !string.IsNullOrWhiteSpace(x.Gateway));

        var aps = all
            .Where(x => x.IsUp &&
                        (wan is null || x.InterfaceIndex != wan.InterfaceIndex) &&
                        IsPrivateIpv4(x.Ipv4) &&
                        string.IsNullOrWhiteSpace(x.Gateway))
            .OrderByDescending(x => x.Ipv4 == "192.168.137.1")
            .ThenBy(x => x.InterfaceIndex)
            .ToList();

        return new NetworkTopologySnapshot { Wan = wan, AccessPoints = aps };
    }

    static int GetBestInternetInterface()
    {
        try
        {
            // 1.1.1.1 keeps byte-order irrelevant here because all octets are identical.
            var dest = BitConverter.ToUInt32(new byte[] { 1, 1, 1, 1 }, 0);
            return GetBestInterface(dest, out var index) == 0 ? unchecked((int)index) : -1;
        }
        catch { return -1; }
    }

    static bool IsPrivateIpv4(string value)
    {
        if (!IPAddress.TryParse(value, out var ip)) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }

    static string BuildSubnet(IPAddress address, IPAddress? mask)
    {
        if (mask is null) return address + "/24";
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var n = new byte[4];
        var prefix = 0;
        for (var i = 0; i < 4; i++)
        {
            n[i] = (byte)(a[i] & m[i]);
            prefix += CountBits(m[i]);
        }
        return new IPAddress(n) + "/" + prefix;
    }

    static int CountBits(byte value)
    {
        var c = 0;
        while (value != 0)
        {
            c += value & 1;
            value >>= 1;
        }
        return c;
    }

    [DllImport("iphlpapi.dll", SetLastError = false)]
    static extern uint GetBestInterface(uint dwDestAddr, out uint pdwBestIfIndex);
}

internal sealed class ExperimentalWfpForwardGate : IDisposable
{
    // Each dynamic WFP session owns its own sublayer. A fixed global key
    // collides (FWP_E_ALREADY_EXISTS) when two gateway/test processes overlap.
    // Random session keys avoid stale/parallel session conflicts without
    // installing any persistent WFP objects.
    readonly Guid subLayerKey = Guid.NewGuid();
    static readonly Guid LayerIpForwardV4 = new("a82acc24-4ee1-4ee1-b465-fd1d25cb10a4");
    static readonly Guid ConditionSourceInterfaceIndex = new("2311334d-c92d-45bf-9496-edf447820e2d");
    static readonly Guid ConditionDestinationInterfaceIndex = new("35cf6522-4139-45ee-a0d5-67b80949d879");
    static readonly Guid ConditionIpSourceAddress = new("ae96897e-2e94-4bc9-b313-b27ee80e574d");

    const uint RpcCAuthnWinnt = 10;
    const uint FwpmSessionFlagDynamic = 0x00000001;
    const uint FwpActionBlock = 0x00001001;
    const uint FwpActionPermit = 0x00001002;

    IntPtr engine;
    readonly List<ulong> filterIds = new();
    string fingerprint = "";

    public int InstalledFilterCount => filterIds.Count;

    public bool Active { get; private set; }
    public string State { get; private set; } = "initializing";
    public string LastError { get; private set; } = "";

    public void Apply(NetworkTopologySnapshot topology, IEnumerable<IPAddress> authorizedAddresses)
    {
        var wan = topology.Wan;
        var aps = topology.AccessPoints;
        if (wan is null || aps.Count == 0)
        {
            if (filterIds.Count > 0) ClearFilters();
            Active = false;
            State = wan is null ? "waiting-for-internet-line" : "waiting-for-access-point-line";
            fingerprint = "";
            return;
        }

        var authorized = authorizedAddresses
            .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
            .DistinctBy(x => x.ToString())
            .OrderBy(x => x.ToString(), StringComparer.Ordinal)
            .ToList();

        var nextFingerprint = $"{wan.InterfaceIndex}|{string.Join(",", aps.Select(x => x.InterfaceIndex).Order())}|{string.Join(",", authorized.Select(x => x))}";
        if (nextFingerprint == fingerprint && Active) return;

        EnsureOpen();

        var newIds = new List<ulong>();
        var begin = NativeMethods.FwpmTransactionBegin0(engine, 0);
        if (begin != 0) throw new InvalidOperationException($"WFP transaction begin failed: 0x{begin:X8}");

        try
        {
            foreach (var id in filterIds)
            {
                var rc = NativeMethods.FwpmFilterDeleteById0(engine, id);
                if (rc != 0) throw new InvalidOperationException($"WFP filter delete failed: 0x{rc:X8}");
            }

            foreach (var ap in aps)
            {
                var allowedOnThisInterface = authorized.Where(ap.Contains).ToArray();
                // IMPORTANT: BLOCK overrides a matching PERMIT in WFP. Merely
                // assigning a higher weight to the per-IP permit is NOT enough.
                // After granting an address, replace the blanket BLOCK with a
                // set of source CIDRs covering ALL OTHER IPv4 addresses.
                // As a result no BLOCK condition matches an authorized client.
                var denyPrefixes = Ipv4TrialRulePlanner.DenyAllExcept(allowedOnThisInterface);
                if (allowedOnThisInterface.Length == 0)
                {
                    // Small, initial deny policy when there are no exceptions.
                    newIds.Add(AddFilter(
                        $"Restaurant WiFi default deny {ap.InterfaceIndex}->{wan.InterfaceIndex}",
                        FwpActionBlock,
                        weightClass: 1,
                        new[]
                        {
                            Condition(ConditionSourceInterfaceIndex, FwpMatchType.Equal, unchecked((uint)ap.InterfaceIndex)),
                            Condition(ConditionDestinationInterfaceIndex, FwpMatchType.Equal, unchecked((uint)wan.InterfaceIndex))
                        }));
                }
                else
                {
                    foreach (var prefix in denyPrefixes)
                        newIds.Add(AddPrefixDenyFilter(ap.InterfaceIndex, wan.InterfaceIndex, prefix));
                }

                foreach (var ip in allowedOnThisInterface)
                {
                    newIds.Add(AddFilter(
                        $"Restaurant WiFi permit {ip} {ap.InterfaceIndex}->{wan.InterfaceIndex}",
                        FwpActionPermit,
                        weightClass: 15,
                        new[]
                        {
                            Condition(ConditionSourceInterfaceIndex, FwpMatchType.Equal, unchecked((uint)ap.InterfaceIndex)),
                            Condition(ConditionDestinationInterfaceIndex, FwpMatchType.Equal, unchecked((uint)wan.InterfaceIndex)),
                            Condition(ConditionIpSourceAddress, FwpMatchType.Equal, Ipv4ToWfpUInt32(ip))
                        }));
                }
            }

            var commit = NativeMethods.FwpmTransactionCommit0(engine);
            if (commit != 0) throw new InvalidOperationException($"WFP transaction commit failed: 0x{commit:X8}");

            filterIds.Clear();
            filterIds.AddRange(newIds);
            fingerprint = nextFingerprint;
            Active = true;
            State = "default-deny-active";
            LastError = "";
        }
        catch
        {
            NativeMethods.FwpmTransactionAbort0(engine);
            throw;
        }
    }

    public void ReportFailure(Exception ex)
    {
        Active = false;
        State = "wfp-error";
        LastError = ex.Message;
    }

    void EnsureOpen()
    {
        if (engine != IntPtr.Zero) return;

        var sessionName = Marshal.StringToHGlobalUni("Restaurant WiFi Gateway");
        var sessionDescription = Marshal.StringToHGlobalUni("Dynamic WFP session for captive portal default-deny forwarding.");
        try
        {
            var session = new FwpmSession0
            {
                displayData = new FwpmDisplayData0 { name = sessionName, description = sessionDescription },
                flags = FwpmSessionFlagDynamic,
                txnWaitTimeoutInMSec = 5_000
            };

            var rc = NativeMethods.FwpmEngineOpen0(null, RpcCAuthnWinnt, IntPtr.Zero, ref session, out engine);
            if (rc != 0 || engine == IntPtr.Zero)
                throw new InvalidOperationException($"WFP engine open failed: 0x{rc:X8}");

            var subName = Marshal.StringToHGlobalUni("Restaurant WiFi Gate");
            var subDescription = Marshal.StringToHGlobalUni("Captive portal forwarding gate.");
            try
            {
                var subLayer = new FwpmSublayer0
                {
                    subLayerKey = subLayerKey,
                    displayData = new FwpmDisplayData0 { name = subName, description = subDescription },
                    weight = 0x7000
                };
                rc = NativeMethods.FwpmSubLayerAdd0(engine, ref subLayer, IntPtr.Zero);
                if (rc != 0)
                    throw new InvalidOperationException($"WFP sublayer add failed: 0x{rc:X8}");
            }
            finally
            {
                Marshal.FreeHGlobal(subName);
                Marshal.FreeHGlobal(subDescription);
            }
        }
        catch
        {
            if (engine != IntPtr.Zero)
            {
                NativeMethods.FwpmEngineClose0(engine);
                engine = IntPtr.Zero;
            }
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(sessionName);
            Marshal.FreeHGlobal(sessionDescription);
        }
    }

    // WFP condition values holding a v4 address/mask use a pointer to the
    // unmanaged FWP_V4_ADDR_AND_MASK structure. fwpmclnt copies that value
    // synchronously during FwpmFilterAdd0. Keep it alive until that call ends.
    ulong AddPrefixDenyFilter(int inbound, int outbound,
        Ipv4TrialRulePlanner.Ipv4Prefix prefix)
    {
        var maskValue = Marshal.AllocHGlobal(Marshal.SizeOf<FwpV4AddrAndMask>());
        try
        {
            Marshal.StructureToPtr(new FwpV4AddrAndMask
            {
                address = prefix.Network,
                mask = prefix.Mask
            }, maskValue, false);
            return AddFilter(
                $"Restaurant WiFi deny {prefix} {inbound}->{outbound}",
                FwpActionBlock,
                weightClass: 1,
                new[]
                {
                    Condition(ConditionSourceInterfaceIndex, FwpMatchType.Equal, unchecked((uint)inbound)),
                    Condition(ConditionDestinationInterfaceIndex, FwpMatchType.Equal, unchecked((uint)outbound)),
                    new FwpmFilterCondition0
                    {
                        fieldKey = ConditionIpSourceAddress,
                        matchType = FwpMatchType.Equal,
                        conditionValue = new FwpConditionValue0
                        {
                            type = FwpDataType.V4AddrMask,
                            value = new FwpValueUnion { pointer = maskValue }
                        }
                    }
                });
        }
        finally { Marshal.FreeHGlobal(maskValue); }
    }

    ulong AddFilter(string name, uint actionType, byte weightClass, FwpmFilterCondition0[] conditions)
    {
        var conditionSize = Marshal.SizeOf<FwpmFilterCondition0>();
        var conditionsPtr = Marshal.AllocHGlobal(conditionSize * conditions.Length);
        var namePtr = Marshal.StringToHGlobalUni(name);
        var descriptionPtr = Marshal.StringToHGlobalUni("Restaurant WiFi captive portal network policy");
        try
        {
            for (var i = 0; i < conditions.Length; i++)
                Marshal.StructureToPtr(conditions[i], IntPtr.Add(conditionsPtr, i * conditionSize), false);

            var filter = new FwpmFilter0
            {
                filterKey = Guid.NewGuid(),
                displayData = new FwpmDisplayData0 { name = namePtr, description = descriptionPtr },
                layerKey = LayerIpForwardV4,
                subLayerKey = subLayerKey,
                weight = new FwpValue0
                {
                    type = FwpDataType.Uint8,
                    value = new FwpValueUnion { uint8 = weightClass }
                },
                numFilterConditions = unchecked((uint)conditions.Length),
                filterCondition = conditionsPtr,
                action = new FwpmAction0 { type = actionType }
            };

            var rc = NativeMethods.FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out var id);
            if (rc != 0)
                throw new InvalidOperationException($"WFP filter add failed ({name}): 0x{rc:X8}");
            return id;
        }
        finally
        {
            Marshal.FreeHGlobal(conditionsPtr);
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(descriptionPtr);
        }
    }

    static FwpmFilterCondition0 Condition(Guid key, FwpMatchType matchType, uint value) =>
        new()
        {
            fieldKey = key,
            matchType = matchType,
            conditionValue = new FwpConditionValue0
            {
                type = FwpDataType.Uint32,
                value = new FwpValueUnion { uint32 = value }
            }
        };

    static uint Ipv4ToWfpUInt32(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        // WFP IPv4 UINT32 filter values use the address value in network byte order.
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    void ClearFilters()
    {
        if (engine == IntPtr.Zero)
        {
            filterIds.Clear();
            return;
        }

        var begin = NativeMethods.FwpmTransactionBegin0(engine, 0);
        if (begin != 0) return;
        try
        {
            foreach (var id in filterIds)
                NativeMethods.FwpmFilterDeleteById0(engine, id);
            NativeMethods.FwpmTransactionCommit0(engine);
            filterIds.Clear();
            fingerprint = "";
        }
        catch
        {
            NativeMethods.FwpmTransactionAbort0(engine);
        }
    }

    public void Dispose()
    {
        if (engine != IntPtr.Zero)
        {
            NativeMethods.FwpmEngineClose0(engine);
            engine = IntPtr.Zero;
        }
        filterIds.Clear();
        Active = false;
    }

    enum FwpDataType : int
    {
        Empty = 0,
        Uint8 = 1,
        Uint16 = 2,
        Uint32 = 3,
        Uint64 = 4,
        // FWP_V4_ADDR_MASK is a pointer type, not a UINT32 filter value.
        V4AddrMask = 0x100
    }

    enum FwpMatchType : int
    {
        Equal = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FwpmDisplayData0
    {
        public IntPtr name;
        public IntPtr description;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FwpByteBlob
    {
        public uint size;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct FwpValueUnion
    {
        [FieldOffset(0)] public byte uint8;
        [FieldOffset(0)] public ushort uint16;
        [FieldOffset(0)] public uint uint32;
        [FieldOffset(0)] public IntPtr uint64;
        [FieldOffset(0)] public IntPtr pointer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FwpV4AddrAndMask
    {
        public uint address;
        public uint mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FwpValue0
    {
        public FwpDataType type;
        public FwpValueUnion value;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FwpConditionValue0
    {
        public FwpDataType type;
        public FwpValueUnion value;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FwpmFilterCondition0
    {
        public Guid fieldKey;
        public FwpMatchType matchType;
        public FwpConditionValue0 conditionValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FwpmAction0
    {
        public uint type;
        public Guid filterType;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct FwpmContextUnion
    {
        [FieldOffset(0)] public ulong rawContext;
        [FieldOffset(0)] public Guid providerContextKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FwpmFilter0
    {
        public Guid filterKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FwpByteBlob providerData;
        public Guid layerKey;
        public Guid subLayerKey;
        public FwpValue0 weight;
        public uint numFilterConditions;
        public IntPtr filterCondition;
        public FwpmAction0 action;
        public FwpmContextUnion context;
        public IntPtr reserved;
        public ulong filterId;
        public FwpValue0 effectiveWeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FwpmSublayer0
    {
        public Guid subLayerKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public IntPtr providerKey;
        public FwpByteBlob providerData;
        public ushort weight;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FwpmSession0
    {
        public Guid sessionKey;
        public FwpmDisplayData0 displayData;
        public uint flags;
        public uint txnWaitTimeoutInMSec;
        public uint processId;
        public IntPtr sid;
        public IntPtr username;
        [MarshalAs(UnmanagedType.Bool)]
        public bool kernelMode;
    }

    static class NativeMethods
    {
        [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint FwpmEngineOpen0(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            uint authnService,
            IntPtr authIdentity,
            ref FwpmSession0 session,
            out IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", ExactSpelling = true)]
        internal static extern uint FwpmEngineClose0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", ExactSpelling = true)]
        internal static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, ref FwpmSublayer0 subLayer, IntPtr securityDescriptor);

        [DllImport("fwpuclnt.dll", ExactSpelling = true)]
        internal static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

        [DllImport("fwpuclnt.dll", ExactSpelling = true)]
        internal static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", ExactSpelling = true)]
        internal static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", ExactSpelling = true)]
        internal static extern uint FwpmFilterAdd0(IntPtr engineHandle, ref FwpmFilter0 filter, IntPtr securityDescriptor, out ulong id);

        [DllImport("fwpuclnt.dll", ExactSpelling = true)]
        internal static extern uint FwpmFilterDeleteById0(IntPtr engineHandle, ulong id);
    }
}
