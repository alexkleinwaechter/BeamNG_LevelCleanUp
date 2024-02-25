using BeamNG_LevelCleanUp.Objects;
using System.Threading.Channels;

namespace BeamNG_LevelCleanUp.Communication
{
    internal static class PubSubChannel
    {
        internal static Channel<PubSubMessage> ch = Channel.CreateUnbounded<PubSubMessage>();
        private static long _counter { get; set; }
        public static void SendMessage(PubSubMessageType messageType, string message, bool modulo = false)
        {
            if (modulo)
            {
                if ((_counter % 10) == 0)
                {
                    ch.Writer.TryWrite(new PubSubMessage
                    {
                        MessageType = messageType,
                        Message = message,
                    });
                }
            }
            else
            {
                for (int i = 0; i < 5; i++)
                {
                    var success = ch.Writer.TryWrite(new PubSubMessage
                    {
                        MessageType = messageType,
                        Message = message,
                    });
                    if (success) break;
                }
            }
            _counter++;
        }

        public static void StopChannel()
        {
            ch.Writer.Complete();
        }
    }
}
