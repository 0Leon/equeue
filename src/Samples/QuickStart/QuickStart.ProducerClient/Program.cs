﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ECommon.Autofac;
using ECommon.Components;
using ECommon.Configurations;
using ECommon.JsonNet;
using ECommon.Log4Net;
using ECommon.Logging;
using ECommon.Scheduling;
using EQueue.Clients.Producers;
using EQueue.Configurations;
using EQueue.Protocols;

namespace QuickStart.ProducerClient
{
    class Program
    {
        static int finished;
        static int messageIndex;
        static Stopwatch watch;

        static void Main(string[] args)
        {
            InitializeEQueue();

            var producer = new Producer("Producer1").Start();
            var parallelCount = 4;
            var messageSize = 1024;
            var messageCount = 2500;
            var message = new byte[messageSize];

            var action = new Action(() =>
            {
                for (var index = 0; index < messageCount; index++)
                {
                    producer.SendAsync(new Message("SampleTopic", 100, message), Interlocked.Increment(ref messageIndex)).ContinueWith(sendTask =>
                    {
                        if (sendTask.Result.SendStatus == SendStatus.Success)
                        {
                            var finishedCount = Interlocked.Increment(ref finished);
                            if (finishedCount == 1)
                            {
                                watch = Stopwatch.StartNew();
                            }
                            if (finishedCount % 1000 == 0)
                            {
                                _logger.InfoFormat("Sent {0} messages, time spent:{1}", finishedCount, watch.ElapsedMilliseconds);
                            }
                        }
                        else
                        {
                            _logger.Error("Sent message timeout.");
                        }
                    });
                }
            });

            var actions = new List<Action>();
            for (var index = 0; index < parallelCount; index++)
            {
                actions.Add(action);
            }

            Parallel.Invoke(actions.ToArray());

            Console.ReadLine();
        }

        static ILogger _logger;
        static void InitializeEQueue()
        {
            Configuration
                .Create()
                .UseAutofac()
                .RegisterCommonComponents()
                .UseLog4Net()
                .UseJsonNet()
                .RegisterEQueueComponents()
                .SetDefault<IQueueSelector, QueueAverageSelector>();
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create("Program");
        }
    }
}
