using BeamNG_LevelCleanUp.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace BeamNG_LevelCleanUp.Communication
{
    internal static class PubSubChannel
    {
        internal static Channel<PubSubMessage> ch = Channel.CreateUnbounded<PubSubMessage>();
        private static long _counter { get; set; }
        public static void SendMessage(bool isError, string message, bool modulo = false)
        {
            if (modulo)
            {
                if ((_counter % 10) == 0)
                {
                    ch.Writer.TryWrite(new PubSubMessage
                    {
                        IsError = isError,
                        Message = message,
                    });
                }
            }
            else
            {
                ch.Writer.TryWrite(new PubSubMessage
                {
                    IsError = isError,
                    Message = message,
                });
            }
            _counter++;
        }

        public static void StopChannel()
        {
            ch.Writer.Complete();
        }
    }
}
