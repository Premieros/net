using RestaurantWiFiNetworking;

namespace RestaurantWiFiControl;

/// <summary>Saved operator selection only. This does not change Windows routing or firewall rules.</summary>
internal sealed class NetworkPreferences
{
    public string UpstreamAdapterId { get; set; } = "";
    public string DownstreamAdapterId { get; set; } = "";
    public ClientAccessMode AccessMode { get; set; } = ClientAccessMode.ExternalAccessPointBridge;
    public string ExperimentalWfpTrialUntilUtc { get; set; } = "";
}

internal sealed class NetworkSetupDialog : Form
{
    readonly ComboBox upstream = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 550 };
    readonly ComboBox downstream = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 550 };
    readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 550 };
    readonly TextBox diagnostic = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, BackColor = Color.White, Font = new Font("Consolas", 10)
    };
    readonly NetworkPreferences original;
    IReadOnlyList<AdapterSnapshot> detected = Array.Empty<AdapterSnapshot>();

    public NetworkSetupDialog(NetworkPreferences preferences)
    {
        original = preferences;
        Text = "إعداد الإنترنت: راوتر ← كمبيوتر ← Access Point / Hotspot";
        Width = 830;
        Height = 730;
        MinimumSize = new Size(720, 640);
        StartPosition = FormStartPosition.CenterParent;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = new Font("Segoe UI", 10);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        Controls.Add(root);

        root.Controls.Add(new Label
        {
            Text = "الراوتر يرسل الإنترنت إلى كابل Ethernet بالكمبيوتر. " +
                   "ثم يخرج الكمبيوتر الإنترنت إلى Access Point خارجي أو Hotspot.",
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight
        }, 0, 0);
        root.Controls.Add(Field("مدخل الإنترنت — كابل LAN من الراوتر", upstream), 0, 1);
        root.Controls.Add(Field("طريقة توزيع الإنترنت", mode), 0, 2);
        root.Controls.Add(Field("مخرج الإنترنت — كارت مختلف للعملاء", downstream), 0, 3);
        root.Controls.Add(diagnostic, 0, 4);
        root.Controls.Add(new Label
        {
            Text = "فحص للاتصالات فقط؛ لا يقوم بتشغيل مشاركة الإنترنت أو التحكم بالعملاء.",
            ForeColor = Color.DarkOrange, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight
        }, 0, 5);

        mode.Items.Add("Access Point خارجي بكابل LAN ثانٍ");
        mode.Items.Add("Hotspot عبر Wi-Fi من الكمبيوتر");
        mode.SelectedIndex = original.AccessMode == ClientAccessMode.WindowsHostedHotspot ? 1 : 0;
        mode.SelectedIndexChanged += (_, _) => Report();
        upstream.SelectedIndexChanged += (_, _) => Report();
        downstream.SelectedIndexChanged += (_, _) => Report();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var scan = new Button { Text = "تحديث الكروت", Width = 135, Height = 35 };
        var inspect = new Button { Text = "فحص المسار", Width = 135, Height = 35 };
        var save = new Button { Text = "حفظ الاختيارات", Width = 145, Height = 35 };
        var close = new Button { Text = "إغلاق", Width = 105, Height = 35 };
        actions.Controls.AddRange(new Control[] { scan, inspect, save, close });
        root.Controls.Add(actions, 0, 7);

        var trialActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var tryBlock = new Button { Text = "اختبار حجب IPv4 لمدة دقيقتين", Width = 275, Height = 35 };
        var stopBlock = new Button { Text = "إيقاف اختبار الحجب", Width = 185, Height = 35 };
        trialActions.Controls.Add(tryBlock);
        trialActions.Controls.Add(stopBlock);
        root.Controls.Add(trialActions, 0, 6);
        tryBlock.Click += (_, _) => UpdateTrial(true);
        stopBlock.Click += (_, _) => UpdateTrial(false);
        scan.Click += (_, _) => Scan();
        inspect.Click += (_, _) => Report();
        save.Click += (_, _) => SaveSelection();
        close.Click += (_, _) => Close();
        Scan();
    }

    static Control Field(string label, Control input)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = false
        };
        panel.Controls.Add(new Label
        {
            Text = label, Width = 620, Height = 26,
            TextAlign = ContentAlignment.MiddleRight
        });
        panel.Controls.Add(input);
        return panel;
    }

    sealed class AdapterChoice
    {
        public AdapterSnapshot Adapter { get; }
        public AdapterChoice(AdapterSnapshot adapter) => Adapter = adapter;
        public override string ToString() =>
            $"{Adapter.Name} [{Adapter.InterfaceType}] — " +
            $"IP: {Adapter.Ipv4Address ?? "بدون"} — " +
            $"Gateway: {Adapter.Ipv4Gateway ?? "بدون"} — " +
            (Adapter.IsUp ? "متصل" : "غير متصل");
    }

    static void SelectById(ComboBox box, string id)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is AdapterChoice choice &&
                string.Equals(choice.Adapter.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
    }

    void Scan()
    {
        var previousUp = (upstream.SelectedItem as AdapterChoice)?.Adapter.Id ?? original.UpstreamAdapterId;
        var previousDown = (downstream.SelectedItem as AdapterChoice)?.Adapter.Id ?? original.DownstreamAdapterId;
        try { detected = WindowsAdapterDiscovery.Discover(); }
        catch (Exception ex)
        {
            diagnostic.Text = "تعذر اكتشاف كروت الشبكة: " + ex.Message;
            return;
        }
        upstream.Items.Clear();
        downstream.Items.Clear();
        foreach (var nic in detected)
        {
            upstream.Items.Add(new AdapterChoice(nic));
            downstream.Items.Add(new AdapterChoice(nic));
        }
        SelectById(upstream, previousUp);
        SelectById(downstream, previousDown);
        Report();
    }

    void Report()
    {
        var os = WindowsCompatibility.InspectCurrent();
        var osText = (os.Family == WindowsEditionFamily.Windows11 ? "Windows 11" :
            os.Family == WindowsEditionFamily.Windows10 ? "Windows 10" : "نظام غير مدعوم") +
            $" (Build {os.Build})\r\n" + os.Message + "\r\n\r\n";
        var uplink = (upstream.SelectedItem as AdapterChoice)?.Adapter;
        var downlink = (downstream.SelectedItem as AdapterChoice)?.Adapter;
        if (uplink is null || downlink is null)
        {
            diagnostic.Text = osText + "اختر كارت Ethernet الذي يصله الراوتر وكارتًا مختلفًا لتوزيع الإنترنت.\r\n" +
                              "قد لا يظهر كارت Hotspot الافتراضي حتى يتم تشغيله من Windows.";
            return;
        }
        var topology = new WindowsSharingTopology(
            uplink.Id, downlink.Id,
            mode.SelectedIndex == 1 ? ClientAccessMode.WindowsHostedHotspot :
                ClientAccessMode.ExternalAccessPointBridge, true);
        var result = TopologyValidator.Assess(topology, detected);
        var safety = GatewayPreflight.Check(topology, detected);
        diagnostic.Text = osText + "مدخل الإنترنت: " + uplink.Name + "\r\n" +
                          "عنوان المدخل: " + (uplink.Ipv4Address ?? "غير معروف") + "\r\n" +
                          "راوتر المصدر: " + (uplink.Ipv4Gateway ?? "غير معروف") + "\r\n" +
                          "مخرج العملاء: " + downlink.Name + "\r\n" +
                          "عنوان المخرج: " + (downlink.Ipv4Address ?? "غير معروف") + "\r\n\r\n" +
                          "تقييم التوصيل: " + (safety.WiringAppearsValid ? "مبدئيًا مناسب" : "يحتاج تصحيح") +
                          "\r\n" + result.Explanation +
                          (safety.Issues.Count == 0 ? "" : "\r\nمشكلات مكتشفة:\r\n- " +
                              string.Join("\r\n- ", safety.Issues)) + "\r\n\r\n" +
                          "تنبيه: لم يتم التأكد من NAT/DHCP أو بوابة العملاء أو فرض حجب الإنترنت.";
    }

    void UpdateTrial(bool enable)
    {
        var uplink = (upstream.SelectedItem as AdapterChoice)?.Adapter;
        var downlink = (downstream.SelectedItem as AdapterChoice)?.Adapter;
        if (uplink is null || downlink is null)
        {
            MessageBox.Show("اختر كرت LAN من الراوتر وكرت توزيع الإنترنت أولاً.", "اختبار الشبكة");
            return;
        }
        var selected = new WindowsSharingTopology(
            uplink.Id, downlink.Id,
            mode.SelectedIndex == 1 ? ClientAccessMode.WindowsHostedHotspot :
                ClientAccessMode.ExternalAccessPointBridge, true);
        var safety = GatewayPreflight.Check(selected, detected);
        if (!safety.WiringAppearsValid)
        {
            MessageBox.Show("لا يمكن الاختبار قبل تصحيح التوصيل:\n" +
                string.Join("\n", safety.Issues), "اختبار الشبكة");
            return;
        }
        if (enable && MessageBox.Show(
                "تجربة متقدمة على شبكة اختبار معزولة فقط.\n" +
                "قد يتوقف الإنترنت عن أجهزة الـAP/Hotspot لمدة دقيقتين، " +
                "ولا تضمن هذه التجربة منع IPv6 أو عمل الأكواد.\n" +
                "لن نقوم بتغيير إعدادات NAT/ICS. اختبر من هاتف منفصل. هل تتابع؟",
                "تأكيد تجربة WFP المؤقتة", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes) return;

        Storage.SetNetwork(new NetworkPreferences
        {
            UpstreamAdapterId = selected.UpstreamAdapterId,
            DownstreamAdapterId = selected.DownstreamAdapterId,
            AccessMode = selected.AccessMode,
            ExperimentalWfpTrialUntilUtc = enable ?
                DateTimeOffset.UtcNow.AddMinutes(2).ToString("O") : ""
        });
        MessageBox.Show(enable ?
            "تم طلب اختبار الحجب لمدة دقيقتين. افتح حالة خدمة Gateway ثم اختبر الإنترنت " +
            "من جهاز عميل متصل بالـAccess Point، وليس من الكمبيوتر." :
            "تم طلب إنهاء اختبار الحجب. تحقق من الحالة والاتصال على جهاز العميل.",
            "اختبار الشبكة", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void SaveSelection()
    {
        var uplink = (upstream.SelectedItem as AdapterChoice)?.Adapter;
        var downlink = (downstream.SelectedItem as AdapterChoice)?.Adapter;
        if (uplink is null || downlink is null || uplink.Id == downlink.Id)
        {
            MessageBox.Show("اختر كارت الدخول وكارت الخروج، ويجب أن يكونا مختلفين.", "إعداد الشبكة");
            return;
        }
        var intendedMode = mode.SelectedIndex == 1 ? ClientAccessMode.WindowsHostedHotspot :
            ClientAccessMode.ExternalAccessPointBridge;
        var preflight = GatewayPreflight.Check(
            new WindowsSharingTopology(uplink.Id, downlink.Id, intendedMode, true), detected);
        if (!preflight.WiringAppearsValid)
        {
            MessageBox.Show("لا يمكن حفظ مسار غير صالح:\n" + string.Join("\n", preflight.Issues),
                "تحقق من توصيل الشبكة", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Storage.SetNetwork(new NetworkPreferences
        {
            UpstreamAdapterId = uplink.Id,
            DownstreamAdapterId = downlink.Id,
            AccessMode = intendedMode
        });
        MessageBox.Show("تم حفظ اختيار كروت الشبكة فقط. لم يتم تشغيل توزيع الإنترنت أو تفعيل قواعد الحجب.",
            "تم الحفظ", MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
    }
}
