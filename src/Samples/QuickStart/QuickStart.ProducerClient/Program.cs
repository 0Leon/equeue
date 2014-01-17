﻿using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EQueue;
using EQueue.Autofac;
using EQueue.Clients.Producers;
using EQueue.Infrastructure.IoC;
using EQueue.Infrastructure.Scheduling;
using EQueue.JsonNet;
using EQueue.Log4Net;
using EQueue.Protocols;

namespace QuickStart.ProducerClient
{
    class Program
    {
        static void Main(string[] args)
        {
            InitializeEQueue();

            var scheduleService = ObjectContainer.Resolve<IScheduleService>();
            var producer = new Producer().Start();
            var index = 100;

            scheduleService.ScheduleTask(() =>
            {
                var message = "message" + Interlocked.Increment(ref index);
                producer.SendAsync(new Message("SampleTopic", Encoding.UTF8.GetBytes(message)), index.ToString()).ContinueWith(sendTask =>
                {
                    Console.WriteLine(string.Format("Sent:{0}, result:{1}", message, sendTask.Result));
                });
            }, 0, 3000);

            Console.ReadLine();
        }

        static void InitializeEQueue()
        {
            Configuration
                .Create()
                .UseAutofac()
                .UseLog4Net()
                .UseJsonNet()
                .RegisterFrameworkComponents();
        }
    }
}
