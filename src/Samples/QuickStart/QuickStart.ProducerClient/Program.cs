﻿using System;
using System.Text;
using EQueue.Clients.Producers;
using EQueue.Protocols;

namespace QuickStart.ProducerClient
{
    class Program
    {
        static void Main(string[] args)
        {
            var producer = new Producer().Start();
            var message1 = new Message("topic1", Encoding.UTF8.GetBytes("Message1"));
            var message2 = new Message("topic2", Encoding.UTF8.GetBytes("Message2"));
            producer.Send(message1, "1");
            producer.Send(message2, "2");
            Console.ReadLine();
        }
    }
}
