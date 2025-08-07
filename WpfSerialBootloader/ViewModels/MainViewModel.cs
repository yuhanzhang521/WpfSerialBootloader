// Add this using statement for WMI to get detailed port names.
// You may need to add the NuGet package: System.Management
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Windows;
using WpfSerialBootloader.Models;
using WpfSerialBootloader.Services;

namespace WpfSerialBootloader.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly SerialPortService serialService_;

        #region Observable Properties
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
        private string firmwareFilePath = Properties.Settings.Default.LastUsedFilePath ?? string.Empty;

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
        #endregion

        #region Public Properties
        public ObservableCollection<TerminalMessage> TerminalOutput { get; } = [];

        public bool IsConnectionPossible => !string.IsNullOrEmpty(SelectedPort);
        #endregion


        #region Relay Commands
        [RelayCommand]
        private void ScanPorts()
        {
            try
            {
                // Use WMI to get ports with their descriptions for a better user experience
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Caption LIKE '%(COM%'");
                var portObjects = searcher.Get().Cast<ManagementObject>().ToList();

                var detailedPorts = SerialPortService.GetAvailablePorts()
                    .Select(p =>
                    {
                        var portObject = portObjects.FirstOrDefault(o => o["Caption"]?.ToString()?.Contains($"({p})") ?? false);
                        var caption = portObject?["Caption"]?.ToString() ?? p;
                        return $"{p} - {caption.Replace($"({p})", "").Trim()}";
                    })
                    .ToList();

                AvailablePorts = new ObservableCollection<string>(detailedPorts);
                StatusText = "Refreshed serial ports.";
            }
            catch (Exception ex)
            {
                // Fallback to simple port names if WMI fails
                AvailablePorts = new ObservableCollection<string>(SerialPortService.GetAvailablePorts());
                StatusText = "Refreshed serial ports (WMI failed).";
                Debug.WriteLine($"WMI query for serial ports failed: {ex.Message}");
            }
        }

        [RelayCommand(CanExecute = nameof(IsConnectionPossible))]
        private void Connect()
        {
            PerformConnect();
        }

        [RelayCommand(CanExecute = nameof(IsConnected))]
        private void Disconnect()
        {
            PerformDisconnect();
        }

        [RelayCommand]
        private void BrowseFile()
        {
            PerformFileBrowse();
        }

        [RelayCommand(CanExecute = nameof(IsConnected))]
        private async Task UploadFirmware()
        {
            await PerformFirmwareUploadAsync();
        }

        [RelayCommand(CanExecute = nameof(IsConnected))]
        private async Task Send()
        {
            await PerformSendAsync();
        }

        [RelayCommand]
        private void ClearTerminal()
        {
            TerminalOutput.Clear();
            AddLogMessage(MessageDirection.INFO, "--- TERMINAL CLEARED ---");
        }
        #endregion

        public MainViewModel()
        {
            serialService_ = new SerialPortService();
            serialService_.DataReceived += OnSerialDataReceived;
            serialService_.ConnectionLost += OnSerialConnectionLost;

            ScanPorts();

            if (AvailablePorts.Any())
            {
                // Try to restore the last used port
                var lastUsedPort = Properties.Settings.Default.LastUsedComPort;
                if (!string.IsNullOrEmpty(lastUsedPort))
                {
                    // Find the port that starts with the saved name (e.g., "COM3")
                    var portToSelect = AvailablePorts.FirstOrDefault(p => p.StartsWith(lastUsedPort));
                    SelectedPort = portToSelect ?? AvailablePorts[0];
                }
                else
                {
                    SelectedPort = AvailablePorts[0];
                }
            }
        }

        #region Command Logic Implementation
        private void PerformConnect()
        {
            if (IsConnected || string.IsNullOrEmpty(SelectedPort)) return;
            try
            {
                // Extract port name (e.g., "COM3") from the full string "COM3 - Description"
                string portName = SelectedPort.Split(' ')[0];

                serialService_.Connect(portName, BaudRate);
                IsConnected = true;
                StatusText = $"Connected to {SelectedPort} at {BaudRate} bps.";
                AddLogMessage(MessageDirection.INFO, $"--- CONNECTED TO {SelectedPort} ---");

                // Save settings
                Properties.Settings.Default.LastUsedComPort = portName;
                Properties.Settings.Default.LastUsedBaudRate = BaudRate;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }
        }

        private void PerformDisconnect()
        {
            if (!IsConnected) return;
            serialService_.Disconnect();
            IsConnected = false;
            StatusText = "Disconnected.";
            AddLogMessage(MessageDirection.INFO, "--- DISCONNECTED ---");
        }

        private void PerformFileBrowse()
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Hex files (*.hex)|*.hex|All files (*.*)|*.*",
                Title = "Select a firmware file"
            };

            string lastPath = Properties.Settings.Default.LastUsedFilePath;
            if (!string.IsNullOrEmpty(lastPath) && !string.IsNullOrEmpty(Path.GetDirectoryName(lastPath)) && Directory.Exists(Path.GetDirectoryName(lastPath)))
            {
                openFileDialog.InitialDirectory = Path.GetDirectoryName(lastPath);
            }

            if (openFileDialog.ShowDialog() == true)
            {
                FirmwareFilePath = openFileDialog.FileName;
                Properties.Settings.Default.LastUsedFilePath = FirmwareFilePath;
                Properties.Settings.Default.Save();
            }
        }

        private async Task PerformFirmwareUploadAsync()
        {
            if (string.IsNullOrEmpty(FirmwareFilePath) || !File.Exists(FirmwareFilePath))
            {
                StatusText = "Please select a valid firmware file first.";
                return;
            }

            IsUploading = true;
            UploadProgress = 0;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                StatusText = "Reading and parsing firmware...";
                var firmware = new Firmware(FirmwareFilePath);
                AddLogMessage(MessageDirection.INFO, "--- UPLOAD START ---");
                AddLogMessage(MessageDirection.INFO, $"Preparing to upload '{Path.GetFileName(FirmwareFilePath)}' ({firmware.TotalSize} bytes)");
                AddLogMessage(MessageDirection.INFO, $"Calculated CRC32: 0x{BitConverter.ToUInt32(firmware.CrcBytes, 0):X8}");

                StatusText = "Uploading: Sending magic word...";
                await serialService_.WriteAsync(firmware.MagicBytes);
                await Task.Delay(10);

                StatusText = "Uploading: Sending program size...";
                await serialService_.WriteAsync(firmware.SizeBytes);
                await Task.Delay(10);

                StatusText = "Uploading: Sending payload...";
                const int chunkSize = 1024;
                long bytesSentSoFar = 0;

                for (int i = 0; i < firmware.TotalSize; i += chunkSize)
                {
                    int size = Math.Min(chunkSize, firmware.TotalSize - i);
                    var chunk = new byte[size];
                    Array.Copy(firmware.Payload, i, chunk, 0, size);
                    await serialService_.WriteAsync(chunk);

                    bytesSentSoFar += size;
                    UploadProgress = (double)bytesSentSoFar / firmware.TotalSize * 100;
                    UpdateUploadStats(bytesSentSoFar, firmware.TotalSize, stopwatch.Elapsed);
                }

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
                stopwatch.Stop();
                IsUploading = false;
                UploadProgress = 0;
            }
        }

        private async Task PerformSendAsync()
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
        #endregion

        #region Private Helpers & Event Handlers
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

        private void UpdateUploadStats(long bytesSent, long totalBytes, TimeSpan elapsed)
        {
            var elapsedSeconds = elapsed.TotalSeconds;
            if (elapsedSeconds < 0.1) return; // Avoid division by zero or skewed initial readings

            double speed = bytesSent / elapsedSeconds; // Bytes per second
            if (speed > 1024 * 1024)
                UploadSpeed = $"{speed / (1024 * 1024):F2} MB/s";
            else if (speed > 1024)
                UploadSpeed = $"{speed / 1024:F2} KB/s";
            else
                UploadSpeed = $"{(int)speed} B/s";

            double bytesLeft = totalBytes - bytesSent;
            double remainingSeconds = speed > 0 ? bytesLeft / speed : 0;
            UploadRemainingTime = $"{TimeSpan.FromSeconds(remainingSeconds):m\\:ss}";
        }
        #endregion
    }
}
