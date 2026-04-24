using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace LiveMonitorPreview
{
    public partial class App : System.Windows.Application
    {
        [DllImport("shcore.dll")]
        static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        protected override void OnStartup(StartupEventArgs e)
        {
            // Set DPI awareness to get true pixel coordinates
            try
            {
                // Try Windows 8.1+ method first (Per Monitor DPI Aware)
                SetProcessDpiAwareness(2); // PROCESS_PER_MONITOR_DPI_AWARE
            }
            catch
            {
                try
                {
                    // Fallback to Windows Vista-8 method (System DPI Aware)
                    SetProcessDPIAware();
                }
                catch
                {
                    // If both fail, continue without DPI awareness
                }
            }

            base.OnStartup(e);
        }
    }
}
