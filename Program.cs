using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ComTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\ComTray.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
            return;

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext());
    }
}

// ---------------------------------------------------------------- port model

/// <summary>Everything known about one serial port that is present right now.</summary>
internal sealed class PortInfo
{
    public string Name { get; init; } = "";          // COM7
    public string Description { get; init; } = "";   // Silicon Labs CP210x USB to UART Bridge
    public string Manufacturer { get; init; } = "";
    public string InstanceId { get; init; } = "";    // USB\VID_10C4&PID_EA60\0001
    public string Vid { get; init; } = "";           // 10C4
    public string Pid { get; init; } = "";           // EA60
    public string Serial { get; init; } = "";        // 0001, empty for composite devices
    public string Nickname { get; set; } = "";

    public int Number =>
        Name.Length > 3 &&
        int.TryParse(Name.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n : int.MaxValue;

    /// <summary>Stable key for remembering settings against the hardware, not the port number.</summary>
    public string DeviceKey => Serial.Length >= 4 ? $"{Vid}:{Pid}:{Serial}" : InstanceId;

    public string MenuLabel
    {
        get
        {
            string subtitle = Nickname.Length > 0 ? Nickname : Description;
            return subtitle.Length > 0 ? $"{Name}   —   {subtitle}" : Name;
        }
    }
}

internal sealed class DeviceDetail
{
    public string Description = "";
    public string Manufacturer = "";
    public string InstanceId = "";
}

internal static class PortScanner
{
    private static readonly Regex PortInCaption = new(@"\((COM\d+)\)", RegexOptions.Compiled);
    private static readonly Regex VidPid =
        new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Which ports exist right now. Sub-millisecond, safe to poll.</summary>
    public static List<string> GetPresentPorts()
    {
        var ports = new List<string>();
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is not null)
            {
                foreach (string valueName in key.GetValueNames())
                {
                    if (key.GetValue(valueName) is not string raw)
                        continue;

                    string port = raw.Trim('\0').Trim();
                    if (port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) &&
                        !ports.Contains(port, StringComparer.OrdinalIgnoreCase))
                    {
                        ports.Add(port);
                    }
                }
            }
        }
        catch { }

        ports.Sort(CompareByNumber);
        return ports;
    }

    /// <summary>Friendly name and hardware ID per port. ~200 ms, so only called on change.</summary>
    public static Dictionary<string, DeviceDetail> GetDetails()
    {
        var map = new Dictionary<string, DeviceDetail>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT Caption, DeviceID, Manufacturer FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");

            foreach (ManagementBaseObject device in searcher.Get())
            {
                using (device)
                {
                    if (device["Caption"] is not string caption || caption.Length == 0)
                        continue;

                    Match match = PortInCaption.Match(caption);
                    if (!match.Success)
                        continue;

                    string name = caption.Substring(0, match.Index).Trim();
                    map[match.Groups[1].Value] = new DeviceDetail
                    {
                        Description = name.Length > 0 ? name : caption,
                        Manufacturer = device["Manufacturer"] as string ?? "",
                        InstanceId = device["DeviceID"] as string ?? ""
                    };
                }
            }
        }
        catch { }

        return map;
    }

    public static (string Vid, string Pid) ParseVidPid(string instanceId)
    {
        Match m = VidPid.Match(instanceId);
        return m.Success
            ? (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant())
            : ("", "");
    }

    /// <summary>Last segment of the instance ID, unless it is a bus-generated composite ID.</summary>
    public static string ParseSerial(string instanceId)
    {
        if (instanceId.Length == 0)
            return "";

        string last = instanceId.Split('\\').Last();
        return last.Contains('&') ? "" : last;
    }

    private static int CompareByNumber(string a, string b)
    {
        int na = NumberOf(a), nb = NumberOf(b);
        return na != nb ? na.CompareTo(nb) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static int NumberOf(string port) =>
        port.Length > 3 && int.TryParse(port.AsSpan(3), out int n) ? n : int.MaxValue;
}

// -------------------------------------------------------------- action rules

internal sealed class ActionRule
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Args { get; set; } = "";
    public string[] Match { get; set; } = new[] { "*" };
    public bool Admin { get; set; }
    public bool Default { get; set; }
    public string WorkingDirectory { get; set; } = "";

    /// <summary>A rule applies if any match token appears in the device instance ID. "*" means always.</summary>
    public bool AppliesTo(PortInfo port)
    {
        if (Match is null || Match.Length == 0)
            return true;

        foreach (string token in Match)
        {
            if (token == "*")
                return true;
            if (!string.IsNullOrEmpty(token) &&
                port.InstanceId.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

internal sealed class ActionFile
{
    public ActionRule[] Actions { get; set; } = Array.Empty<ActionRule>();
}

internal static class ActionStore
{
    private const string FileName = "actions.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public const string DefaultJson = """
    {
      // Each action becomes a menu item under every port it matches.
      //
      //   match    substrings tested against the device instance ID,
      //            e.g. "VID_2341" for Arduino. "*" matches every port.
      //   args     placeholders: {port} {portnum} {baud} {vid} {pid}
      //                          {serial} {instance} {desc} {nickname}
      //   admin    true prompts for elevation (UAC)
      //   default  true shows the action in bold as the primary one
      //
      "actions": [
        {
          "name": "PuTTY at {baud}",
          "command": "putty.exe",
          "args": "-serial {port} -sercfg {baud},8,n,1,N",
          "match": ["*"],
          "default": true
        },
        {
          "name": "Arduino IDE",
          "command": "%LOCALAPPDATA%\\Programs\\Arduino IDE\\Arduino IDE.exe",
          "match": ["VID_2341", "VID_2A03", "VID_1A86", "VID_10C4"]
        },
        {
          "name": "PlatformIO monitor",
          "command": "wt.exe",
          "args": "cmd /k pio device monitor -p {port} -b {baud}",
          "match": ["*"]
        },
        {
          "name": "esptool flash ID",
          "command": "wt.exe",
          "args": "cmd /k esptool --port {port} flash_id",
          "match": ["VID_10C4", "VID_1A86", "VID_303A"]
        },
        {
          "name": "Device properties",
          "command": "rundll32.exe",
          "args": "devmgr.dll,DeviceProperties_RunDLL /MachineName \"\" /DeviceID \"{instance}\"",
          "match": ["*"]
        },
        {
          "name": "Restart this device",
          "command": "pnputil.exe",
          "args": "/restart-device \"{instance}\"",
          "match": ["*"],
          "admin": true
        }
      ]
    }
    """;

    /// <summary>Portable first (next to the exe), then per-user AppData.</summary>
    public static string ResolvePath()
    {
        string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (exeDir is not null)
        {
            string portable = Path.Combine(exeDir, FileName);
            if (File.Exists(portable))
                return portable;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ComTray", FileName);
    }

    public static void EnsureExists()
    {
        try
        {
            string path = ResolvePath();
            if (File.Exists(path))
                return;

            string? dir = Path.GetDirectoryName(path);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, DefaultJson);
        }
        catch { }
    }

    /// <summary>Read fresh every time the menu opens, so edits take effect with no restart.</summary>
    public static (List<ActionRule> Rules, string? Error) Load()
    {
        try
        {
            string path = ResolvePath();
            string json = File.Exists(path) ? File.ReadAllText(path) : DefaultJson;
            ActionFile? parsed = JsonSerializer.Deserialize<ActionFile>(json, JsonOptions);
            return (parsed?.Actions.ToList() ?? new List<ActionRule>(), null);
        }
        catch (JsonException ex)
        {
            return (new List<ActionRule>(), ex.Message);
        }
        catch (Exception ex)
        {
            return (new List<ActionRule>(), ex.Message);
        }
    }

    public static string Substitute(string template, PortInfo port, int baud)
    {
        if (string.IsNullOrEmpty(template))
            return "";

        return template
            .Replace("{port}", port.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{portnum}", port.Number.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{baud}", baud.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{vid}", port.Vid, StringComparison.OrdinalIgnoreCase)
            .Replace("{pid}", port.Pid, StringComparison.OrdinalIgnoreCase)
            .Replace("{serial}", port.Serial, StringComparison.OrdinalIgnoreCase)
            .Replace("{instance}", port.InstanceId, StringComparison.OrdinalIgnoreCase)
            .Replace("{desc}", port.Description, StringComparison.OrdinalIgnoreCase)
            .Replace("{nickname}", port.Nickname, StringComparison.OrdinalIgnoreCase);
    }
}

// ------------------------------------------------------------- small dialogs

internal static class Prompt
{
    public static string? AskText(string title, string label, string initial)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(360, 116),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = true
        };

        var prompt = new Label { Left = 12, Top = 14, Width = 336, Height = 20, Text = label };
        var input = new TextBox { Left = 12, Top = 40, Width = 336, Text = initial };
        var ok = new Button { Text = "Save", Left = 190, Top = 74, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 273, Top = 74, Width = 75, DialogResult = DialogResult.Cancel };

        form.Controls.AddRange(new Control[] { prompt, input, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
    }
}

// ------------------------------------------------------- device change window

internal sealed class DeviceChangeWindow : NativeWindow, IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    public event Action? DeviceChanged;

    public DeviceChangeWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "ComTrayDeviceWatcher",
            X = 0, Y = 0, Width = 0, Height = 0,
            Style = 0,
            ExStyle = WS_EX_TOOLWINDOW
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE)
            DeviceChanged?.Invoke();

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }
}

// ------------------------------------------------------------------ the tray

internal sealed class TrayContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKeyPath = @"Software\ComTray";
    private const string NicknamesKeyPath = SettingsKeyPath + @"\Nicknames";
    private const string BaudKeyPath = SettingsKeyPath + @"\Baud";
    private const string AppName = "ComTray";
    private const int DefaultBaud = 115200;

    private static readonly int[] BaudRates = { 9600, 19200, 38400, 57600, 115200, 230400, 921600 };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly DeviceChangeWindow _watcher;
    private readonly System.Windows.Forms.Timer _debounce;
    private readonly System.Windows.Forms.Timer _poll;

    private List<PortInfo> _ports = new();
    private Icon? _icon;
    private bool _notifyOnChange;
    private bool _firstScan = true;
    private string? _lastReportedConfigError;

    public TrayContext()
    {
        _notifyOnChange = LoadSetting("NotifyOnChange", true);
        ActionStore.EnsureExists();

        _menu = new ContextMenuStrip { ShowImageMargin = false };
        _menu.Opening += (_, _) => BuildMenu();

        _tray = new NotifyIcon { Text = AppName, Visible = true, ContextMenuStrip = _menu };
        _tray.MouseClick += OnTrayClick;
        _tray.BalloonTipClicked += (_, _) => OpenDeviceManager();

        _debounce = new System.Windows.Forms.Timer { Interval = 400 };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };

        _watcher = new DeviceChangeWindow();
        _watcher.DeviceChanged += () => { _debounce.Stop(); _debounce.Start(); };

        _poll = new System.Windows.Forms.Timer { Interval = 5000 };
        _poll.Tick += (_, _) => Refresh();
        _poll.Start();

        Refresh(force: true);
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;

        BuildMenu();
        _menu.Show(Cursor.Position);
        SetForegroundWindow(_menu.Handle);
    }

    // ---------- scanning ----------

    private void Refresh(bool force = false)
    {
        List<string> current = PortScanner.GetPresentPorts();
        List<string> previous = _ports.Select(p => p.Name).ToList();

        if (!force && current.SequenceEqual(previous, StringComparer.OrdinalIgnoreCase))
            return;

        Dictionary<string, DeviceDetail> details = PortScanner.GetDetails();

        var updated = new List<PortInfo>();
        foreach (string name in current)
        {
            details.TryGetValue(name, out DeviceDetail? detail);
            string instance = detail?.InstanceId ?? "";
            (string vid, string pid) = PortScanner.ParseVidPid(instance);

            var port = new PortInfo
            {
                Name = name,
                Description = detail?.Description ?? "",
                Manufacturer = detail?.Manufacturer ?? "",
                InstanceId = instance,
                Vid = vid,
                Pid = pid,
                Serial = PortScanner.ParseSerial(instance)
            };
            port.Nickname = ReadKeyed(NicknamesKeyPath, port.DeviceKey) ?? "";
            updated.Add(port);
        }

        List<PortInfo> added = updated
            .Where(p => !previous.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        List<string> removed = previous
            .Where(n => !current.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();

        _ports = updated;
        UpdateIcon();
        UpdateTooltip();

        if (!_firstScan && _notifyOnChange && (added.Count > 0 || removed.Count > 0))
            ShowChangeBalloon(added, removed);

        _firstScan = false;
    }

    private void ShowChangeBalloon(List<PortInfo> added, List<string> removed)
    {
        string title =
            added.Count == 1 && removed.Count == 0 ? added[0].Name + " connected" :
            removed.Count == 1 && added.Count == 0 ? removed[0] + " disconnected" :
            "COM ports changed";

        var lines = new List<string>();
        foreach (PortInfo p in added)
        {
            string subtitle = p.Nickname.Length > 0 ? p.Nickname : p.Description;
            lines.Add("+ " + (subtitle.Length > 0 ? $"{p.Name}  {subtitle}" : p.Name));
        }
        foreach (string name in removed)
            lines.Add("- " + name);

        _tray.ShowBalloonTip(4000, title, string.Join(Environment.NewLine, lines), ToolTipIcon.Info);
    }

    // ---------- menu ----------

    private void BuildMenu()
    {
        _menu.Items.Clear();

        (List<ActionRule> rules, string? error) = ActionStore.Load();
        if (error is not null && error != _lastReportedConfigError)
        {
            _lastReportedConfigError = error;
            _tray.ShowBalloonTip(6000, "actions.json could not be read", error, ToolTipIcon.Warning);
        }
        else if (error is null)
        {
            _lastReportedConfigError = null;
        }

        _menu.Items.Add(new ToolStripMenuItem(
            _ports.Count == 0 ? "No COM ports"
                              : $"{_ports.Count} COM port{(_ports.Count == 1 ? "" : "s")}")
        { Enabled = false });

        if (_ports.Count > 0)
        {
            _menu.Items.Add(new ToolStripSeparator());
            foreach (PortInfo port in _ports)
                _menu.Items.Add(BuildPortItem(port, rules));

            var copyAll = new ToolStripMenuItem("Copy all as text");
            copyAll.Click += (_, _) => CopyToClipboard(string.Join(
                Environment.NewLine,
                _ports.Select(p => p.Description.Length > 0 ? $"{p.Name}\t{p.Description}" : p.Name)));
            _menu.Items.Add(copyAll);
        }

        _menu.Items.Add(new ToolStripSeparator());

        var deviceManager = new ToolStripMenuItem("Open Device Manager");
        deviceManager.Click += (_, _) => OpenDeviceManager();
        _menu.Items.Add(deviceManager);

        var editActions = new ToolStripMenuItem("Edit actions.json");
        editActions.Click += (_, _) => OpenActionsFile();
        _menu.Items.Add(editActions);

        var refresh = new ToolStripMenuItem("Refresh now");
        refresh.Click += (_, _) => Refresh(force: true);
        _menu.Items.Add(refresh);

        var notify = new ToolStripMenuItem("Notify on connect and disconnect")
        { CheckOnClick = true, Checked = _notifyOnChange };
        notify.CheckedChanged += (s, _) =>
        {
            _notifyOnChange = ((ToolStripMenuItem)s!).Checked;
            SaveSetting("NotifyOnChange", _notifyOnChange);
        };
        _menu.Items.Add(notify);

        var startup = new ToolStripMenuItem("Start with Windows")
        { CheckOnClick = true, Checked = IsInStartup() };
        startup.CheckedChanged += (s, _) => SetStartup(((ToolStripMenuItem)s!).Checked);
        _menu.Items.Add(startup);

        _menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();
        _menu.Items.Add(exit);
    }

    private ToolStripMenuItem BuildPortItem(PortInfo port, List<ActionRule> rules)
    {
        var item = new ToolStripMenuItem(port.MenuLabel);
        int baud = GetBaud(port);
        PortInfo captured = port;

        foreach (ActionRule rule in rules.Where(r => r.AppliesTo(port)))
        {
            ActionRule capturedRule = rule;
            var action = new ToolStripMenuItem(ActionStore.Substitute(rule.Name, port, baud));
            if (rule.Default)
                action.Font = new Font(action.Font, FontStyle.Bold);

            action.Click += (_, _) => RunAction(capturedRule, captured);
            item.DropDownItems.Add(action);
        }

        if (item.DropDownItems.Count > 0)
            item.DropDownItems.Add(new ToolStripSeparator());

        var baudMenu = new ToolStripMenuItem($"Baud: {baud}");
        foreach (int rate in BaudRates)
        {
            int capturedRate = rate;
            var option = new ToolStripMenuItem(rate.ToString(CultureInfo.InvariantCulture))
            { Checked = rate == baud };
            option.Click += (_, _) => SetBaud(captured, capturedRate);
            baudMenu.DropDownItems.Add(option);
        }
        item.DropDownItems.Add(baudMenu);

        var copyName = new ToolStripMenuItem("Copy " + port.Name);
        copyName.Click += (_, _) => CopyToClipboard(captured.Name);
        item.DropDownItems.Add(copyName);

        var copyPython = new ToolStripMenuItem("Copy as Python");
        copyPython.Click += (_, _) => CopyToClipboard(
            $"serial.Serial('{captured.Name}', {GetBaud(captured)}, timeout=1)");
        item.DropDownItems.Add(copyPython);

        var copyHardwareId = new ToolStripMenuItem("Copy hardware ID")
        { Enabled = port.InstanceId.Length > 0 };
        copyHardwareId.Click += (_, _) => CopyToClipboard(captured.InstanceId);
        item.DropDownItems.Add(copyHardwareId);

        var rename = new ToolStripMenuItem(
            port.Nickname.Length > 0 ? "Rename this device..." : "Name this device...");
        rename.Click += (_, _) => RenameDevice(captured);
        item.DropDownItems.Add(rename);

        return item;
    }

    // ---------- actions ----------

    private void RunAction(ActionRule rule, PortInfo port)
    {
        int baud = GetBaud(port);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Environment.ExpandEnvironmentVariables(
                    ActionStore.Substitute(rule.Command, port, baud)),
                Arguments = ActionStore.Substitute(rule.Args, port, baud),
                UseShellExecute = true
            };

            if (rule.Admin)
                psi.Verb = "runas";

            if (rule.WorkingDirectory.Length > 0)
                psi.WorkingDirectory = Environment.ExpandEnvironmentVariables(rule.WorkingDirectory);

            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The UAC prompt was declined. Nothing to report.
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
        {
            _tray.ShowBalloonTip(6000, rule.Name + " could not start",
                $"{rule.Command} was not found. Edit actions.json to point at the right path.",
                ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(6000, rule.Name + " failed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void RenameDevice(PortInfo port)
    {
        string label = port.Description.Length > 0
            ? $"Name for {port.Name} ({port.Description}):"
            : $"Name for {port.Name}:";

        string? entered = Prompt.AskText("Name this device", label, port.Nickname);
        if (entered is null)
            return;

        WriteKeyed(NicknamesKeyPath, port.DeviceKey, entered);
        port.Nickname = entered;
        UpdateTooltip();
    }

    private static int GetBaud(PortInfo port)
    {
        string? stored = ReadKeyed(BaudKeyPath, port.DeviceKey);
        return stored is not null && int.TryParse(stored, out int baud) ? baud : DefaultBaud;
    }

    private static void SetBaud(PortInfo port, int baud) =>
        WriteKeyed(BaudKeyPath, port.DeviceKey, baud.ToString(CultureInfo.InvariantCulture));

    private static void OpenActionsFile()
    {
        try
        {
            ActionStore.EnsureExists();
            Process.Start(new ProcessStartInfo(ActionStore.ResolvePath()) { UseShellExecute = true });
        }
        catch { }
    }

    private static void OpenDeviceManager()
    {
        try
        {
            Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true });
        }
        catch { }
    }

    private static void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try { Clipboard.SetText(text); }
        catch (ExternalException)
        {
            try { Clipboard.SetText(text); } catch { }
        }
    }

    // ---------- presentation ----------

    private void UpdateTooltip()
    {
        string text = _ports.Count == 0
            ? "No COM ports"
            : $"{_ports.Count} COM port{(_ports.Count == 1 ? "" : "s")}: " +
              string.Join(", ", _ports.Select(p => p.Name));

        if (text.Length > 63)
            text = text.Substring(0, 60) + "...";

        _tray.Text = text;
    }

    private void UpdateIcon()
    {
        Icon? old = _icon;
        _icon = CreateCountIcon(_ports.Count);
        _tray.Icon = _icon;
        old?.Dispose();
    }

    private static Icon CreateCountIcon(int count)
    {
        const int size = 32;
        Color fill = count == 0 ? Color.FromArgb(107, 114, 128) : Color.FromArgb(37, 99, 235);
        string label = count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
        float fontSize = label.Length switch { 1 => 20f, 2 => 16f, _ => 12f };

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            using var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), 7);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);

            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            using var format = new StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, textBrush, new RectangleF(0, 0, size, size), format);
        }

        IntPtr handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---------- settings ----------

    private static string? ReadKeyed(string keyPath, string valueName)
    {
        if (string.IsNullOrEmpty(valueName))
            return null;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    private static void WriteKeyed(string keyPath, string valueName, string value)
    {
        if (string.IsNullOrEmpty(valueName))
            return;

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath);
            if (value.Length == 0)
                key.DeleteValue(valueName, throwOnMissingValue: false);
            else
                key.SetValue(valueName, value, RegistryValueKind.String);
        }
        catch { }
    }

    private static bool IsInStartup()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AppName) is not null;
        }
        catch { return false; }
    }

    private static void SetStartup(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(AppName, "\"" + exe + "\"");
            }
            else
            {
                key.DeleteValue(AppName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    private static bool LoadSetting(string name, bool fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            return key?.GetValue(name) is int value ? value != 0 : fallback;
        }
        catch { return fallback; }
    }

    private static void SaveSetting(string name, bool value)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
            key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    // ---------- shutdown ----------

    private void ExitApp()
    {
        _poll.Stop();
        _debounce.Stop();
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _debounce.Dispose();
            _watcher.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _icon?.Dispose();
        }

        base.Dispose(disposing);
    }
}
