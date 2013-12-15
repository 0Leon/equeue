﻿using System;
using EQueue.Common;

namespace EQueue.Clients.Producers
{
    public class SendResult
    {
        public SendStatus SendStatus { get; set; }
        public Guid MessageId { get; set; }
        public MessageQueue MessageQueue { get; set; }
        public long QueueOffset { get; set; }
    }
}
