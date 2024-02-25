namespace BeamNG_LevelCleanUp.Objects
{
    internal enum PubSubMessageType
    {
        Info,
        Error,
        Warning
    }

    internal class PubSubMessage
    {
        internal PubSubMessageType MessageType { get; set; }
        internal string Message { get; set; }
    }
}
