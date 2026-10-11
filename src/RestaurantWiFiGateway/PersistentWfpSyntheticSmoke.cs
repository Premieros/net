using System.Runtime.InteropServices;

namespace RestaurantWiFiGateway;

/// <summary>
/// Native persistent WFP lifecycle probe restricted to synthetic interface indices.
/// This is an explicit CI diagnostic, NEVER a production policy installer.
/// The persisted filter is deleted in finally, even after reopen/check failures.
/// </summary>
internal static class PersistentWfpSyntheticSmoke
{
    const uint AuthWinnt = 10, Persistent = 0x1, Block = 0x1001;
    static readonly Guid ForwardV4 = new("a82acc24-4ee1-4ee1-b465-fd1d25cb10a4");
    static readonly Guid SourceIf = new("2311334d-c92d-45bf-9496-edf447820e2d");
    static readonly Guid DestinationIf = new("35cf6522-4139-45ee-a0d5-67b80949d879");

    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        const uint inbound = int.MaxValue - 101, outbound = int.MaxValue - 102;
        var used = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Select(n => n.GetIPProperties().GetIPv4Properties()?.Index ?? -1).ToHashSet();
        if (used.Contains((int)inbound) || used.Contains((int)outbound))
            throw new InvalidOperationException("Synthetic NIC indices already exist");

        var subkey = Guid.NewGuid();
        var filterkey = Guid.NewGuid();
        IntPtr engine = IntPtr.Zero;
        try
        {
            engine = Open();
            WithDisplay("Restaurant WiFi synthetic persistent guard test", name =>
            {
                var sub = new Sublayer
                {
                    key = subkey, display = new Display { name = name },
                    flags = Persistent, weight = 0x7000
                };
                Check(FwpmSubLayerAdd0(engine, ref sub, IntPtr.Zero), "persistent sublayer create");
            });
            var conditions = new[]
            {
                Condition(SourceIf, inbound),
                Condition(DestinationIf, outbound)
            };
            var length = Marshal.SizeOf<FilterCondition>();
            var ptr = Marshal.AllocHGlobal(length * conditions.Length);
            try
            {
                for (var i = 0; i < conditions.Length; i++)
                    Marshal.StructureToPtr(conditions[i], IntPtr.Add(ptr, i * length), false);
                WithDisplay("Restaurant WiFi synthetic persistent deny", name =>
                {
                    var filter = new Filter
                    {
                        key = filterkey, display = new Display { name = name },
                        flags = Persistent, layer = ForwardV4, sublayer = subkey,
                        weight = new Value { type = 1, value = new Union { byteValue = 1 } },
                        count = (uint)conditions.Length, conditions = ptr,
                        action = new Action { type = Block }
                    };
                    Check(FwpmFilterAdd0(engine, ref filter, IntPtr.Zero, out _),
                        "persistent synthetic filter create");
                });
            }
            finally { Marshal.FreeHGlobal(ptr); }

            Check(FwpmEngineClose0(engine), "engine close before persistence check");
            engine = IntPtr.Zero;
            engine = Open();
            var result = FwpmFilterGetByKey0(engine, ref filterkey, out var found);
            Check(result, "filter survived closing original WFP engine");
            if (found == IntPtr.Zero) throw new InvalidOperationException("Filter query returned null");
            FwpmFreeMemory0(ref found);
            Console.WriteLine("PASS: synthetic WFP persistent filter survived engine close/reopen.");
            Console.WriteLine("NOTE: Synthetic indices do not prove actual packet blocking.");
        }
        finally
        {
            if (engine == IntPtr.Zero)
            {
                try { engine = Open(); } catch { }
            }
            if (engine != IntPtr.Zero)
            {
                try
                {
                    var filterDelete = FwpmFilterDeleteByKey0(engine, ref filterkey);
                    var subDelete = FwpmSubLayerDeleteByKey0(engine, ref subkey);
                    if (filterDelete != 0 || subDelete != 0)
                        throw new InvalidOperationException(
                            $"Persistent synthetic cleanup failed: filter=0x{filterDelete:X8}, sublayer=0x{subDelete:X8}");
                    Console.WriteLine("PASS: synthetic persistent objects removed.");
                }
                finally { FwpmEngineClose0(engine); }
            }
        }
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

    static void WithDisplay(string display, Action<IntPtr> body)
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
