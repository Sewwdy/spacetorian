using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Monitorian.Core.Models.Monitor;

internal static class SpacetorianTcpServer
{
	private static TcpListener _listener;
	private static CancellationTokenSource _cts;

	public static ConcurrentDictionary<string, NetworkMonitorItem> ConnectedMonitors { get; } = new();
	public static event EventHandler ConnectedMonitorsChanged;

	public static void StartServer(int port = 8080)
	{
		if (_listener != null)
			return;

		_cts = new CancellationTokenSource();
		_listener = new TcpListener(IPAddress.Any, port);

		try
		{
			_listener.Start();
			Task.Run(() => AcceptClientsAsync(_cts.Token));
			Debug.WriteLine($"[SpacetorianTcpServer] Started on port {port}");
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[SpacetorianTcpServer] Failed to start: {ex.Message}");
		}
	}

	public static void StopServer()
	{
		_cts?.Cancel();
		_listener?.Stop();

		var hadConnectedMonitor = !ConnectedMonitors.IsEmpty;
		foreach (var monitor in ConnectedMonitors.Values)
		{
			monitor.Disconnect();
		}
		ConnectedMonitors.Clear();

		if (hadConnectedMonitor)
		{
			RaiseConnectedMonitorsChanged();
		}
	}

	private static async Task AcceptClientsAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				var client = await _listener.AcceptTcpClientAsync();
				_ = Task.Run(() => HandleClientAsync(client, token));
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[SpacetorianTcpServer] Accept error: {ex.Message}");
			}
		}
	}

	private static async Task HandleClientAsync(TcpClient client, CancellationToken token)
	{
		var endpoint = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
		string deviceId = "NETWORK\\" + endpoint;

		Debug.WriteLine(string.Format("[SpacetorianTcpServer] Client connected: {0}", endpoint));

		try
		{
			var stream = client.GetStream();
			var reader = new StreamReader(stream, Encoding.UTF8);

			// Wait for HELLO message.
			string name = string.Format("Laptop ({0})", endpoint.Split(':')[0]);
			string firstLine = await reader.ReadLineAsync();
			if (firstLine != null && firstLine.StartsWith("HELLO:"))
			{
				name = firstLine.Substring(6);
			}

			var networkMonitor = new NetworkMonitorItem(client, deviceId, name);
			ConnectedMonitors[deviceId] = networkMonitor;
			RaiseConnectedMonitorsChanged();

			if (firstLine != null && firstLine.StartsWith("BRIGHTNESS:"))
			{
				if (int.TryParse(firstLine.Substring(11), out int brightness))
				{
					networkMonitor.UpdateLocalBrightnessValue(brightness);
				}
			}

			while (!token.IsCancellationRequested && client.Connected)
			{
				string line = await reader.ReadLineAsync();
				if (line == null)
					break;

				if (line.StartsWith("BRIGHTNESS:"))
				{
					if (int.TryParse(line.Substring(11), out int brightness))
					{
						networkMonitor.UpdateLocalBrightnessValue(brightness);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[SpacetorianTcpServer] Client error: {ex.Message}");
		}
		finally
		{
			if (ConnectedMonitors.TryRemove(deviceId, out var removedMonitor))
			{
				removedMonitor.Disconnect();
				RaiseConnectedMonitorsChanged();
			}

			client.Close();
			Debug.WriteLine(string.Format("[SpacetorianTcpServer] Client disconnected: {0}", endpoint));
		}
	}

	private static void RaiseConnectedMonitorsChanged()
	{
		ConnectedMonitorsChanged?.Invoke(null, EventArgs.Empty);
	}
}
