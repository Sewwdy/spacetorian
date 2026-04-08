using System;
using System.Drawing;
using System.IO;
using System.Management;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SpacetorianViewerClient
{
    public class Program : Form
    {
        private const string SingleInstanceMutexName = @"Local\SpacetorianViewerClient";

        private static readonly string SettingsDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpacetorianViewerClient");

        private static readonly string SettingsFilePath = Path.Combine(SettingsDirectoryPath, "connection.txt");

        private NotifyIcon trayIcon;
        private ContextMenu trayMenu;
        private TcpClient client;
        private CancellationTokenSource cts;

        private string lastIp = "127.0.0.1";
        private string lastName = Environment.MachineName;

        [STAThread]
        public static void Main()
        {
            bool isCreated;
            using (var mutex = new Mutex(true, SingleInstanceMutexName, out isCreated))
            {
                if (!isCreated)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Program());
            }
        }

        public Program()
        {
            LoadStoredConnection();

            trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Open Viewer Client", OnOpenViewerClient);
            trayMenu.MenuItems.Add("Exit", OnExit);

            trayIcon = new NotifyIcon();
            trayIcon.Text = "Spacetorian Viewer Client";

            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(24, 95, 191));
                g.FillEllipse(Brushes.White, 3, 3, 10, 10);
            }
            trayIcon.Icon = Icon.FromHandle(bmp.GetHicon());

            trayIcon.ContextMenu = trayMenu;
            trayIcon.Visible = true;
            trayIcon.MouseClick += OnTrayIconMouseClick;

            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.SizableToolWindow;
            Load += (_, __) =>
            {
                Hide();
                BeginInvoke(new Action(() => OnOpenViewerClient(this, EventArgs.Empty)));
            };
        }

        private void OnTrayIconMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                OnOpenViewerClient(sender, EventArgs.Empty);
            }
        }

        private void OnOpenViewerClient(object sender, EventArgs e)
        {
            using (var form = new ViewerConnectForm(lastIp, lastName))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    lastIp = form.IPAddress;
                    lastName = form.DisplayName;
                    SaveStoredConnection();
                    ConnectToServer(lastIp, lastName);
                }
            }
        }

        private void LoadStoredConnection()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return;

                string[] lines = File.ReadAllLines(SettingsFilePath);
                if ((lines.Length > 0) && !string.IsNullOrWhiteSpace(lines[0]))
                {
                    lastIp = lines[0].Trim();
                }
                if ((lines.Length > 1) && !string.IsNullOrWhiteSpace(lines[1]))
                {
                    lastName = lines[1].Trim();
                }
            }
            catch
            {
                // Ignore storage read errors and continue with defaults.
            }
        }

        private void SaveStoredConnection()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectoryPath);
                File.WriteAllLines(SettingsFilePath, new[]
                {
                    lastIp ?? string.Empty,
                    lastName ?? string.Empty,
                });
            }
            catch
            {
                // Ignore storage write errors.
            }
        }

        private async void ConnectToServer(string ipStr, string name)
        {
            if (client != null && client.Connected)
            {
                cts?.Cancel();
                client.Close();
            }

            client = new TcpClient();
            cts = new CancellationTokenSource();

            try
            {
                trayIcon.Text = string.Format("Viewer Client: Connecting to {0}...", ipStr);
                await client.ConnectAsync(ipStr, 8080);
                trayIcon.Text = string.Format("Viewer Client: Connected to {0}", ipStr);

                var stream = client.GetStream();
                var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync("HELLO:" + name);

                _ = Task.Run(() => HandleConnectionAsync(cts.Token));
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    string.Format("Failed to connect to {0}: {1}", ipStr, ex.Message),
                    "Viewer Client Connection Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                trayIcon.Text = "Spacetorian Viewer Client";
            }
        }

        private async Task HandleConnectionAsync(CancellationToken token)
        {
            try
            {
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (!token.IsCancellationRequested && client.Connected)
                    {
                        string line = await reader.ReadLineAsync();
                        if (line == null)
                            break;

                        if (line.StartsWith("SET_BRIGHTNESS:"))
                        {
                            if (int.TryParse(line.Substring(15), out int brightness))
                            {
                                SetBrightness(brightness);
                            }
                        }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (!IsDisposed)
                {
                    BeginInvoke(new Action(() =>
                    {
                        trayIcon.Text = "Spacetorian Viewer Client (Disconnected)";
                    }));
                }
            }
        }

        private void SetBrightness(int brightness)
        {
            try
            {
                var scope = new ManagementScope("root\\WMI");
                var query = new SelectQuery("WmiMonitorBrightnessMethods");
                using (var searcher = new ManagementObjectSearcher(scope, query))
                using (var objectCollection = searcher.Get())
                {
                    foreach (ManagementObject mObj in objectCollection)
                    {
                        mObj.InvokeMethod("WmiSetBrightness", new object[] { uint.MaxValue, (byte)brightness });
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Brightness set error: " + ex.Message);
            }
        }

        private void OnExit(object sender, EventArgs e)
        {
            trayIcon.Visible = false;
            trayIcon.Dispose();
            cts?.Cancel();
            client?.Close();
            Application.Exit();
        }
    }

    public class ViewerConnectForm : Form
    {
        public string IPAddress { get; private set; }
        public string DisplayName { get; private set; }

        private readonly TextBox txtIp;
        private readonly TextBox txtName;

        public ViewerConnectForm(string defaultIp, string defaultName)
        {
            Text = "Spacetorian Viewer Client";
            ClientSize = new Size(420, 260);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 204);

            bool darkMode = IsDarkModeEnabled();
            Color backgroundColor = darkMode ? Color.FromArgb(32, 32, 32) : SystemColors.Window;
            Color inputColor = darkMode ? Color.FromArgb(44, 44, 48) : Color.FromArgb(245, 245, 245);
            Color textColor = darkMode ? Color.WhiteSmoke : SystemColors.ControlText;
            Color accentColor = SystemColors.Highlight;

            BackColor = backgroundColor;
            ForeColor = textColor;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 18, 20, 18),
                ColumnCount = 1,
                RowCount = 7,
                BackColor = Color.Transparent,
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = new Label
            {
                Text = "Viewer Client",
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold, GraphicsUnit.Point, 204),
                ForeColor = textColor,
                Margin = new Padding(0, 0, 0, 2)
            };

            var subtitle = new Label
            {
                Text = "Connect this PC as a remote brightness viewer.",
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point, 204),
                ForeColor = darkMode ? Color.Gainsboro : Color.FromArgb(70, 70, 70),
                Margin = new Padding(0, 0, 0, 0)
            };

            var fields = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 2,
                AutoSize = true,
                Margin = new Padding(0),
            };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var lblIp = CreateLabel("Main PC IP", textColor);
            var lblName = CreateLabel("Viewer Name", textColor);

            txtIp = CreateTextBox(defaultIp, inputColor, textColor);
            txtName = CreateTextBox(defaultName, inputColor, textColor);

            fields.Controls.Add(lblIp, 0, 0);
            fields.Controls.Add(txtIp, 1, 0);
            fields.Controls.Add(lblName, 0, 1);
            fields.Controls.Add(txtName, 1, 1);

            var buttonBar = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                AutoSize = true,
                WrapContents = false
            };

            var btnConnect = CreatePrimaryButton("Connect", accentColor, darkMode);
            btnConnect.Click += OnConnectClicked;

            var btnCancel = CreateSecondaryButton("Cancel", darkMode, textColor);
            btnCancel.Click += (_, __) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };

            buttonBar.Controls.Add(btnConnect);
            buttonBar.Controls.Add(btnCancel);

            root.Controls.Add(title, 0, 0);
            root.Controls.Add(subtitle, 0, 1);
            root.Controls.Add(fields, 0, 3);
            root.Controls.Add(buttonBar, 0, 6);

            Controls.Add(root);

            AcceptButton = btnConnect;
            CancelButton = btnCancel;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyWindows11Backdrop();
        }

        private void OnConnectClicked(object sender, EventArgs e)
        {
            string ip = txtIp.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                MessageBox.Show("Please enter Main PC IP.", "Viewer Client", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            IPAddress = ip;
            DisplayName = string.IsNullOrWhiteSpace(txtName.Text) ? Environment.MachineName : txtName.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        }

        private static Label CreateLabel(string text, Color color)
        {
            return new Label
            {
                Text = text,
                ForeColor = color,
                AutoSize = true,
                Margin = new Padding(0, 9, 8, 0),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point, 204)
            };
        }

        private static TextBox CreateTextBox(string value, Color backColor, Color foreColor)
        {
            return new TextBox
            {
                Text = value,
                Width = 220,
                Margin = new Padding(0, 4, 0, 8),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = backColor,
                ForeColor = foreColor,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point, 204)
            };
        }

        private static Button CreatePrimaryButton(string text, Color accent, bool darkMode)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(14, 6, 14, 6),
                BackColor = accent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(8, 0, 0, 0)
            };
        }

        private static Button CreateSecondaryButton(string text, bool darkMode, Color textColor)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(14, 6, 14, 6),
                BackColor = darkMode ? Color.FromArgb(56, 56, 56) : Color.FromArgb(233, 233, 233),
                ForeColor = textColor,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(8, 0, 0, 0)
            };
        }

        private static bool IsDarkModeEnabled()
        {
            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme",
                    1);

                if (value is int mode)
                {
                    return mode == 0;
                }
            }
            catch
            {
            }

            return false;
        }

        private void ApplyWindows11Backdrop()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            if (Environment.OSVersion.Version < new Version(10, 0, 22000, 0))
                return;

            int useDark = IsDarkModeEnabled() ? 1 : 0;
            int rounded = 2;
            int backdropType = 2;

            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, sizeof(int));
            DwmSetWindowAttribute(Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

            var margins = new MARGINS { cxLeftWidth = -1 };
            DwmExtendFrameIntoClientArea(Handle, ref margins);
        }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);
    }
}
