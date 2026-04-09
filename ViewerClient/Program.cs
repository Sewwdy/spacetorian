
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
    public sealed class ConnectionAttemptResult
    {
        public bool Success { get; }
        public string Message { get; }

        private ConnectionAttemptResult(bool success, string message)
        {
            Success = success;
            Message = message;
        }

        public static ConnectionAttemptResult Ok(string message = "Connected.")
        {
            return new ConnectionAttemptResult(true, message);
        }

        public static ConnectionAttemptResult Fail(string message)
        {
            return new ConnectionAttemptResult(false, message);
        }
    }

    public class Program : Form
    {
        private const string SingleInstanceMutexName = @"Local\SpacetorianViewerClient";
        private const string OpenWindowEventName = @"Local\SpacetorianViewerClient.OpenWindow";

        private static readonly string SettingsDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpacetorianViewerClient");

        private static readonly string SettingsFilePath = Path.Combine(SettingsDirectoryPath, "connection.txt");

        private readonly object writerLock = new object();
        private readonly object reconnectSync = new object();
        private readonly object brightnessQueueLock = new object();
        private int pendingBrightness = -1;
        private int brightnessWorkerActive;

        private NotifyIcon trayIcon;
        private ContextMenu trayMenu;
        private TcpClient client;
        private CancellationTokenSource cts;
        private StreamWriter writer;

        private EventWaitHandle openWindowEvent;
        private RegisteredWaitHandle openWindowRegistration;

        private ViewerConnectForm activeConnectDialog;
        private ViewerReconnectForm reconnectDialog;

        private CancellationTokenSource reconnectLoopCts;
        private bool isExiting;
        private bool closingReconnectWindowInternally;
        private int activeConnectionId;

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
                    try
                    {
                        using (var openWindowSignal = EventWaitHandle.OpenExisting(OpenWindowEventName))
                        {
                            openWindowSignal.Set();
                        }
                    }
                    catch
                    {
                    }

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

            openWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, OpenWindowEventName);
            openWindowRegistration = ThreadPool.RegisterWaitForSingleObject(
                openWindowEvent,
                (_, __) =>
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(OpenOrFocusViewerWindow));
                    }
                },
                null,
                Timeout.Infinite,
                false);

            trayMenu = new ContextMenu();
            trayMenu.MenuItems.Add("Open Viewer Client", OnOpenViewerClient);
            trayMenu.MenuItems.Add("Exit", OnExit);

            trayIcon = new NotifyIcon();
            SetTrayText("Spacetorian Viewer Client");

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
                BeginInvoke(new Action(OpenOrFocusViewerWindow));
            };
        }

        private void OnTrayIconMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                OpenOrFocusViewerWindow();
            }
        }

        private void OnOpenViewerClient(object sender, EventArgs e)
        {
            OpenOrFocusViewerWindow();
        }

        private void OpenOrFocusViewerWindow()
        {
            if (isExiting)
                return;

            StopReconnectLoop(closeWindow: true);

            if (activeConnectDialog != null && !activeConnectDialog.IsDisposed)
            {
                FocusWindow(activeConnectDialog);
                return;
            }

            using (var form = new ViewerConnectForm(lastIp, lastName, TryConnectFromUiAsync))
            {
                activeConnectDialog = form;
                form.ShowDialog();
                activeConnectDialog = null;
            }
        }

        private async Task<ConnectionAttemptResult> TryConnectFromUiAsync(string ipStr, string name)
        {
            StopReconnectLoop(closeWindow: true);

            var result = await ConnectToServerAsync(ipStr, name);
            if (result.Success)
            {
                lastIp = ipStr;
                lastName = name;
                SaveStoredConnection();
            }

            return result;
        }

        private void StartReconnectLoop()
        {
            if (isExiting)
                return;

            CancellationTokenSource reconnectCts;
            lock (reconnectSync)
            {
                if (reconnectLoopCts != null && !reconnectLoopCts.IsCancellationRequested)
                    return;

                reconnectLoopCts = new CancellationTokenSource();
                reconnectCts = reconnectLoopCts;
            }

            ShowReconnectWindow();
            _ = Task.Run(() => ReconnectLoopAsync(reconnectCts));
        }

        private void StopReconnectLoop(bool closeWindow)
        {
            CancellationTokenSource reconnectCts;
            lock (reconnectSync)
            {
                reconnectCts = reconnectLoopCts;
                reconnectLoopCts = null;
            }

            if (reconnectCts != null)
            {
                try
                {
                    reconnectCts.Cancel();
                }
                catch
                {
                }
                reconnectCts.Dispose();
            }

            if (closeWindow)
            {
                RunOnUiThread(CloseReconnectWindowInternal);
            }
        }

        private async Task ReconnectLoopAsync(CancellationTokenSource reconnectCts)
        {
            CancellationToken token = reconnectCts.Token;
            try
            {
                while (!token.IsCancellationRequested && !isExiting)
                {
                    UpdateReconnectWindow(
                        "Trying to reconnect",
                        string.Format("Main PC: {0}\r\nViewer: {1}", lastIp, lastName));

                    ConnectionAttemptResult result = await ConnectToServerAsync(lastIp, lastName);
                    if (result.Success)
                    {
                        RunOnUiThread(CloseReconnectWindowInternal);
                        return;
                    }

                    for (int seconds = 10; seconds > 0; seconds--)
                    {
                        if (token.IsCancellationRequested || isExiting)
                            return;

                        UpdateReconnectWindow(
                            "Trying to reconnect",
                            string.Format(
                                "{0}\r\nRetrying in {1}s.\r\nClose this window to exit Viewer Client.",
                                result.Message,
                                seconds));

                        try
                        {
                            await Task.Delay(1000, token);
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
            finally
            {
                lock (reconnectSync)
                {
                    if (ReferenceEquals(reconnectLoopCts, reconnectCts))
                    {
                        reconnectLoopCts = null;
                    }
                }
            }
        }

        private void ShowReconnectWindow()
        {
            RunOnUiThread(() =>
            {
                if (reconnectDialog == null || reconnectDialog.IsDisposed)
                {
                    reconnectDialog = new ViewerReconnectForm();
                    reconnectDialog.FormClosed += OnReconnectDialogClosed;
                }

                reconnectDialog.SetTarget(lastIp, lastName);
                reconnectDialog.UpdateStatus(
                    "Trying to reconnect",
                    string.Format("Main PC: {0}\r\nViewer: {1}", lastIp, lastName));

                if (!reconnectDialog.Visible)
                {
                    reconnectDialog.Show();
                }

                FocusWindow(reconnectDialog);
            });
        }

        private void UpdateReconnectWindow(string title, string details)
        {
            RunOnUiThread(() =>
            {
                if (reconnectDialog == null || reconnectDialog.IsDisposed)
                    return;

                reconnectDialog.UpdateStatus(title, details);
            });
        }

        private void OnReconnectDialogClosed(object sender, FormClosedEventArgs e)
        {
            reconnectDialog = null;

            if (isExiting || closingReconnectWindowInternally)
                return;

            OnExit(this, EventArgs.Empty);
        }

        private void CloseReconnectWindowInternal()
        {
            if (reconnectDialog == null || reconnectDialog.IsDisposed)
            {
                reconnectDialog = null;
                return;
            }

            closingReconnectWindowInternally = true;
            try
            {
                reconnectDialog.Close();
            }
            finally
            {
                closingReconnectWindowInternally = false;
                reconnectDialog = null;
            }
        }

        private async Task<ConnectionAttemptResult> ConnectToServerAsync(string ipStr, string name)
        {
            int connectionId = Interlocked.Increment(ref activeConnectionId);
            DisconnectCurrentConnection();

            var nextClient = new TcpClient();
            nextClient.NoDelay = true;
            nextClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            var nextCts = new CancellationTokenSource();
            StreamWriter nextWriter = null;

            try
            {
                SetTrayText(string.Format("Viewer Client: Connecting to {0}...", ipStr));

                Task connectTask = nextClient.ConnectAsync(ipStr, 8080);
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(8), nextCts.Token);
                Task completed = await Task.WhenAny(connectTask, timeoutTask);

                if (completed != connectTask)
                {
                    try
                    {
                        nextClient.Close();
                    }
                    catch
                    {
                    }

                    SetTrayText("Spacetorian Viewer Client (Disconnected)");
                    return ConnectionAttemptResult.Fail(string.Format("Failed to connect to {0}: connection timed out.", ipStr));
                }

                await connectTask;

                nextWriter = new StreamWriter(nextClient.GetStream(), Encoding.UTF8) { AutoFlush = true };
                await nextWriter.WriteLineAsync("HELLO:" + name);

                lock (writerLock)
                {
                    client = nextClient;
                    cts = nextCts;
                    writer = nextWriter;
                }

                int? currentBrightness = TryGetCurrentBrightness();
                if (currentBrightness.HasValue)
                {
                    SendBrightness(currentBrightness.Value);
                }

                SetTrayText(string.Format("Viewer Client: Connected to {0}", ipStr));
                _ = Task.Run(() => HandleConnectionAsync(nextClient, nextCts, connectionId));

                return ConnectionAttemptResult.Ok();
            }
            catch (Exception ex)
            {
                try
                {
                    nextWriter?.Dispose();
                }
                catch
                {
                }

                try
                {
                    nextCts.Cancel();
                }
                catch
                {
                }

                nextCts.Dispose();

                try
                {
                    nextClient.Close();
                }
                catch
                {
                }

                SetTrayText("Spacetorian Viewer Client (Disconnected)");
                return ConnectionAttemptResult.Fail(string.Format("Failed to connect to {0}: {1}", ipStr, ex.Message));
            }
        }

        private async Task HandleConnectionAsync(TcpClient activeClient, CancellationTokenSource activeCts, int connectionId)
        {
            try
            {
                using (var stream = activeClient.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    while (!activeCts.IsCancellationRequested && activeClient.Connected)
                    {
                        string line = await reader.ReadLineAsync();
                        if (line == null)
                            break;

                        if (line == "GET_BRIGHTNESS")
                        {
                            int? currentBrightness = TryGetCurrentBrightness();
                            if (currentBrightness.HasValue)
                            {
                                SendBrightness(currentBrightness.Value);
                            }
                        }
                        else if (line.StartsWith("SET_BRIGHTNESS:"))
                        {
                            if (int.TryParse(line.Substring(15), out int brightness))
                            {
                                EnqueueBrightnessChange(brightness, activeCts.Token);
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
                bool shouldReconnect = false;

                lock (writerLock)
                {
                    if (connectionId == activeConnectionId && ReferenceEquals(client, activeClient))
                    {
                        try
                        {
                            writer?.Dispose();
                        }
                        catch
                        {
                        }

                        writer = null;
                        client = null;
                        cts = null;

                        shouldReconnect = !isExiting;
                    }
                }

                try
                {
                    activeCts.Cancel();
                }
                catch
                {
                }

                activeCts.Dispose();

                try
                {
                    activeClient.Close();
                }
                catch
                {
                }

                if (shouldReconnect)
                {
                    SetTrayText("Spacetorian Viewer Client (Disconnected)");
                    StartReconnectLoop();
                }
            }
        }

        private void DisconnectCurrentConnection()
        {
            TcpClient oldClient;
            CancellationTokenSource oldCts;
            StreamWriter oldWriter;

            lock (writerLock)
            {
                oldClient = client;
                oldCts = cts;
                oldWriter = writer;

                client = null;
                cts = null;
                writer = null;
            }

            if (oldCts != null)
            {
                try
                {
                    oldCts.Cancel();
                }
                catch
                {
                }

                oldCts.Dispose();
            }
            try
            {
                oldWriter?.Dispose();
            }
            catch
            {
            }

            try
            {
                oldClient?.Close();
            }
            catch
            {
            }

            lock (brightnessQueueLock)
            {
                pendingBrightness = -1;
            }
        }

        private void EnqueueBrightnessChange(int brightness, CancellationToken token)
        {
            lock (brightnessQueueLock)
            {
                pendingBrightness = brightness;
            }

            if (Interlocked.CompareExchange(ref brightnessWorkerActive, 1, 0) == 0)
            {
                _ = Task.Run(() => ProcessBrightnessQueueAsync(token));
            }
        }

        private async Task ProcessBrightnessQueueAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && !isExiting)
                {
                    int brightness;
                    lock (brightnessQueueLock)
                    {
                        brightness = pendingBrightness;
                        pendingBrightness = -1;
                    }

                    if (brightness < 0)
                        break;

                    if (SetBrightness(brightness))
                    {
                        SendBrightness(brightness);
                    }

                    await Task.Yield();
                }
            }
            finally
            {
                Interlocked.Exchange(ref brightnessWorkerActive, 0);

                bool hasPending;
                lock (brightnessQueueLock)
                {
                    hasPending = (pendingBrightness >= 0);
                }

                if (hasPending && !token.IsCancellationRequested && !isExiting && (Interlocked.CompareExchange(ref brightnessWorkerActive, 1, 0) == 0))
                {
                    _ = Task.Run(() => ProcessBrightnessQueueAsync(token));
                }
            }
        }

        private int? TryGetCurrentBrightness()
        {
            try
            {
                var scope = new ManagementScope("root\\WMI");
                var query = new SelectQuery("WmiMonitorBrightness");
                using (var searcher = new ManagementObjectSearcher(scope, query))
                using (var objectCollection = searcher.Get())
                {
                    foreach (ManagementObject mObj in objectCollection)
                    {
                        object value = mObj["CurrentBrightness"];
                        if (value != null)
                        {
                            return Convert.ToInt32(value);
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private void SendBrightness(int brightness)
        {
            try
            {
                lock (writerLock)
                {
                    if (writer == null || client == null)
                        return;

                    writer.WriteLine("BRIGHTNESS:" + brightness.ToString());
                }
            }
            catch
            {
            }
        }

        private bool SetBrightness(int brightness)
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
                        mObj.InvokeMethod("WmiSetBrightness", new object[] { 0u, (byte)brightness });
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Brightness set error: " + ex.Message);
            }

            return false;
        }

        private void OnExit(object sender, EventArgs e)
        {
            if (isExiting)
                return;

            isExiting = true;

            StopReconnectLoop(closeWindow: true);

            if (activeConnectDialog != null && !activeConnectDialog.IsDisposed)
            {
                activeConnectDialog.Close();
                activeConnectDialog = null;
            }

            openWindowRegistration?.Unregister(null);
            openWindowRegistration = null;

            openWindowEvent?.Dispose();
            openWindowEvent = null;

            DisconnectCurrentConnection();

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }

            Application.Exit();
        }

        private void SetTrayText(string text)
        {
            RunOnUiThread(() =>
            {
                if (trayIcon == null)
                    return;

                string safeText = string.IsNullOrWhiteSpace(text) ? "Spacetorian Viewer Client" : text;
                if (safeText.Length > 63)
                {
                    safeText = safeText.Substring(0, 63);
                }

                trayIcon.Text = safeText;
            });
        }

        private void FocusWindow(Form form)
        {
            if (form == null || form.IsDisposed)
                return;

            if (form.WindowState == FormWindowState.Minimized)
            {
                form.WindowState = FormWindowState.Normal;
            }

            form.TopMost = true;
            form.Activate();
            form.TopMost = false;
        }

        private void RunOnUiThread(Action action)
        {
            if (action == null || IsDisposed)
                return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(action);
                }
                catch
                {
                }
            }
            else
            {
                action();
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
            }
        }
    }

    public class ViewerReconnectForm : Form
    {
        private readonly Label targetLabel;
        private readonly Label statusTitleLabel;
        private readonly Label statusDetailLabel;

        public ViewerReconnectForm()
        {
            bool darkMode = ViewerUiTheme.IsDarkModeEnabled();
            Color backgroundColor = darkMode ? Color.FromArgb(24, 24, 24) : Color.FromArgb(248, 248, 248);
            Color textColor = darkMode ? Color.WhiteSmoke : Color.FromArgb(26, 26, 26);
            Color mutedTextColor = darkMode ? Color.Gainsboro : Color.FromArgb(78, 78, 78);
            Color accentColor = ViewerUiTheme.GetSystemAccentColor();

            Text = "Spacetorian Viewer Client";
            ClientSize = new Size(430, 210);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point, 204);
            Opacity = 0.97;
            BackColor = backgroundColor;
            ForeColor = textColor;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 18, 20, 18),
                ColumnCount = 1,
                RowCount = 5,
                BackColor = Color.Transparent,
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            statusTitleLabel = new Label
            {
                Text = "Trying to reconnect",
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold, GraphicsUnit.Point, 204),
                ForeColor = accentColor,
                Margin = new Padding(0, 0, 0, 4),
            };

            targetLabel = new Label
            {
                Text = string.Empty,
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point, 204),
                ForeColor = mutedTextColor,
                Margin = new Padding(0, 0, 0, 0),
            };
            statusDetailLabel = new Label
            {
                Text = "Waiting for the next reconnect attempt.",
                AutoSize = true,
                MaximumSize = new Size(380, 0),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point, 204),
                ForeColor = textColor,
                Margin = new Padding(0),
            };

            var closeHintLabel = new Label
            {
                Text = "Close this window to exit Viewer Client.",
                AutoSize = true,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 204),
                ForeColor = mutedTextColor,
                Margin = new Padding(0, 8, 0, 0),
            };

            root.Controls.Add(statusTitleLabel, 0, 0);
            root.Controls.Add(targetLabel, 0, 1);
            root.Controls.Add(statusDetailLabel, 0, 3);
            root.Controls.Add(closeHintLabel, 0, 4);

            Controls.Add(root);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ViewerUiTheme.ApplyWindows11Backdrop(Handle, ViewerUiTheme.IsDarkModeEnabled());
        }

        public void SetTarget(string ipAddress, string displayName)
        {
            targetLabel.Text = string.Format("Main PC: {0} | Viewer: {1}", ipAddress, displayName);
        }

        public void UpdateStatus(string title, string details)
        {
            statusTitleLabel.Text = title;
            statusDetailLabel.Text = details;
        }
    }

    public class ViewerConnectForm : Form
    {
        public string IPAddress { get; private set; }
        public string DisplayName { get; private set; }

        private readonly Func<string, string, Task<ConnectionAttemptResult>> connectAction;
        private readonly TextBox txtIp;
        private readonly TextBox txtName;
        private readonly Label statusLabel;
        private readonly Button btnConnect;
        private readonly Button btnCancel;

        private readonly Color defaultStatusColor;
        private readonly Color errorStatusColor;

        private bool isConnecting;

        public ViewerConnectForm(string defaultIp, string defaultName, Func<string, string, Task<ConnectionAttemptResult>> connectAction)
        {
            this.connectAction = connectAction ?? throw new ArgumentNullException(nameof(connectAction));

            bool darkMode = ViewerUiTheme.IsDarkModeEnabled();
            Color backgroundColor = darkMode ? Color.FromArgb(24, 24, 24) : Color.FromArgb(248, 248, 248);
            Color surfaceColor = darkMode ? Color.FromArgb(34, 34, 34) : Color.FromArgb(252, 252, 252);
            Color borderColor = darkMode ? Color.FromArgb(88, 88, 88) : Color.FromArgb(188, 188, 188);
            Color textColor = darkMode ? Color.WhiteSmoke : Color.FromArgb(26, 26, 26);
            Color mutedTextColor = darkMode ? Color.Gainsboro : Color.FromArgb(70, 70, 70);
            Color accentColor = ViewerUiTheme.GetSystemAccentColor();

            defaultStatusColor = mutedTextColor;
            errorStatusColor = darkMode ? Color.FromArgb(255, 170, 170) : Color.FromArgb(156, 0, 6);

            Text = "Spacetorian Viewer Client";
            ClientSize = new Size(430, 290);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            Opacity = 0.97;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 204);
            BackColor = backgroundColor;
            ForeColor = textColor;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 18, 20, 18),
                ColumnCount = 1,
                RowCount = 8,
                BackColor = Color.Transparent,
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 8));
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
                ForeColor = mutedTextColor,
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

            txtIp = CreateTextBox(defaultIp, surfaceColor, textColor);
            txtName = CreateTextBox(defaultName, surfaceColor, textColor);

            fields.Controls.Add(lblIp, 0, 0);
            fields.Controls.Add(CreateTextBoxHost(txtIp, borderColor, surfaceColor), 1, 0);
            fields.Controls.Add(lblName, 0, 1);
            fields.Controls.Add(CreateTextBoxHost(txtName, borderColor, surfaceColor), 1, 1);

            statusLabel = new Label
            {
                Text = "Enter Main PC IP and connect.",
                AutoSize = true,
                MaximumSize = new Size(390, 0),
                ForeColor = defaultStatusColor,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 204),
                Margin = new Padding(0, 4, 0, 0),
            };

            var buttonBar = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill,
                Margin = new Padding(0),
                AutoSize = true,
                WrapContents = false
            };

            btnConnect = CreatePrimaryButton("Connect", accentColor);
            btnConnect.Click += OnConnectClicked;

            btnCancel = CreateSecondaryButton("Close", textColor, borderColor, surfaceColor);
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
            root.Controls.Add(statusLabel, 0, 5);
            root.Controls.Add(buttonBar, 0, 7);

            Controls.Add(root);

            AcceptButton = btnConnect;
            CancelButton = btnCancel;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ViewerUiTheme.ApplyWindows11Backdrop(Handle, ViewerUiTheme.IsDarkModeEnabled());
        }

        private async void OnConnectClicked(object sender, EventArgs e)
        {
            if (isConnecting)
                return;

            string ip = txtIp.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                SetStatus("Please enter Main PC IP.", isError: true);
                return;
            }

            string name = string.IsNullOrWhiteSpace(txtName.Text)
                ? Environment.MachineName
                : txtName.Text.Trim();

            SetConnectingState(true);
            SetStatus(string.Format("Connecting to {0}...", ip), isError: false);

            ConnectionAttemptResult result;
            try
            {
                result = await connectAction(ip, name);
            }
            catch (Exception ex)
            {
                result = ConnectionAttemptResult.Fail("Connection error: " + ex.Message);
            }

            if (IsDisposed)
                return;

            if (result.Success)
            {
                IPAddress = ip;
                DisplayName = name;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            SetConnectingState(false);
            SetStatus(result.Message, isError: true);
        }

        private void SetConnectingState(bool connecting)
        {
            isConnecting = connecting;

            txtIp.Enabled = !connecting;
            txtName.Enabled = !connecting;
            btnCancel.Enabled = !connecting;
            btnConnect.Enabled = !connecting;
            btnConnect.Text = connecting ? "Connecting..." : "Connect";
        }

        private void SetStatus(string text, bool isError)
        {
            statusLabel.ForeColor = isError ? errorStatusColor : defaultStatusColor;
            statusLabel.Text = text;
        }
        private static Label CreateLabel(string text, Color color)
        {
            return new Label
            {
                Text = text,
                ForeColor = color,
                AutoSize = true,
                Margin = new Padding(0, 11, 8, 0),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point, 204)
            };
        }

        private static TextBox CreateTextBox(string value, Color backColor, Color foreColor)
        {
            return new TextBox
            {
                Text = value,
                BorderStyle = BorderStyle.None,
                BackColor = backColor,
                ForeColor = foreColor,
                Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point, 204),
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
            };
        }

        private static Control CreateTextBoxHost(TextBox textBox, Color borderColor, Color fillColor)
        {
            var host = new Panel
            {
                Height = 30,
                Margin = new Padding(0, 4, 0, 8),
                Padding = new Padding(10, 7, 10, 7),
                BackColor = borderColor,
                Dock = DockStyle.Top,
            };

            var inner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = fillColor,
                Padding = new Padding(0),
            };

            inner.Controls.Add(textBox);
            host.Controls.Add(inner);
            return host;
        }

        private static Button CreatePrimaryButton(string text, Color accent)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(18, 7, 18, 7),
                BackColor = accent,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(8, 0, 0, 0)
            };
            button.FlatAppearance.BorderSize = 0;
            return button;
        }

        private static Button CreateSecondaryButton(string text, Color textColor, Color borderColor, Color surfaceColor)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(18, 7, 18, 7),
                BackColor = surfaceColor,
                ForeColor = textColor,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(8, 0, 0, 0)
            };
            button.FlatAppearance.BorderColor = borderColor;
            button.FlatAppearance.BorderSize = 1;
            return button;
        }
    }

    internal static class ViewerUiTheme
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
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

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetColorizationColor(out uint pcrColorization, out bool pfOpaqueBlend);

        public static bool IsDarkModeEnabled()
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

        public static Color GetSystemAccentColor()
        {
            try
            {
                if (DwmGetColorizationColor(out uint rawColor, out _) == 0)
                {
                    byte a = (byte)((rawColor >> 24) & 0xFF);
                    byte r = (byte)((rawColor >> 16) & 0xFF);
                    byte g = (byte)((rawColor >> 8) & 0xFF);
                    byte b = (byte)(rawColor & 0xFF);
                    if (a == 0)
                        a = 255;

                    return Color.FromArgb(a, r, g, b);
                }
            }
            catch
            {
            }

            try
            {
                object value = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                    "AccentColorMenu",
                    null);

                if (value is int accent)
                {
                    byte a = (byte)((accent >> 24) & 0xFF);
                    byte r = (byte)(accent & 0xFF);
                    byte g = (byte)((accent >> 8) & 0xFF);
                    byte b = (byte)((accent >> 16) & 0xFF);
                    if (a == 0)
                        a = 255;

                    return Color.FromArgb(a, r, g, b);
                }
            }
            catch
            {
            }

            return SystemColors.Highlight;
        }

        public static void ApplyWindows11Backdrop(IntPtr handle, bool darkMode)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            if (Environment.OSVersion.Version < new Version(10, 0, 22000, 0))
                return;

            int useDark = darkMode ? 1 : 0;
            int rounded = 2;
            int backdropType = 2;
            int captionColor = ColorTranslator.ToWin32(darkMode ? Color.FromArgb(24, 24, 24) : Color.FromArgb(245, 245, 245));
            int captionTextColor = ColorTranslator.ToWin32(darkMode ? Color.White : Color.Black);

            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref captionTextColor, sizeof(int));

            var margins = new MARGINS { cxLeftWidth = -1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);
        }
    }
}
