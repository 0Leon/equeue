﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Remoting;
using ECommon.Serializing;
using EQueue.Broker.Client;
using EQueue.Broker.LongPolling;
using EQueue.Broker.Storage;
using EQueue.Protocols;
using EQueue.Utils;

namespace EQueue.Broker.RequestHandlers
{
    public class PullMessageRequestHandler : IRequestHandler
    {
        private ConsumerManager _consumerManager;
        private SuspendedPullRequestManager _suspendedPullRequestManager;
        private IMessageStore _messageStore;
        private IQueueStore _queueStore;
        private IConsumeOffsetStore _offsetStore;
        private IBinarySerializer _binarySerializer;
        private ILogger _logger;
        private readonly byte[] EmptyResponseData;

        public PullMessageRequestHandler()
        {
            _consumerManager = ObjectContainer.Resolve<ConsumerManager>();
            _suspendedPullRequestManager = ObjectContainer.Resolve<SuspendedPullRequestManager>();
            _messageStore = ObjectContainer.Resolve<IMessageStore>();
            _queueStore = ObjectContainer.Resolve<IQueueStore>();
            _offsetStore = ObjectContainer.Resolve<IConsumeOffsetStore>();
            _binarySerializer = ObjectContainer.Resolve<IBinarySerializer>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().FullName);
            EmptyResponseData = new byte[0];
        }

        public RemotingResponse HandleRequest(IRequestHandlerContext context, RemotingRequest remotingRequest)
        {
            var request = DeserializePullMessageRequest(remotingRequest.Body);
            var topic = request.MessageQueue.Topic;
            var queueId = request.MessageQueue.QueueId;
            var pullOffset = request.QueueOffset;

            //如果消费者第一次过来拉取消息，则计算下一个应该拉取的位置，并返回给消费者
            if (pullOffset < 0)
            {
                var nextConsumeOffset = GetNextConsumeOffset(topic, queueId, request.ConsumerGroup, request.ConsumeFromWhere);
                return BuildNextOffsetResetResponse(remotingRequest, nextConsumeOffset);
            }

            //尝试拉取消息
            var pullResult = PullMessages(topic, queueId, pullOffset, request.PullMessageBatchSize);

            //处理消息拉取结果
            if (pullResult.Status == PullStatus.Found)
            {
                return BuildFoundResponse(remotingRequest, pullResult.Messages);
            }
            else if (pullResult.Status == PullStatus.NextOffsetReset)
            {
                return BuildNextOffsetResetResponse(remotingRequest, pullResult.NextBeginOffset);
            }
            else if (pullResult.Status == PullStatus.QueueNotExist)
            {
                return BuildQueueNotExistResponse(remotingRequest);
            }
            else if (pullResult.Status == PullStatus.NoNewMessage)
            {
                if (request.SuspendPullRequestMilliseconds > 0)
                {
                    var pullRequest = new PullRequest(
                        remotingRequest,
                        request,
                        context,
                        DateTime.Now,
                        request.SuspendPullRequestMilliseconds,
                        ExecutePullRequest,
                        ExecutePullRequest,
                        ExecuteReplacedPullRequest);
                    _suspendedPullRequestManager.SuspendPullRequest(pullRequest);
                    return null;
                }
                return BuildNoNewMessageResponse(remotingRequest);
            }
            else
            {
                throw new Exception("Invalid pull result status.");
            }
        }

        private PullMessageResult PullMessages(string topic, int queueId, long pullOffset, int maxPullSize)
        {
            //先找到队列
            var queue = _queueStore.GetQueue(topic, queueId);
            if (queue == null)
            {
                return new PullMessageResult
                {
                    Status = PullStatus.QueueNotExist
                };
            }

            //尝试拉取消息
            var queueCurrentOffset = _queueStore.GetQueueCurrentOffset(topic, queueId);
            var messages = new List<byte[]>();
            var queueOffset = pullOffset;

            while (queueOffset <= queueCurrentOffset && messages.Count < maxPullSize)
            {
                try
                {
                    var messagePosition = queue.GetMessagePosition(queueOffset);
                    if (messagePosition < 0)
                    {
                        break;
                    }

                    var message = _messageStore.GetMessage(messagePosition);
                    if (message == null)
                    {
                        break;
                    }
                    messages.Add(message);

                    queueOffset++;
                }
                catch (ChunkNotExistException)
                {
                    _logger.ErrorFormat("Chunk not exist, topic: {0}, queueId: {1}, queueOffset: {2}", topic, queueId, queueOffset);
                }
                catch (ChunkReadException ex)
                {
                    _logger.ErrorFormat("Chunk read failed, topic: {0}, queueId: {1}, queueOffset: {2}, errorMsg: {3}", topic, queueId, queueOffset, ex.Message);
                    throw;
                }
            }

            //如果拉取到至少一个消息，则直接返回即可；
            if (messages.Count > 0)
            {
                return new PullMessageResult
                {
                    Status = PullStatus.Found,
                    Messages = messages
                };
            }

            //如果没有拉取到消息，则继续判断pullOffset的位置是否合法，分析没有拉取到消息的原因

            //pullOffset太小
            var queueMinOffset = _queueStore.GetQueueMinOffset(topic, queueId);
            if (pullOffset < queueMinOffset)
            {
                return new PullMessageResult
                {
                    Status = PullStatus.NextOffsetReset,
                    NextBeginOffset = queueMinOffset
                };
            }
            //pullOffset太大
            else if (pullOffset > queueCurrentOffset + 1)
            {
                return new PullMessageResult
                {
                    Status = PullStatus.NextOffsetReset,
                    NextBeginOffset = queueCurrentOffset + 1
                };
            }
            //如果正好等于queueMaxOffset+1，属于正常情况，表示当前队列没有新消息，告诉客户端没有新消息即可
            else if (pullOffset == queueCurrentOffset + 1)
            {
                return new PullMessageResult
                {
                    Status = PullStatus.NoNewMessage
                };
            }
            //到这里，说明当前的pullOffset在队列的正确范围，即：>=queueMinOffset and <= queueCurrentOffset；
            //但如果还是没有拉取到消息，则可能的情况有：
            //1）要拉取的消息还未来得及刷盘；
            //2）要拉取的消息的文件或消息索引的文件被删除了；
            //不管是哪种情况，告诉Consumer没有新消息即可。如果是情况1，则也许下次PullRequest就会拉取到消息；如果是情况2，则下次PullRequest就会被判断出pullOffset过小
            else
            {
                return new PullMessageResult
                {
                    Status = PullStatus.NoNewMessage
                };
            }
        }
        private void ExecutePullRequest(PullRequest pullRequest)
        {
            if (!IsPullRequestValid(pullRequest))
            {
                return;
            }

            var pullMessageRequest = pullRequest.PullMessageRequest;
            var topic = pullMessageRequest.MessageQueue.Topic;
            var queueId = pullMessageRequest.MessageQueue.QueueId;
            var pullOffset = pullMessageRequest.QueueOffset;
            var pullResult = PullMessages(topic, queueId, pullOffset, pullMessageRequest.PullMessageBatchSize);
            var remotingRequest = pullRequest.RemotingRequest;

            if (pullResult.Status == PullStatus.Found)
            {
                SendRemotingResponse(pullRequest, BuildFoundResponse(pullRequest.RemotingRequest, pullResult.Messages));
            }
            else if (pullResult.Status == PullStatus.NextOffsetReset)
            {
                SendRemotingResponse(pullRequest, BuildNextOffsetResetResponse(remotingRequest, pullResult.NextBeginOffset));
            }
            else if (pullResult.Status == PullStatus.QueueNotExist)
            {
                SendRemotingResponse(pullRequest, BuildQueueNotExistResponse(remotingRequest));
            }
            else if (pullResult.Status == PullStatus.NoNewMessage)
            {
                SendRemotingResponse(pullRequest, BuildNoNewMessageResponse(pullRequest.RemotingRequest));
            }
        }
        private void ExecuteReplacedPullRequest(PullRequest pullRequest)
        {
            if (!IsPullRequestValid(pullRequest))
            {
                return;
            }
            SendRemotingResponse(pullRequest, BuildIgnoredResponse(pullRequest.RemotingRequest));
        }
        private bool IsPullRequestValid(PullRequest pullRequest)
        {
            try
            {
                return _consumerManager.IsConsumerActive(pullRequest.PullMessageRequest.ConsumerGroup, pullRequest.PullMessageRequest.ConsumerId);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
        private RemotingResponse BuildNoNewMessageResponse(RemotingRequest remotingRequest)
        {
            return RemotingResponseFactory.CreateResponse(remotingRequest, (short)PullStatus.NoNewMessage);
        }
        private RemotingResponse BuildIgnoredResponse(RemotingRequest remotingRequest)
        {
            return RemotingResponseFactory.CreateResponse(remotingRequest, (short)PullStatus.Ignored);
        }
        private RemotingResponse BuildNextOffsetResetResponse(RemotingRequest remotingRequest, long nextOffset)
        {
            return RemotingResponseFactory.CreateResponse(remotingRequest, (short)PullStatus.NextOffsetReset, BitConverter.GetBytes(nextOffset));
        }
        private RemotingResponse BuildQueueNotExistResponse(RemotingRequest remotingRequest)
        {
            return RemotingResponseFactory.CreateResponse(remotingRequest, (short)PullStatus.QueueNotExist);
        }
        private RemotingResponse BuildFoundResponse(RemotingRequest remotingRequest, IEnumerable<byte[]> messages)
        {
            return RemotingResponseFactory.CreateResponse(remotingRequest, (short)PullStatus.Found, Combine(messages));
        }
        private void SendRemotingResponse(PullRequest pullRequest, RemotingResponse remotingResponse)
        {
            pullRequest.RequestHandlerContext.SendRemotingResponse(remotingResponse);
        }
        private long GetNextConsumeOffset(string topic, int queueId, string consumerGroup, ConsumeFromWhere consumerFromWhere)
        {
            var queueConsumedOffset = _offsetStore.GetConsumeOffset(topic, queueId, consumerGroup);
            if (queueConsumedOffset >= 0)
            {
                var queueCurrentOffset = _queueStore.GetQueueCurrentOffset(topic, queueId);
                return queueCurrentOffset < queueConsumedOffset ? queueCurrentOffset + 1 : queueConsumedOffset + 1;
            }

            if (consumerFromWhere == ConsumeFromWhere.FirstOffset)
            {
                var queueMinOffset = _queueStore.GetQueueMinOffset(topic, queueId);
                if (queueMinOffset < 0)
                {
                    return 0;
                }
                return queueMinOffset;
            }
            else
            {
                var queueCurrentOffset = _queueStore.GetQueueCurrentOffset(topic, queueId);
                if (queueCurrentOffset < 0)
                {
                    return 0;
                }
                return queueCurrentOffset + 1;
            }
        }
        private static PullMessageRequest DeserializePullMessageRequest(byte[] data)
        {
            using (var stream = new MemoryStream(data))
            {
                return PullMessageRequest.ReadFromStream(stream);
            }
        }
        private static byte[] Combine(IEnumerable<byte[]> arrays)
        {
            byte[] destination = new byte[arrays.Sum(x => x.Length)];
            int offset = 0;
            foreach (byte[] data in arrays)
            {
                Buffer.BlockCopy(data, 0, destination, offset, data.Length);
                offset += data.Length;
            }
            return destination;
        }

        class PullMessageResult
        {
            public PullStatus Status { get; set; }
            public long NextBeginOffset { get; set; }
            public IEnumerable<byte[]> Messages { get; set; }
        }
    }
}
