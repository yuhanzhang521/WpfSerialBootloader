using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
        private int baudRate = Properties.Settings.Default.LastUsedBaudRate;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        [NotifyCanExecuteChangedFor(nameof(UploadFirmwareCommand))]
        private bool isConnected = false;

        [ObservableProperty]
        private string firmwareFilePath = Properties.Settings.Default.LastUsedFilePath ?? String.Empty;

        [ObservableProperty]
        private string userInput = string.Empty;

        [ObservableProperty]
        private double uploadProgress;

        [ObservableProperty]
        private bool isUploading;

        [ObservableProperty]
        private string statusText = "Ready";

        [ObservableProperty]
        private string uploadSpeed = "0 KB/s";

        [ObservableProperty]
        private string uploadRemainingTime = "N/A";

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
                var lastUsedPort = Properties.Settings.Default.LastUsedComPort;

                var portToSelect = AvailablePorts.FirstOrDefault(p => p == lastUsedPort);

                SelectedPort = portToSelect ?? AvailablePorts[0];
            }
        }

        private void OnSerialConnectionLost()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsConnected = false;
                StatusText = "Error: Device disconnected.";
                AddLogMessage(MessageDirection.INFO, "--- CONNECTION LOST ---");
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
                AddLogMessage(MessageDirection.INFO, $"--- CONNECTED TO {SelectedPort} ---");

                Properties.Settings.Default.LastUsedComPort = SelectedPort;
                Properties.Settings.Default.LastUsedBaudRate = BaudRate;
                Properties.Settings.Default.Save();
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
            AddLogMessage(MessageDirection.INFO, "--- DISCONNECTED ---");
        }

        [RelayCommand]
        private void BrowseFile()
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Hex files (*.hex)|*.hex|All files (*.*)|*.*",
                Title = "Select a firmware file"
            };

            string lastPath = Properties.Settings.Default.LastUsedFilePath;

            if (!string.IsNullOrEmpty(lastPath) && Directory.Exists(lastPath))
            {
                openFileDialog.InitialDirectory = Path.GetDirectoryName(lastPath);
            }

            if (openFileDialog.ShowDialog() == true)
            {
                // A file was successfully selected.
                FirmwareFilePath = openFileDialog.FileName;

                string? currentPath = FirmwareFilePath;

                // 5. Save the new directory back to the settings for next time.
                Properties.Settings.Default.LastUsedFilePath = currentPath;
                Properties.Settings.Default.Save(); // This is crucial to persist the change!
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
                AddLogMessage(MessageDirection.INFO, $"Preparing to upload '{Path.GetFileName(FirmwareFilePath)}' ({firmware.TotalSize} bytes)");
                AddLogMessage(MessageDirection.INFO, $"Calculated CRC32: 0x{BitConverter.ToUInt32(firmware.CrcBytes, 0):X8}");

                // 2. Send data sequentially
                StatusText = "Uploading: Sending magic word...";
                await serialService_.WriteAsync(firmware.MagicBytes);
                await Task.Delay(10); // Small delay like in the python script

                StatusText = "Uploading: Sending program size...";
                await serialService_.WriteAsync(firmware.SizeBytes);
                await Task.Delay(10);

                StatusText = "Uploading: Sending payload...";
                int chunkSize = 1024; // Using a slightly larger chunk size
                long bytesSentSoFar = 0;
                var stopwatch = new Stopwatch();
                stopwatch.Start(); // Start timing right before payload transfer

                for (int i = 0; i < firmware.TotalSize; i += chunkSize)
                {
                    int size = Math.Min(chunkSize, firmware.TotalSize - i);
                    var chunk = new byte[size];
                    Array.Copy(firmware.Payload, i, chunk, 0, size);
                    await serialService_.WriteAsync(chunk);

                    bytesSentSoFar += size;
                    UploadProgress = ((double)bytesSentSoFar / firmware.TotalSize) * 100;

                    var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                    if (elapsedSeconds > 0.1) // Update speed and remaining time only after a short moment
                    {
                        double speed = bytesSentSoFar / elapsedSeconds; // Bytes per second
                        if (speed > 1024 * 1024)
                            UploadSpeed = $"{speed / (1024 * 1024):F2} MB/s";
                        else if (speed > 1024)
                            UploadSpeed = $"{speed / 1024:F2} KB/s";
                        else
                            UploadSpeed = $"{(int)speed} B/s";

                        // Calculate and update remaining time
                        double bytesLeft = firmware.TotalSize - bytesSentSoFar;
                        double remainingSeconds = speed > 0 ? bytesLeft / speed : 0;
                        UploadRemainingTime = $"{elapsedSeconds:F0}s < {remainingSeconds:F0} s";
                    }
                }
                stopwatch.Stop();

                StatusText = "Uploading: Sending CRC...";
                await serialService_.WriteAsync(firmware.CrcBytes);

                StatusText = "Firmware upload complete!";
                AddLogMessage(MessageDirection.INFO, "--- UPLOAD COMPLETE ---");
            }
            catch (Exception ex)
            {
                StatusText = $"Error during upload: {ex.Message}";
                AddLogMessage(MessageDirection.INFO, $"--- UPLOAD FAILED: {ex.Message} ---");
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
