using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace AXVideoPlayer;

public partial class MainWindow
{
	private void Fullscreen_Click(object sender, RoutedEventArgs e)
	{
		ToggleFullscreen();
	}

	private void ToggleFullscreenDebounced()
	{
		DateTime utcNow = DateTime.UtcNow;
		if (!((utcNow - _lastFullscreenToggleUtc).TotalMilliseconds < 280.0))
		{
			_lastFullscreenToggleUtc = utcNow;
			ToggleFullscreen();
		}
	}

	private void ToggleFullscreen()
	{
		_singleClickTimer.Stop();
		_lastVideoClickTime = DateTime.MinValue;
		if (!_isFullscreen)
		{
			EnterTrueFullscreen();
		}
		else
		{
			ExitTrueFullscreen();
		}
		_isUiVisible = true;
		UpdateLayoutState();
		Focus();
	}

	private void EnterTrueFullscreen()
	{
		_playlistWasVisibleBeforeFullscreen = _isPlaylistVisible;
		_windowStyleBeforeFullscreen = base.WindowStyle;
		_windowStateBeforeFullscreen = base.WindowState;
		_resizeModeBeforeFullscreen = base.ResizeMode;
		_windowBoundsBeforeFullscreen = new Rect(base.Left, base.Top, base.Width, base.Height);
		ApplyManualWindowChromeHidden(hidden: false);
		base.WindowState = WindowState.Normal;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		base.Topmost = true;
		base.Background = Brushes.Black;
		ApplyFullscreenMonitorBounds();
		_isFullscreen = true;
		_manualUiHidden = false;
		_isUiVisible = true;
		_isPlaylistVisible = false;
		ApplyPlaylistVisibility();
	}

	private void ExitTrueFullscreen()
	{
		base.WindowState = WindowState.Normal;
		base.WindowStyle = (_windowStyleBeforeFullscreen == WindowStyle.None) ? WindowStyle.SingleBorderWindow : _windowStyleBeforeFullscreen;
		base.ResizeMode = _resizeModeBeforeFullscreen;
		base.Topmost = _userSettings.AlwaysOnTop;
		if (!_windowBoundsBeforeFullscreen.IsEmpty)
		{
			base.Left = _windowBoundsBeforeFullscreen.Left;
			base.Top = _windowBoundsBeforeFullscreen.Top;
			base.Width = Math.Max(base.MinWidth, _windowBoundsBeforeFullscreen.Width);
			base.Height = Math.Max(base.MinHeight, _windowBoundsBeforeFullscreen.Height);
		}
		if (_windowStateBeforeFullscreen == WindowState.Maximized)
		{
			base.WindowState = WindowState.Maximized;
		}
		_isFullscreen = false;
		_isPlaylistVisible = _playlistWasVisibleBeforeFullscreen;
		ApplyPlaylistVisibility();
		if (_manualUiHidden)
		{
			ApplyManualWindowChromeHidden(hidden: true);
		}
	}

	private void ApplyFullscreenMonitorBounds()
	{
		Rect bounds = GetCurrentMonitorBounds();
		if (bounds.IsEmpty)
			return;

		base.Left = bounds.Left;
		base.Top = bounds.Top;
		base.Width = bounds.Width;
		base.Height = bounds.Height;
	}

	private Rect GetCurrentMonitorBounds()
	{
		nint handle = GetMainWindowHandle();
		if (handle == IntPtr.Zero)
			return Rect.Empty;

		nint monitor = MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
		if (monitor == IntPtr.Zero)
			return Rect.Empty;

		var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
		if (!GetMonitorInfo(monitor, ref info))
			return Rect.Empty;

		var topLeft = new Point(info.rcMonitor.Left, info.rcMonitor.Top);
		var bottomRight = new Point(info.rcMonitor.Right, info.rcMonitor.Bottom);
		if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget != null)
		{
			topLeft = source.CompositionTarget.TransformFromDevice.Transform(topLeft);
			bottomRight = source.CompositionTarget.TransformFromDevice.Transform(bottomRight);
		}
		return new Rect(topLeft, bottomRight);
	}

	private void ShowUiForFullscreenNearCursor(Point windowPoint)
	{
		if (_isFullscreen)
		{
			if (_manualUiHidden)
			{
				_manualUiHidden = false;
			}
			if (!_isUiVisible)
			{
				_isUiVisible = true;
				UpdateLayoutState();
			}
			else if (IsVideoActivelyPlaying())
			{
				_fullscreenHideTimer.Stop();
				_fullscreenHideTimer.Start();
			}
		}
	}

	private void FullscreenHideTimer_Tick(object? sender, EventArgs e)
	{
		_fullscreenHideTimer.Stop();
		if (_isFullscreen && !_manualUiHidden && IsVideoActivelyPlaying())
		{
			_isUiVisible = false;
			UpdateLayoutState();
		}
	}

	private void Window_MouseMove(object sender, MouseEventArgs e)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		if (_isFullscreen)
		{
			Point position = e.GetPosition(this);
			if (Distance(position, _lastMousePosition) > 2.0)
			{
				_lastMousePosition = position;
				ShowUiForFullscreenNearCursor(position);
			}
		}
	}

	private void SingleClickTimer_Tick(object? sender, EventArgs e)
	{
		_singleClickTimer.Stop();
		if (!AreVideoClickShortcutsSuspended())
		{
			TogglePlayPause();
		}
	}

	private void ComponentDispatcher_ThreadFilterMessage(ref MSG msg, ref bool handled)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0188: Unknown result type (might be due to invalid IL or missing references)
		//IL_0189: Unknown result type (might be due to invalid IL or missing references)
		if (msg.message == 512)
		{
			if (_isFullscreen && IsCursorInsideThisWindow())
			{
				Point cursorPointInThisWindow = GetCursorPointInThisWindow();
				if (Distance(cursorPointInThisWindow, _lastMousePosition) > 2.0)
				{
					_lastMousePosition = cursorPointInThisWindow;
					ShowUiForFullscreenNearCursor(cursorPointInThisWindow);
				}
			}
		}
		else
		{
			if ((msg.message != 513 && msg.message != 515) || AreVideoClickShortcutsSuspended())
			{
				return;
			}
			Point cursorScreenPoint = GetCursorScreenPoint();
			bool isDoubleClick = msg.message == 515;
			if (HandleVideoSurfaceLeftButtonShortcut(cursorScreenPoint, isDoubleClick))
			{
				if (isDoubleClick)
				{
					handled = true;
				}
			}
		}
	}

	private void InstallMouseHook()
	{
		if (_mouseHookHandle == IntPtr.Zero)
		{
			_mouseHookProc = LowLevelMouseHookCallback;
			_mouseHookHandle = SetWindowsHookEx(14, _mouseHookProc, IntPtr.Zero, 0u);
		}
	}

	private nint LowLevelMouseHookCallback(int nCode, nint wParam, nint lParam)
	{
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		if (nCode >= 0 && wParam == new IntPtr(513))
		{
			if (IsLiveSuperResolutionEnabled())
			{
				return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
			}
			MSLLHOOKSTRUCT mSLLHOOKSTRUCT = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
			Point screenPoint = new Point((double)mSLLHOOKSTRUCT.pt.X, (double)mSLLHOOKSTRUCT.pt.Y);
			if (AreVideoClickShortcutsSuspended())
			{
				return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
			}
			if (!IsMainWindowClientClickAtHookTime(screenPoint))
			{
				return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
			}
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)delegate
			{
				//IL_0007: Unknown result type (might be due to invalid IL or missing references)
				HandleGlobalMouseLeftButtonDown(screenPoint);
			}, (DispatcherPriority)5, Array.Empty<object>());
		}
		return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
	}

	private void HandleGlobalMouseLeftButtonDown(Point screenPoint)
	{
		if (IsLiveSuperResolutionEnabled() || AreVideoClickShortcutsSuspended())
		{
			return;
		}
		nint foregroundWindow = GetForegroundWindow();
		if (foregroundWindow == IntPtr.Zero)
		{
			return;
		}
		nint mainWindowHandle = GetMainWindowHandle();
		if (IsMainWindowOrChildWindow(mainWindowHandle, foregroundWindow) && IsScreenPointInMainWindowClientArea(screenPoint) && IsScreenPointInsideMainViewingSurface(screenPoint))
		{
			HandleVideoSurfaceLeftButtonShortcut(screenPoint, isDoubleClick: false);
		}
	}

	private bool AreVideoClickShortcutsSuspended()
	{
		return DateTime.UtcNow < _videoClickShortcutsSuspendedUntilUtc || IsResumePromptOpen() || _subtitleMenuOpen || (_toolsWindow != null && _toolsWindow.IsVisible) || (_audioEqualizerWindow != null && _audioEqualizerWindow.IsVisible) || (_videoEqualizerWindow != null && _videoEqualizerWindow.IsVisible) || (_audioTrackSelectionWindow != null && _audioTrackSelectionWindow.IsVisible);
	}

	private void SuspendVideoClickShortcuts(int milliseconds)
	{
		DateTime until = DateTime.UtcNow.AddMilliseconds(Math.Max(0, milliseconds));
		if (until > _videoClickShortcutsSuspendedUntilUtc)
		{
			_videoClickShortcutsSuspendedUntilUtc = until;
		}
		_lastVideoClickTime = DateTime.MinValue;
		_lastVideoShortcutDownUtc = DateTime.MinValue;
		_suppressVideoShortcutDownUntilUtc = DateTime.MinValue;
		_singleClickTimer.Stop();
		UpdateVideoCursorVisibility();
	}

	private bool IsCursorInsideThisWindow()
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		Point cursorPointInThisWindow = GetCursorPointInThisWindow();
		return cursorPointInThisWindow.X >= 0.0 && cursorPointInThisWindow.X <= base.ActualWidth && cursorPointInThisWindow.Y >= 0.0 && cursorPointInThisWindow.Y <= base.ActualHeight;
	}

	private Point GetCursorPointInThisWindow()
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		return PointFromScreen(GetCursorScreenPoint());
	}

	private Point GetCursorScreenPoint()
	{
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		GetCursorPos(out var lpPoint);
		return new Point((double)lpPoint.X, (double)lpPoint.Y);
	}

	private bool IsWindowPointInsideMainViewingSurface(Point windowPoint)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0004: Unknown result type (might be due to invalid IL or missing references)
		return IsScreenPointInsideMainViewingSurface(PointToScreen(windowPoint));
	}

	private bool IsScreenPointInsideMainViewingSurface(Point screenPoint)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Unknown result type (might be due to invalid IL or missing references)
		Point windowPoint = PointFromScreen(screenPoint);
		if (!IsWindowPointInsideRootGrid(windowPoint))
		{
			return false;
		}
		if (MainVideoArea.Visibility != Visibility.Visible || MainVideoArea.ActualWidth <= 0.0 || MainVideoArea.ActualHeight <= 0.0)
		{
			return false;
		}
		if (!IsScreenPointInsideVisibleElement(screenPoint, MainVideoArea))
		{
			return false;
		}
		return !IsScreenPointInsideVisibleElement(screenPoint, TopBar) && !IsScreenPointInsideVisibleElement(screenPoint, ControlPanel) && !IsScreenPointInsideVisibleElement(screenPoint, PlaylistPanel) && (!IsShowUiButtonVisible() || !IsScreenPointInsideVisibleElement(screenPoint, ShowUiButton));
	}

	private bool IsWindowPointInsideRootGrid(Point windowPoint)
	{
		return RootGrid.ActualWidth > 0.0 && RootGrid.ActualHeight > 0.0 && windowPoint.X >= 0.0 && windowPoint.X <= RootGrid.ActualWidth && windowPoint.Y >= 0.0 && windowPoint.Y <= RootGrid.ActualHeight;
	}

	private bool IsScreenPointInMainWindowClientArea(Point screenPoint)
	{
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		nint mainWindowHandle = GetMainWindowHandle();
		if (mainWindowHandle == IntPtr.Zero)
		{
			return false;
		}
		nint num = SendMessage(mainWindowHandle, 132, IntPtr.Zero, MakeLParam(screenPoint));
		return num == new IntPtr(1);
	}

	private bool IsMainWindowClientClickAtHookTime(Point screenPoint)
	{
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		nint mainWindowHandle = GetMainWindowHandle();
		if (mainWindowHandle == IntPtr.Zero)
		{
			return false;
		}
		nint foregroundWindow = GetForegroundWindow();
		if (!IsMainWindowOrChildWindow(mainWindowHandle, foregroundWindow))
		{
			return false;
		}
		nint num = SendMessage(mainWindowHandle, 132, IntPtr.Zero, MakeLParam(screenPoint));
		return num == new IntPtr(1);
	}

	private nint GetMainWindowHandle()
	{
		if (_mainWindowHandle == IntPtr.Zero)
		{
			_mainWindowHandle = new WindowInteropHelper(this).Handle;
		}
		return _mainWindowHandle;
	}

	private static bool IsMainWindowOrChildWindow(nint mainHandle, nint foregroundWindow)
	{
		return foregroundWindow == mainHandle || IsChild(mainHandle, foregroundWindow);
	}

	private bool IsWindowPointInsideVisibleElement(Point windowPoint, FrameworkElement element)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0004: Unknown result type (might be due to invalid IL or missing references)
		return IsScreenPointInsideVisibleElement(PointToScreen(windowPoint), element);
	}

	private bool IsScreenPointInsideVisibleElement(Point screenPoint, FrameworkElement element)
	{
		//IL_0099: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		if (_isFullscreen && !_isUiVisible && (element == TopBar || element == ControlPanel))
		{
			return false;
		}
		if (element.Visibility != Visibility.Visible || element.Opacity <= 0.05 || element.ActualWidth <= 0.0 || element.ActualHeight <= 0.0)
		{
			return false;
		}
		Point val = element.PointToScreen(new Point(0.0, 0.0));
		Point val2 = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
		double num = Math.Min(val.X, val2.X);
		double num2 = Math.Max(val.X, val2.X);
		double num3 = Math.Min(val.Y, val2.Y);
		double num4 = Math.Max(val.Y, val2.Y);
		return screenPoint.X >= num && screenPoint.X <= num2 && screenPoint.Y >= num3 && screenPoint.Y <= num4;
	}

	private Point TranslatePoint(Point windowPoint, FrameworkElement target)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Unknown result type (might be due to invalid IL or missing references)
		Point val = PointToScreen(windowPoint);
		Point val2 = target.PointToScreen(new Point(0.0, 0.0));
		return new Point(val.X - val2.X, val.Y - val2.Y);
	}

	private static double Distance(Point a, Point b)
	{
		double num = (a).X - (b).X;
		double num2 = (a).Y - (b).Y;
		return Math.Sqrt(num * num + num2 * num2);
	}

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT lpPoint);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool UnhookWindowsHookEx(nint hhk);

	[DllImport("user32.dll")]
	private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern nint GetForegroundWindow();

	[DllImport("user32.dll")]
	private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

	[DllImport("user32.dll")]
	private static extern bool IsChild(nint hWndParent, nint hWnd);

	[DllImport("user32.dll")]
	private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

	[DllImport("user32.dll")]
	private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

	private static nint MakeLParam(Point point)
	{
		int num = (short)Math.Round(point.X);
		int num2 = (short)Math.Round(point.Y);
		int value = (num2 << 16) | (num & 0xFFFF);
		return new IntPtr(value);
	}

}
