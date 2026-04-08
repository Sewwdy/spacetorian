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
            this.Load += (s, e) => { this.Hide(); };
        }

        private void OnConnect(object sender, EventArgs e)
        {
            string ip = Microsoft.VisualBasic.Interaction.InputBox("Enter Main PC IP Address:", "Connect to Spacetorian", "127.0.0.1");
            if (!string.IsNullOrWhiteSpace(ip))
            {
                ConnectToServer(ip);
            }
        }

        private async void ConnectToServer(string ipStr)
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
}
