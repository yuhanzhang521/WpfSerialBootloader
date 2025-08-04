namespace WpfSerialBootloader.Models
{
    public enum MessageDirection
    {
        TX, // Transmit (Host to Device)
        RX, // Receive (Device to Host)
        INFO // Informational message
    }

    /// <summary>
    /// Represents a single message displayed in the terminal.
    /// </summary>
    public class TerminalMessage(MessageDirection direction, string content)
    {
        public DateTime Timestamp { get; } = DateTime.Now;
        public MessageDirection Direction { get; } = direction;
        public string Content { get; } = content;
        public string FormattedHeader => $"[{Timestamp:HH:mm:ss.fff}] {Direction} >";
    }
}
