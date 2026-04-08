using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Monitorian.Core.Models.Monitor;

namespace Monitorian.Core.Models.Monitor;

internal static class SpacetorianTcpServer
{
	private static TcpListener _listener;
	private static CancellationTokenSource _cts;
	public static ConcurrentDictionary<string, NetworkMonitorItem> ConnectedMonitors { get; } = new();

	public static void StartServer(int port = 8080)
	{
		if (_listener != null) return;

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
		foreach (var m in ConnectedMonitors.Values)
		{
			m.Disconnect();
		}
		ConnectedMonitors.Clear();
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
			catch (ObjectDisposedException) { break; }
			catch (Exception ex) { Debug.WriteLine($"[SpacetorianTcpServer] Accept error: {ex.Message}"); }
		}
	}

	private static async Task HandleClientAsync(TcpClient client, CancellationToken token)
	{
		var endpoint = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
		string deviceId = "NETWORK\\" + endpoint;
		
		Debug.WriteLine($"[SpacetorianTcpServer] Client connected: {endpoint}");

		var networkMonitor = new NetworkMonitorItem(
			client,
			deviceId,
			$"Laptop ({endpoint.Split(':')[0]})"
		);

		ConnectedMonitors[deviceId] = networkMonitor;
		
		// Important: We should trigger a Rescan of monitors in the Monitorian system here.
		// MonitorManager check changed checks monitor count.
		// The easiest hack is to just let the periodic rescan find it, or we can invoke CheckMonitorsChanged manually if there was an event.

		try
		{
			using var stream = client.GetStream();
			using var reader = new StreamReader(stream, Encoding.UTF8);

			while (!token.IsCancellationRequested && client.Connected)
			{
				string line = await reader.ReadLineAsync();
				if (line == null) break; // disconnected

				// If client sends messages, parse them here.
				// For example, they could send current brightness updates: "BRIGHTNESS:50"
				if (line.StartsWith("BRIGHTNESS:"))
				{
					if (int.TryParse(line.Substring(11), out int b))
					{
						networkMonitor.UpdateLocalBrightnessValue(b);
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
			ConnectedMonitors.TryRemove(deviceId, out _);
			networkMonitor.Disconnect();
			client.Close();
			Debug.WriteLine($"[SpacetorianTcpServer] Client disconnected: {endpoint}");
		}
	}
}
