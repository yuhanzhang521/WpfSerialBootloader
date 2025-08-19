using System.IO.Ports;
using System.Text;

namespace WpfSerialBootloader.Services
{
    /// <summary>
    /// A wrapper service for System.IO.Ports.SerialPort to manage connection and communication.
    /// This version intelligently splits messages based on new log prefixes or newlines
    /// to handle interleaved/interrupted log messages correctly.
    /// </summary>
    public class SerialPortService : IDisposable
    {
        private const int ReceiveTimeoutMs = 20;
        private readonly StringBuilder receiveBuffer_ = new();
        private readonly object bufferLock_ = new();
        private Timer? receiveTimer_;

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

            receiveTimer_ = new Timer(OnReceiveTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

            serialPort_ = new SerialPort(portName, baudRate)
            {
                Encoding = Encoding.UTF8,
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
                throw;
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

            receiveTimer_?.Dispose();
            receiveTimer_ = null;
            lock (bufferLock_)
            {
                receiveBuffer_.Clear();
            }
        }

        private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            ConnectionLost?.Invoke();
        }

        /// <summary>
        /// Processes incoming data, splitting messages by newline or by interrupting log prefixes.
        /// </summary>
        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (serialPort_ == null || !serialPort_.IsOpen) return;
            try
            {
                string newData = serialPort_.ReadExisting();
                string allMessagesToProcess = string.Empty;

                Console.Write(newData);

                lock (bufferLock_)
                {
                    receiveBuffer_.Append(newData);
                    // Stop any pending timer since we're about to process now.
                    receiveTimer_?.Change(Timeout.Infinite, Timeout.Infinite);

                    string[] prefixes = { "[D]", "[I]", "[W]", "[E]" };

                    while (receiveBuffer_.Length > 0)
                    {
                        string currentBuffer = receiveBuffer_.ToString();
                        int splitEnd = -1;

                        // Find the first newline.
                        int newlineIndex = currentBuffer.IndexOf('\n');

                        // Find the first occurrence of a log prefix, but NOT at the very beginning (index 0).
                        // This identifies an *interrupting* prefix.
                        int prefixIndex = prefixes
                            .Select(p => currentBuffer.IndexOf(p, 1))
                            .Where(i => i != -1)
                            .DefaultIfEmpty(-1)
                            .Min();
                        if (prefixIndex == -1) prefixIndex = -1; // Correctly handle case where no prefix is found.

                        // --- Decision Logic ---
                        if (prefixIndex != -1 && (newlineIndex == -1 || prefixIndex < newlineIndex))
                        {
                            // Case 1: An interrupting prefix is found before any newline.
                            // The message is the text *before* the prefix.
                            splitEnd = prefixIndex;
                        }
                        else if (newlineIndex != -1)
                        {
                            // Case 2: A newline is found, and it's the first significant terminator.
                            // The message is the line *including* the newline.
                            splitEnd = newlineIndex + 1;
                        }
                        else
                        {
                            // Case 3: No terminators found. We need more data.
                            break;
                        }

                        // Queue the determined message for processing and remove it from the buffer.
                        allMessagesToProcess += currentBuffer.Substring(0, splitEnd);
                        receiveBuffer_.Remove(0, splitEnd);
                    }

                    // If there's anything left in the buffer, it's an incomplete fragment.
                    // Start the timer to flush it if nothing else arrives.
                    if (receiveBuffer_.Length > 0)
                    {
                        receiveTimer_?.Change(ReceiveTimeoutMs, Timeout.Infinite);
                    }
                } // End lock

                // Process all extracted messages outside the lock.
                if (!string.IsNullOrEmpty(allMessagesToProcess))
                {
                    DataReceived?.Invoke(allMessagesToProcess);
                }
            }
            catch (Exception)
            {
                // Ignore read errors.
            }
        }

        /// <summary>
        /// Fallback to flush the buffer if data sits for too long without a terminator.
        /// </summary>
        private void OnReceiveTimerElapsed(object? state)
        {
            string message;
            lock (bufferLock_)
            {
                if (receiveBuffer_.Length == 0) return;
                message = receiveBuffer_.ToString();
                receiveBuffer_.Clear();
            }
            DataReceived?.Invoke(message);
        }

        public async Task WriteAsync(byte[] data)
        {
            if (serialPort_ == null || !serialPort_.IsOpen)
                throw new InvalidOperationException("Serial port is not open.");
            await serialPort_.BaseStream.WriteAsync(data.AsMemory(), CancellationToken.None);
        }

        public async Task ToggleRtsForResetAsync(int downTimeMs, int waitTimeMs)
        {
            if (serialPort_ == null || !serialPort_.IsOpen)
                throw new InvalidOperationException("Serial port is not open.");

            serialPort_.RtsEnable = true;
            await Task.Delay(downTimeMs);
            serialPort_.RtsEnable = false;
            await Task.Delay(waitTimeMs);
        }

        public async Task PulseDtrAsync(int pulseDurationMs)
        {
            if (serialPort_ == null || !serialPort_.IsOpen)
                throw new InvalidOperationException("Serial port is not open.");

            serialPort_.DtrEnable = true;
            await Task.Delay(pulseDurationMs);
            serialPort_.DtrEnable = false;
        }

        public void Dispose()
        {
            Disconnect();
            GC.SuppressFinalize(this);
        }
    }
}