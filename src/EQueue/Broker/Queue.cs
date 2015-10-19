﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Serializing;
using EQueue.Broker.Storage;

namespace EQueue.Broker
{
    public class Queue
    {
        private const string QueueSettingFileName = "queue.setting";
        private readonly ChunkWriter _chunkWriter;
        private readonly ChunkReader _chunkReader;
        private readonly ChunkManager _chunkManager;
        private long _nextOffset = 0;
        private readonly IJsonSerializer _jsonSerializer;
        private QueueSetting _setting;
        private readonly string _queueSettingFile;
        private ILogger _logger;

        public string Topic { get; private set; }
        public int QueueId { get; private set; }
        public long NextOffset { get { return _nextOffset; } }
        public QueueSetting Setting { get { return _setting; } }

        public Queue(string topic, int queueId)
        {
            Topic = topic;
            QueueId = queueId;

            _jsonSerializer = ObjectContainer.Resolve<IJsonSerializer>();
            _chunkManager = new ChunkManager(string.Format("{0}-{1}", Topic, QueueId), BrokerController.Instance.Setting.QueueChunkConfig, Topic + @"\" + QueueId);
            _chunkWriter = new ChunkWriter(_chunkManager);
            _chunkReader = new ChunkReader(_chunkManager, _chunkWriter);
            _queueSettingFile = Path.Combine(_chunkManager.ChunkPath, QueueSettingFileName);
            _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(this.GetType().FullName);
        }

        public void CleanChunks()
        {
            _chunkManager.Clean();
        }
        public void Load()
        {
            _setting = LoadQueueSetting();
            if (_setting == null)
            {
                _setting = new QueueSetting { Status = QueueStatus.Normal };
                SaveQueueSetting();
            }
            _chunkManager.Load(ReadMessageIndex);
            _chunkWriter.Open();

            var lastChunk = _chunkManager.GetLastChunk();
            var lastOffsetGlobalPosition = lastChunk.DataPosition + lastChunk.ChunkHeader.ChunkDataStartPosition;
            if (lastOffsetGlobalPosition > 0)
            {
                _nextOffset = lastOffsetGlobalPosition / _chunkManager.Config.ChunkDataUnitSize;
            }
        }
        public void Close()
        {
            _chunkWriter.Close();
            _chunkManager.Close();
        }
        public void AddMessage(long messagePosition)
        {
            _chunkWriter.Write(new QueueLogRecord(messagePosition + 1));
        }
        public long GetMessagePosition(long queueOffset, bool autoCache = true)
        {
            var position = queueOffset * _chunkManager.Config.ChunkDataUnitSize;
            var record = _chunkReader.TryReadAt(position, ReadMessageIndex, autoCache);
            if (record == null)
            {
                return -1L;
            }
            return record.MessageLogPosition - 1;
        }
        public void Enable()
        {
            _setting.Status = QueueStatus.Normal;
            SaveQueueSetting();
        }
        public void Disable()
        {
            _setting.Status = QueueStatus.Disabled;
            SaveQueueSetting();
        }
        public void Delete()
        {
            Close();
            CleanChunks();
            Directory.Delete(_chunkManager.ChunkPath);
        }
        public long IncrementNextOffset()
        {
            return _nextOffset++;
        }
        public long GetMinQueueOffset()
        {
            return _chunkManager.GetFirstChunk().ChunkHeader.ChunkDataStartPosition / _chunkManager.Config.ChunkDataUnitSize;
        }
        public void DeleteMessages(long minMessagePosition)
        {
            var chunks = _chunkManager.GetAllChunks().Where(x => x.IsCompleted).OrderBy(x => x.ChunkHeader.ChunkNumber);

            foreach (var chunk in chunks)
            {
                var maxPosition = chunk.ChunkHeader.ChunkDataEndPosition - _chunkManager.Config.ChunkDataUnitSize;
                var record = _chunkReader.TryReadAt(maxPosition, ReadMessageIndex, false);
                if (record == null)
                {
                    continue;
                }
                var chunkLastMessagePosition = record.MessageLogPosition - 1;
                if (chunkLastMessagePosition < minMessagePosition)
                {
                    if (_chunkManager.RemoveChunk(chunk))
                    {
                        _logger.InfoFormat("Queue (topic: {0}, queueId: {1}) chunk #{2} is deleted, chunkLastMessagePosition: {3}, messageStoreMinMessagePosition: {4}", Topic, QueueId, chunk.ChunkHeader.ChunkNumber, chunkLastMessagePosition, minMessagePosition);
                    }
                }
            }
        }

        private QueueLogRecord ReadMessageIndex(byte[] recordBuffer)
        {
            var record = new QueueLogRecord();
            record.ReadFrom(recordBuffer);
            if (record.MessageLogPosition <= 0)
            {
                return null;
            }
            return record;
        }
        private QueueSetting LoadQueueSetting()
        {
            if (!Directory.Exists(_chunkManager.ChunkPath))
            {
                Directory.CreateDirectory(_chunkManager.ChunkPath);
            }
            using (var stream = new FileStream(_queueSettingFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                using (var reader = new StreamReader(stream))
                {
                    var text = reader.ReadToEnd();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return _jsonSerializer.Deserialize<QueueSetting>(text);
                    }
                    return null;
                }
            }
        }
        private void SaveQueueSetting()
        {
            if (!Directory.Exists(_chunkManager.ChunkPath))
            {
                Directory.CreateDirectory(_chunkManager.ChunkPath);
            }
            using (var stream = new FileStream(_queueSettingFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(_jsonSerializer.Serialize(_setting));
                }
            }
        }
    }
    public class QueueSetting
    {
        public QueueStatus Status;
    }
}
