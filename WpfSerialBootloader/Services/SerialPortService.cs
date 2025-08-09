using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WpfSerialBootloader.Services
{
    /// <summary>
    /// A wrapper service for System.IO.Ports.SerialPort to manage connection and communication.
    /// This version includes a buffer and timer to consolidate fragmented messages.
    /// It also includes methods for controlling RTS and DTR pins.
    /// </summary>
    public class SerialPortService : IDisposable
    {
        // --- New members for the buffering mechanism ---
        private const int ReceiveTimeoutMs = 10;
        private readonly StringBuilder _receiveBuffer = new();
        private readonly object _bufferLock = new();
        private Timer? _receiveTimer;
        // --- End of new members ---

        private SerialPort? serialPort_;

        public event Action<string>? DataReceived;
        public event Action? ConnectionLost;
        public bool IsOpen => serialPort_?.IsOpen ?? false;

        public static string[] GetAvailablePorts()
        {
            return SerialPort.GetPortNames();
        }

        public void Connect(string portName, int baudRate)
        {
            if (IsOpen) Disconnect();

            // Instantiate the timer that will fire when data reception has paused.
            _receiveTimer = new Timer(OnReceiveTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

            serialPort_ = new SerialPort(portName, baudRate)
            {
                Encoding = Encoding.UTF8,
                // Initialize DTR and RTS to be inactive (high) by default.
                // This prevents an immediate reset on connect for some boards.
                DtrEnable = false,
                RtsEnable = false
            };
            serialPort_.DataReceived += OnDataReceived;
            serialPort_.ErrorReceived += OnErrorReceived;

            try
            {
                serialPort_.Open();
            }
            catch (Exception)
            {
                serialPort_ = null;
                throw; // Rethrow to be caught by ViewModel
            }
        }

        public void Disconnect()
        {
            if (serialPort_ != null)
            {
                serialPort_.DataReceived -= OnDataReceived;
                serialPort_.ErrorReceived -= OnErrorReceived;
                if (serialPort_.IsOpen)
                {
                    serialPort_.Close();
                }
                serialPort_.Dispose();
                serialPort_ = null;
            }

            // Dispose the timer and clear the buffer
            _receiveTimer?.Dispose();
            _receiveTimer = null;
            lock (_bufferLock)
            {
                _receiveBuffer.Clear();
            }
        }

        private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            // This can be triggered by unplugging the device
            ConnectionLost?.Invoke();
        }

        // This method is now responsible for buffering data and resetting the timer.
        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (serialPort_ == null || !serialPort_.IsOpen) return;
            try
            {
                string data = serialPort_.ReadExisting();

                lock (_bufferLock)
                {
                    _receiveBuffer.Append(data);
                }

                // Reset the timer to fire after the timeout period.
                // If more data arrives, this line will execute again, pushing the timer back.
                _receiveTimer?.Change(ReceiveTimeoutMs, Timeout.Infinite);
            }
            catch (Exception)
            {
                // Ignore errors during read if port is closing
            }
        }

        // This new method is the callback for our timer.
        // It fires when the data stream has been quiet for ReceiveTimeoutMs.
        private void OnReceiveTimerElapsed(object? state)
        {
            string message;
            lock (_bufferLock)
            {
                if (_receiveBuffer.Length == 0) return;
                message = _receiveBuffer.ToString();
                _receiveBuffer.Clear();
            }

            // Invoke the event with the consolidated message.
            DataReceived?.Invoke(message);
        }

        public async Task WriteAsync(byte[] data)
        {
            if (serialPort_ == null || !serialPort_.IsOpen)
                throw new InvalidOperationException("Serial port is not open.");
            await serialPort_.BaseStream.WriteAsync(data.AsMemory(), CancellationToken.None);
        }

        /// <summary>
        /// Toggles the RTS pin for automatic reset before firmware upload.
        /// Low-active: RtsEnable=true means low signal.
        /// </summary>
        /// <param name="downTimeMs">Duration to keep the pin low (ms).</param>
        /// <param name="waitTimeMs">Duration to wait after releasing the pin (ms).</param>
        public async Task ToggleRtsForResetAsync(int downTimeMs, int waitTimeMs)
        {
            if (serialPort_ == null || !serialPort_.IsOpen)
                throw new InvalidOperationException("Serial port is not open.");

            // Pull RTS low
            serialPort_.RtsEnable = true;
            await Task.Delay(downTimeMs);
            // Release RTS high
            serialPort_.RtsEnable = false;
            // Wait for the device to be ready
            await Task.Delay(waitTimeMs);
        }

        /// <summary>
        /// Pulses the DTR pin to reset the target program.
        /// Low-active: DtrEnable=true means low signal.
        /// </summary>
        /// <param name="pulseDurationMs">Duration to keep the pin low (ms).</param>
        public async Task PulseDtrAsync(int pulseDurationMs)
        {
            if (serialPort_ == null || !serialPort_.IsOpen)
                throw new InvalidOperationException("Serial port is not open.");

            // Pull DTR low
            serialPort_.DtrEnable = true;
            await Task.Delay(pulseDurationMs);
            // Release DTR high
            serialPort_.DtrEnable = false;
        }

        public void Dispose()
        {
            Disconnect();
            GC.SuppressFinalize(this);
        }
    }
}
