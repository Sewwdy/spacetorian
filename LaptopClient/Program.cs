using System;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SpacetorianLaptop
{
    public class Program : Form
    {
        private NotifyIcon trayIcon;
        private ContextMenu trayMenu;
        private TcpClient client;
        private CancellationTokenSource cts;

        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Program());
        }

        public Program()
        {
            // Set up a simple ContextMenu
            trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Connect...", OnConnect);
            trayMenu.MenuItems.Add("Exit", OnExit);

            // Create tray icon
            trayIcon = new NotifyIcon();
            trayIcon.Text = "Spacetorian Laptop Client";
            
            // Generate a simple colored icon since we don't have an .ico
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.DarkBlue);
                g.FillEllipse(Brushes.White, 3, 3, 10, 10);
            }
            trayIcon.Icon = Icon.FromHandle(bmp.GetHicon());
            
            trayIcon.ContextMenu = trayMenu;
            trayIcon.Visible = true;
            
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            this.Load += (s, e) => { this.Hide(); OnConnect(null, null); };
        }

        private string lastIp = "127.0.0.1";
        private string lastName = Environment.MachineName;

        private void OnConnect(object sender, EventArgs e)
        {
            using (var form = new ConnectForm(lastIp, lastName))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    lastIp = form.IPAddress;
                    lastName = form.DisplayName;
                    ConnectToServer(lastIp, lastName);
                }
            }
        }

        private async void ConnectToServer(string ipStr, string name)
        {
            if (client != null && client.Connected)
            {
                if (cts != null) cts.Cancel();
                client.Close();
            }

            client = new TcpClient();
            cts = new CancellationTokenSource();

            try
            {
                trayIcon.Text = string.Format("Connecting to {0}...", ipStr);
                await client.ConnectAsync(ipStr, 8080);
                trayIcon.Text = string.Format("Connected to {0}", ipStr);
                
                // Send HELLO
                var stream = client.GetStream();
                var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync("HELLO:" + name);

                Task.Run(() => HandleConnectionAsync(cts.Token));
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format("Failed to connect to {0}: {1}", ipStr, ex.Message), "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                trayIcon.Text = "Spacetorian Laptop Client";
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
                        if (line == null) break;

                        if (line.StartsWith("SET_BRIGHTNESS:"))
                        {
                            int b;
                            if (int.TryParse(line.Substring(15), out b))
                            {
                                SetBrightness(b);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Disconnected
            }
            finally
            {
                Invoke(new Action(() => {
                    trayIcon.Text = "Spacetorian Laptop Client (Disconnected)";
                }));
            }
        }

        private void SetBrightness(int brightness)
        {
            try
            {
                var scope = new ManagementScope("root\\WMI");
                var query = new SelectQuery("WmiMonitorBrightnessMethods");
                using (var searcher = new ManagementObjectSearcher(scope, query))
                {
                    using (var objectCollection = searcher.Get())
                    {
                        foreach (ManagementObject mObj in objectCollection)
                        {
                            mObj.InvokeMethod("WmiSetBrightness", new object[] { uint.MaxValue, (byte)brightness });
                            break;
                        }
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
            if (cts != null) cts.Cancel();
            if (client != null) client.Close();
            Application.Exit();
        }
    }

    public class ConnectForm : Form
    {
        public string IPAddress { get; private set; }
        public string DisplayName { get; private set; }

        private TextBox txtIp;
        private TextBox txtName;

        public ConnectForm(string defaultIp, string defaultName)
        {
            Text = "Connect to Spacetorian Server";
            Size = new Size(300, 200);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var lblIp = new Label() { Text = "Main PC IP:", Left = 10, Top = 20, Width = 80 };
            txtIp = new TextBox() { Text = defaultIp, Left = 100, Top = 20, Width = 150 };

            var lblName = new Label() { Text = "Display Name:", Left = 10, Top = 60, Width = 80 };
            txtName = new TextBox() { Text = defaultName, Left = 100, Top = 60, Width = 150 };

            var btnOk = new Button() { Text = "Connect", Left = 100, Top = 110, Width = 70 };
            btnOk.Click += (s, e) => {
                IPAddress = txtIp.Text.Trim();
                DisplayName = txtName.Text.Trim();
                if (!string.IsNullOrEmpty(IPAddress)) 
                {
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

            var btnCancel = new Button() { Text = "Cancel", Left = 180, Top = 110, Width = 70 };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(lblIp);
            Controls.Add(txtIp);
            Controls.Add(lblName);
            Controls.Add(txtName);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);
            
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }
    }
}
