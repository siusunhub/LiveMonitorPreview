using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

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

        private ObservableCollection<MonitorViewModel> _monitors;
        private DispatcherTimer _refreshTimer;
        private int _refreshInterval = 2000; // Default 2 seconds
        private bool _showMonitorNames = true;
        private bool _lowQualityMode = false;
        private bool _captureCursor = false;
        private MonitorViewModel _draggedMonitor;
        private int _refreshCount = 0;

        public MainWindow()
        {
            InitializeComponent();
            InitializeMonitors();
            SetupRefreshTimer();

            // Update card sizes when window is resized
            this.SizeChanged += (s, e) => UpdateCardSizes();
        }

        private void InitializeMonitors()
        {
            _monitors = new ObservableCollection<MonitorViewModel>();
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
            
            // Calculate card height to maintain roughly 16:9 aspect ratio for the image
            double cardHeight = (cardWidth * 9 / 16) + 40; // +40 for text and padding

            foreach (var monitor in _monitors)
            {
                monitor.CardWidth = cardWidth;
                monitor.CardHeight = cardHeight;
            }
        }

        private void SetupRefreshTimer()
        {
            _refreshTimer = new DispatcherTimer();
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(_refreshInterval);
            _refreshTimer.Tick += (s, e) => RefreshAllMonitors();
            _refreshTimer.Start();
        }

        private void RefreshAllMonitors()
        {
            var screens = Screen.AllScreens;
            
            // Check if monitor count has changed
            if (screens.Length != _monitors.Count)
            {
                RebuildMonitorList(screens);
                UpdateCardSizes();
                return;
            }
            
            // Update each monitor by matching DeviceName (not by position)
            foreach (var monitor in _monitors)
            {
                // Find the corresponding screen by device name
                var screen = screens.FirstOrDefault(s => s.DeviceName == monitor.MonitorName);
                
                if (screen != null)
                {
                    // Update resolution in case it changed
                    monitor.Bounds = screen.Bounds;
                    monitor.IsPrimary = screen.Primary;
                    monitor.UpdateDisplayName();
                    
                    // Update preview only if not disabled
                    if (!monitor.IsRefreshDisabled)
                    {
                        monitor.Preview = CaptureScreen(monitor.Bounds);
                    }
                }
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
        
        private void RebuildMonitorList(Screen[] screens)
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
                monitor.Preview = CaptureScreen(monitor.Bounds);
                _monitors.Add(monitor);
            }
        }

        private BitmapImage CaptureScreen(Rectangle bounds)
        {
            try
            {
                if (_lowQualityMode)
                {
                    // Low quality mode: reduced resolution and JPEG compression
                    int width = bounds.Width / 2;
                    int height = bounds.Height / 2;

                    using (Bitmap fullBitmap = new Bitmap(bounds.Width, bounds.Height))
                    {
                        using (Graphics gFull = Graphics.FromImage(fullBitmap))
                        {
                            gFull.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);

                            // Draw cursor if enabled
                            if (_captureCursor)
                            {
                                DrawCursorOnBitmap(gFull, bounds);
                            }
                        }

                        using (Bitmap scaledBitmap = new Bitmap(width, height))
                        {
                            using (Graphics g = Graphics.FromImage(scaledBitmap))
                            {
                                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                                g.DrawImage(fullBitmap, 0, 0, width, height);
                            }

                            using (MemoryStream memory = new MemoryStream())
                            {
                                scaledBitmap.Save(memory, ImageFormat.Jpeg);
                                memory.Position = 0;

                                BitmapImage bitmapImage = new BitmapImage();
                                bitmapImage.BeginInit();
                                bitmapImage.StreamSource = memory;
                                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                                bitmapImage.DecodePixelWidth = 180;
                                bitmapImage.EndInit();
                                bitmapImage.Freeze();

                                return bitmapImage;
                            }
                        }
                    }
                }
                else
                {
                    // High quality mode: full resolution PNG
                    using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height))
                    {
                        using (Graphics g = Graphics.FromImage(bitmap))
                        {
                            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);

                            // Draw cursor if enabled
                            if (_captureCursor)
                            {
                                DrawCursorOnBitmap(g, bounds);
                            }
                        }

                        using (MemoryStream memory = new MemoryStream())
                        {
                            bitmap.Save(memory, ImageFormat.Png);
                            memory.Position = 0;

                            BitmapImage bitmapImage = new BitmapImage();
                            bitmapImage.BeginInit();
                            bitmapImage.StreamSource = memory;
                            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapImage.EndInit();
                            bitmapImage.Freeze();

                            return bitmapImage;
                        }
                    }
                }
            }
            catch
            {
                return null;
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
                                // Calculate position relative to the bitmap
                                int x = cursorInfo.ptScreenPos.x - bounds.X - iconInfo.xHotspot;
                                int y = cursorInfo.ptScreenPos.y - bounds.Y - iconInfo.yHotspot;

                                // Draw the cursor
                                IntPtr hdc = g.GetHdc();
                                DrawIcon(hdc, x, y, cursorInfo.hCursor);
                                g.ReleaseHdc(hdc);

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
            catch
            {
                // Silently ignore cursor drawing errors
            }
        }

        private void MonitorBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                var border = sender as Border;
                _draggedMonitor = border.DataContext as MonitorViewModel;
                DragDrop.DoDragDrop(border, _draggedMonitor, System.Windows.DragDropEffects.Move);
            }
        }

        private void MonitorBorder_Drop(object sender, System.Windows.DragEventArgs e)
        {
            var targetBorder = sender as Border;
            var targetMonitor = targetBorder.DataContext as MonitorViewModel;

            if (_draggedMonitor != null && targetMonitor != null && _draggedMonitor != targetMonitor)
            {
                int draggedIndex = _monitors.IndexOf(_draggedMonitor);
                int targetIndex = _monitors.IndexOf(targetMonitor);

                _monitors.Move(draggedIndex, targetIndex);
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
                _refreshInterval = 33;
                _refreshTimer.Stop();
                _refreshTimer.Interval = TimeSpan.FromMilliseconds(_refreshInterval);
                _refreshTimer.Start();

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
                _refreshInterval = 500;
                _refreshTimer.Stop();
                _refreshTimer.Interval = TimeSpan.FromMilliseconds(_refreshInterval);
                _refreshTimer.Start();

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
                var refreshOption = new MenuItem
                {
                    Header = $"{seconds} sec",
                    IsCheckable = true,
                    IsChecked = _refreshInterval == seconds * 1000
                };
                int interval = seconds * 1000;
                refreshOption.Click += (s, args) =>
                {
                    _refreshInterval = interval;
                    _refreshTimer.Stop();
                    _refreshTimer.Interval = TimeSpan.FromMilliseconds(_refreshInterval);
                    _refreshTimer.Start();

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
                    var mon = (s as MenuItem).Tag as MonitorViewModel;
                    mon.IsRefreshDisabled = (s as MenuItem).IsChecked;
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
                    // Qualify Application to avoid ambiguity with System.Windows.Forms.Application
                    System.Windows.Application.Current.Shutdown();
                }
            };
            contextMenu.Items.Add(exitItem);

            contextMenu.IsOpen = true;
        }
    }

    public class MonitorViewModel : INotifyPropertyChanged
    {
        private BitmapImage _preview;
        private bool _showName = true;
        private double _cardWidth = 180;
        private double _cardHeight = 160;
        private string _displayName;
        private bool _isRefreshDisabled = false;

        public int MonitorIndex { get; set; }
        public string MonitorName { get; set; }

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

        public BitmapImage Preview
        {
            get => _preview;
            set
            {
                // Option 2: Clear reference to old image to help GC
                var oldPreview = _preview;
                _preview = value;
                OnPropertyChanged(nameof(Preview));

                // Allow old image to be garbage collected
                if (oldPreview != null)
                {
                    oldPreview = null;
                }
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

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}