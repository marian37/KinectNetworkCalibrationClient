using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClientApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public Client client;

        private KinectAdapter kinectAdapter;

        private IPAddress clientAddress;
        private IPAddress subnetMask;

        private bool IsConnected;

        public MainWindow()
        {
            InitializeComponent();
            client = new Client(this);
            this.kinectAdapter = new KinectAdapter(this);
            client.ChangeStatusEventHandler += OnChangeStatus;
            ipAddressesComboBox.ItemsSource = client.getPossibleIpAddresses();
            ipAddressesComboBox.SelectedIndex = 0;
            portTxtBox.Text = "" + Client.DEFAULT_PORT;
        }

        public void OnChangeStatus(object sender, ChangeStatusEventArgs e)
        {
            Dispatcher.Invoke(() => { statusBar.Text = e.status; });
        }

        private void connectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (IsConnected)
            {
                Disconnect();
            }
            else
            {
                SearchAndConnect();
            }
        }

        private void SearchAndConnect()
        {
            try
            {
                clientAddress = (IPAddress)ipAddressesComboBox.SelectedItem;
                subnetMask = Client.GetSubnetMask(clientAddress);

                if (clientAddress != null && subnetMask != null)
                {
                    int port = int.Parse(portTxtBox.Text);
                    Task.Run(() =>
                    {
                        return client.SearchAndConnect(clientAddress, subnetMask, port);
                    }).ContinueWith((Task<bool> task) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (task.Result)
                            {
                                searchBtn.Content = "Disconnect";
                                IsConnected = true;
                            }
                        });
                    });
                }
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.ToString());
            }
        }

        private void Disconnect()
        {
            client.Disconnect();
            IsConnected = false;
            searchBtn.Content = "Search";
        }

        private void connectKinectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (this.kinectAdapter.sensorOpened)
            {
                this.kinectAdapter.Close();
                connectKinectBtn.Content = "Connect Kinect";
            }
            else
            {
                this.kinectAdapter.Open();
                connectKinectBtn.Content = "Disconnect Kinect";
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (IsConnected)
            {
                Disconnect();
            }

            if (this.kinectAdapter != null)
            {
                this.kinectAdapter.Dispose();
                this.kinectAdapter = null;
            }
        }
    }
}
