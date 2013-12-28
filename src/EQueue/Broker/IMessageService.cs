﻿using EQueue.Common;

namespace EQueue.Broker
{
    public interface IMessageService
    {
        MessageStoreResult StoreMessage(Message message, object arg);
        QueueMessage GetMessage(string topic, int queueId, long queueOffset);
        long GetQueueCurrentOffset(string topic, int queueId);
    }
}
