﻿using System.Threading;
using EQueue.Infrastructure;
using EQueue.Infrastructure.IoC;
using EQueue.Infrastructure.Logging;
using EQueue.Protocols;
using EQueue.Remoting;
using EQueue.Remoting.Requests;
using EQueue.Remoting.Responses;

namespace EQueue.Broker.Processors
{
    public class SendMessageRequestHandler : IRequestHandler
    {
        private IMessageService _messageService;
        private IBinarySerializer _binarySerializer;
        private ILogger _logger;

        public SendMessageRequestHandler()
        {
            _messageService = ObjectContainer.Resolve<IMessageService>();
            _binarySerializer = ObjectContainer.Resolve<IBinarySerializer>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(GetType().Name);
        }

        public RemotingResponse HandleRequest(IRequestHandlerContext context, RemotingRequest request)
        {
            var sendMessageRequest = _binarySerializer.Deserialize<SendMessageRequest>(request.Body);
            var storeResult = _messageService.StoreMessage(sendMessageRequest.Message, sendMessageRequest.Arg);
            var sendMessageResponse = new SendMessageResponse(
                storeResult.MessageOffset,
                new MessageQueue(sendMessageRequest.Message.Topic, storeResult.QueueId),
                storeResult.QueueOffset);
            var responseData = _binarySerializer.Serialize(sendMessageResponse);

            var current = Interlocked.Increment(ref total);
            if (current % 2000 == 0)
            {
                _logger.Debug(current + "," + sendMessageResponse.MessageOffset);
            }

            return new RemotingResponse((int)ResponseCode.Success, request.Sequence, responseData);
        }

        int total = 0;
    }
}
