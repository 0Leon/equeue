﻿using EQueue.Broker.Processors;
using EQueue.Infrastructure.IoC;
using EQueue.Remoting;

namespace EQueue.Broker
{
    public class BrokerController
    {
        private readonly IMessageService _messageService;
        private readonly IRemotingServer _remotingServer;

        public BrokerController()
        {
            _messageService = ObjectContainer.Resolve<IMessageService>();
            _remotingServer = ObjectContainer.Resolve<IRemotingServer>();
        }

        public BrokerController Initialize()
        {
            _remotingServer.RegisterRequestProcessor((int)RequestCode.SendMessage, new SendMessageRequestProcessor());
            _remotingServer.RegisterRequestProcessor((int)RequestCode.PullMessage, new PullMessageRequestProcessor());
            return this;
        }
        public void Start()
        {
            _remotingServer.Start();
        }
        public void Shutdown()
        {

        }
    }
}
