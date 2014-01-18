﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EQueue.Infrastructure;
using EQueue.Infrastructure.Extensions;
using EQueue.Infrastructure.IoC;
using EQueue.Infrastructure.Logging;
using EQueue.Infrastructure.Scheduling;
using EQueue.Protocols;
using EQueue.Remoting;
using EQueue.Remoting.Requests;
using EQueue.Remoting.Responses;

namespace EQueue.Clients.Consumers
{
    public class PullRequest
    {
        private readonly SocketRemotingClient _remotingClient;
        private readonly Worker _pullMessageorker;
        private readonly Worker _handleMessageWorker;
        private readonly ILogger _logger;
        private readonly IBinarySerializer _binarySerializer;
        private readonly BlockingCollection<WrappedMessage> _messageQueue;
        private readonly MessageHandleMode _messageHandleMode;
        private readonly IMessageHandler _messageHandler;
        private readonly PullRequestSetting _setting;
        private long flowControlTimes1;
        private long flowControlTimes2;

        public string ConsumerId { get; private set; }
        public string GroupName { get; private set; }
        public MessageQueue MessageQueue { get; private set; }
        public ProcessQueue ProcessQueue { get; private set; }
        public long NextOffset { get; set; }

        #region Constructors

        public PullRequest(
            string consumerId,
            string groupName,
            MessageQueue messageQueue,
            SocketRemotingClient remotingClient,
            MessageHandleMode messageHandleMode,
            IMessageHandler messageHandler,
            PullRequestSetting setting)
        {
            ConsumerId = consumerId;
            GroupName = groupName;
            MessageQueue = messageQueue;
            ProcessQueue = new ProcessQueue();

            _remotingClient = remotingClient;
            _setting = setting;
            _messageHandleMode = messageHandleMode;
            _messageHandler = messageHandler;
            _messageQueue = new BlockingCollection<WrappedMessage>(new ConcurrentQueue<WrappedMessage>());
            _pullMessageorker = new Worker(() =>
            {
                try
                {
                    PullMessage();
                }
                catch (Exception ex)
                {
                    _logger.Error(string.Format("[{0}]: PullMessage has exception. PullRequest: {1}.", ConsumerId, this), ex);
                }
            });
            _handleMessageWorker = new Worker(HandleMessage);
            _binarySerializer = ObjectContainer.Resolve<IBinarySerializer>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().Name);
        }

        #endregion

        public void Start()
        {
            _pullMessageorker.Start();
            _handleMessageWorker.Start();
        }
        public void Stop()
        {
            _pullMessageorker.Stop();
            _handleMessageWorker.Stop();
        }

        public override string ToString()
        {
            return string.Format("[ConsumerId={0}, GroupName={0}, MessageQueue={1}, NextOffset={2}]", GroupName, MessageQueue, NextOffset);
        }

        private void PullMessage()
        {
            var messageCount = ProcessQueue.GetMessageCount();
            var messageSpan = ProcessQueue.GetMessageSpan();

            if (messageCount >= _setting.PullThresholdForQueue)
            {
                Thread.Sleep(_setting.PullTimeDelayMillsWhenFlowControl);
                if ((flowControlTimes1++ % 3000) == 0)
                {
                    _logger.WarnFormat("[{0}]: the consumer message buffer is full, so do flow control, [messageCount={1},pullRequest={2},flowControlTimes={3}]", ConsumerId, messageCount, this, flowControlTimes1);
                }
            }
            else if (messageSpan >= _setting.ConsumeMaxSpan)
            {
                Thread.Sleep(_setting.PullTimeDelayMillsWhenFlowControl);
                if ((flowControlTimes2++ % 3000) == 0)
                {
                    _logger.WarnFormat("[{0}]: the consumer message span too long, so do flow control, [messageSpan={1},pullRequest={2},flowControlTimes={3}]", ConsumerId, messageSpan, this, flowControlTimes2);
                }
            }

            var request = new PullMessageRequest
            {
                ConsumerGroup = GroupName,
                MessageQueue = MessageQueue,
                QueueOffset = NextOffset,
                PullMessageBatchSize = _setting.PullMessageBatchSize
            };
            var data = _binarySerializer.Serialize(request);
            var remotingRequest = new RemotingRequest((int)RequestCode.PullMessage, data);
            var remotingResponse = _remotingClient.InvokeSync(remotingRequest, _setting.PullRequestTimeoutMilliseconds);
            var response = _binarySerializer.Deserialize<PullMessageResponse>(remotingResponse.Body);

            if (remotingResponse.Code == (int)PullStatus.Found && response.Messages.Count() > 0)
            {
                NextOffset += response.Messages.Count();
                ProcessQueue.AddMessages(response.Messages);
                response.Messages.ForEach(x => _messageQueue.Add(new WrappedMessage(x, MessageQueue, ProcessQueue)));
            }
        }
        private void HandleMessage()
        {
            var wrappedMessage = _messageQueue.Take();
            Action handleAction = () =>
            {
                try
                {
                    _messageHandler.Handle(wrappedMessage.QueueMessage);
                }
                catch { }  //TODO,处理失败的消息放到本地队列继续重试消费
                var offset = wrappedMessage.ProcessQueue.RemoveMessage(wrappedMessage.QueueMessage);
                if (offset >= 0)
                {
                    //TODO
                    //_offsetStore.UpdateOffset(wrappedMessage.MessageQueue, offset);
                }
            };
            if (_messageHandleMode == MessageHandleMode.Sequential)
            {
                handleAction();
            }
            else if (_messageHandleMode == MessageHandleMode.Parallel)
            {
                Task.Factory.StartNew(handleAction);
            }
        }
    }
}
