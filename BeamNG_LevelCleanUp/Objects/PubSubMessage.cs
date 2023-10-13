﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
