using System.Runtime.InteropServices;

namespace RestaurantWiFiGateway;

/// <summary>
/// Explicitly installed durable IPv4 forwarding DENY, scoped to discovered WAN/AP pair.
/// Not a production policy: IPv6 is not enforced, voucher permits are not integrated.
/// Never invoke installation automatically; use only in an isolated lab.
/// </summary>
internal static class PersistentIpv4Guard
{
    const uint AuthWinnt = 10, Persistent = 0x1, Block = 0x1001;
    static readonly Guid ForwardV4 = new("a82acc24-4ee1-4ee1-b465-fd1d25cb10a4");
    static readonly Guid SourceIf = new("2311334d-c92d-45bf-9496-edf447820e2d");
    static readonly Guid DestinationIf = new("35cf6522-4139-45ee-a0d5-67b80949d879");

    // Stable IDs make the policy manageable after process termination/reboot.
    static Guid Subkey => new("19e93347-e6d2-4ae0-813a-5c8f6f248a03");
    static Guid ForwardKey => new("9b3920ad-6628-438b-bb91-9f0a8d4b9293");
    static Guid ReverseKey => new("6648f979-9256-4a67-ae42-2310ab2b22f7");

    public static void Install(int upstream, int downstream)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (upstream <= 0 || downstream <= 0 || upstream == downstream)
            throw new ArgumentException("Distinct positive interface indices required.");
        // Refuse indexes that do not match the discovered physical adapter pair.
        var discovered = RestaurantWiFiNetworking.NetworkDiscovery.Discover();
        var wan = discovered.Wan;
        var ap = discovered.AccessPoints.SingleOrDefault(a => a.InterfaceIndex == downstream);
        if (wan is null || wan.InterfaceIndex != upstream || ap is null)
            throw new InvalidOperationException("Selected WFP interfaces do not match Windows routing.");
        var safe = RestaurantWiFiNetworking.PersistentGuardTopology.Validate(
            new RestaurantWiFiNetworking.PersistentGuardTopology.Selection(
                upstream, downstream, ap.Ipv4, ap.Mask, wan.Ipv4));
        if (!safe.SafeToStage)
            throw new InvalidOperationException("Unsafe interface scope: " + safe.Reason);

        var engine = Open();
        try
        {
            Check(FwpmTransactionBegin0(engine, 0), "persistent guard transaction begin");
            try
            {
                var subkey = Subkey;
                WithDisplay("Restaurant WiFi persistent IPv4 guard", name =>
                {
                    var sub = new Sublayer
                    {
                        key = subkey, display = new Display { name = name },
                        flags = Persistent, weight = 0x7000
                    };
                    Check(FwpmSubLayerAdd0(engine, ref sub, IntPtr.Zero),
                        "persistent guard sublayer create");
                });
                AddBlock(engine, ForwardKey, subkey, downstream, upstream,
                    "Restaurant WiFi IPv4 client to internet block");
                AddBlock(engine, ReverseKey, subkey, upstream, downstream,
                    "Restaurant WiFi IPv4 internet to client block");
                Check(FwpmTransactionCommit0(engine), "persistent guard commit");
            }
            catch
            {
                FwpmTransactionAbort0(engine);
                throw;
            }
        }
        finally { FwpmEngineClose0(engine); }
    }

    public static void Remove()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var engine = Open();
        try
        {
            Check(FwpmTransactionBegin0(engine, 0), "persistent guard removal begin");
            try
            {
                var f = ForwardKey; var r = ReverseKey; var sub = Subkey;
                // WFP not-found is safe for removal, including partially installed policies.
                CheckAbsentOkay(FwpmFilterDeleteByKey0(engine, ref f), "forward filter remove");
                CheckAbsentOkay(FwpmFilterDeleteByKey0(engine, ref r), "reverse filter remove");
                CheckAbsentOkay(FwpmSubLayerDeleteByKey0(engine, ref sub), "sublayer remove");
                Check(FwpmTransactionCommit0(engine), "persistent guard removal commit");
            }
            catch
            {
                FwpmTransactionAbort0(engine);
                throw;
            }
        }
        finally { FwpmEngineClose0(engine); }
    }

    static void CheckAbsentOkay(uint rc, string operation)
    {
        // FWP_E_FILTER_NOT_FOUND and FWP_E_SUBLAYER_NOT_FOUND
        if (rc != 0 && rc != 0x80320003 && rc != 0x80320007)
            Check(rc, operation);
    }

    static void AddBlock(IntPtr engine, Guid key, Guid sublayer,
        uint source, uint destination, string caption)
    {
        var conditions = new[] { Condition(SourceIf, source), Condition(DestinationIf, destination) };
        var length = Marshal.SizeOf<FilterCondition>();
        var ptr = Marshal.AllocHGlobal(length * conditions.Length);
        try
        {
            for (var i = 0; i < conditions.Length; i++)
                Marshal.StructureToPtr(conditions[i], IntPtr.Add(ptr, i * length), false);
            WithDisplay(caption, name =>
            {
                var filter = new Filter
                {
                    key = key, display = new Display { name = name },
                    flags = Persistent, layer = ForwardV4, sublayer = sublayer,
                    weight = new Value { type = 1, value = new Union { byteValue = 1 } },
                    count = (uint)conditions.Length, conditions = ptr,
                    action = new Action { type = Block }
                };
                Check(FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _),
                    "persistent guard filter add");
            });
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    static IntPtr Open()
    {
        Check(FwpmEngineOpen0(null, AuthWinnt, IntPtr.Zero, IntPtr.Zero, out var h),
            "WFP persistent probe engine open");
        return h;
    }

    static void Check(uint rc, string operation)
    {
        if (rc != 0) throw new InvalidOperationException(
            $"{operation} failed: 0x{rc:X8}");
    }

    static void WithDisplay(string display, System.Action<IntPtr> body)
    {
        var ptr = Marshal.StringToHGlobalUni(display);
        try { body(ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    static FilterCondition Condition(Guid field, uint val) => new()
    {
        field = field, match = 0,
        value = new Value { type = 3, value = new Union { uintValue = val } }
    };

    [StructLayout(LayoutKind.Sequential)] struct Display { public IntPtr name, description; }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public uint size; public IntPtr data; }
    [StructLayout(LayoutKind.Explicit)] struct Union
    {
        [FieldOffset(0)] public byte byteValue;
        [FieldOffset(0)] public uint uintValue;
        [FieldOffset(0)] public IntPtr pointer;
    }
    [StructLayout(LayoutKind.Sequential)] struct Value { public int type; public Union value; }
    [StructLayout(LayoutKind.Sequential)] struct FilterCondition
    {
        public Guid field; public int match; public Value value;
    }
    [StructLayout(LayoutKind.Sequential)] struct Action { public uint type; public Guid callout; }
    [StructLayout(LayoutKind.Explicit)] struct Context
    {
        [FieldOffset(0)] public ulong raw;
        [FieldOffset(0)] public Guid providerContext;
    }
    [StructLayout(LayoutKind.Sequential)] struct Sublayer
    {
        public Guid key; public Display display; public uint flags;
        public IntPtr provider; public Blob data; public ushort weight;
    }
    [StructLayout(LayoutKind.Sequential)] struct Filter
    {
        public Guid key; public Display display; public uint flags;
        public IntPtr provider; public Blob data; public Guid layer, sublayer;
        public Value weight; public uint count; public IntPtr conditions;
        public Action action; public Context context; public IntPtr reserved;
        public ulong id; public Value effectiveWeight;
    }
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint FwpmEngineOpen0(string? server, uint auth, IntPtr identity,
        IntPtr session, out IntPtr engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    static extern uint FwpmTransactionAbort0(IntPtr engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    static extern uint FwpmSubLayerAdd0(IntPtr engine, ref Sublayer value, IntPtr sd);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter value, IntPtr sd, out ulong id);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    static extern uint FwpmFilterGetByKey0(IntPtr engine, ref Guid key, out IntPtr value);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    static extern uint FwpmFilterDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)]
    static extern void FwpmFreeMemory0(ref IntPtr p);
}
