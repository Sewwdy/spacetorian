using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;

namespace Monitorian.Core.Models.Monitor;

internal class NetworkMonitorItem : MonitorItem
{
	private readonly TcpClient _client;
	private StreamWriter _writer;

	public override bool IsBrightnessSupported => true;
	public override bool IsContrastSupported => false;

	public NetworkMonitorItem(
		TcpClient client,
		string deviceInstanceId,
		string description)
		: base(
			deviceInstanceId: deviceInstanceId,
			description: description,
			displayIndex: 255, // Virtual monitor
			monitorIndex: 255,
			monitorRect: Rect.Empty,
			isInternal: false,
			isReachable: true)
	{
		_client = client;
		if (client.Connected)
		{
			_writer = new StreamWriter(_client.GetStream(), Encoding.UTF8) { AutoFlush = true };
		}
	}

	public void UpdateLocalBrightnessValue(int value)
	{
		this.Brightness = value;
		this.BrightnessSystemAdjusted = value;
	}

	public override AccessResult UpdateBrightness(int value = -1)
	{
		// Ask the remote client for its brightness
		if (_client.Connected && _writer != null)
		{
			try
			{
				_writer.WriteLine("GET_BRIGHTNESS");
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[NetworkMonitorItem] GetBrightness error: {ex.Message}");
				return AccessResult.Failed;
			}
		}
		// The client will respond with "BRIGHTNESS:50" asynchronously which updates our local properties.
		return AccessResult.Succeeded;
	}

	public override AccessResult SetBrightness(int brightness)
	{
		if (brightness < 0 || brightness > 100)
			return AccessResult.Failed;

		if (_client.Connected && _writer != null)
		{
			try
			{
				_writer.WriteLine($"SET_BRIGHTNESS:{brightness}");
				UpdateLocalBrightnessValue(brightness);
				return AccessResult.Succeeded;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[NetworkMonitorItem] SetBrightness error: {ex.Message}");
				return AccessResult.Failed;
			}
		}
		return AccessResult.Failed;
	}

	public void Disconnect()
	{
		try
		{
			_writer?.Dispose();
		}
		catch { }
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		Disconnect();
	}
}
