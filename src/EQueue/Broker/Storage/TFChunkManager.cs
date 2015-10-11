﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ECommon.Components;
using ECommon.Logging;
using ECommon.Scheduling;
using ECommon.Utilities;

namespace EQueue.Broker.Storage
{
    public class TFChunkManager : IDisposable
    {
        private static readonly ILogger _logger = ObjectContainer.Resolve<ILoggerFactory>().Create(typeof(TFChunkManager));
        private readonly object _chunksLocker = new object();
        private readonly TFChunkManagerConfig _config;
        private readonly IDictionary<int, TFChunk> _chunks;
        private readonly string _chunkPath;
        private readonly IScheduleService _scheduleService;
        private readonly string _uncacheChunkTaskName;
        private int _nextChunkNumber;
        private int _uncachingChunks;

        public string Name { get; private set; }
        public TFChunkManagerConfig Config { get { return _config; } }
        public string ChunkPath { get { return _chunkPath; } }

        public TFChunkManager(string name, TFChunkManagerConfig config, string relativePath = null)
        {
            Ensure.NotNull(name, "name");
            Ensure.NotNull(config, "config");

            Name = name;
            _config = config;
            if (string.IsNullOrEmpty(relativePath))
            {
                _chunkPath = _config.BasePath;
            }
            else
            {
                _chunkPath = Path.Combine(_config.BasePath, relativePath);
            }
            _chunks = new ConcurrentDictionary<int, TFChunk>();
            _scheduleService = ObjectContainer.Resolve<IScheduleService>();
            _uncacheChunkTaskName = string.Format("{0}.{1}.UncacheChunks", Name, this.GetType().Name);

            if (!_config.ForceCacheChunkInMemory)
            {
                _scheduleService.StartTask(_uncacheChunkTaskName, () => UncacheChunks(), 1000, 1000);
            }
        }

        public void Load<T>(Func<int, BinaryReader, T> readRecordFunc) where T : ILogRecord
        {
            lock (_chunksLocker)
            {
                if (!Directory.Exists(_chunkPath))
                {
                    Directory.CreateDirectory(_chunkPath);
                }

                var tempFiles = _config.FileNamingStrategy.GetTempFiles(_chunkPath);
                foreach (var file in tempFiles)
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }

                var files = _config.FileNamingStrategy.GetChunkFiles(_chunkPath);
                if (files.Length > 0)
                {
                    var cachedChunkCount = 0;
                    for (var i = files.Length - 2; i >= 0; i--)
                    {
                        var file = files[i];
                        File.SetAttributes(file, FileAttributes.Normal);
                        var chunk = TFChunk.FromCompletedFile(file, this, _config);
                        if (_config.ForceCacheChunkInMemory || cachedChunkCount < _config.PreCacheChunkCount)
                        {
                            if (chunk.TryCacheInMemory(false))
                            {
                                cachedChunkCount++;
                            }
                        }
                        AddChunk(chunk);
                    }
                    if (!EnsureMemoryEnough())
                    {
                        var applyMemoryInfo = GetChunkApplyMemoryInfo();
                        var errorMsg = string.Format("Not enough memory to create ongoing chunk, physicalMemorySize: {0}MB, currentUsedMemorySize: {1}MB, chunkSize: {2}MB, remainingMemory: {3}MB, usedMemoryPercent: {4}%, maxAllowUseMemoryPercent: {5}%",
                            applyMemoryInfo.PhysicalMemoryMB,
                            applyMemoryInfo.UsedMemoryMB,
                            applyMemoryInfo.ChunkSizeMB,
                            applyMemoryInfo.RemainingMemoryMB,
                            applyMemoryInfo.UsedMemoryPercent,
                            _config.ChunkCacheMaxPercent);
                        throw new ChunkCreateException(errorMsg);
                    }
                    var lastFile = files[files.Length - 1];
                    File.SetAttributes(lastFile, FileAttributes.Normal);
                    AddChunk(TFChunk.FromOngoingFile(lastFile, this, _config, readRecordFunc));
                }
            }
        }
        public IList<TFChunk> GetAllChunks()
        {
            return _chunks.Values.ToList();
        }
        public TFChunk AddNewChunk()
        {
            lock (_chunksLocker)
            {
                if (!EnsureMemoryEnough())
                {
                    var applyMemoryInfo = GetChunkApplyMemoryInfo();
                    var errorMsg = string.Format("Not enough memory to create new chunk, physicalMemorySize: {0}MB, currentUsedMemorySize: {1}MB, chunkSize: {2}MB, remainingMemory: {3}MB, usedMemoryPercent: {4}%, maxAllowUseMemoryPercent: {5}%",
                        applyMemoryInfo.PhysicalMemoryMB,
                        applyMemoryInfo.UsedMemoryMB,
                        applyMemoryInfo.ChunkSizeMB,
                        applyMemoryInfo.RemainingMemoryMB,
                        applyMemoryInfo.UsedMemoryPercent,
                        _config.ChunkCacheMaxPercent);
                    throw new ChunkCreateException(errorMsg);
                }

                var chunkNumber = _nextChunkNumber;
                var chunkFileName = _config.FileNamingStrategy.GetFileNameFor(_chunkPath, chunkNumber);
                var chunk = TFChunk.CreateNew(chunkFileName, chunkNumber, this, _config);

                AddChunk(chunk);

                return chunk;
            }
        }
        public TFChunk GetFirstChunk()
        {
            lock (_chunksLocker)
            {
                if (_chunks.Count == 0)
                {
                    AddNewChunk();
                }
                var minChunkNum = _chunks.Keys.Min();
                return _chunks[minChunkNum];
            }
        }
        public TFChunk GetLastChunk()
        {
            lock (_chunksLocker)
            {
                if (_chunks.Count == 0)
                {
                    AddNewChunk();
                }
                return _chunks[_nextChunkNumber - 1];
            }
        }
        public TFChunk GetChunkFor(long dataPosition)
        {
            var chunkNum = (int)(dataPosition / _config.GetChunkDataSize());
            return GetChunk(chunkNum);
        }
        public TFChunk GetChunk(int chunkNum)
        {
            if (_chunks.ContainsKey(chunkNum))
            {
                return _chunks[chunkNum];
            }
            return null;
        }
        public bool RemoveChunk(TFChunk chunk)
        {
            lock (_chunksLocker)
            {
                if (_chunks.Remove(chunk.ChunkHeader.ChunkNumber))
                {
                    try
                    {
                        chunk.Delete();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(string.Format("Delete chunk {0} has exception.", chunk), ex);
                    }
                    return true;
                }
                return false;
            }
        }
        public void TryCacheNextChunk(TFChunk currentChunk)
        {
            var nextChunkNumber = currentChunk.ChunkHeader.ChunkNumber + 1;
            var nextChunk = GetChunk(nextChunkNumber);
            if (nextChunk != null && !nextChunk.IsMemoryChunk && nextChunk.IsCompleted)
            {
                if (!nextChunk.HasCachedChunk)
                {
                    nextChunk.TryCacheInMemory(false);
                }
            }
        }

        public void Dispose()
        {
            Close();
        }
        public void Close()
        {
            lock (_chunksLocker)
            {
                if (!_config.ForceCacheChunkInMemory)
                {
                    _scheduleService.StopTask(_uncacheChunkTaskName);
                }

                foreach (var chunk in _chunks.Values)
                {
                    try
                    {
                        chunk.Close();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(string.Format("Chunk {0} close failed.", chunk), ex);
                    }
                }
            }
        }

        private ChunkUtil.ChunkApplyMemoryInfo GetChunkApplyMemoryInfo()
        {
            ChunkUtil.ChunkApplyMemoryInfo applyMemoryInfo;
            var chunkSize = (ulong)(ChunkHeader.Size + _config.GetChunkDataSize() + ChunkFooter.Size);
            ChunkUtil.IsMemoryEnoughToCacheChunk(chunkSize, (uint)_config.ChunkCacheMaxPercent, out applyMemoryInfo);
            return applyMemoryInfo;
        }
        private bool EnsureMemoryEnough()
        {
            ChunkUtil.ChunkApplyMemoryInfo applyMemoryInfo;
            var chunkSize = (ulong)(ChunkHeader.Size + _config.GetChunkDataSize() + ChunkFooter.Size);

            //检查剩余物理内存是否足够，如果足够直接返回true
            var hasEnoughMemory = ChunkUtil.IsMemoryEnoughToCacheChunk(chunkSize, (uint)_config.ChunkCacheMaxPercent, out applyMemoryInfo);
            if (hasEnoughMemory)
            {
                return true;
            }

            //如果不足，则尝试释放一些前面已经完成的Chunk文件
            var tryTimes = 1;
            var maxTryTimes = 10;

            while (!hasEnoughMemory && tryTimes <= maxTryTimes)
            {
                _logger.WarnFormat("Not enough memory to create new chunk, try to release old completed chunks, tryTimes: {0}, physicalMemory: {1}MB, currentUsedMemory: {2}MB, chunkSize: {3}MB, remainingMemory: {4}MB, usedMemoryPercent: {5}%, maxAllowUseMemoryPercent: {6}%",
                    tryTimes,
                    applyMemoryInfo.PhysicalMemoryMB,
                    applyMemoryInfo.UsedMemoryMB,
                    applyMemoryInfo.ChunkSizeMB,
                    applyMemoryInfo.RemainingMemoryMB,
                    applyMemoryInfo.UsedMemoryPercent,
                    _config.ChunkCacheMaxPercent);

                UncacheChunks();
                Thread.Sleep(1000); 
                hasEnoughMemory = ChunkUtil.IsMemoryEnoughToCacheChunk(chunkSize, (uint)_config.ChunkCacheMaxPercent, out applyMemoryInfo);
                tryTimes++;
            }

            return hasEnoughMemory;
        }
        private void AddChunk(TFChunk chunk)
        {
            _chunks.Add(chunk.ChunkHeader.ChunkNumber, chunk);
            _nextChunkNumber = chunk.ChunkHeader.ChunkNumber + 1;
        }
        private int UncacheChunks(int maxUncacheCount = 10)
        {
            var uncachedCount = 0;

            if (Interlocked.CompareExchange(ref _uncachingChunks, 1, 0) == 0)
            {
                try
                {
                    var usedMemoryPercent = ChunkUtil.GetUsedMemoryPercent();
                    if (usedMemoryPercent <= (ulong)_config.ChunkCacheMinPercent)
                    {
                        return 0;
                    }

                    if (_logger.IsDebugEnabled)
                    {
                        _logger.DebugFormat("Current memory usage {0}% exceed the chunkCacheMinPercent {1}%, try to uncache chunks.", usedMemoryPercent, _config.ChunkCacheMinPercent);
                    }

                    var chunks = _chunks.Values.Where(x => x.IsCompleted && !x.IsMemoryChunk && x.HasCachedChunk).OrderBy(x => x.LastActiveTime).ToList();

                    foreach (var chunk in chunks)
                    {
                        if ((DateTime.Now - chunk.LastActiveTime).TotalSeconds >= _config.ChunkInactiveTimeMaxSeconds)
                        {
                            if (chunk.UnCacheFromMemory())
                            {
                                Thread.Sleep(1000); //即便有内存释放了，由于通过API读取到的内存使用数可能不会立即更新，所以等待一定时间后检查内存是否足够
                                uncachedCount++;
                                if (uncachedCount >= maxUncacheCount || ChunkUtil.GetUsedMemoryPercent() <= (ulong)_config.ChunkCacheMinPercent)
                                {
                                    break;
                                }
                            }
                        }
                    }

                    if (_logger.IsDebugEnabled)
                    {
                        if (uncachedCount > 0)
                        {
                            _logger.DebugFormat("Uncached {0} chunks, current memory usage: {1}%", uncachedCount, ChunkUtil.GetUsedMemoryPercent());
                        }
                        else
                        {
                            _logger.Debug("No chunks uncached.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("Uncaching chunks has exception.", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _uncachingChunks, 0);
                }
            }

            return uncachedCount;
        }
    }
}
