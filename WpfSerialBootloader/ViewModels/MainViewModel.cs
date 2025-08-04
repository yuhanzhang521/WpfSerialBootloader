using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using WpfSerialBootloader.Models;
using WpfSerialBootloader.Services;

namespace WpfSerialBootloader.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly SerialPortService serialService_;

        [ObservableProperty]
        private ObservableCollection<string> availablePorts = [];

        [ObservableProperty]
        private string selectedPort = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsConnectionPossible))]
        private int baudRate = 115200;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        [NotifyCanExecuteChangedFor(nameof(UploadFirmwareCommand))]
        private bool isConnected = false;

        [ObservableProperty]
        private string firmwareFilePath = string.Empty;

        [ObservableProperty]
        private string userInput = string.Empty;

        [ObservableProperty]
        private double uploadProgress;

        [ObservableProperty]
        private bool isUploading;

        [ObservableProperty]
        private string statusText = "Ready";

        public ObservableCollection<TerminalMessage> TerminalOutput { get; } = [];

        public bool IsConnectionPossible => !string.IsNullOrEmpty(SelectedPort);

        public MainViewModel()
        {
            serialService_ = new SerialPortService();
            serialService_.DataReceived += OnSerialDataReceived;
            serialService_.ConnectionLost += OnSerialConnectionLost;

            ScanPorts();
            if (AvailablePorts.Any())
            {
                SelectedPort = AvailablePorts[0];
            }
        }

        private void OnSerialConnectionLost()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsConnected = false;
                StatusText = "Error: Device disconnected.";
                AddLogMessage(MessageDirection.RX, "--- CONNECTION LOST ---");
            });
        }

        private void OnSerialDataReceived(string data)
        {
            // Data is received on a background thread, so we must dispatch to the UI thread.
            Application.Current.Dispatcher.Invoke(() =>
            {
                AddLogMessage(MessageDirection.RX, data.Trim());
            });
        }

        private void AddLogMessage(MessageDirection direction, string message)
        {
            if (TerminalOutput.Count > 2000) // Keep the log from growing indefinitely
            {
                TerminalOutput.RemoveAt(0);
            }
            TerminalOutput.Add(new TerminalMessage(direction, message));
        }

        [RelayCommand]
        private void ScanPorts()
        {
            AvailablePorts = new ObservableCollection<string>(SerialPortService.GetAvailablePorts());
            StatusText = "Refreshed serial ports.";
        }

        [RelayCommand(CanExecute = nameof(IsConnectionPossible))]
        private void Connect()
        {
            if (IsConnected) return;
            try
            {
                serialService_.Connect(SelectedPort, BaudRate);
                IsConnected = true;
                StatusText = $"Connected to {SelectedPort} at {BaudRate} bps.";
                AddLogMessage(MessageDirection.RX, "--- CONNECTED ---");
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }
        }

        [RelayCommand(CanExecute = nameof(IsConnected))]
        private void Disconnect()
        {
            if (!IsConnected) return;
            serialService_.Disconnect();
            IsConnected = false;
            StatusText = "Disconnected.";
            AddLogMessage(MessageDirection.RX, "--- DISCONNECTED ---");
        }

        [RelayCommand]
        private void BrowseFile()
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Hex files (*.hex)|*.hex|All files (*.*)|*.*",
                Title = "Select a firmware file"
            };
            if (openFileDialog.ShowDialog() == true)
            {
                FirmwareFilePath = openFileDialog.FileName;
            }
        }

        [RelayCommand(CanExecute = nameof(IsConnected))]
        private async Task UploadFirmware()
        {
            if (string.IsNullOrEmpty(FirmwareFilePath))
            {
                StatusText = "Please select a firmware file first.";
                return;
            }

            IsUploading = true;
            UploadProgress = 0;

            try
            {
                // 1. Load and parse firmware file
                StatusText = "Reading and parsing firmware...";
                var firmware = new Firmware(FirmwareFilePath);
                AddLogMessage(MessageDirection.TX, $"Preparing to upload '{Path.GetFileName(FirmwareFilePath)}' ({firmware.TotalSize} bytes)");
                AddLogMessage(MessageDirection.TX, $"Calculated CRC32: 0x{BitConverter.ToUInt32(firmware.CrcBytes, 0):X8}");

                // 2. Send data sequentially
                StatusText = "Uploading: Sending magic word...";
                await serialService_.WriteAsync(firmware.MagicBytes);
                await Task.Delay(10); // Small delay like in the python script

                StatusText = "Uploading: Sending program size...";
                await serialService_.WriteAsync(firmware.SizeBytes);
                await Task.Delay(10);

                StatusText = "Uploading: Sending payload...";
                int chunkSize = 256;
                for (int i = 0; i < firmware.TotalSize; i += chunkSize)
                {
                    int size = Math.Min(chunkSize, firmware.TotalSize - i);
                    var chunk = new byte[size];
                    Array.Copy(firmware.Payload, i, chunk, 0, size);
                    await serialService_.WriteAsync(chunk);
                    UploadProgress = ((double)(i + size) / firmware.TotalSize) * 100;
                }

                StatusText = "Uploading: Sending CRC...";
                await serialService_.WriteAsync(firmware.CrcBytes);

                StatusText = "Firmware upload complete!";
                AddLogMessage(MessageDirection.TX, "--- UPLOAD COMPLETE ---");
            }
            catch (Exception ex)
            {
                StatusText = $"Error during upload: {ex.Message}";
                AddLogMessage(MessageDirection.RX, $"--- UPLOAD FAILED: {ex.Message} ---");
            }
            finally
            {
                IsUploading = false;
                UploadProgress = 0;
            }
        }

        [RelayCommand(CanExecute = nameof(IsConnected))]
        private async Task Send()
        {
            if (string.IsNullOrEmpty(UserInput)) return;

            try
            {
                string messageToSend = UserInput + "\n";
                byte[] data = Encoding.UTF8.GetBytes(messageToSend);
                await serialService_.WriteAsync(data);

                AddLogMessage(MessageDirection.TX, UserInput);
                UserInput = string.Empty; // Clear input box
            }
            catch (Exception ex)
            {
                StatusText = $"Send error: {ex.Message}";
            }
        }
    }
}
