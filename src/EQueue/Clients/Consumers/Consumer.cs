﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EQueue.Common;
using EQueue.Common.Logging;

namespace EQueue.Clients.Consumers
{
    public class Consumer : IConsumer
    {
        private readonly ConcurrentDictionary<MessageQueue, ProcessQueue> _processQueueDict = new ConcurrentDictionary<MessageQueue, ProcessQueue>();
        private readonly ConcurrentDictionary<string, IList<MessageQueue>> _topicSubscribeInfoDict = new ConcurrentDictionary<string, IList<MessageQueue>>();
        private readonly List<string> _subscriptionTopics = new List<string>();
        private readonly Client _client;
        private readonly IAllocateMessageQueueStrategy _allocateMessageQueueStragegy;
        private readonly IOffsetStore _offsetStore;
        private readonly IMessageHandler _messageHandler;
        private readonly ILogger _logger;

        public string GroupName { get; private set; }
        public MessageModel MessageModel { get; private set; }

        public IEnumerable<string> SubscriptionTopics
        {
            get { return _subscriptionTopics; }
        }

        public Consumer(
            Client client,
            string groupName,
            MessageModel messageModel,
            IMessageHandler messageHandler,
            IOffsetStore offsetStore,
            IAllocateMessageQueueStrategy allocateMessageQueueStrategy,
            ILoggerFactory loggerFactory)
        {
            _client = client;
            GroupName = groupName;
            MessageModel = messageModel;
            _messageHandler = messageHandler;
            _offsetStore = offsetStore;
            _allocateMessageQueueStragegy = allocateMessageQueueStrategy;
            _logger = loggerFactory.Create(GetType().Name);
        }

        public void Start()
        {
            _logger.Info("consumer started...");
        }
        public void Subscribe(string topic)
        {
            if (!_subscriptionTopics.Contains(topic))
            {
                _subscriptionTopics.Add(topic);
            }
        }
        public void Shutdown()
        {
            //TODO
            _logger.Info("consumer shutdown...");
        }
        public void PullMessage(PullRequest pullRequest)
        {
            StartPullMessageTask(pullRequest).ContinueWith((task) => ProcessPullResult(pullRequest, task.Result));
        }
        public void UpdateTopicSubscribeInfo(string topic, IEnumerable<MessageQueue> messageQueues)
        {
            _topicSubscribeInfoDict[topic] = messageQueues.ToList();
        }
        public bool IsSubscribeTopicNeedUpdate(string topic)
        {
            return !_subscriptionTopics.Any(x => x == topic);
        }
        public void DoRebalance()
        {
            if (MessageModel == MessageModel.BROADCASTING)
            {
                foreach (var topic in _subscriptionTopics)
                {
                    try
                    {
                        RebalanceBroadCasting(topic);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("RebalanceBroadCasting has exception", ex);
                    }
                }
            }
            else if (MessageModel == MessageModel.CLUSTERING)
            {
                foreach (var topic in _subscriptionTopics)
                {
                    try
                    {
                        RebalanceClustering(topic);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("RebalanceClustering has exception", ex);
                    }
                }
            }

            TruncateMessageQueueNotMyTopic();
        }
        public void PersistOffset()
        {
            foreach (var messageQueue in _processQueueDict.Keys)
            {
                try
                {
                    _offsetStore.Persist(messageQueue);
                }
                catch (Exception ex)
                {
                    _logger.Error("PersistOffset exception.", ex);
                }
            }
        }

        private Task<PullResult> StartPullMessageTask(PullRequest pullRequest)
        {
            //TODO
            return null;
        }
        private void ProcessPullResult(PullRequest pullRequest, PullResult pullResult)
        {
            pullRequest.NextOffset = pullResult.NextBeginOffset;
            pullRequest.ProcessQueue.AddMessages(pullResult.Messages);
            StartConsumeTask(pullRequest, pullResult);
            _client.EnqueuePullRequest(pullRequest);
        }
        private void StartConsumeTask(PullRequest pullRequest, PullResult pullResult)
        {
            Task.Factory.StartNew(() =>
            {
                foreach (var message in pullResult.Messages)
                {
                    try
                    {
                        _messageHandler.Handle(message);
                    }
                    catch { }  //TODO,处理失败的消息放到本地队列继续重试消费
                }
                long offset = pullRequest.ProcessQueue.RemoveMessages(pullResult.Messages);
                if (offset >= 0)
                {
                    _offsetStore.UpdateOffset(pullRequest.MessageQueue, offset);
                }
            });
        }
        private void RebalanceBroadCasting(string topic)
        {
            if (_topicSubscribeInfoDict.ContainsKey(topic))
            {
                var messageQueues = _topicSubscribeInfoDict[topic];
                var changed = UpdateProcessQueueDict(topic, messageQueues);
                if (changed)
                {
                    _logger.InfoFormat("messageQueueChanged [consumerGroup:{0}, topic:{1}, allocatedMessageQueues:{2}]", GroupName, topic, string.Join("|", messageQueues));
                }
            }
            else
            {
                _logger.WarnFormat("DoRebalance of broad casting, consumerGroup: {0}, but the topic[{1}] not exist.", GroupName, topic);
            }
        }
        private void RebalanceClustering(string topic)
        {
            if (_topicSubscribeInfoDict.ContainsKey(topic))
            {
                var messageQueues = _topicSubscribeInfoDict[topic];
                var consumerIds = _client.FindConsumerIdList(GroupName);

                var messageQueueList = messageQueues.ToList();
                var consumerIdList = consumerIds.ToList();
                messageQueueList.Sort();
                consumerIdList.Sort();

                IEnumerable<MessageQueue> allocatedMessageQueues = new List<MessageQueue>();
                try
                {
                    allocatedMessageQueues = _allocateMessageQueueStragegy.Allocate(_client.ClientId, messageQueueList, consumerIdList);
                }
                catch (Exception ex)
                {
                    _logger.Error("Allocate message queue has exception.", ex);
                }

                var allocatedMessageQueueList = allocatedMessageQueues.ToList();
                var changed = UpdateProcessQueueDict(topic, allocatedMessageQueueList);
                if (changed)
                {
                    _logger.InfoFormat("messageQueueChanged [consumerGroup:{0}, topic:{1}, allocatedMessageQueues:{2}, consumerIds:{3}]", GroupName, topic, string.Join("|", allocatedMessageQueueList), string.Join("|", consumerIdList));
                }
            }
            else
            {
                _logger.WarnFormat("DoRebalance of clustering, consumerGroup: {0}, but the topic[{1}] not exist.", GroupName, topic);
            }
        }
        private bool UpdateProcessQueueDict(string topic, IList<MessageQueue> messageQueues)
        {
            var changed = false;

            foreach (var messageQueue in _processQueueDict.Keys)
            {
                if (messageQueue.Topic == topic)
                {
                    if (!messageQueues.Contains(messageQueue))
                    {
                        changed = true;
                        ProcessQueue processQueue;
                        _processQueueDict.TryRemove(messageQueue, out processQueue);
                        RemoveUnnecessaryMessageQueue(messageQueue, processQueue);
                    }
                }
            }

            var pullRequestList = new List<PullRequest>();
            foreach (var messageQueue in messageQueues)
            {
                if (!_processQueueDict.ContainsKey(messageQueue))
                {
                    var pullRequest = new PullRequest();
                    pullRequest.ConsumerGroup = GroupName;
                    pullRequest.MessageQueue = messageQueue;
                    pullRequest.ProcessQueue = new ProcessQueue();

                    long nextOffset = ComputePullFromWhere(messageQueue);
                    if (nextOffset >= 0)
                    {
                        changed = true;
                        pullRequest.NextOffset = nextOffset;
                        pullRequestList.Add(pullRequest);
                        _processQueueDict.TryAdd(messageQueue, pullRequest.ProcessQueue);
                        _logger.InfoFormat("DoRebalance, ConsumerGroup: {0}, Add a new messageQueue, {1}", GroupName, messageQueue);
                    }
                    else
                    {
                        _logger.WarnFormat("DoRebalance, ConsumerGroup: {0}, The new messageQueue {1} cannot be added as the nextOffset is < 0.", GroupName, messageQueue);
                    }
                }
            }

            DispatchPullRequest(pullRequestList);

            return changed;
        }
        private void TruncateMessageQueueNotMyTopic()
        {
            var shouldRemoveQueues = new List<MessageQueue>();
            foreach (var messageQueue in _processQueueDict.Keys)
            {
                if (!_subscriptionTopics.Contains(messageQueue.Topic))
                {
                    shouldRemoveQueues.Add(messageQueue);
                }
            }
            foreach (var queue in shouldRemoveQueues)
            {
                ProcessQueue removedQueue;
                _processQueueDict.TryRemove(queue, out removedQueue);
            }
        }
        private void RemoveUnnecessaryMessageQueue(MessageQueue messageQueue, ProcessQueue processQueue)
        {
            _offsetStore.Persist(messageQueue);
            _offsetStore.RemoveOffset(messageQueue);
        }
        private void DispatchPullRequest(IEnumerable<PullRequest> pullRequestList)
        {
            foreach (var pullRequest in pullRequestList)
            {
                _client.EnqueuePullRequest(pullRequest);
                _logger.InfoFormat("doRebalance, consumerGroup:{0}, add a new pull request {1}", GroupName, pullRequest);
            }
        }
        private long ComputePullFromWhere(MessageQueue messageQueue)
        {
            var offset = -1L;

            var lastOffset = _offsetStore.ReadOffset(messageQueue, OffsetReadType.ReadFromStore);
            if (lastOffset >= 0)
            {
                offset = lastOffset;
            }
            else if (lastOffset == -1)
            {
                offset = long.MaxValue;
            }

            return offset;
        }
    }
}
