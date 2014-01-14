﻿using System.Collections.Generic;
using EQueue.Protocols;

namespace EQueue.Broker
{
    public interface IMessageService
    {
        MessageStoreResult StoreMessage(Message message, string arg);
        IEnumerable<QueueMessage> GetMessages(string topic, int queueId, long queueOffset, int batchSize);
        long GetQueueCurrentOffset(string topic, int queueId);
        int GetTopicQueueCount(string topic);
    }
}
