namespace WpfSerialBootloader.Models
{
    public enum MessageDirection
    {
        TX, // Transmit (Host to Device)
        RX  // Receive (Device to Host)
    }

    /// <summary>
    /// Represents a single message displayed in the terminal.
    /// </summary>
    public class TerminalMessage
    {
        public DateTime Timestamp { get; }
        public MessageDirection Direction { get; }
        public string Content { get; }
        public string FormattedHeader => $"[{Timestamp:HH:mm:ss.fff}] {Direction} >";

        public TerminalMessage(MessageDirection direction, string content)
        {
            Timestamp = DateTime.Now;
            Direction = direction;
            Content = content;
        }
    }
}
