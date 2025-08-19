using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using WpfSerialBootloader.Models;
using WpfSerialBootloader.Services;

namespace WpfSerialBootloader.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly SerialPortService serialService_;

        // --- New members for hot-plug detection ---
        private ManagementEventWatcher? _portWatcher;
        private string _connectedPortName = string.Empty;

        // --- New members for real-time speed calculation ---
        private long _lastBytesSent;
        private TimeSpan _lastElapsed;

        #region Observable Properties
        [ObservableProperty]
        private ObservableCollection<string> availablePorts = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsConnectionPossible))]
        private string selectedPort = string.Empty;

        [ObservableProperty]
        private int baudRate = Properties.Settings.Default.LastUsedBaudRate;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(SendCommand))]
        [NotifyCanExecuteChangedFor(nameof(UploadFirmwareCommand))]
        [NotifyCanExecuteChangedFor(nameof(ResetProgramCommand))] // Notify new command
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

                var previouslySelectedPort = SelectedPort;
                AvailablePorts = new ObservableCollection<string>(detailedPorts);

                // Try to re-select the previously selected port if it still exists
                if (!string.IsNullOrEmpty(previouslySelectedPort) && AvailablePorts.Contains(previouslySelectedPort))
                {
                    SelectedPort = previouslySelectedPort;
                }
                else if (AvailablePorts.Any())
                {
                    SelectedPort = AvailablePorts[0];
                }
                else
                {
                    SelectedPort = string.Empty;
                }

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

        // --- New Command for DTR Reset ---
        [RelayCommand(CanExecute = nameof(IsConnected))]
        private async Task ResetProgram()
        {
            StatusText = "Resetting program via DTR...";
            AddLogMessage(MessageDirection.INFO, "--- PROGRAM RESET (DTR) ---");
            try
            {
                await serialService_.PulseDtrAsync(100);
                StatusText = "Program reset complete.";
            }
            catch (Exception ex)
            {
                StatusText = $"Reset failed: {ex.Message}";
                AddLogMessage(MessageDirection.INFO, $"--- RESET FAILED: {ex.Message} ---");
            }
        }
        #endregion

        public MainViewModel()
        {
            serialService_ = new SerialPortService();
            serialService_.DataReceived += OnSerialDataReceived;
            serialService_.ConnectionLost += OnSerialConnectionLost;

            InitializePortWatcher();
            ScanPorts();

            if (AvailablePorts.Any())
            {
                var lastUsedPortName = Properties.Settings.Default.LastUsedComPort;
                if (!string.IsNullOrEmpty(lastUsedPortName))
                {
                    var portToSelect = AvailablePorts.FirstOrDefault(p => p.StartsWith(lastUsedPortName));
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
                string portName = SelectedPort.Split(' ')[0];
                _connectedPortName = portName; // Store the raw port name for hot-plug check

                serialService_.Connect(portName, BaudRate);
                IsConnected = true;
                StatusText = $"Connected to {SelectedPort} at {BaudRate} bps.";
                AddLogMessage(MessageDirection.INFO, $"--- CONNECTED TO {SelectedPort} ---");

                Properties.Settings.Default.LastUsedComPort = portName;
                Properties.Settings.Default.LastUsedBaudRate = BaudRate;
                Properties.Settings.Default.Save();
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
                _connectedPortName = string.Empty;
            }
        }

        private void PerformDisconnect()
        {
            if (!IsConnected) return;
            serialService_.Disconnect();
            IsConnected = false;
            _connectedPortName = string.Empty;
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
            _lastBytesSent = 0; // Reset for real-time speed calculation
            _lastElapsed = TimeSpan.Zero; // Reset for real-time speed calculation
            var stopwatch = Stopwatch.StartNew();

            try
            {
                StatusText = "Reading and parsing firmware...";
                var firmware = new Firmware(FirmwareFilePath);
                AddLogMessage(MessageDirection.INFO, "--- UPLOAD START ---");
                AddLogMessage(MessageDirection.INFO, $"Preparing to upload '{Path.GetFileName(FirmwareFilePath)}' ({firmware.TotalSize} bytes)");
                AddLogMessage(MessageDirection.INFO, $"Calculated CRC32: 0x{BitConverter.ToUInt32(firmware.CrcBytes, 0):X8}");

                // --- Automatic Reset via RTS ---
                StatusText = "Resetting device via RTS...";
                await serialService_.ToggleRtsForResetAsync(100, 100);
                AddLogMessage(MessageDirection.INFO, "Device reset via RTS. Starting upload...");
                // --- End of Reset Logic ---

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
                if (!IsConnected) return; // Avoid multiple notifications
                IsConnected = false;
                _connectedPortName = string.Empty;
                StatusText = "Error: Device disconnected.";
                AddLogMessage(MessageDirection.INFO, "--- CONNECTION LOST ---");
            });
        }

        private void OnSerialDataReceived(string data)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Split the received data into individual lines
                var lines = data.Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    // Trim the line to handle potential leading/trailing whitespace
                    var trimmedLine = line.Trim();
                    MessageDirection direction;
                    string content;

                    // Check for log level prefixes
                    if (trimmedLine.StartsWith("[E]"))
                    {
                        direction = MessageDirection.RX_ERROR;
                        content = trimmedLine;
                    }
                    else if (trimmedLine.StartsWith("[W]"))
                    {
                        direction = MessageDirection.RX_WARN;
                        content = trimmedLine;
                    }
                    else if (trimmedLine.StartsWith("[I]"))
                    {
                        direction = MessageDirection.RX_INFO;
                        content = trimmedLine;
                    }
                    // A new prefix for Debug level, assuming "[D]"
                    else if (trimmedLine.StartsWith("[D]"))
                    {
                        direction = MessageDirection.RX_DEBUG;
                        content = trimmedLine;
                    }
                    else
                    {
                        // If no prefix matches, treat it as default received data
                        direction = MessageDirection.RX_DEFAULT;
                        content = trimmedLine;
                    }

                    AddLogMessage(direction, content);
                }
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
            // --- Real-time speed calculation logic ---
            var timeDelta = (elapsed - _lastElapsed).TotalSeconds;
            // Update speed every 250ms for a smoother, less frantic display
            if (timeDelta > 0.25)
            {
                long bytesDelta = bytesSent - _lastBytesSent;
                double speed = bytesDelta / timeDelta; // Bytes per second

                if (speed > 1024 * 1024)
                    UploadSpeed = $"{speed / (1024 * 1024):F2} MB/s";
                else if (speed > 1024)
                    UploadSpeed = $"{speed / 1024:F2} KB/s";
                else
                    UploadSpeed = $"{(int)speed} B/s";

                // Update trackers for the next calculation
                _lastBytesSent = bytesSent;
                _lastElapsed = elapsed;
            }

            // --- Remaining time calculation (based on average speed for stability) ---
            var elapsedSeconds = elapsed.TotalSeconds;
            if (elapsedSeconds > 0.1)
            {
                double averageSpeed = bytesSent / elapsedSeconds;
                double bytesLeft = totalBytes - bytesSent;
                double remainingSeconds = averageSpeed > 0 ? bytesLeft / averageSpeed : 0;
                UploadRemainingTime = $"{TimeSpan.FromSeconds(remainingSeconds):m\\:ss}";
            }
        }

        // --- Hot-plug detection logic ---
        private void InitializePortWatcher()
        {
            try
            {
                var query = new WqlEventQuery("SELECT * FROM __InstanceOperationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity' AND TargetInstance.Caption LIKE '%(COM%'");
                _portWatcher = new ManagementEventWatcher(query);
                _portWatcher.EventArrived += OnPortChanged;
                _portWatcher.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to initialize ManagementEventWatcher: {ex.Message}");
                StatusText = "Could not start port watcher.";
            }
        }

        private void OnPortChanged(object sender, EventArrivedEventArgs e)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // Check if the disconnected port was the one we were using
                if (e.NewEvent.ClassPath.ClassName == "__InstanceDeletionEvent")
                {
                    ManagementBaseObject targetInstance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
                    string caption = targetInstance["Caption"]?.ToString() ?? "";
                    if (IsConnected && !string.IsNullOrEmpty(_connectedPortName) && caption.Contains($"({_connectedPortName})"))
                    {
                        // Our connected device was just removed.
                        OnSerialConnectionLost();
                    }
                }

                // Rescan ports to update the UI list
                ScanPorts();
            });
        }

        public void Dispose()
        {
            _portWatcher?.Stop();
            _portWatcher?.Dispose();
            serialService_?.Dispose();
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
