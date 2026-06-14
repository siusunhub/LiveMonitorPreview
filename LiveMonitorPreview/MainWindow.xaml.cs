using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace LiveMonitorPreview
{
    // DPI Awareness
    public enum PROCESS_DPI_AWARENESS
    {
        Process_DPI_Unaware = 0,
        Process_System_DPI_Aware = 1,
        Process_Per_Monitor_DPI_Aware = 2
    }

    // Windows API for cursor capture
    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO
    {
        public Int32 cbSize;
        public Int32 flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public Int32 x;
        public Int32 y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool fIcon;
        public Int32 xHotspot;
        public Int32 yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    public partial class MainWindow : Window
    {
        [DllImport("shcore.dll")]
        static extern int SetProcessDpiAwareness(PROCESS_DPI_AWARENESS value);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
        
        [DllImport("user32.dll")]
        static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        [DllImport("user32.dll")]
        static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr hObject);

        private readonly ObservableCollection<MonitorViewModel> _monitors = new();
        private DispatcherTimer? _refreshTimer;
        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private int _refreshInterval = 2000; // Default 2 seconds
        private bool _showMonitorNames = true;
        private bool _lowQualityMode = false;
        private bool _captureCursor = false;
        private MonitorViewModel? _draggedMonitor;
        private int _refreshCount = 0;
        private bool _isRefreshing = false;

        public MainWindow()
        {
            InitializeComponent();
            InitializeMonitors();
            SetupRefreshTimer();
            SetupNotifyIcon();

            // Hook hotkeys
            this.KeyDown += MainWindow_KeyDown;

            // Update card sizes when window is resized
            this.SizeChanged += (s, e) => UpdateCardSizes();
        }

        private void InitializeMonitors()
        {
            _monitors.Clear();
            var screens = Screen.AllScreens;

            for (int i = 0; i < screens.Length; i++)
            {
                var monitor = new MonitorViewModel
                {
                    MonitorIndex = i + 1,
                    MonitorName = screens[i].DeviceName,
                    Bounds = screens[i].Bounds,
                    IsPrimary = screens[i].Primary
                };
                monitor.UpdateDisplayName();
                _monitors.Add(monitor);
            }

            MonitorsItemsControl.ItemsSource = _monitors;
            RefreshAllMonitors();
            UpdateCardSizes();
        }

        private void UpdateCardSizes()
        {
            if (_monitors == null || _monitors.Count == 0) return;
            
            // Calculate card width based on window width and number of monitors
            // Subtract margins and padding (10px window margin + 5px per card margin * 2 sides * count + 20px buffer)
            double availableWidth = this.ActualWidth - 20 - (_monitors.Count * 10) - 20;
            double cardWidth = Math.Max(120, availableWidth / _monitors.Count);

            foreach (var monitor in _monitors)
            {
                monitor.CardWidth = cardWidth;
                
                // Calculate height based on actual aspect ratio of the monitor
                double aspect = monitor.Bounds.Width > 0 
                    ? (double)monitor.Bounds.Height / monitor.Bounds.Width 
                    : 9.0 / 16.0;
                
                monitor.CardHeight = (cardWidth * aspect) + 40; // +40 for text and padding
            }
        }

        private void SetupRefreshTimer()
        {
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(_refreshInterval);
            _refreshTimer.Tick += (s, e) => RefreshAllMonitors();
            _refreshTimer.Start();
        }

        private void SetRefreshInterval(int milliseconds)
        {
            _refreshInterval = milliseconds;
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Interval = TimeSpan.FromMilliseconds(_refreshInterval);
                _refreshTimer.Start();
            }
        }

        private void SetupNotifyIcon()
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon();
            
            // Try to load app icon from base directory
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "computer_115306.ico");
                if (File.Exists(iconPath))
                {
                    _notifyIcon.Icon = new Icon(iconPath);
                }
                else
                {
                    _notifyIcon.Icon = SystemIcons.Application;
                }
            }
            catch
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }

            _notifyIcon.Text = "Monitor Preview";
            _notifyIcon.Visible = true;
            
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    this.Show();
                    this.WindowState = WindowState.Normal;
                    this.Activate();
                }
            };

            // Hook close event to clean up tray icon
            this.Closed += (s, e) =>
            {
                if (_notifyIcon != null)
                {
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                }
            };
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized)
            {
                this.Hide(); // Hide from taskbar and show only in tray
            }
            base.OnStateChanged(e);
        }

        private void MainWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    this.WindowState = WindowState.Minimized;
                    break;
                case Key.T:
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        this.Topmost = !this.Topmost;
                    }
                    break;
                case Key.Q:
                    if (Keyboard.Modifiers == ModifierKeys.Control)
                    {
                        System.Windows.Application.Current.Shutdown();
                    }
                    break;
            }
        }

        private async void RefreshAllMonitors()
        {
            if (_isRefreshing) return;
            _isRefreshing = true;

            try
            {
                var screens = Screen.AllScreens;
                
                // Check if monitor count has changed
                if (screens.Length != _monitors.Count)
                {
                    await RebuildMonitorListAsync(screens);
                    UpdateCardSizes();
                    return;
                }
                
                // Capture all monitors concurrently on background threads
                var tasks = _monitors.Select(async monitor =>
                {
                    var screen = screens.FirstOrDefault(s => s.DeviceName == monitor.MonitorName);
                    if (screen != null)
                    {
                        monitor.Bounds = screen.Bounds;
                        monitor.IsPrimary = screen.Primary;
                        monitor.UpdateDisplayName();
                        
                        if (!monitor.IsRefreshDisabled)
                        {
                            var bounds = monitor.Bounds;
                            var cardWidth = monitor.CardWidth;
                            ImageSource? preview = await Task.Run(() => CaptureScreen(bounds, cardWidth));
                            if (preview != null)
                            {
                                monitor.Preview = preview;
                            }
                        }
                    }
                }).ToList();

                await Task.WhenAll(tasks);
            }
            catch
            {
                // Silently ignore refresh errors to prevent app crashes
            }
            finally
            {
                _isRefreshing = false;
            }

            // Force garbage collection every 50 refreshes
            _refreshCount++;
            if (_refreshCount >= 50)
            {
                _refreshCount = 0;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
        
        private async Task RebuildMonitorListAsync(Screen[] screens)
        {
            _monitors.Clear();
            
            for (int i = 0; i < screens.Length; i++)
            {
                var monitor = new MonitorViewModel
                {
                    MonitorIndex = i + 1,
                    MonitorName = screens[i].DeviceName,
                    Bounds = screens[i].Bounds,
                    IsPrimary = screens[i].Primary
                };
                monitor.UpdateDisplayName();
                _monitors.Add(monitor);
            }

            // Capture initial previews in the background
            var tasks = _monitors.Select(async monitor =>
            {
                var bounds = monitor.Bounds;
                var cardWidth = monitor.CardWidth;
                var preview = await Task.Run(() => CaptureScreen(bounds, cardWidth));
                if (preview != null)
                {
                    monitor.Preview = preview;
                }
            }).ToList();

            await Task.WhenAll(tasks);
        }

        private BitmapSource? CaptureScreen(Rectangle bounds, double targetWidth)
        {
            try
            {
                // Determine scale dimensions based on card size
                int width = (int)targetWidth;
                int height = (int)(targetWidth * bounds.Height / bounds.Width);

                using (Bitmap fullBitmap = new Bitmap(bounds.Width, bounds.Height))
                {
                    using (Graphics gFull = Graphics.FromImage(fullBitmap))
                    {
                        gFull.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);

                        if (_captureCursor)
                        {
                            DrawCursorOnBitmap(gFull, bounds);
                        }
                    }

                    using (Bitmap scaledBitmap = new Bitmap(width, height))
                    {
                        using (Graphics g = Graphics.FromImage(scaledBitmap))
                        {
                            g.InterpolationMode = _lowQualityMode 
                                ? System.Drawing.Drawing2D.InterpolationMode.Low 
                                : System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                            g.DrawImage(fullBitmap, 0, 0, width, height);
                        }

                        return ConvertToBitmapSource(scaledBitmap);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static BitmapSource ConvertToBitmapSource(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);
            try
            {
                int size = bitmapData.Stride * bitmapData.Height;
                System.Windows.Media.PixelFormat pf;
                switch (bitmap.PixelFormat)
                {
                    case System.Drawing.Imaging.PixelFormat.Format24bppRgb:
                        pf = System.Windows.Media.PixelFormats.Bgr24;
                        break;
                    case System.Drawing.Imaging.PixelFormat.Format32bppRgb:
                        pf = System.Windows.Media.PixelFormats.Bgr32;
                        break;
                    case System.Drawing.Imaging.PixelFormat.Format32bppArgb:
                    default:
                        pf = System.Windows.Media.PixelFormats.Bgra32;
                        break;
                }

                BitmapSource bitmapSource = BitmapSource.Create(
                    bitmap.Width,
                    bitmap.Height,
                    bitmap.HorizontalResolution,
                    bitmap.VerticalResolution,
                    pf,
                    null,
                    bitmapData.Scan0,
                    size,
                    bitmapData.Stride);

                bitmapSource.Freeze();
                return bitmapSource;
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }
        }

        private void DrawCursorOnBitmap(Graphics g, Rectangle bounds)
        {
            try
            {
                CURSORINFO cursorInfo;
                cursorInfo.cbSize = Marshal.SizeOf(typeof(CURSORINFO));

                if (GetCursorInfo(out cursorInfo))
                {
                    // Check if cursor is visible (flags == 1 means visible)
                    if (cursorInfo.flags == 0x00000001)
                    {
                        // Check if cursor is within this monitor's bounds
                        if (cursorInfo.ptScreenPos.x >= bounds.Left &&
                            cursorInfo.ptScreenPos.x < bounds.Right &&
                            cursorInfo.ptScreenPos.y >= bounds.Top &&
                            cursorInfo.ptScreenPos.y < bounds.Bottom)
                        {
                            // Get cursor hotspot
                            ICONINFO iconInfo;
                            if (GetIconInfo(cursorInfo.hCursor, out iconInfo))
                            {
                                try
                                {
                                    // Calculate position relative to the bitmap
                                    int x = cursorInfo.ptScreenPos.x - bounds.X - iconInfo.xHotspot;
                                    int y = cursorInfo.ptScreenPos.y - bounds.Y - iconInfo.yHotspot;

                                    // Draw the cursor
                                    IntPtr hdc = g.GetHdc();
                                    try
                                    {
                                        DrawIcon(hdc, x, y, cursorInfo.hCursor);
                                    }
                                    finally
                                    {
                                        g.ReleaseHdc(hdc);
                                    }
                                }
                                finally
                                {
                                    // Clean up
                                    if (iconInfo.hbmColor != IntPtr.Zero)
                                        DeleteObject(iconInfo.hbmColor);
                                    if (iconInfo.hbmMask != IntPtr.Zero)
                                        DeleteObject(iconInfo.hbmMask);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Silently ignore cursor drawing errors
            }
        }

        private void MonitorBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && sender is Border border && border.DataContext is MonitorViewModel model)
            {
                _draggedMonitor = model;
                DragDrop.DoDragDrop(border, _draggedMonitor, System.Windows.DragDropEffects.Move);
            }
        }

        private void MonitorBorder_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (sender is Border targetBorder && targetBorder.DataContext is MonitorViewModel targetMonitor)
            {
                if (_draggedMonitor != null && _draggedMonitor != targetMonitor)
                {
                    int draggedIndex = _monitors.IndexOf(_draggedMonitor);
                    int targetIndex = _monitors.IndexOf(targetMonitor);

                    if (draggedIndex >= 0 && targetIndex >= 0)
                    {
                        _monitors.Move(draggedIndex, targetIndex);
                    }
                }
            }
        }

        private void MonitorBorder_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && e.LeftButton == MouseButtonState.Pressed)
            {
                try
                {
                    this.DragMove();
                }
                catch (InvalidOperationException)
                {
                    // Ignore if DragMove is called in an invalid state
                }
            }
        }

        private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var contextMenu = new ContextMenu();

            // Always on Top
            var alwaysOnTopItem = new MenuItem
            {
                Header = "Always on Top",
                IsCheckable = true,
                IsChecked = this.Topmost
            };
            alwaysOnTopItem.Click += (s, args) =>
            {
                this.Topmost = alwaysOnTopItem.IsChecked;
            };
            contextMenu.Items.Add(alwaysOnTopItem);

            // Refresh Time submenu
            var refreshTimeItem = new MenuItem { Header = "Refresh Time" };

            // Add Realtime option (30 FPS = ~33ms)
            var realtimeOption = new MenuItem
            {
                Header = "Realtime",
                IsCheckable = true,
                IsChecked = _refreshInterval == 33
            };
            realtimeOption.Click += (s, args) =>
            {
                SetRefreshInterval(33);

                foreach (MenuItem item in refreshTimeItem.Items)
                {
                    item.IsChecked = false;
                }
                realtimeOption.IsChecked = true;
            };
            refreshTimeItem.Items.Add(realtimeOption);

            // Add 0.5 seconds option
            var halfSecOption = new MenuItem
            {
                Header = "0.5 sec",
                IsCheckable = true,
                IsChecked = _refreshInterval == 500
            };
            halfSecOption.Click += (s, args) =>
            {
                SetRefreshInterval(500);

                foreach (MenuItem item in refreshTimeItem.Items)
                {
                    item.IsChecked = false;
                }
                halfSecOption.IsChecked = true;
            };
            refreshTimeItem.Items.Add(halfSecOption);

            // Add 1-5 seconds options
            foreach (int seconds in new[] { 1, 2, 3, 4, 5 })
            {
                int interval = seconds * 1000;
                var refreshOption = new MenuItem
                {
                    Header = $"{seconds} sec",
                    IsCheckable = true,
                    IsChecked = _refreshInterval == interval
                };
                refreshOption.Click += (s, args) =>
                {
                    SetRefreshInterval(interval);

                    foreach (MenuItem item in refreshTimeItem.Items)
                    {
                        item.IsChecked = false;
                    }
                    refreshOption.IsChecked = true;
                };
                refreshTimeItem.Items.Add(refreshOption);
            }
            contextMenu.Items.Add(refreshTimeItem);

            // Show Monitor Name
            var showNameItem = new MenuItem
            {
                Header = "Show Monitor Name",
                IsCheckable = true,
                IsChecked = _showMonitorNames
            };
            showNameItem.Click += (s, args) =>
            {
                _showMonitorNames = showNameItem.IsChecked;
                foreach (var monitor in _monitors)
                {
                    monitor.ShowName = _showMonitorNames;
                }
            };
            contextMenu.Items.Add(showNameItem);

            // Disable Refresh submenu
            var disableRefreshItem = new MenuItem { Header = "Disable Refresh" };
            foreach (var monitor in _monitors)
            {
                var monitorItem = new MenuItem
                {
                    Header = monitor.DisplayName,
                    IsCheckable = true,
                    IsChecked = monitor.IsRefreshDisabled,
                    Tag = monitor
                };
                monitorItem.Click += (s, args) =>
                {
                    if (s is MenuItem item && item.Tag is MonitorViewModel mon)
                    {
                        mon.IsRefreshDisabled = item.IsChecked;
                    }
                };
                disableRefreshItem.Items.Add(monitorItem);
            }
            contextMenu.Items.Add(disableRefreshItem);

            // Low Quality Preview
            var lowQualityItem = new MenuItem
            {
                Header = "Low Quality Preview",
                IsCheckable = true,
                IsChecked = _lowQualityMode
            };
            lowQualityItem.Click += (s, args) =>
            {
                _lowQualityMode = lowQualityItem.IsChecked;
            };
            contextMenu.Items.Add(lowQualityItem);

            // Capture Mouse Cursor
            var captureCursorItem = new MenuItem
            {
                Header = "Capture Mouse Cursor",
                IsCheckable = true,
                IsChecked = _captureCursor
            };
            captureCursorItem.Click += (s, args) =>
            {
                _captureCursor = captureCursorItem.IsChecked;
            };
            contextMenu.Items.Add(captureCursorItem);

            contextMenu.Items.Add(new Separator());

            // Exit
            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (s, args) =>
            {
                var result = System.Windows.MessageBox.Show("Are you sure you want to exit?", "Confirm Exit",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    System.Windows.Application.Current.Shutdown();
                }
            };
            contextMenu.Items.Add(exitItem);

            contextMenu.IsOpen = true;
        }
    }

    public class MonitorViewModel : INotifyPropertyChanged
    {
        private ImageSource? _preview;
        private bool _showName = true;
        private double _cardWidth = 180;
        private double _cardHeight = 160;
        private string _displayName = string.Empty;
        private bool _isRefreshDisabled = false;

        public int MonitorIndex { get; set; }
        public string MonitorName { get; set; } = string.Empty;

        private Rectangle _bounds;
        public Rectangle Bounds
        {
            get => _bounds;
            set
            {
                _bounds = value;
                UpdateDisplayName();
            }
        }

        private bool _isPrimary;
        public bool IsPrimary
        {
            get => _isPrimary;
            set
            {
                _isPrimary = value;
                UpdateDisplayName();
            }
        }

        public bool IsRefreshDisabled
        {
            get => _isRefreshDisabled;
            set
            {
                _isRefreshDisabled = value;
                OnPropertyChanged(nameof(IsRefreshDisabled));
                OnPropertyChanged(nameof(DisabledOverlayVisibility));
            }
        }

        public Visibility DisabledOverlayVisibility => IsRefreshDisabled ? Visibility.Visible : Visibility.Collapsed;

        public double CardWidth
        {
            get => _cardWidth;
            set
            {
                _cardWidth = value;
                OnPropertyChanged(nameof(CardWidth));
            }
        }

        public double CardHeight
        {
            get => _cardHeight;
            set
            {
                _cardHeight = value;
                OnPropertyChanged(nameof(CardHeight));
            }
        }

        public ImageSource? Preview
        {
            get => _preview;
            set
            {
                _preview = value;
                OnPropertyChanged(nameof(Preview));
            }
        }

        public bool ShowName
        {
            get => _showName;
            set
            {
                _showName = value;
                OnPropertyChanged(nameof(ShowName));
                OnPropertyChanged(nameof(NameVisibility));
            }
        }

        public Visibility NameVisibility => ShowName ? Visibility.Visible : Visibility.Collapsed;

        public string DisplayName
        {
            get => _displayName;
            private set
            {
                _displayName = value;
                OnPropertyChanged(nameof(DisplayName));
            }
        }

        public void UpdateDisplayName()
        {
            string resolution = $"{Bounds.Width}x{Bounds.Height}";
            string primaryTag = IsPrimary ? " (Primary)" : "";
            DisplayName = $"M{MonitorIndex} - {resolution}{primaryTag}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}