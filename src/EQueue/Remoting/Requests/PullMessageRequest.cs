﻿using EQueue.Protocols;

namespace EQueue.Remoting.Requests
{
    public class PullMessageRequest
    {
        public string ConsumerGroup { get; set; }
        public MessageQueue MessageQueue { get; set; }
        public long QueueOffset { get; set; }
        public int PullMessageBatchSize { get; set; }
    }
}
