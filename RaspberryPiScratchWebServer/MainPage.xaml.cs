using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace RaspberryPiScratchWebServer
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        readonly HttpServer _httpServer = new HttpServer(80);

        // set everything up and go
        public MainPage()
        {
            this.InitializeComponent();
            _httpServer.StatusUpdate += _httpServer_StatusUpdate;
            _httpServer.InitialiseHardware();
            _httpServer.StartServer();
            this.textBoxPiFaceStatus.Text = "";
            this.textBoxGertbotStatus.Text = "";
        }

        // show status updates
        private void _httpServer_StatusUpdate(object sender, HttpServerStatusArgs e)
        {
            if (e.Status != null)
            {
                var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (e.PeripheralId == 0)
                    {
                        this.textBoxPiFaceState.Text = _httpServer.HardwareOk(e.PeripheralId) ? "Hardware Ok" : "Hardware NOT Ok!";
                        this.textBoxPiFaceStatus.Text = e.Status;
                    }
                    else if (e.PeripheralId == 1)
                    {
                        this.textBoxGertbotState.Text = _httpServer.HardwareOk(e.PeripheralId) ? "Hardware Ok" : "Hardware NOT Ok!";
                        this.textBoxGertbotStatus.Text = e.Status;
                    }
                });
            }
            else
            {
                var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    this.textBoxDistance.Text = e.Distance.ToString();
                });
            }
        }
    }
}
