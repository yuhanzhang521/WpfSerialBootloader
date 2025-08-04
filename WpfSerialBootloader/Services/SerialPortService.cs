using System.IO.Ports;

namespace WpfSerialBootloader.Services
{
    /// <summary>
    /// A wrapper service for System.IO.Ports.SerialPort to manage connection and communication.
    /// </summary>
    public class SerialPortService : IDisposable
    {
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

            serialPort_ = new SerialPort(portName, baudRate);
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
        }

        private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            // This can indicate the device was disconnected.
            ConnectionLost?.Invoke();
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (serialPort_ == null || !serialPort_.IsOpen) return;
            try
            {
                string data = serialPort_.ReadExisting();
                DataReceived?.Invoke(data);
            }
            catch (Exception)
            {
                // Ignore errors during read if port is closing
            }
        }

        public async Task WriteAsync(byte[] data)
        {
            if (serialPort_ == null || !serialPort_.IsOpen)
                throw new InvalidOperationException("Serial port is not open.");
            await serialPort_.BaseStream.WriteAsync(data.AsMemory(), CancellationToken.None);
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
