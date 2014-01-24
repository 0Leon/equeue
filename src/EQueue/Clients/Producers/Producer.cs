﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using ECommon.IoC;
using ECommon.Logging;
using ECommon.Remoting;
using ECommon.Serializing;
using EQueue.Protocols;
using EQueue.Utils;

namespace EQueue.Clients.Producers
{
    public class Producer
    {
        private const int SendMessageTimeoutMilliseconds = 10 * 1000;
        private readonly ConcurrentDictionary<string, int> _topicQueueCountDict;
        private readonly SocketRemotingClient _remotingClient;
        private readonly IBinarySerializer _binarySerializer;
        private readonly IQueueSelector _queueSelector;
        private readonly ILogger _logger;

        public string Id { get; private set; }

        public Producer(string id) : this(id, "127.0.0.1", 5000) { }
        public Producer(string id, string brokerAddress, int brokerPort)
        {
            Id = id;
            _topicQueueCountDict = new ConcurrentDictionary<string, int>();
            _remotingClient = new SocketRemotingClient(brokerAddress, brokerPort);
            _binarySerializer = ObjectContainer.Resolve<IBinarySerializer>();
            _queueSelector = ObjectContainer.Resolve<IQueueSelector>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().Name);
        }

        public Producer Start()
        {
            _remotingClient.Start();
            _logger.InfoFormat("Producer[{0}] started.", Id);
            return this;
        }
        public void Shutdown()
        {
            _remotingClient.Shutdown();
        }
        public SendResult Send(Message message, object arg)
        {
            var queueCount = GetTopicQueueCount(message.Topic);
            if (queueCount == 0)
            {
                throw new Exception(string.Format("No available queue for topic [{0}].", message.Topic));
            }
            var queueId = _queueSelector.SelectQueueId(queueCount, message, arg);
            var remotingRequest = BuildSendMessageRequest(message, queueId);
            var remotingResponse = _remotingClient.InvokeSync(remotingRequest, SendMessageTimeoutMilliseconds);
            var response = _binarySerializer.Deserialize<SendMessageResponse>(remotingResponse.Body);
            var sendStatus = SendStatus.Success; //TODO, figure from remotingResponse.Code;
            return new SendResult(sendStatus, response.MessageOffset, response.MessageQueue, response.QueueOffset);
        }
        public Task<SendResult> SendAsync(Message message, object arg)
        {
            var queueCount = GetTopicQueueCount(message.Topic);
            if (queueCount == 0)
            {
                throw new Exception(string.Format("No available queue for topic [{0}].", message.Topic));
            }
            var queueId = _queueSelector.SelectQueueId(queueCount, message, arg);
            var remotingRequest = BuildSendMessageRequest(message, queueId);
            var taskCompletionSource = new TaskCompletionSource<SendResult>();
            _remotingClient.InvokeAsync(remotingRequest, SendMessageTimeoutMilliseconds).ContinueWith((requestTask) =>
            {
                var remotingResponse = requestTask.Result;
                if (remotingResponse != null)
                {
                    var response = _binarySerializer.Deserialize<SendMessageResponse>(remotingResponse.Body);
                    var sendStatus = SendStatus.Success; //TODO, figure from remotingResponse.Code;
                    var result = new SendResult(sendStatus, response.MessageOffset, response.MessageQueue, response.QueueOffset);
                    taskCompletionSource.SetResult(result);
                }
                else
                {
                    var result = new SendResult(SendStatus.Failed, "Send message request failed or wait for response timeout.");
                    taskCompletionSource.SetResult(result);
                }
            });
            return taskCompletionSource.Task;
        }

        private int GetTopicQueueCount(string topic)
        {
            int count;
            if (!_topicQueueCountDict.TryGetValue(topic, out count))
            {
                var countFromServer = GetTopicQueueCountFromBroker(topic);
                _topicQueueCountDict[topic] = countFromServer;
                count = countFromServer;
            }

            return count;
        }
        private int GetTopicQueueCountFromBroker(string topic)
        {
            var remotingRequest = new RemotingRequest((int)RequestCode.GetTopicQueueCount, Encoding.UTF8.GetBytes(topic));
            var remotingResponse = _remotingClient.InvokeSync(remotingRequest, 10000);
            if (remotingResponse.Code == (int)ResponseCode.Success)
            {
                return BitConverter.ToInt32(remotingResponse.Body, 0);
            }
            else
            {
                throw new Exception(string.Format("[{0}]: GetTopicQueueCountFromBroker has exception, remoting response code:{1}", Id, remotingResponse.Code));
            }
        }
        private RemotingRequest BuildSendMessageRequest(Message message, int queueId)
        {
            var request = new SendMessageRequest { Message = message, QueueId = queueId };
            var data = MessageUtils.EncodeSendMessageRequest(request);
            return new RemotingRequest((int)RequestCode.SendMessage, data);
        }
    }
}
