using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace QMK_Toolbox.Hid
{
    public partial class HidConsoleWindow : Form
    {
        private static HidConsoleWindow instance = null;

        #region Window Events
        public HidConsoleWindow()
        {
            InitializeComponent();
        }

        // When hosted inside the main window's tab control, show only the log.
        // The device list remains present internally with "all devices" active.
        // This gives the Flash Log and HID Console the same exact display bounds.
        public void ConfigureEmbeddedLayout()
        {
            SuspendLayout();
            consoleList.Visible = false;
            Padding = Padding.Empty;
            logTextBox.Anchor = AnchorStyles.None;
            logTextBox.Margin = Padding.Empty;
            logTextBox.Dock = DockStyle.Fill;
            ResumeLayout(true);
        }

        private bool hidListenerStarted;

        public void StartHidListening()
        {
            if (hidListenerStarted)
            {
                return;
            }
            hidListenerStarted = true;
            // A background HID event may arrive immediately. Ensure Invoke()
            // already has a window handle, even when this is an inactive tab.
            _ = Handle;
            hidListener.hidDeviceConnected += HidDeviceConnected;
            hidListener.hidDeviceDisconnected += HidDeviceDisconnected;
            hidListener.consoleReportReceived += ConsoleReportReceived;
            hidListener.Start();
        }

        public static HidConsoleWindow GetInstance()
        {
            if (instance == null)
            {
                instance = new HidConsoleWindow();
                instance.FormClosed += delegate { instance = null; };
            }

            return instance;
        }

        private void HidConsoleWindow_Load(object sender, EventArgs e)
        {
            if (TopLevel)
            {
                CenterToParent();
            }

            // The parent starts an embedded listener after its window appears.
            // Standalone HID Console windows retain the original behavior.
            if (TopLevel)
            {
                StartHidListening();
            }
        }

        private void HidConsoleWindow_FormClosing(object sender, FormClosingEventArgs e)
        {
            hidListener.hidDeviceConnected -= HidDeviceConnected;
            hidListener.hidDeviceDisconnected -= HidDeviceDisconnected;
            hidListener.consoleReportReceived -= ConsoleReportReceived;
            hidListener.Dispose();
        }
        #endregion

        #region HID Console
        private readonly HidListener hidListener = new();

        private HidConsoleDevice lastReportedDevice;

        // Custom VIA command used by the matching V6 Max firmware:
        // [07, F1, 01, enable, 'V', '6', 'L', 'G', ...]
        // QMK VIA echoes the command after applying the requested state.
        public async Task<bool> SetKeyboardLoggingAsync(bool enabled)
        {
            const ushort vendorId = 0x3434;
            const ushort productId = 0x0960;

            var device = hidListener.Devices?
                .OfType<RawDevice>()
                .FirstOrDefault(d => d.VendorId == vendorId && d.ProductId == productId);

            if (device == null)
            {
                logTextBox.LogError("Keychron V6 Max Raw HID not found. Connect with a USB cable.");
                return false;
            }

            try
            {
                bool accepted = await Task.Run(() =>
                {
                    var hid = device.HidDevice;
                    hid.OpenDevice();
                    try
                    {
                        int outputLength = hid.Capabilities.OutputReportByteLength;
                        if (outputLength != 33)
                        {
                            return false; // 32-byte VIA payload plus report ID
                        }

                        var report = new byte[outputLength];
                        report[0] = 0;       // HID report ID
                        report[1] = 0x07;    // VIA id_custom_set_value
                        report[2] = 0xF1;    // reserved V6 Max log channel
                        report[3] = 0x01;    // log control
                        report[4] = enabled ? (byte)1 : (byte)0;
                        report[5] = (byte)'V';
                        report[6] = (byte)'6';
                        report[7] = (byte)'L';
                        report[8] = (byte)'G';

                        if (!hid.Write(report))
                        {
                            return false;
                        }

                        var response = hid.ReadReport(1200);
                        var data = response?.Data;
                        return data != null && data.Length >= 8 &&
                               data[0] == 0x07 && data[1] == 0xF1 &&
                               data[2] == 0x01 && data[3] == (enabled ? 1 : 0) &&
                               data[4] == (byte)'V' && data[5] == (byte)'6' &&
                               data[6] == (byte)'L' && data[7] == (byte)'G';
                    }
                    finally
                    {
                        hid.CloseDevice();
                    }
                });

                if (accepted)
                {
                    logTextBox.LogHid(enabled
                        ? "Keyboard confirmed: HID key logging ON"
                        : "Keyboard confirmed: HID key logging OFF");
                }
                else
                {
                    logTextBox.LogError("No matching HID reply. Flash the V6 Max PC-log-control firmware first.");
                }
                return accepted;
            }
            catch (Exception ex)
            {
                logTextBox.LogError($"HID log control failed: {ex.Message}");
                return false;
            }
        }

        private void HidDeviceConnected(BaseHidDevice device)
        {
            Invoke(new Action(() =>
            {
                if (device is HidConsoleDevice)
                {
                    lastReportedDevice = device as HidConsoleDevice;
                    UpdateConsoleList();
                    logTextBox.LogHid($"HID console connected: {device}");
                }
                else
                {
                    logTextBox.LogHid($"Raw HID device connected: {device}");
                }
            }));
        }

        private void HidDeviceDisconnected(BaseHidDevice device)
        {
            Invoke(new Action(() =>
            {
                if (device is HidConsoleDevice)
                {
                    lastReportedDevice = null;
                    UpdateConsoleList();
                    logTextBox.LogHid($"HID console disconnected: {device}");
                }
                else
                {
                    logTextBox.LogHid($"Raw HID device disconnected: {device}");
                }
            }));
        }

        private void ConsoleReportReceived(HidConsoleDevice device, string report)
        {
            Invoke(new Action(() =>
            {
                int selectedDevice = consoleList.SelectedIndex;
                var consoleDevices = hidListener.Devices.Where(d => d is HidConsoleDevice).ToList();
                if (selectedDevice == 0 || consoleDevices[selectedDevice - 1] == device)
                {
                    if (lastReportedDevice != device)
                    {
                        logTextBox.LogHid($"{device.ManufacturerString} {device.ProductString}:");
                        lastReportedDevice = device;
                    }
                    logTextBox.LogHidOutput(report);
                }
            }));
        }

        private void UpdateConsoleList()
        {
            var selected = consoleList.SelectedIndex != -1 ? consoleList.SelectedIndex : 0;
            consoleList.Items.Clear();

            foreach (var device in hidListener.Devices.Where(d => d is HidConsoleDevice))
            {
                consoleList.Items.Add(device.ToString());
            }

            if (consoleList.Items.Count > 0)
            {
                consoleList.Items.Insert(0, "(All connected devices)");
                consoleList.SelectedIndex = consoleList.Items.Count > selected ? selected : 0;
            }
        }
        #endregion

        #region Log Box
        private void LogContextMenuStrip_Opening(object sender, CancelEventArgs e)
        {
            copyToolStripMenuItem.Enabled = logTextBox.SelectedText.Length > 0;
            selectAllToolStripMenuItem.Enabled = logTextBox.Text.Length > 0;
            clearToolStripMenuItem.Enabled = logTextBox.Text.Length > 0;
        }

        private void CopyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            logTextBox.Copy();
        }

        private void SelectAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            logTextBox.SelectAll();
        }
        private void ClearToolStripMenuItem_Click(object sender, EventArgs e)
        {
            logTextBox.Clear();
        }
        #endregion
    }
}
