namespace WpfSerialBootloader.Models
{
    public enum MessageDirection
    {
        TX,     // Transmit (Host to Device)
        INFO,   // Informational message from the application itself

        // log levels for received data
        RX_DEFAULT, // Default for non-parsed incoming data
        RX_DEBUG,   // [D] Debug level
        RX_INFO,    // [I] Info level
        RX_WARN,    // [W] Warning level
        RX_ERROR    // [E] Error level
    }

    /// <summary>
    /// Represents a single message displayed in the terminal.
    /// </summary>
    public class TerminalMessage(MessageDirection direction, string content)
    {
        public DateTime Timestamp { get; } = DateTime.Now;
        public MessageDirection Direction { get; } = direction;
        public string Content { get; } = content;
        public string FormattedHeader => $"[{Timestamp:HH:mm:ss.fff}] {Direction.ToString().Replace("RX_", "")} >";
    }
}
