﻿using System;
using System.Configuration;
using System.Text;
using ECommon.Autofac;
using ECommon.Components;
using ECommon.JsonNet;
using ECommon.Log4Net;
using ECommon.Scheduling;
using EQueue.Broker;
using EQueue.Configurations;
using EQueue.Utils;
using ECommonConfiguration = ECommon.Configurations.Configuration;

namespace QuickStart.BrokerServer
{
    class Program
    {
        static void Main(string[] args)
        {
            InitializeEQueue();
            BrokerController.Create(new BrokerSetting().SetMessageChunkConfig(ConfigurationManager.AppSettings["fileStoreRootPath"])).Start();
            Console.ReadLine();
        }

        static void InitializeEQueue()
        {
            var configuration = ECommonConfiguration
                .Create()
                .UseAutofac()
                .RegisterCommonComponents()
                .UseLog4Net()
                .UseJsonNet()
                .RegisterUnhandledExceptionHandler()
                .RegisterEQueueComponents();
        }
    }
}
