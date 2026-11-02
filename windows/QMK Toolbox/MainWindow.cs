using QMK_Toolbox.Helpers;
using QMK_Toolbox.Hid;
using QMK_Toolbox.KeyTester;
using QMK_Toolbox.Properties;
using QMK_Toolbox.Usb;
using QMK_Toolbox.Usb.Bootloader;
using Syroot.Windows.IO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QMK_Toolbox
{
    public partial class MainWindow : Form
    {
        private readonly WindowState windowState = new();

        private readonly string _filePassedIn = string.Empty;

        private HidConsoleWindow embeddedHidConsole;
        private Button hidLogButton;
        private bool keyboardLogEnabled;

        // The HID console is always visible; this button only asks the firmware
        // to start/stop emitting key events.
        private void InitializeHidLogWorkspace()
        {
            SuspendLayout();

            // Enlarge the initial window so both logs are usable.
            Width = Math.Max(Width, 1320);
            MinimumSize = new Size(1050, 600);

            Controls.Remove(logTextBox);
            logTextBox.Anchor = AnchorStyles.None;
            logTextBox.Margin = new Padding(0);
            // Dock must be assigned last: assigning Anchor after Dock can
            // cancel Fill and leave the old designer coordinates in effect.
            logTextBox.Dock = DockStyle.Fill;

            var split = new SplitContainer
            {
                Name = "logSplit",
                Location = new Point(14, 133),
                Size = new Size(ClientSize.Width - 28, ClientSize.Height - 146),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6,
                Panel1MinSize = 250,
                Panel2MinSize = 250
            };
            split.SplitterDistance = Math.Max(250, split.Width / 2);

            // Match the embedded HID console's log rectangle:
            // 36px header + 45px device-picker area, then 14px side/bottom margins.
            // Both dark log boxes now start and end on the same horizontal lines.
            var flashLogHost = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(14, 81, 14, 14)
            };
            flashLogHost.Controls.Add(logTextBox);
            flashLogHost.Controls.Add(new Label
            {
                Text = "Flash Log",
                Location = new Point(4, 4),
                Size = new Size(120, 28),
                TextAlign = ContentAlignment.MiddleLeft
            });
            split.Panel1.Controls.Add(flashLogHost);

            var rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            rightLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var header = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 3, 4, 3) };
            header.Controls.Add(new Label
            {
                Text = "HID Console",
                Dock = DockStyle.Left,
                AutoSize = false,
                Width = 120,
                TextAlign = ContentAlignment.MiddleLeft
            });

            hidLogButton = new Button
            {
                Text = "Enable HID Log",
                Dock = DockStyle.Right,
                Width = 145
            };
            hidLogButton.Click += async (sender, e) =>
            {
                var requestedState = !keyboardLogEnabled;
                hidLogButton.Enabled = false;
                try
                {
                    if (await embeddedHidConsole.SetKeyboardLoggingAsync(requestedState))
                    {
                        keyboardLogEnabled = requestedState;
                        hidLogButton.Text = keyboardLogEnabled ? "Disable HID Log" : "Enable HID Log";
                    }
                }
                finally
                {
                    hidLogButton.Enabled = true;
                }
            };
            header.Controls.Add(hidLogButton);
            rightLayout.Controls.Add(header, 0, 0);

            // Reuse the existing HID listener, avoiding a second reader.
            embeddedHidConsole = HidConsoleWindow.GetInstance();
            embeddedHidConsole.TopLevel = false;
            embeddedHidConsole.FormBorderStyle = FormBorderStyle.None;
            embeddedHidConsole.MinimumSize = Size.Empty;
            embeddedHidConsole.MaximumSize = Size.Empty;
            embeddedHidConsole.Dock = DockStyle.Fill;
            rightLayout.Controls.Add(embeddedHidConsole, 0, 1);
            split.Panel2.Controls.Add(rightLayout);
            Controls.Add(split);
            embeddedHidConsole.Show();
            ResumeLayout(true);
        }

        #region Window Events
        public MainWindow()
        {
            InitializeComponent();
            InitializeHidLogWorkspace();
        }

        public MainWindow(string path) : this()
        {
            if (path != string.Empty)
            {
                var extension = Path.GetExtension(path)?.ToLower();
                if (extension == ".hex" || extension == ".bin")
                {
                    _filePassedIn = path;
                }
                else
                {
                    MessageBox.Show("QMK Toolbox doesn't support this kind of file", "File Type Error", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
            }
        }

        private void MainWindow_Load(object sender, EventArgs e)
        {
            windowStateBindingSource.DataSource = windowState;
            windowState.PropertyChanged += AutoFlashEnabledChanged;
            windowState.PropertyChanged += ShowAllDevicesEnabledChanged;
            windowState.ShowAllDevices = Settings.Default.showAllDevices;

            if (Settings.Default.hexFileCollection != null)
            {
                filepathBox.Items.AddRange(Settings.Default.hexFileCollection.ToArray());
            }

            mcuBox.SelectedValue = Settings.Default.targetSetting;

            logTextBox.LogInfo($"QMK Toolbox {Application.ProductVersion} (https://qmk.fm/toolbox)");
            logTextBox.LogInfo("Supported bootloaders:");
            logTextBox.LogInfo(" - ARM DFU (APM32, Kiibohd, STM32, STM32duino) and RISC-V DFU (GD32V) via dfu-util (http://dfu-util.sourceforge.net/)");
            logTextBox.LogInfo(" - Atmel/LUFA/QMK DFU via dfu-programmer (http://dfu-programmer.github.io/)");
            logTextBox.LogInfo(" - Atmel SAM-BA (Massdrop) via Massdrop Loader (https://github.com/massdrop/mdloader)");
            logTextBox.LogInfo(" - BootloadHID (Atmel, PS2AVRGB) via bootloadHID (https://www.obdev.at/products/vusb/bootloadhid.html)");
            logTextBox.LogInfo(" - Caterina (Arduino, Pro Micro) via avrdude (http://nongnu.org/avrdude/)");
            logTextBox.LogInfo(" - HalfKay (Teensy, Ergodox EZ) via Teensy Loader (https://pjrc.com/teensy/loader_cli.html)");
            logTextBox.LogInfo(" - LUFA/QMK HID via hid_bootloader_cli (https://github.com/abcminiuser/lufa)");
            logTextBox.LogInfo(" - WB32 DFU via wb32-dfu-updater_cli (https://github.com/WestberryTech/wb32-dfu-updater)");
            logTextBox.LogInfo(" - LUFA Mass Storage");
            logTextBox.LogInfo("Supported ISP flashers:");
            logTextBox.LogInfo(" - AVRISP (Arduino ISP)");
            logTextBox.LogInfo(" - USBasp (AVR ISP)");
            logTextBox.LogInfo(" - USBTiny (AVR Pocket)");

            usbListener.usbDeviceConnected += UsbDeviceConnected;
            usbListener.usbDeviceDisconnected += UsbDeviceDisconnected;
            usbListener.bootloaderDeviceConnected += BootloaderDeviceConnected;
            usbListener.bootloaderDeviceDisconnected += BootloaderDeviceDisconnected;
            usbListener.outputReceived += BootloaderCommandOutputReceived;

            try
            {
                usbListener.Start();
            }
            catch (COMException ex)
            {
                logTextBox.LogError("USB device enumeration failed.");
                logTextBox.LogError($"{ex}");
            }

            if (_filePassedIn != string.Empty)
            {
                SetFilePath(_filePassedIn);
            }

            EnableUI();
        }

        private void MainWindow_Shown(object sender, EventArgs e)
        {
            if (Settings.Default.firstStart)
            {
                EmbeddedResourceHelper.InitResourceFolder();
                Settings.Default.Upgrade();
            }

            if (Settings.Default.firstStart)
            {
                DriverInstaller.DisplayPrompt();
                Settings.Default.firstStart = false;
                Settings.Default.Save();
            }
        }

        private void MainWindow_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.Length == 1)
                {
                    var extension = Path.GetExtension(files.First())?.ToLower();
                    if (extension == ".hex" || extension == ".bin")
                    {
                        e.Effect = DragDropEffects.Copy;
                    }
                }
            }
        }

        private void MainWindow_DragDrop(object sender, DragEventArgs e)
        {
            SetFilePath(((string[])e.Data.GetData(DataFormats.FileDrop, false)).First());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WmShowme)
            {
                ShowMe();
                if (File.Exists(Path.Combine(Path.GetTempPath(), "qmk_toolbox_file.txt")))
                {
                    using (var sr = new StreamReader(Path.Combine(Path.GetTempPath(), "qmk_toolbox_file.txt")))
                    {
                        SetFilePath(sr.ReadLine());
                    }
                    File.Delete(Path.Combine(Path.GetTempPath(), "qmk_toolbox_file.txt"));
                }
            }

            base.WndProc(ref m);
        }

        private void ShowMe()
        {
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }
            Activate();
        }

        private void MainWindow_FormClosing(object sender, FormClosingEventArgs e)
        {
            var arraylist = new ArrayList(filepathBox.Items);
            Settings.Default.hexFileCollection = arraylist;
            Settings.Default.targetSetting = (string)mcuBox.SelectedValue;
            Settings.Default.Save();

            embeddedHidConsole?.Close();
            usbListener.Dispose();
        }

        private void MainWindow_FormClosed(object sender, FormClosedEventArgs e)
        {
            Settings.Default.Save();
        }
        #endregion Window Events

        #region USB Devices & Bootloaders
        private readonly UsbListener usbListener = new();

        private void BootloaderDeviceConnected(BootloaderDevice device)
        {
            Invoke(new Action(() =>
            {
                logTextBox.LogBootloader($"{device.Name} device connected ({device.Driver}): {device}");

                if (device.PreferredDriver != device.Driver)
                {
                    logTextBox.LogError($"{device.Name} device has {device.Driver} driver assigned but should be {device.PreferredDriver}. Flashing may not succeed.");
                }

                if (windowState.AutoFlashEnabled)
                {
                    FlashAllAsync();
                }
                else
                {
                    EnableUI();
                }
            }));
        }

        private void BootloaderDeviceDisconnected(BootloaderDevice device)
        {
            Invoke(new Action(() =>
            {
                logTextBox.LogBootloader($"{device.Name} device disconnected ({device.Driver}): {device}");

                if (!windowState.AutoFlashEnabled)
                {
                    EnableUI();
                }
            }));
        }

        private void BootloaderCommandOutputReceived(BootloaderDevice device, string data, MessageType type)
        {
            Invoke(new Action(() =>
            {
                logTextBox.Log(data, type);
            }));
        }

        private void UsbDeviceConnected(UsbDevice device)
        {
            Invoke(new Action(() =>
            {
                if (windowState.ShowAllDevices)
                {
                    logTextBox.LogUsb($"USB device connected ({device.Driver}): {device}");
                }
            }));
        }

        private void UsbDeviceDisconnected(UsbDevice device)
        {
            Invoke(new Action(() =>
            {
                if (windowState.ShowAllDevices)
                {
                    logTextBox.LogUsb($"USB device disconnected ({device.Driver}): {device}");
                }
            }));
        }
        #endregion

        #region UI Interaction
        private void AutoFlashEnabledChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "AutoFlashEnabled")
            {
                if (windowState.AutoFlashEnabled)
                {
                    logTextBox.LogInfo("Auto-flash enabled");
                    DisableUI();
                }
                else
                {
                    logTextBox.LogInfo("Auto-flash disabled");
                    EnableUI();
                }
            }
        }

        private void ShowAllDevicesEnabledChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "ShowAllDevices")
            {
                Settings.Default.showAllDevices = windowState.ShowAllDevices;
            }
        }

        private async void FlashAllAsync()
        {
            string selectedMcu = (string)mcuBox.SelectedValue;
            string filePath = filepathBox.Text;

            if (filePath.Length == 0)
            {
                logTextBox.LogError("Please select a file");
                return;
            }

            if (!File.Exists(filePath))
            {
                logTextBox.LogError("File does not exist!");
                return;
            }

            if (!windowState.AutoFlashEnabled)
            {
                Invoke(new Action(DisableUI));
            }

            foreach (BootloaderDevice b in FindBootloaders())
            {
                logTextBox.LogBootloader("Attempting to flash, please don't remove device");
                await b.Flash(selectedMcu, filePath);
                logTextBox.LogBootloader("Flash complete");
            }

            if (!windowState.AutoFlashEnabled)
            {
                Invoke(new Action(EnableUI));
            }
        }

        private async void ResetAllAsync()
        {
            string selectedMcu = (string)mcuBox.SelectedValue;

            if (!windowState.AutoFlashEnabled)
            {
                Invoke(new Action(DisableUI));
            }

            foreach (BootloaderDevice b in FindBootloaders())
            {
                if (b.IsResettable)
                {
                    await b.Reset(selectedMcu);
                }
            }

            if (!windowState.AutoFlashEnabled)
            {
                Invoke(new Action(EnableUI));
            }
        }

        private async void ClearEepromAllAsync()
        {
            string selectedMcu = (string)mcuBox.SelectedValue;

            if (!windowState.AutoFlashEnabled)
            {
                Invoke(new Action(DisableUI));
            }

            foreach (BootloaderDevice b in FindBootloaders())
            {
                if (b.IsEepromFlashable)
                {
                    logTextBox.LogBootloader("Attempting to clear EEPROM, please don't remove device");
                    await b.FlashEeprom(selectedMcu, "reset.eep");
                    logTextBox.LogBootloader("EEPROM clear complete");
                }
            }

            if (!windowState.AutoFlashEnabled)
            {
                Invoke(new Action(EnableUI));
            }
        }

        private async void SetHandednessAllAsync(bool left)
        {
            string selectedMcu = (string)mcuBox.SelectedValue;
            string file = left ? "reset_left.eep" : "reset_right.eep";

            if (!windowState.AutoFlashEnabled)
            {
                Invoke(new Action(DisableUI));
            }

            foreach (BootloaderDevice b in FindBootloaders())
            {
                if (b.IsEepromFlashable)
                {
                    logTextBox.LogBootloader("Attempting to set handedness, please don't remove device");
                    await b.FlashEeprom(selectedMcu, file);
                    logTextBox.LogBootloader("EEPROM write complete");
                }
            }

            if (!windowState.AutoFlashEnabled)
            {
                Invoke(new Action(EnableUI));
            }
        }

        private void FlashButton_Click(object sender, EventArgs e)
        {
            FlashAllAsync();
        }

        private void ResetButton_Click(object sender, EventArgs e)
        {
            ResetAllAsync();
        }

        private void ClearEepromButton_Click(object sender, EventArgs e)
        {
            ClearEepromAllAsync();
        }

        private void SetHandednessButton_Click(object sender, EventArgs e)
        {
            SetHandednessAllAsync(sender == eepromLeftToolStripMenuItem);
        }

        private List<BootloaderDevice> FindBootloaders()
        {
            return usbListener.Devices.Where(d => d is BootloaderDevice).Select(b => b as BootloaderDevice).ToList();
        }

        private void OpenFileButton_Click(object sender, EventArgs e)
        {
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                SetFilePath(openFileDialog.FileName);
            }
        }

        private void FilepathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                SetFilePath(filepathBox.Text);
                e.Handled = true;
            }
        }

        private void SetFilePath(string filepath)
        {
            if (!string.IsNullOrEmpty(filepath))
            {
                if (filepath.StartsWith("qmk:"))
                {
                    string unwrappedUrl = filepath[(filepath.StartsWith("qmk://") ? 6 : 4)..];
                    DownloadFile(unwrappedUrl);
                }
                else
                {
                    LoadLocalFile(filepath);
                }
            }
        }

        private void LoadLocalFile(string path)
        {
            if (!filepathBox.Items.Contains(path))
            {
                filepathBox.Items.Add(path);
            }
            filepathBox.SelectedItem = path;
        }

        private async void DownloadFile(string url)
        {
            logTextBox.LogInfo($"Downloading the file: {url}");

            try
            {
                string destFile = Path.Combine(KnownFolders.Downloads.Path, url[(url.LastIndexOf("/") + 1)..]);

                var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("QMK Toolbox");

                var response = await client.GetAsync(url);
                using (var fs = new FileStream(destFile, FileMode.CreateNew))
                {
                    await response.Content.CopyToAsync(fs);
                    logTextBox.LogInfo($"File saved to: {destFile}");
                }

                LoadLocalFile(destFile);
            }
            catch (Exception e)
            {
                logTextBox.LogError($"Could not download file: {e.Message}");
            }
        }

        private void DisableUI()
        {
            windowState.CanFlash = false;
            windowState.CanReset = false;
            windowState.CanClearEeprom = false;
        }

        private void EnableUI()
        {
            List<BootloaderDevice> bootloaders = FindBootloaders();
            windowState.CanFlash = bootloaders.Any();
            windowState.CanReset = bootloaders.Any(b => b.IsResettable);
            windowState.CanClearEeprom = bootloaders.Any(b => b.IsEepromFlashable);
        }

        private void ExitMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private void AboutMenuItem_Click(object sender, EventArgs e)
        {
            new AboutBox().ShowDialog();
        }

        private void InstallDriversMenuItem_Click(object sender, EventArgs e)
        {
            DriverInstaller.DisplayPrompt();
        }

        private void ClearResourcesMenuItem_Click(object sender, EventArgs e)
        {
            EmbeddedResourceHelper.InitResourceFolder();
        }

        private void KeyTesterToolStripMenuItem_Click(object sender, EventArgs e)
        {
            KeyTesterWindow.GetInstance().Show(this);
            KeyTesterWindow.GetInstance().Focus();
        }

        private void HidConsoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            embeddedHidConsole.Focus();
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
