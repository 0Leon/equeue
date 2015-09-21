﻿using System;
using System.Collections.Generic;
using ECommon.Components;
using ECommon.Logging;
using EQueue.Broker.Storage;
using EQueue.Protocols;

namespace EQueue.Broker
{
    public class MessageService : IMessageService
    {
        private readonly IQueueService _queueService;
        private readonly IMessageStore _messageStore;
        private readonly IOffsetManager _offsetManager;
        private readonly ILogger _logger;
        private readonly object _syncObj = new object();
        private long _totalRecoveredQueueIndex;

        public MessageService(IQueueService queueService, IMessageStore messageStore, IOffsetManager offsetManager)
        {
            _queueService = queueService;
            _messageStore = messageStore;
            _offsetManager = offsetManager;
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);
        }

        public void Start()
        {
            _totalRecoveredQueueIndex = 0;
            _messageStore.Start();
        }
        public void Shutdown()
        {
            _messageStore.Shutdown();
        }
        public MessageStoreResult StoreMessage(Message message, int queueId, string routingKey)
        {
            var queue = _queueService.GetQueue(message.Topic, queueId);
            if (queue == null)
            {
                throw new Exception(string.Format("Queue not exist, topic: {0}, queueId: {1}", message.Topic, queueId));
            }

            //消息写文件需要加锁
            lock (_syncObj)
            {
                var queueOffset = queue.CurrentOffset;
                var messageLogPosition = _messageStore.StoreMessage(queueId, queueOffset, message, routingKey);
                queue.AddMessage(messageLogPosition);
                queue.IncrementCurrentOffset();
                var messageId = CreateMessageId(messageLogPosition);
                return new MessageStoreResult(message.Key, messageId, message.Code, message.Topic, queueId, queueOffset);
            }
        }
        public IEnumerable<MessageLogRecord> GetMessages(string topic, int queueId, long queueOffset, int batchSize)
        {
            var queue = _queueService.GetQueue(topic, queueId);
            if (queue == null)
            {
                return new MessageLogRecord[0];
            }

            var messages = new List<MessageLogRecord>();
            var currentQueueOffset = queueOffset;
            while (currentQueueOffset <= queue.CurrentOffset && messages.Count < batchSize)
            {
                var messageOffset = queue.GetMessageOffset(currentQueueOffset);
                if (messageOffset < 0)
                {
                    break;
                }
                if (messageOffset >= 0)
                {
                    var message = _messageStore.GetMessage(messageOffset);
                    if (message != null)
                    {
                        messages.Add(message);
                    }
                    else
                    {
                        queue.RemoveQueueOffset(currentQueueOffset);
                    }
                }
                currentQueueOffset++;
            }
            return messages;
        }

        private static string CreateMessageId(long messageLogPosition)
        {
            //TODO，还要结合当前的Broker的IP作为MessageId的一部分
            return messageLogPosition.ToString();
        }
        private void BatchLoadQueueIndexToMemory(Queue queue, long startQueueOffset)
        {
            if (_messageStore.SupportBatchLoadQueueIndex)
            {
                var indexDict = _messageStore.BatchLoadQueueIndex(queue.Topic, queue.QueueId, startQueueOffset);
                foreach (var entry in indexDict)
                {
                    queue.SetQueueIndex(entry.Key, entry.Value);
                }
            }
        }
        private void ProcessRecoveredMessage(long messageOffset, string topic, int queueId, long queueOffset)
        {
            var queue = _queueService.GetQueue(topic, queueId);
            if (queue == null)
            {
                _logger.ErrorFormat("Queue not found when recovering message. messageOffset: {0}, topic: {1}, queueId: {2}, queueOffset: {3}", messageOffset, topic, queueId, queueOffset);
                return;
            }
            var allowSetQueueIndex = !_messageStore.SupportBatchLoadQueueIndex || _totalRecoveredQueueIndex < BrokerController.Instance.Setting.QueueIndexMaxCacheSize;
            queue.RecoverQueueIndex(queueOffset, messageOffset, allowSetQueueIndex);
            _totalRecoveredQueueIndex++;
        }
    }
}
