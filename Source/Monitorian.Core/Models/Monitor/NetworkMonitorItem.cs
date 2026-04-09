using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Monitorian.Core.Models.Monitor;

internal class NetworkMonitorItem : MonitorItem
{
	private readonly TcpClient _client;
	private readonly object _writerLock = new();
	private readonly object _queueLock = new();
	private StreamWriter _writer;
	private int _queuedBrightness = -1;
	private int _senderActive;

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
		Brightness = value;
		BrightnessSystemAdjusted = value;
	}

	public override AccessResult UpdateBrightness(int value = -1)
	{
		// Brightness is pushed by the viewer client on connect and after successful updates.
		// Avoid active polling here to keep control stable when remote desktop viewers are foregrounded.
		return AccessResult.Succeeded;
	}

	public override AccessResult SetBrightness(int brightness)
	{
		if (brightness < 0 || brightness > 100)
			return AccessResult.Failed;

		lock (_writerLock)
		{
			if (_writer is null)
				return new AccessResult(AccessStatus.TransmissionFailed, "Viewer client is unavailable.");
		}

		UpdateLocalBrightnessValue(brightness);
		EnqueueBrightnessSend(brightness);
		return AccessResult.Succeeded;
	}

	private void EnqueueBrightnessSend(int brightness)
	{
		lock (_queueLock)
		{
			_queuedBrightness = brightness;
		}

		if (Interlocked.CompareExchange(ref _senderActive, 1, 0) == 0)
		{
			_ = Task.Run(ProcessQueuedBrightnessAsync);
		}
	}

	private async Task ProcessQueuedBrightnessAsync()
	{
		try
		{
			while (true)
			{
				int brightness;
				lock (_queueLock)
				{
					brightness = _queuedBrightness;
					_queuedBrightness = -1;
				}

				if (brightness < 0)
					break;

				if (!TrySendCommand($"SET_BRIGHTNESS:{brightness}", suppressFailureLog: false))
					break;

				await Task.Yield();
			}
		}
		finally
		{
			Interlocked.Exchange(ref _senderActive, 0);

			bool hasPending;
			lock (_queueLock)
			{
				hasPending = (_queuedBrightness >= 0);
			}

			if (hasPending && (Interlocked.CompareExchange(ref _senderActive, 1, 0) == 0))
			{
				_ = Task.Run(ProcessQueuedBrightnessAsync);
			}
		}
	}

	private bool TrySendCommand(string command, bool suppressFailureLog)
	{
		lock (_writerLock)
		{
			if (_writer is null)
			{
				if (!suppressFailureLog)
				{
					Debug.WriteLine("[NetworkMonitorItem] Command skipped because writer is unavailable.");
				}
				return false;
			}

			try
			{
				_writer.WriteLine(command);
				return true;
			}
			catch (Exception ex)
			{
				if (!suppressFailureLog)
				{
					Debug.WriteLine($"[NetworkMonitorItem] Command send error: {ex.Message}");
				}

				return false;
			}
		}
	}

	public void Disconnect()
	{
		lock (_queueLock)
		{
			_queuedBrightness = -1;
		}

		lock (_writerLock)
		{
			DisposeWriterUnsafe();
		}
	}

	private void DisposeWriterUnsafe()
	{
		try
		{
			_writer?.Dispose();
		}
		catch
		{
		}
		finally
		{
			_writer = null;
		}
	}

	protected override void Dispose(bool disposing)
	{
		base.Dispose(disposing);
		Disconnect();
	}
}
