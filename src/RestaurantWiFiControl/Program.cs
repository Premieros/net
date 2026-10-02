using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RestaurantWiFiControl;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Storage.Initialize();
        if (!Storage.HasPassword())
        {
            using var setup = new PasswordDialog(true);
            if (setup.ShowDialog() != DialogResult.OK) return;
        }
        using var login = new PasswordDialog(false);
        if (login.ShowDialog() != DialogResult.OK) return;
        Application.Run(new MainForm());
    }
}

internal sealed class AppData
{
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string RestaurantName { get; set; } = "Restaurant Wi-Fi Control";
    public List<AccessGroup> Groups { get; set; } = new();
    public List<AccessCode> Codes { get; set; } = new();
    public List<ClientRecord> Clients { get; set; } = new();
}

internal sealed class AccessGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public int Minutes { get; set; } = 60;
    public int QuotaMb { get; set; } = 1024;
    public decimal DownloadMbps { get; set; } = 5;
    public decimal UploadMbps { get; set; } = 2;
    public int MaxDevices { get; set; } = 1;
    public int MaxUsesPerDevice { get; set; } = 1;
    public bool BlockVideo { get; set; } = false;
    public string Kind { get; set; } = "customer";
    public bool Enabled { get; set; } = true;
}

internal sealed class AccessCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public Guid GroupId { get; set; }
    public string Label { get; set; } = "";
    public int MaxUses { get; set; } = 1;
    public int Uses { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

internal sealed class ClientRecord
{
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Device { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Group { get; set; } = "";
    public double UsedMb { get; set; }
    public bool Connected { get; set; }
}

internal static class Storage
{
    static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Restaurant WiFi Control");
    static readonly string FilePath = Path.Combine(Folder, "v9-data.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static AppData Data { get; private set; } = new();

    public static void Initialize()
    {
        Directory.CreateDirectory(Folder);
        Reload();

        if (Data.Groups.Count == 0)
        {
            Data.Groups.AddRange(new[]
            {
                new AccessGroup { Name = "العملاء", Kind = "customer", Minutes = 60, QuotaMb = 1024, DownloadMbps = 5, UploadMbps = 2, MaxDevices = 1, MaxUsesPerDevice = 2, BlockVideo = true },
                new AccessGroup { Name = "الموظفين", Kind = "employee", Minutes = 720, QuotaMb = 4096, DownloadMbps = 10, UploadMbps = 5, MaxDevices = 1, MaxUsesPerDevice = 20, BlockVideo = false },
                new AccessGroup { Name = "المديرين", Kind = "manager", Minutes = 1440, QuotaMb = 0, DownloadMbps = 0, UploadMbps = 0, MaxDevices = 2, MaxUsesPerDevice = 100, BlockVideo = false }
            });
            Save();
        }
    }

    public static void Reload()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            Data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(FilePath), JsonOptions) ?? new AppData();
        }
        catch
        {
            // Keep the current in-memory snapshot if the gateway is writing the file at the same moment.
        }
    }

    public static void Save() =>
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Data, JsonOptions), Encoding.UTF8);

    public static bool HasPassword() =>
        !string.IsNullOrWhiteSpace(Data.PasswordSalt) && !string.IsNullOrWhiteSpace(Data.PasswordHash);

    public static void SetPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 220_000, HashAlgorithmName.SHA256, 32);
        Data.PasswordSalt = Convert.ToBase64String(salt);
        Data.PasswordHash = Convert.ToBase64String(hash);
        Save();
    }

    public static bool VerifyPassword(string password)
    {
        if (!HasPassword()) return false;
        var salt = Convert.FromBase64String(Data.PasswordSalt);
        var expected = Convert.FromBase64String(Data.PasswordHash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 220_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}

internal sealed class PasswordDialog : Form
{
    readonly bool _setup;
    readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top, Height = 36, Font = new Font("Segoe UI", 12) };
    readonly TextBox _confirm = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top, Height = 36, Font = new Font("Segoe UI", 12) };

    public PasswordDialog(bool setup)
    {
        _setup = setup;
        Text = setup ? "إعداد كلمة مرور المدير" : "تسجيل الدخول";
        Width = 440;
        Height = setup ? 330 : 280;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        BackColor = Color.FromArgb(17, 24, 39);

        var title = new Label
        {
            Text = "Restaurant Wi-Fi Control\nV10 Network Gate",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 72,
            TextAlign = ContentAlignment.MiddleCenter
        };

        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(35, 15, 35, 25) };
        var hint = new Label
        {
            Text = setup ? "أنشئ كلمة مرور المدير" : "أدخل كلمة مرور المدير",
            ForeColor = Color.Gainsboro,
            Dock = DockStyle.Top,
            Height = 35,
            TextAlign = ContentAlignment.MiddleRight
        };
        var passwordLabel = new Label { Text = "كلمة المرور", ForeColor = Color.WhiteSmoke, Dock = DockStyle.Top, Height = 26 };
        var confirmLabel = new Label { Text = "تأكيد كلمة المرور", ForeColor = Color.WhiteSmoke, Dock = DockStyle.Top, Height = 26 };
        var button = new Button
        {
            Text = setup ? "حفظ والدخول" : "دخول",
            Dock = DockStyle.Bottom,
            Height = 42,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => Submit();

        Controls.Add(panel);
        Controls.Add(title);
        panel.Controls.Add(button);
        if (setup)
        {
            panel.Controls.Add(_confirm);
            panel.Controls.Add(confirmLabel);
        }
        panel.Controls.Add(_password);
        panel.Controls.Add(passwordLabel);
        panel.Controls.Add(hint);
        AcceptButton = button;
    }

    void Submit()
    {
        if (_setup)
        {
            if (_password.Text.Length < 4)
            {
                MessageBox.Show("كلمة المرور يجب ألا تقل عن 4 أحرف.", "تنبيه", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_password.Text != _confirm.Text)
            {
                MessageBox.Show("كلمتا المرور غير متطابقتين.", "تنبيه", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            Storage.SetPassword(_password.Text);
            DialogResult = DialogResult.OK;
            return;
        }

        if (!Storage.VerifyPassword(_password.Text))
        {
            MessageBox.Show("كلمة المرور غير صحيحة.", "رفض الدخول", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _password.Clear();
            _password.Focus();
            return;
        }
        DialogResult = DialogResult.OK;
    }
}

internal sealed class MainForm : Form
{
    readonly Panel _body = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(243, 244, 246) };
    readonly Label _pageTitle = new()
    {
        Dock = DockStyle.Right,
        Width = 320,
        TextAlign = ContentAlignment.MiddleRight,
        Font = new Font("Segoe UI", 17, FontStyle.Bold),
        ForeColor = Color.FromArgb(17, 24, 39)
    };
    readonly Dictionary<string, Control> _pages = new();
    readonly Dictionary<string, Button> _nav = new();

    DataGridView? _groupsGrid;
    DataGridView? _codesGrid;
    DataGridView? _clientsGrid;
    ComboBox? _quickGroup;
    ComboBox? _codeGroup;
    Label? _metricGroups;
    Label? _metricCodes;
    Label? _metricOnline;
    Label? _metricClients;
    Label? _gatewayStatus;
    Label? _wanLine;
    Label? _apLines;
    Label? _gatePolicy;
    readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public MainForm()
    {
        Text = "Restaurant Wi-Fi Control — V10 Network Gate";
        Width = 1450;
        Height = 850;
        MinimumSize = new Size(1150, 680);
        StartPosition = FormStartPosition.CenterScreen;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;
        Font = new Font("Segoe UI", 10);

        var sidebar = BuildSidebar();
        var topbar = BuildTopbar();
        Controls.Add(_body);
        Controls.Add(topbar);
        Controls.Add(sidebar);

        BuildDashboard();
        BuildGroups();
        BuildCodes();
        BuildClients();
        BuildSettings();
        ShowPage("dashboard", "الرئيسية");
        RefreshAll();
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += async (_, _) => await CheckGatewayAsync();
        timer.Start();
        Shown += async (_, _) => await CheckGatewayAsync();
    }

    async Task CheckGatewayAsync()
    {
        if (_gatewayStatus is null) return;
        try
        {
            var json = await _http.GetStringAsync("http://127.0.0.1:8765/status/");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var gate = root.GetProperty("gate");
            var active = gate.GetProperty("active").GetBoolean();
            var state = gate.GetProperty("state").GetString() ?? "";

            _gatewayStatus.Text = active
                ? "Gateway: متصل — الحجب مفعل قبل الكود"
                : $"Gateway: متصل — {state}";
            _gatewayStatus.ForeColor = active ? Color.SeaGreen : Color.DarkOrange;

            if (_gatePolicy is not null)
            {
                _gatePolicy.Text = active
                    ? "سياسة الإنترنت: مغلق افتراضياً — يفتح فقط للجهاز بعد قبول الكود"
                    : $"سياسة الإنترنت: غير جاهزة ({state})";
                _gatePolicy.ForeColor = active ? Color.SeaGreen : Color.DarkOrange;
            }

            if (_wanLine is not null)
            {
                if (root.TryGetProperty("wan", out var wan) && wan.ValueKind == JsonValueKind.Object)
                {
                    var name = wan.GetProperty("name").GetString() ?? "";
                    var ip = wan.GetProperty("ipv4").GetString() ?? "";
                    var gw = wan.GetProperty("gateway").GetString() ?? "";
                    var idx = wan.GetProperty("interfaceIndex").GetInt32();
                    _wanLine.Text = $"الخط الداخل (WAN): {name} — IP {ip} — Gateway {gw} — IF#{idx}";
                }
                else _wanLine.Text = "الخط الداخل (WAN): لم يتم اكتشافه";
            }

            if (_apLines is not null)
            {
                var lines = new List<string>();
                if (root.TryGetProperty("accessPoints", out var aps) && aps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ap in aps.EnumerateArray())
                    {
                        var name = ap.GetProperty("name").GetString() ?? "";
                        var ip = ap.GetProperty("ipv4").GetString() ?? "";
                        var subnet = ap.GetProperty("subnet").GetString() ?? "";
                        var idx = ap.GetProperty("interfaceIndex").GetInt32();
                        lines.Add($"{name} — {ip} — {subnet} — IF#{idx}");
                    }
                }
                _apLines.Text = lines.Count == 0
                    ? "الخطوط الخارجة إلى Access Point: لم يتم اكتشاف خط خروج"
                    : "الخطوط الخارجة إلى Access Point:\n" + string.Join("\n", lines);
            }

            Storage.Reload();
            RefreshAll();
        }
        catch
        {
            _gatewayStatus.Text = "Gateway Service: غير متصل";
            _gatewayStatus.ForeColor = Color.DarkOrange;
            if (_gatePolicy is not null)
            {
                _gatePolicy.Text = "سياسة الإنترنت: Gateway غير متصل";
                _gatePolicy.ForeColor = Color.DarkOrange;
            }
        }
    }

    Panel BuildSidebar()
    {
        var sidebar = new Panel { Dock = DockStyle.Right, Width = 235, BackColor = Color.FromArgb(17, 24, 39) };
        var brand = new Label
        {
            Text = "Wi-Fi Control\nV10 Network Gate",
            Dock = DockStyle.Top,
            Height = 95,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 17, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        sidebar.Controls.Add(brand);

        AddNav(sidebar, "settings", "الإعدادات", "الإعدادات");
        AddNav(sidebar, "clients", "المستخدمون والأجهزة", "المستخدمون والأجهزة");
        AddNav(sidebar, "codes", "الأكواد", "إدارة الأكواد");
        AddNav(sidebar, "groups", "المجموعات والباقات", "المجموعات والباقات");
        AddNav(sidebar, "dashboard", "الرئيسية", "الرئيسية");
        return sidebar;
    }

    void AddNav(Panel sidebar, string key, string text, string title)
    {
        var b = new Button
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 48,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(17, 24, 39),
            ForeColor = Color.FromArgb(229, 231, 235),
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, 18, 0)
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => ShowPage(key, title);
        _nav[key] = b;
        sidebar.Controls.Add(b);
        sidebar.Controls.SetChildIndex(b, 0);
    }

    Panel BuildTopbar()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Color.White };
        _gatewayStatus = new Label
        {
            Text = "Gateway Service: جاري الفحص...",
            Dock = DockStyle.Left,
            Width = 320,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.SeaGreen,
            Padding = new Padding(15, 0, 0, 0)
        };
        top.Controls.Add(_gatewayStatus);
        top.Controls.Add(_pageTitle);
        return top;
    }

    Panel Card(Control parent, DockStyle dock = DockStyle.Top, int height = 120)
    {
        var p = new Panel
        {
            Dock = dock,
            Height = height,
            BackColor = Color.White,
            Margin = new Padding(8),
            Padding = new Padding(14)
        };
        parent.Controls.Add(p);
        return p;
    }

    void BuildDashboard()
    {
        var page = NewPage();
        var metrics = new TableLayoutPanel { Dock = DockStyle.Top, Height = 130, ColumnCount = 4, Padding = new Padding(0, 0, 0, 10) };
        for (int i = 0; i < 4; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        _metricGroups = Metric(metrics, 0, "المجموعات");
        _metricCodes = Metric(metrics, 1, "الأكواد الفعالة");
        _metricOnline = Metric(metrics, 2, "المتصلون الآن");
        _metricClients = Metric(metrics, 3, "المستخدمون");
        page.Controls.Add(metrics);

        var network = Card(page, DockStyle.Top, 185);
        network.Controls.Add(new Label
        {
            Text = "حالة خطوط الشبكة والحجب",
            Dock = DockStyle.Top,
            Height = 34,
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight
        });
        _wanLine = new Label
        {
            Text = "الخط الداخل (WAN): جاري الاكتشاف...",
            Dock = DockStyle.Top,
            Height = 32,
            TextAlign = ContentAlignment.MiddleRight
        };
        _apLines = new Label
        {
            Text = "الخطوط الخارجة إلى Access Point: جاري الاكتشاف...",
            Dock = DockStyle.Top,
            Height = 65,
            TextAlign = ContentAlignment.TopRight
        };
        _gatePolicy = new Label
        {
            Text = "سياسة الإنترنت: جاري التحقق...",
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight
        };
        network.Controls.Add(_gatePolicy);
        network.Controls.Add(_apLines);
        network.Controls.Add(_wanLine);

        var quick = Card(page, DockStyle.Top, 150);
        quick.Controls.Add(new Label { Text = "إنشاء كود سريع", Dock = DockStyle.Top, Height = 35, Font = new Font("Segoe UI", 13, FontStyle.Bold) });
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 55, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        _quickGroup = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList };
        var label = new TextBox { Width = 230, PlaceholderText = "اسم/وصف الكود" };
        var create = PrimaryButton("إنشاء كود", 120);
        create.Click += (_, _) =>
        {
            if (_quickGroup.SelectedItem is not AccessGroup group) return;
            var code = GenerateCode(6);
            Storage.Data.Codes.Add(new AccessCode { Code = code, GroupId = group.Id, Label = label.Text.Trim() });
            Storage.Save();
            Clipboard.SetText(code);
            MessageBox.Show($"تم إنشاء الكود ونسخه:\n{code}", "تم");
            label.Clear();
            RefreshAll();
        };
        row.Controls.Add(create);
        row.Controls.Add(label);
        row.Controls.Add(_quickGroup);
        quick.Controls.Add(row);
        _pages["dashboard"] = page;
    }

    Label Metric(TableLayoutPanel parent, int column, string title)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(7), Padding = new Padding(15) };
        var titleLabel = new Label { Text = title, Dock = DockStyle.Top, Height = 30, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleRight };
        var value = new Label { Text = "0", Dock = DockStyle.Fill, Font = new Font("Segoe UI", 26, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, RightToLeft = RightToLeft.No };
        card.Controls.Add(value);
        card.Controls.Add(titleLabel);
        parent.Controls.Add(card, column, 0);
        return value;
    }

    void BuildGroups()
    {
        var page = NewPage();
        var form = Card(page, DockStyle.Top, 180);
        form.Controls.Add(new Label { Text = "إنشاء مجموعة / باقة", Dock = DockStyle.Top, Height = 35, Font = new Font("Segoe UI", 13, FontStyle.Bold) });
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 90, FlowDirection = FlowDirection.RightToLeft, AutoScroll = true };
        var name = Field(row, "الاسم", "مجموعة جديدة", 155);
        var minutes = Field(row, "الوقت بالدقائق", "60", 115);
        var quota = Field(row, "الحجم MB (0 غير محدود)", "1024", 140);
        var down = Field(row, "تحميل Mbps", "5", 105);
        var up = Field(row, "رفع Mbps", "2", 105);
        var devices = Field(row, "عدد الأجهزة", "1", 95);
        var add = PrimaryButton("إضافة", 105);
        add.Margin = new Padding(8, 28, 8, 0);
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) ||
                !int.TryParse(minutes.Text, out var min) || min <= 0 ||
                !int.TryParse(quota.Text, out var q) || q < 0 ||
                !decimal.TryParse(down.Text, out var d) || d < 0 ||
                !decimal.TryParse(up.Text, out var u) || u < 0 ||
                !int.TryParse(devices.Text, out var dev) || dev <= 0)
            {
                MessageBox.Show("راجع بيانات المجموعة.", "تنبيه", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (Storage.Data.Groups.Any(x => x.Name.Equals(name.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("اسم المجموعة موجود بالفعل.", "تنبيه");
                return;
            }
            Storage.Data.Groups.Add(new AccessGroup { Name = name.Text.Trim(), Minutes = min, QuotaMb = q, DownloadMbps = d, UploadMbps = u, MaxDevices = dev });
            Storage.Save();
            name.Clear();
            RefreshAll();
        };
        row.Controls.Add(add);
        form.Controls.Add(row);

        _groupsGrid = Grid();
        page.Controls.Add(_groupsGrid);
        _groupsGrid.BringToFront();
        _pages["groups"] = page;
    }

    void BuildCodes()
    {
        var page = NewPage();
        var form = Card(page, DockStyle.Top, 175);
        form.Controls.Add(new Label { Text = "إنشاء أكواد متعددة", Dock = DockStyle.Top, Height = 35, Font = new Font("Segoe UI", 13, FontStyle.Bold) });
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 90, FlowDirection = FlowDirection.RightToLeft, AutoScroll = true };
        _codeGroup = new ComboBox { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(7, 28, 7, 0) };
        var count = Field(row, "عدد الأكواد", "1", 90);
        var length = Field(row, "طول الكود", "6", 90);
        var maxUses = Field(row, "أقصى استخدام", "1", 100);
        var create = PrimaryButton("إنشاء الأكواد", 125);
        create.Margin = new Padding(7, 28, 7, 0);
        create.Click += (_, _) =>
        {
            if (_codeGroup.SelectedItem is not AccessGroup group ||
                !int.TryParse(count.Text, out var n) || n < 1 || n > 500 ||
                !int.TryParse(length.Text, out var len) || len < 4 || len > 12 ||
                !int.TryParse(maxUses.Text, out var max) || max < 1)
            {
                MessageBox.Show("راجع إعدادات إنشاء الأكواد.", "تنبيه");
                return;
            }
            var created = new List<string>();
            for (var i = 0; i < n; i++)
            {
                string code;
                do code = GenerateCode(len); while (Storage.Data.Codes.Any(x => x.Code == code));
                Storage.Data.Codes.Add(new AccessCode { Code = code, GroupId = group.Id, MaxUses = max });
                created.Add(code);
            }
            Storage.Save();
            Clipboard.SetText(string.Join(Environment.NewLine, created));
            MessageBox.Show($"تم إنشاء {created.Count} كود ونسخها للحافظة.", "تم");
            RefreshAll();
        };
        row.Controls.Add(create);
        row.Controls.Add(_codeGroup);
        form.Controls.Add(row);

        _codesGrid = Grid();
        page.Controls.Add(_codesGrid);
        _codesGrid.BringToFront();
        _pages["codes"] = page;
    }

    void BuildClients()
    {
        var page = NewPage();
        var info = Card(page, DockStyle.Bottom, 55);
        info.Controls.Add(new Label
        {
            Text = "V10: الإنترنت محجوب افتراضياً على خطوط الـAccess Point عبر WFP، ولا يُسمح للجهاز بالمرور إلى WAN إلا بعد قبول الكود وحتى انتهاء الجلسة.",
            Dock = DockStyle.Fill,
            ForeColor = Color.DarkOrange,
            TextAlign = ContentAlignment.MiddleRight
        });
        _clientsGrid = Grid();
        page.Controls.Add(_clientsGrid);
        _pages["clients"] = page;
    }

    void BuildSettings()
    {
        var page = NewPage();
        var card = Card(page, DockStyle.Top, 185);
        card.Controls.Add(new Label { Text = "الأمان والإعدادات", Dock = DockStyle.Top, Height = 38, Font = new Font("Segoe UI", 13, FontStyle.Bold) });
        var nameBox = new TextBox { Text = Storage.Data.RestaurantName, Width = 300 };
        var changePassword = PrimaryButton("تغيير كلمة المرور", 160);
        var save = PrimaryButton("حفظ اسم النظام", 140);
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 55, FlowDirection = FlowDirection.RightToLeft };
        row.Controls.Add(save);
        row.Controls.Add(nameBox);
        row.Controls.Add(changePassword);
        save.Click += (_, _) =>
        {
            Storage.Data.RestaurantName = string.IsNullOrWhiteSpace(nameBox.Text) ? "Restaurant Wi-Fi Control" : nameBox.Text.Trim();
            Storage.Save();
            MessageBox.Show("تم الحفظ.", "تم");
        };
        changePassword.Click += (_, _) =>
        {
            var current = PromptPassword("كلمة المرور الحالية");
            if (current is null) return;
            if (!Storage.VerifyPassword(current))
            {
                MessageBox.Show("كلمة المرور الحالية غير صحيحة.", "خطأ");
                return;
            }
            var next = PromptPassword("كلمة المرور الجديدة");
            if (string.IsNullOrWhiteSpace(next) || next.Length < 4)
            {
                MessageBox.Show("كلمة المرور الجديدة قصيرة.", "خطأ");
                return;
            }
            Storage.SetPassword(next);
            MessageBox.Show("تم تغيير كلمة المرور.", "تم");
        };
        card.Controls.Add(row);
        _pages["settings"] = page;
    }

    Panel NewPage() => new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(243, 244, 246), Padding = new Padding(16) };

    TextBox Field(FlowLayoutPanel row, string label, string value, int width)
    {
        var p = new Panel { Width = width, Height = 78, Margin = new Padding(6, 0, 6, 0) };
        p.Controls.Add(new TextBox { Text = value, Dock = DockStyle.Bottom, Height = 32 });
        p.Controls.Add(new Label { Text = label, Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.DimGray });
        row.Controls.Add(p);
        return (TextBox)p.Controls[0];
    }

    Button PrimaryButton(string text, int width) =>
        new()
        {
            Text = text,
            Width = width,
            Height = 38,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(7)
        };

    DataGridView Grid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            RightToLeft = RightToLeft.Yes
        };
        g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        g.RowTemplate.Height = 34;
        return g;
    }

    void ShowPage(string key, string title)
    {
        _body.Controls.Clear();
        _body.Controls.Add(_pages[key]);
        _pageTitle.Text = title;
        foreach (var item in _nav)
            item.Value.BackColor = item.Key == key ? Color.FromArgb(31, 41, 55) : Color.FromArgb(17, 24, 39);
    }

    void RefreshAll()
    {
        var groups = Storage.Data.Groups.Where(g => g.Enabled && g.Kind != "customer").ToList();
        if (_quickGroup is not null)
        {
            _quickGroup.DataSource = groups.ToList();
            _quickGroup.DisplayMember = nameof(AccessGroup.Name);
        }
        if (_codeGroup is not null)
        {
            _codeGroup.DataSource = groups.ToList();
            _codeGroup.DisplayMember = nameof(AccessGroup.Name);
        }

        if (_groupsGrid is not null)
        {
            _groupsGrid.DataSource = Storage.Data.Groups.Select(g => new
            {
                المجموعة = g.Name,
                الوقت = $"{g.Minutes} دقيقة",
                الحجم = g.QuotaMb == 0 ? "غير محدود" : $"{g.QuotaMb} MB",
                تحميل = g.DownloadMbps == 0 ? "غير محدود" : $"{g.DownloadMbps} Mbps",
                رفع = g.UploadMbps == 0 ? "غير محدود" : $"{g.UploadMbps} Mbps",
                الأجهزة = g.MaxDevices,
                مرات_الجهاز = g.MaxUsesPerDevice,
                الفيديو = g.BlockVideo ? "محجوب" : "مسموح",
                النوع = g.Kind == "customer" ? "عملاء" : g.Kind == "employee" ? "موظفون" : "مديرون",
                الحالة = g.Enabled ? "مفعل" : "موقوف"
            }).ToList();
        }

        if (_codesGrid is not null)
        {
            _codesGrid.DataSource = Storage.Data.Codes.Select(c => new
            {
                الكود = c.Code,
                المجموعة = Storage.Data.Groups.FirstOrDefault(g => g.Id == c.GroupId)?.Name ?? "",
                الوصف = c.Label,
                الاستخدام = c.Uses,
                الحد = c.MaxUses,
                الحالة = c.Enabled ? "مفعل" : "موقوف"
            }).ToList();
        }

        if (_clientsGrid is not null)
        {
            _clientsGrid.DataSource = Storage.Data.Clients.Select(c => new
            {
                الاسم = c.Name,
                الهاتف = c.Phone,
                الجهاز = c.Device,
                IP = c.Ip,
                MAC = c.Mac,
                المجموعة = c.Group,
                الاستهلاك = $"{c.UsedMb:0.0} MB",
                الحالة = c.Connected ? "متصل" : "غير متصل"
            }).ToList();
        }

        if (_metricGroups is not null) _metricGroups.Text = Storage.Data.Groups.Count.ToString();
        if (_metricCodes is not null) _metricCodes.Text = Storage.Data.Codes.Count(c => c.Enabled).ToString();
        if (_metricOnline is not null) _metricOnline.Text = Storage.Data.Clients.Count(c => c.Connected).ToString();
        if (_metricClients is not null) _metricClients.Text = Storage.Data.Clients.Count.ToString();
    }

    static string GenerateCode(int length)
    {
        const string digits = "0123456789";
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(length);
        foreach (var b in bytes) sb.Append(digits[b % digits.Length]);
        return sb.ToString();
    }

    string? PromptPassword(string title)
    {
        using var f = new Form { Width = 390, Height = 180, Text = title, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, RightToLeft = RightToLeft.Yes, RightToLeftLayout = true };
        var t = new TextBox { UseSystemPasswordChar = true, Left = 30, Top = 35, Width = 310 };
        var ok = new Button { Text = "موافق", Left = 215, Top = 80, Width = 125, DialogResult = DialogResult.OK };
        f.Controls.Add(t); f.Controls.Add(ok); f.AcceptButton = ok;
        return f.ShowDialog(this) == DialogResult.OK ? t.Text : null;
    }
}
